using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;

namespace BabyAI.Setup;

internal sealed record ModelDownloadManifest(
    string Url,
    string Sha256,
    long Size,
    string Filename,
    string DisplayName);

internal static class ModelDownload
{
    private const long MinimumModelBytes = 64L * 1024L * 1024L;
    private const long MaximumModelBytes = 32L * 1024L * 1024L * 1024L;
    private const int MaximumAttempts = 4;
    private static readonly HttpClient Http = CreateHttpClient();

    public static ModelDownloadManifest? TryReadManifest(string bundleRoot)
    {
        var path = Path.Combine(bundleRoot, "model.json");
        if (!File.Exists(path))
            return null;

        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var root = document.RootElement;
        var url = root.TryGetProperty("url", out var urlElement) ? urlElement.GetString() : null;
        var sha256 = root.TryGetProperty("sha256", out var shaElement) ? shaElement.GetString() : null;
        var size = root.TryGetProperty("size", out var sizeElement) && sizeElement.TryGetInt64(out var parsedSize) ? parsedSize : 0L;
        var filename = root.TryGetProperty("filename", out var filenameElement) ? filenameElement.GetString() : null;
        var displayName = root.TryGetProperty("display_name", out var displayNameElement) ? displayNameElement.GetString() : null;

        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            throw new InvalidDataException("model.json содержит небезопасный URL модели.");
        if (string.IsNullOrWhiteSpace(sha256) || sha256.Length != 64 || !sha256.All(Uri.IsHexDigit))
            throw new InvalidDataException("model.json содержит некорректный SHA-256.");
        if (size < MinimumModelBytes || size > MaximumModelBytes)
            throw new InvalidDataException("model.json содержит недопустимый размер модели.");
        if (string.IsNullOrWhiteSpace(filename) || !filename.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(filename).Equals(filename, StringComparison.Ordinal))
            throw new InvalidDataException("model.json содержит некорректное имя GGUF-файла.");

        return new ModelDownloadManifest(uri.AbsoluteUri, sha256.ToLowerInvariant(), size, filename,
            string.IsNullOrWhiteSpace(displayName) ? filename : displayName);
    }

    public static async Task<string> DownloadAsync(
        ModelDownloadManifest manifest,
        string installRoot,
        IProgress<(string Message, int Value)> progress,
        CancellationToken cancellationToken)
    {
        var modelsRoot = Path.Combine(installRoot, "models");
        Directory.CreateDirectory(modelsRoot);
        var finalPath = Path.Combine(modelsRoot, manifest.Filename);
        var partialPath = finalPath + ".partial";

        if (File.Exists(finalPath))
        {
            var existing = await ComputeSha256Async(finalPath, cancellationToken);
            if (existing.Equals(manifest.Sha256, StringComparison.OrdinalIgnoreCase))
                return finalPath;
        }

        DeletePartialBestEffort(partialPath);

        for (var attempt = 1; attempt <= MaximumAttempts; attempt++)
        {
            try
            {
                using var response = await Http.GetAsync(manifest.Url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                if (response.StatusCode == HttpStatusCode.TooManyRequests || (int)response.StatusCode >= 500)
                {
                    if (attempt == MaximumAttempts)
                        throw new HttpRequestException($"Сервер модели временно недоступен ({(int)response.StatusCode}). Попробуйте позже.", null, response.StatusCode);

                    var delay = GetRetryDelay(response, attempt);
                    progress.Report(($"Сервер модели занят. Повторяю через {Math.Ceiling(delay.TotalSeconds):0} сек…", 94));
                    await Task.Delay(delay, cancellationToken);
                    continue;
                }
                response.EnsureSuccessStatusCode();

                if (response.Content.Headers.ContentLength is long contentLength && contentLength != manifest.Size)
                    throw new InvalidDataException("Размер загружаемой модели не совпадает с model.json.");

                long total = 0;
                await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken))
                await using (var destination = new FileStream(partialPath, FileMode.Create, FileAccess.Write, FileShare.None,
                    1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
                {
                    var buffer = new byte[1024 * 1024];
                    while (true)
                    {
                        var read = await source.ReadAsync(buffer, cancellationToken);
                        if (read == 0)
                            break;

                        total += read;
                        if (total > manifest.Size || total > MaximumModelBytes)
                            throw new InvalidDataException("Загрузка модели превысила заявленный размер.");

                        await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                        var percent = (int)Math.Clamp((total * 100L) / manifest.Size, 0, 100);
                        progress.Report(($"Загружаю {manifest.DisplayName}… {percent}%", 94 + (percent * 4 / 100)));
                    }
                    await destination.FlushAsync(cancellationToken);
                }

                // The writer must be disposed before Windows can reopen the .partial file for hashing.
                if (total != manifest.Size)
                    throw new InvalidDataException("Загруженная модель имеет неожиданный размер.");

                progress.Report(("Проверяю SHA-256 модели…", 98));
                var actualHash = await ComputeSha256Async(partialPath, cancellationToken);
                if (!actualHash.Equals(manifest.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("SHA-256 модели не совпадает с model.json.");

                File.Move(partialPath, finalPath, true);
                return finalPath;
            }
            catch (HttpRequestException) when (attempt < MaximumAttempts)
            {
                DeletePartialBestEffort(partialPath);
                var delay = TimeSpan.FromSeconds(Math.Min(30, 2 * attempt * attempt));
                progress.Report(($"Сеть временно недоступна. Повторяю через {delay.TotalSeconds:0} сек…", 94));
                await Task.Delay(delay, cancellationToken);
            }
            catch
            {
                DeletePartialBestEffort(partialPath);
                throw;
            }
        }

        throw new HttpRequestException("Не удалось загрузить модель после нескольких попыток.");
    }

    private static TimeSpan GetRetryDelay(HttpResponseMessage response, int attempt)
    {
        var retryAfter = response.Headers.RetryAfter;
        if (retryAfter?.Delta is TimeSpan delta && delta > TimeSpan.Zero)
            return delta > TimeSpan.FromMinutes(2) ? TimeSpan.FromMinutes(2) : delta;
        if (retryAfter?.Date is DateTimeOffset date)
        {
            var until = date - DateTimeOffset.UtcNow;
            if (until > TimeSpan.Zero)
                return until > TimeSpan.FromMinutes(2) ? TimeSpan.FromMinutes(2) : until;
        }
        return TimeSpan.FromSeconds(Math.Min(30, 2 * attempt * attempt));
    }

    private static void DeletePartialBestEffort(string partialPath)
    {
        try
        {
            if (File.Exists(partialPath))
                File.Delete(partialPath);
        }
        catch (IOException)
        {
            // A stale handle should not hide the original failure; a later retry can clean it up.
        }
        catch (UnauthorizedAccessException)
        {
            // Best effort cleanup only.
        }
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[1024 * 1024];
        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken);
            if (read == 0)
                break;
            hash.AppendData(buffer, 0, read);
        }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static HttpClient CreateHttpClient()
    {
        var handler = new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.None,
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 5,
        };
        return new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
    }
}
