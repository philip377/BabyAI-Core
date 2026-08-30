using System.Text;
using System.Text.Json;
using Vosk;

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
    internal const string DefaultModelName = "vosk-model-small-ru-0.22";

    public static ISpeechToTextProvider CreateDefault()
    {
        var configured = Environment.GetEnvironmentVariable("BABYAI_STT_MODEL_DIR");
        var modelPath = string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(AppContext.BaseDirectory, "stt", DefaultModelName)
            : Path.GetFullPath(Environment.ExpandEnvironmentVariables(configured.Trim()));

        return new VoskSpeechToTextProvider(modelPath);
    }
}

internal sealed class VoskSpeechToTextProvider : ISpeechToTextProvider
{
    private readonly Model _model;
    private bool _disposed;

    public VoskSpeechToTextProvider(string modelPath)
    {
        if (string.IsNullOrWhiteSpace(modelPath))
            throw new ArgumentException("STT model path is required.", nameof(modelPath));
        if (!Directory.Exists(modelPath))
        {
            throw new DirectoryNotFoundException(
                $"Локальная STT-модель не найдена: {modelPath}. " +
                "Переустановите актуальную сборку BabyAI или задайте BABYAI_STT_MODEL_DIR.");
        }

        global::Vosk.Vosk.SetLogLevel(-1);
        _model = new Model(modelPath);
        ModelPath = modelPath;
    }

    public string Name => "vosk-local";

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
        using var recognizer = new VoskRecognizer(_model, sampleRate);
        recognizer.SetMaxAlternatives(0);
        recognizer.SetWords(false);

        var text = new StringBuilder();
        const int chunkSize = 4_096;
        for (var offset = 0; offset < audio.Length; offset += chunkSize)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var count = Math.Min(chunkSize, audio.Length - offset);
            var chunk = new byte[count];
            Buffer.BlockCopy(audio, offset, chunk, 0, count);
            if (recognizer.AcceptWaveform(chunk, count))
                AppendResultText(text, recognizer.Result());
        }

        cancellationToken.ThrowIfCancellationRequested();
        AppendResultText(text, recognizer.FinalResult());
        return NormalizeTranscript(text.ToString());
    }

    private static void AppendResultText(StringBuilder target, string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return;

        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("text", out var textElement))
            return;

        var text = textElement.GetString()?.Trim();
        if (string.IsNullOrWhiteSpace(text))
            return;

        if (target.Length > 0)
            target.Append(' ');
        target.Append(text);
    }

    private static string NormalizeTranscript(string text)
    {
        var words = text
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return string.Join(' ', words).Trim();
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _model.Dispose();
    }
}
