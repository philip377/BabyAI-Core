using System.Buffers.Binary;
using System.Diagnostics;
using SherpaOnnx;

namespace BabyAI.Desktop;

internal interface ISpeechToTextProvider : IDisposable
{
    string Name { get; }

    Task<string> TranscribeAsync(
        ReadOnlyMemory<byte> pcm16Mono,
        int sampleRate,
        CancellationToken cancellationToken = default);
}

internal static class SpeechToTextProviderFactory
{
    internal const string DefaultModelName = "sherpa-onnx-whisper-tiny";
    internal const string ComparisonModelName = "sherpa-onnx-whisper-base";

    public static ISpeechToTextProvider CreateDefault()
    {
        var modelPath = ResolveModelPath("BABYAI_STT_MODEL_DIR", DefaultModelName);
        return new SherpaOnnxWhisperSpeechToTextProvider(
            modelPath,
            modelPrefix: "tiny",
            providerName: DefaultModelName);
    }

    public static ISpeechToTextProvider CreateBaseComparison()
    {
        var modelPath = ResolveModelPath("BABYAI_STT_AB_BASE_MODEL_DIR", ComparisonModelName);
        return new SherpaOnnxWhisperSpeechToTextProvider(
            modelPath,
            modelPrefix: "base",
            providerName: ComparisonModelName);
    }

    private static string ResolveModelPath(string environmentVariable, string modelName)
    {
        var configured = Environment.GetEnvironmentVariable(environmentVariable);
        return string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(AppContext.BaseDirectory, "stt", modelName)
            : Path.GetFullPath(Environment.ExpandEnvironmentVariables(configured.Trim()));
    }
}

internal sealed class SherpaOnnxWhisperSpeechToTextProvider : ISpeechToTextProvider
{
    private readonly string _encoderFileName;
    private readonly string _decoderFileName;
    private readonly string _tokensFileName;
    private readonly string _name;
    private readonly object _recognizerSync = new();
    private OfflineRecognizer? _recognizer;
    private bool _disposed;

    public SherpaOnnxWhisperSpeechToTextProvider(string modelPath)
        : this(
            modelPath,
            modelPrefix: "tiny",
            providerName: SpeechToTextProviderFactory.DefaultModelName)
    {
    }

    internal SherpaOnnxWhisperSpeechToTextProvider(
        string modelPath,
        string modelPrefix,
        string providerName)
    {
        if (string.IsNullOrWhiteSpace(modelPath))
            throw new ArgumentException("STT model path is required.", nameof(modelPath));
        if (string.IsNullOrWhiteSpace(modelPrefix))
            throw new ArgumentException("STT model prefix is required.", nameof(modelPrefix));
        if (string.IsNullOrWhiteSpace(providerName))
            throw new ArgumentException("STT provider name is required.", nameof(providerName));
        if (!Directory.Exists(modelPath))
        {
            throw new DirectoryNotFoundException(
                $"Локальная STT-модель не найдена: {modelPath}. " +
                "Переустановите актуальную диагностическую сборку UNIX или проверьте путь к модели.");
        }

        _encoderFileName = $"{modelPrefix}-encoder.int8.onnx";
        _decoderFileName = $"{modelPrefix}-decoder.int8.onnx";
        _tokensFileName = $"{modelPrefix}-tokens.txt";
        _name = providerName.Trim();

        foreach (var fileName in new[] { _encoderFileName, _decoderFileName, _tokensFileName })
        {
            var path = Path.Combine(modelPath, fileName);
            if (!File.Exists(path))
            {
                throw new FileNotFoundException(
                    $"Локальная STT-модель неполная: отсутствует {fileName}.",
                    path);
            }
        }

        ModelPath = modelPath;
    }

    public string Name => _name;

    internal string ModelPath { get; }

    public async Task<string> TranscribeAsync(
        ReadOnlyMemory<byte> pcm16Mono,
        int sampleRate,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (sampleRate != 16_000)
            throw new ArgumentOutOfRangeException(nameof(sampleRate), "STT v1 expects 16 kHz mono PCM16 audio.");
        if (pcm16Mono.Length < sizeof(short))
            return string.Empty;

        var audio = pcm16Mono.ToArray();
        return await Task.Run(
            () => TranscribeCore(audio, sampleRate, cancellationToken),
            cancellationToken).ConfigureAwait(false);
    }

    private string TranscribeCore(byte[] audio, int sampleRate, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var samples = ConvertPcm16ToFloat(audio);
        if (samples.Length == 0)
            return string.Empty;

        lock (_recognizerSync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            cancellationToken.ThrowIfCancellationRequested();

            var recognizer = _recognizer ??= CreateRecognizer();
            using var stream = recognizer.CreateStream();
            stream.AcceptWaveform(sampleRate, samples);

            cancellationToken.ThrowIfCancellationRequested();
            recognizer.Decode(stream);
            cancellationToken.ThrowIfCancellationRequested();

            return NormalizeTranscript(stream.Result.Text);
        }
    }

    private OfflineRecognizer CreateRecognizer()
    {
        var config = new OfflineRecognizerConfig();
        config.FeatConfig.SampleRate = 16_000;
        config.FeatConfig.FeatureDim = 80;
        config.ModelConfig.Tokens = Path.Combine(ModelPath, _tokensFileName);
        config.ModelConfig.NumThreads = 2;
        config.ModelConfig.Debug = 0;
        config.ModelConfig.Provider = "cpu";
        config.ModelConfig.Whisper.Encoder = Path.Combine(ModelPath, _encoderFileName);
        config.ModelConfig.Whisper.Decoder = Path.Combine(ModelPath, _decoderFileName);
        config.ModelConfig.Whisper.Language = "ru";
        config.ModelConfig.Whisper.Task = "transcribe";
        config.DecodingMethod = "greedy_search";
        return new OfflineRecognizer(config);
    }

    private static float[] ConvertPcm16ToFloat(byte[] audio)
    {
        var sampleCount = audio.Length / sizeof(short);
        var samples = new float[sampleCount];
        for (var i = 0; i < sampleCount; i++)
        {
            var value = BinaryPrimitives.ReadInt16LittleEndian(audio.AsSpan(i * sizeof(short), sizeof(short)));
            samples[i] = value / 32768f;
        }
        return samples;
    }

    private static string NormalizeTranscript(string text)
    {
        var words = text
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return string.Join(' ', words).Trim();
    }

    public void Dispose()
    {
        lock (_recognizerSync)
        {
            if (_disposed)
                return;
            _disposed = true;
            _recognizer?.Dispose();
            _recognizer = null;
        }
    }
}

internal sealed record SpeechSignalMetrics(
    int DurationMilliseconds,
    double RmsDbfs,
    double PeakDbfs,
    double ClippingPercent)
{
    public static SpeechSignalMetrics FromPcm16(ReadOnlyMemory<byte> pcm16Mono, int sampleRate)
    {
        if (sampleRate <= 0)
            throw new ArgumentOutOfRangeException(nameof(sampleRate));

        var audio = pcm16Mono.Span;
        var sampleCount = audio.Length / sizeof(short);
        if (sampleCount == 0)
            return new SpeechSignalMetrics(0, double.NegativeInfinity, double.NegativeInfinity, 0);

        double sumSquares = 0;
        var peak = 0;
        var clippedSamples = 0;
        for (var i = 0; i < sampleCount; i++)
        {
            var sample = BinaryPrimitives.ReadInt16LittleEndian(audio.Slice(i * sizeof(short), sizeof(short)));
            var absolute = Math.Abs((int)sample);
            peak = Math.Max(peak, absolute);
            if (absolute >= 32760)
                clippedSamples++;

            var normalized = sample / 32768d;
            sumSquares += normalized * normalized;
        }

        var rms = Math.Sqrt(sumSquares / sampleCount);
        var peakNormalized = peak / 32768d;
        return new SpeechSignalMetrics(
            DurationMilliseconds: (int)Math.Round(sampleCount * 1000d / sampleRate),
            RmsDbfs: ToDbfs(rms),
            PeakDbfs: ToDbfs(peakNormalized),
            ClippingPercent: clippedSamples * 100d / sampleCount);
    }

    private static double ToDbfs(double amplitude)
        => amplitude <= 0 ? double.NegativeInfinity : 20d * Math.Log10(amplitude);
}

internal sealed record SpeechToTextMeasurement(
    string ProviderName,
    string Transcript,
    long DecodeMilliseconds);

internal sealed record SpeechToTextAbResult(
    SpeechSignalMetrics Signal,
    SpeechToTextMeasurement Tiny,
    SpeechToTextMeasurement Base);

internal static class SpeechToTextAbComparison
{
    public static async Task<SpeechToTextAbResult> RunAsync(
        ReadOnlyMemory<byte> pcm16Mono,
        int sampleRate,
        ISpeechToTextProvider tiny,
        ISpeechToTextProvider @base,
        CancellationToken cancellationToken = default)
    {
        var metrics = SpeechSignalMetrics.FromPcm16(pcm16Mono, sampleRate);

        var stopwatch = Stopwatch.StartNew();
        var tinyTranscript = await tiny.TranscribeAsync(pcm16Mono, sampleRate, cancellationToken);
        stopwatch.Stop();
        var tinyMeasurement = new SpeechToTextMeasurement(tiny.Name, tinyTranscript, stopwatch.ElapsedMilliseconds);

        stopwatch.Restart();
        var baseTranscript = await @base.TranscribeAsync(pcm16Mono, sampleRate, cancellationToken);
        stopwatch.Stop();
        var baseMeasurement = new SpeechToTextMeasurement(@base.Name, baseTranscript, stopwatch.ElapsedMilliseconds);

        return new SpeechToTextAbResult(metrics, tinyMeasurement, baseMeasurement);
    }
}
