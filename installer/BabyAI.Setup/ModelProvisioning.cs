using System.IO;

namespace BabyAI.Setup;

internal static class ModelProvisioning
{
    public static async Task<string?> EnsureModelAsync(
        string bundleRoot,
        string installRoot,
        IProgress<(string Message, int Value)> progress,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        progress.Report(("Ищу локальную GGUF-модель…", 94));
        var local = ModelDiscovery.TryAdoptLocalModel(installRoot);
        if (!string.IsNullOrWhiteSpace(local))
            return local;

        var manifest = ModelDownload.TryReadManifest(bundleRoot);
        if (manifest is null)
        {
            progress.Report(("Модель не найдена — загрузка не настроена в этом релизе", 96));
            return null;
        }

        progress.Report(($"Готовлю загрузку {manifest.DisplayName}…", 94));
        var downloaded = await ModelDownload.DownloadAsync(
            manifest,
            installRoot,
            progress,
            cancellationToken);

        if (!File.Exists(downloaded))
            throw new FileNotFoundException("Verified model download did not produce a model file.", downloaded);

        ModelDiscovery.PersistModelPath(installRoot, downloaded);
        progress.Report(($"Модель {manifest.DisplayName} готова", 98));
        return downloaded;
    }
}
