using System.Buffers.Binary;
using System.Diagnostics;
using SoundFlow.Enums;
using SoundFlow.Extensions.WebRtc.Apm;
using SoundFlow.Extensions.WebRtc.Apm.Components;
using SoundFlow.Interfaces;
using SoundFlow.Metadata.Models;
using SoundFlow.Structs;

namespace BabyAI.Desktop;

internal sealed record SpeechNoiseSuppressionMetrics(
    double NoiseFloorDbfs,
    double EstimatedSnrDb,
    long ProcessingMilliseconds);

internal sealed record SpeechDenoiseAbResult(
    SpeechSignalMetrics RawSignal,
    SpeechSignalMetrics CleanSignal,
    SpeechNoiseSuppressionMetrics NoiseSuppression,
    SpeechToTextMeasurement TinyRaw,
    SpeechToTextMeasurement BaseRaw,
    SpeechToTextMeasurement BaseClean);

internal static class SpeechDenoiseAbComparison
{
    public static async Task<SpeechDenoiseAbResult> RunAsync(
        ReadOnlyMemory<byte> pcm16Mono,
        int sampleRate,
        ISpeechToTextProvider tiny,
        ISpeechToTextProvider @base,
        CancellationToken cancellationToken = default)
    {
        var rawMetrics = SpeechSignalMetrics.FromPcm16(pcm16Mono, sampleRate);
        var noiseFloorDbfs = EstimateNoiseFloorDbfs(pcm16Mono, sampleRate, 200);
        var estimatedSnrDb = double.IsNegativeInfinity(noiseFloorDbfs)
            ? double.PositiveInfinity
            : rawMetrics.RmsDbfs - noiseFloorDbfs;

        var stopwatch = Stopwatch.StartNew();
        var tinyRawTranscript = await tiny.TranscribeAsync(pcm16Mono, sampleRate, cancellationToken);
        stopwatch.Stop();
        var tinyRaw = new SpeechToTextMeasurement(tiny.Name, tinyRawTranscript, stopwatch.ElapsedMilliseconds);

        stopwatch.Restart();
        var baseRawTranscript = await @base.TranscribeAsync(pcm16Mono, sampleRate, cancellationToken);
        stopwatch.Stop();
        var baseRaw = new SpeechToTextMeasurement(@base.Name, baseRawTranscript, stopwatch.ElapsedMilliseconds);

        stopwatch.Restart();
        var cleanPcm = await Task.Run(
            () => WebRtcSpeechNoiseSuppressor.Process(pcm16Mono, sampleRate, cancellationToken),
            cancellationToken).ConfigureAwait(false);
        stopwatch.Stop();
        var nsMetrics = new SpeechNoiseSuppressionMetrics(
            noiseFloorDbfs,
            estimatedSnrDb,
            stopwatch.ElapsedMilliseconds);
        var cleanMetrics = SpeechSignalMetrics.FromPcm16(cleanPcm, sampleRate);

        stopwatch.Restart();
        var baseCleanTranscript = await @base.TranscribeAsync(cleanPcm, sampleRate, cancellationToken);
        stopwatch.Stop();
        var baseClean = new SpeechToTextMeasurement(@base.Name, baseCleanTranscript, stopwatch.ElapsedMilliseconds);

        return new SpeechDenoiseAbResult(
            rawMetrics,
            cleanMetrics,
            nsMetrics,
            tinyRaw,
            baseRaw,
            baseClean);
    }

    private static double EstimateNoiseFloorDbfs(ReadOnlyMemory<byte> pcm16Mono, int sampleRate, int windowMilliseconds)
    {
        var span = pcm16Mono.Span;
        var availableSamples = span.Length / sizeof(short);
        var requestedSamples = Math.Max(1, sampleRate * windowMilliseconds / 1000);
        var sampleCount = Math.Min(availableSamples, requestedSamples);
        if (sampleCount <= 0)
            return double.NegativeInfinity;

        double sumSquares = 0;
        for (var i = 0; i < sampleCount; i++)
        {
            var sample = BinaryPrimitives.ReadInt16LittleEndian(span.Slice(i * sizeof(short), sizeof(short)));
            var normalized = sample / 32768d;
            sumSquares += normalized * normalized;
        }

        var rms = Math.Sqrt(sumSquares / sampleCount);
        return rms <= 0 ? double.NegativeInfinity : 20d * Math.Log10(rms);
    }
}

internal static class WebRtcSpeechNoiseSuppressor
{
    public static byte[] Process(
        ReadOnlyMemory<byte> pcm16Mono,
        int sampleRate,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (sampleRate != 16_000)
            throw new ArgumentOutOfRangeException(nameof(sampleRate), "WebRTC diagnostic expects 16 kHz mono PCM16 audio.");

        var input = ConvertPcm16ToFloat(pcm16Mono.Span);
        if (input.Length == 0)
            return [];

        using var provider = new InMemoryFloatSoundDataProvider(input, sampleRate);
        var format = new AudioFormat
        {
            Format = SampleFormat.F32,
            SampleRate = sampleRate,
            Channels = 1,
        };

        // Diagnostic pass: NS only. Deliberately keep AEC/AGC/HPF/preamp disabled
        // so the A/B test changes one signal-processing factor at a time.
        using var suppressor = new NoiseSuppressor(
            provider,
            format,
            suppressionLevel: NoiseSuppressionLevel.High,
            useMultichannelProcessing: false);

        cancellationToken.ThrowIfCancellationRequested();
        var clean = suppressor.ProcessAll();
        cancellationToken.ThrowIfCancellationRequested();
        return ConvertFloatToPcm16(clean);
    }

    private static float[] ConvertPcm16ToFloat(ReadOnlySpan<byte> audio)
    {
        var sampleCount = audio.Length / sizeof(short);
        var samples = new float[sampleCount];
        for (var i = 0; i < sampleCount; i++)
        {
            var value = BinaryPrimitives.ReadInt16LittleEndian(audio.Slice(i * sizeof(short), sizeof(short)));
            samples[i] = value / 32768f;
        }
        return samples;
    }

    private static byte[] ConvertFloatToPcm16(ReadOnlySpan<float> samples)
    {
        var audio = new byte[samples.Length * sizeof(short)];
        for (var i = 0; i < samples.Length; i++)
        {
            var clamped = Math.Clamp(samples[i], -1f, 0.9999695f);
            var value = (short)Math.Round(clamped * 32768f);
            BinaryPrimitives.WriteInt16LittleEndian(audio.AsSpan(i * sizeof(short), sizeof(short)), value);
        }
        return audio;
    }
}

internal sealed class InMemoryFloatSoundDataProvider : ISoundDataProvider
{
    private readonly float[] _samples;
    private int _position;
    private bool _disposed;

    public InMemoryFloatSoundDataProvider(float[] samples, int sampleRate)
    {
        _samples = samples ?? throw new ArgumentNullException(nameof(samples));
        if (sampleRate <= 0)
            throw new ArgumentOutOfRangeException(nameof(sampleRate));
        SampleRate = sampleRate;
    }

    public int Position => _position;
    public int Length => _samples.Length;
    public bool CanSeek => true;
    public SampleFormat SampleFormat => SampleFormat.F32;
    public int SampleRate { get; }
    public bool IsDisposed => _disposed;
    public SoundFormatInfo? FormatInfo => null;

    public event EventHandler<EventArgs>? EndOfStreamReached;
    public event EventHandler<PositionChangedEventArgs>? PositionChanged;

    public int ReadBytes(Span<float> buffer)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (buffer.Length == 0 || _position >= _samples.Length)
            return 0;

        var count = Math.Min(buffer.Length, _samples.Length - _position);
        _samples.AsSpan(_position, count).CopyTo(buffer);
        _position += count;
        PositionChanged?.Invoke(this, new PositionChangedEventArgs(_position));
        if (_position >= _samples.Length)
            EndOfStreamReached?.Invoke(this, EventArgs.Empty);
        return count;
    }

    public void Seek(int offset)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _position = Math.Clamp(offset, 0, _samples.Length);
        PositionChanged?.Invoke(this, new PositionChangedEventArgs(_position));
    }

    public void Dispose()
    {
        _disposed = true;
        GC.SuppressFinalize(this);
    }
}
