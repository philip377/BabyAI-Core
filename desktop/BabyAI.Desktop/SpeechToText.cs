using System.Buffers.Binary;
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

    public static ISpeechToTextProvider CreateDefault()
    {
        var configured = Environment.GetEnvironmentVariable("BABYAI_STT_MODEL_DIR");
        var modelPath = string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(AppContext.BaseDirectory, "stt", DefaultModelName)
            : Path.GetFullPath(Environment.ExpandEnvironmentVariables(configured.Trim()));

        return new SherpaOnnxWhisperSpeechToTextProvider(modelPath);
    }
}

internal sealed class SherpaOnnxWhisperSpeechToTextProvider : ISpeechToTextProvider
{
    private const string EncoderFileName = "tiny-encoder.int8.onnx";
    private const string DecoderFileName = "tiny-decoder.int8.onnx";
    private const string TokensFileName = "tiny-tokens.txt";

    private readonly object _recognizerSync = new();
    private OfflineRecognizer? _recognizer;
    private bool _disposed;

    public SherpaOnnxWhisperSpeechToTextProvider(string modelPath)
    {
        if (string.IsNullOrWhiteSpace(modelPath))
            throw new ArgumentException("STT model path is required.", nameof(modelPath));
        if (!Directory.Exists(modelPath))
        {
            throw new DirectoryNotFoundException(
                $"Локальная STT-модель не найдена: {modelPath}. " +
                "Переустановите актуальную сборку BabyAI или задайте BABYAI_STT_MODEL_DIR.");
        }

        foreach (var fileName in new[] { EncoderFileName, DecoderFileName, TokensFileName })
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

    public string Name => "sherpa-onnx-whisper-tiny";

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
        config.ModelConfig.Tokens = Path.Combine(ModelPath, TokensFileName);
        config.ModelConfig.NumThreads = 2;
        config.ModelConfig.Debug = 0;
        config.ModelConfig.Provider = "cpu";
        config.ModelConfig.Whisper.Encoder = Path.Combine(ModelPath, EncoderFileName);
        config.ModelConfig.Whisper.Decoder = Path.Combine(ModelPath, DecoderFileName);
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
