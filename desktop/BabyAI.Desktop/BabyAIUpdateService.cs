using System.Diagnostics;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;

namespace BabyAI.Desktop;

public sealed record BabyAIUpdateInfo(
    string CurrentVersion,
    string? LatestVersion,
    string? ReleaseUrl,
    bool UpdateAvailable,
    string? InstallerUrl = null,
    string? InstallerChecksumUrl = null)
{
    public bool DownloadAvailable =>
        UpdateAvailable
        && !string.IsNullOrWhiteSpace(LatestVersion)
        && !string.IsNullOrWhiteSpace(InstallerUrl)
        && !string.IsNullOrWhiteSpace(InstallerChecksumUrl);
}

public sealed record BabyAIDownloadedUpdate(
    string Version,
    string InstallerPath,
    string Sha256);

public static class BabyAIUpdateService
{
    private const long MaxReleaseBytes = 1_073_741_824;
    private static readonly Uri LatestReleaseUri = new(
        "https://api.github.com/repos/philip377/BabyAI-Core/releases/latest");
    private static readonly HttpClient Client = CreateClient();

    public static Version CurrentVersion =>
        Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 1, 0);

    public static string CurrentVersionText => FormatVersion(CurrentVersion);

    public static async Task<BabyAIUpdateInfo> CheckAsync(CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(8));
        using var request = new HttpRequestMessage(HttpMethod.Get, LatestReleaseUri);
        request.Headers.Accept.ParseAdd("application/vnd.github+json");

        using var response = await Client.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            timeout.Token);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return new BabyAIUpdateInfo(
                CurrentVersionText,
                null,
                null,
                UpdateAvailable: false);
        }

        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: timeout.Token);
        var root = document.RootElement;

        var tag = root.TryGetProperty("tag_name", out var tagElement)
            ? tagElement.GetString()
            : null;
        var releaseUrl = root.TryGetProperty("html_url", out var urlElement)
            ? urlElement.GetString()
            : null;

        if (!TryParseReleaseVersion(tag, out var latest))
        {
            return new BabyAIUpdateInfo(
                CurrentVersionText,
                tag,
                releaseUrl,
                UpdateAvailable: false);
        }

        var latestText = FormatVersion(latest);
        var installerName = $"BabyAI-Setup-{latestText}.exe";
        var checksumName = installerName + ".sha256";
        var installerUrl = FindReleaseAssetUrl(root, installerName);
        var checksumUrl = FindReleaseAssetUrl(root, checksumName);

        return new BabyAIUpdateInfo(
            CurrentVersionText,
            latestText,
            releaseUrl,
            latest.CompareTo(CurrentVersion) > 0,
            installerUrl,
            checksumUrl);
    }

    public static async Task<BabyAIDownloadedUpdate> DownloadVerifiedAsync(
        BabyAIUpdateInfo update,
        CancellationToken cancellationToken = default)
    {
        if (!update.DownloadAvailable || string.IsNullOrWhiteSpace(update.LatestVersion))
            throw new InvalidOperationException("This release does not contain a downloadable BabyAI Windows installer.");

        var installerUri = RequireGitHubHttpsUri(update.InstallerUrl);
        var checksumUri = RequireGitHubHttpsUri(update.InstallerChecksumUrl);
        var version = update.LatestVersion;
        var installerName = $"BabyAI-Setup-{version}.exe";
        var cacheDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "BabyAI",
            "updates",
            version);
        Directory.CreateDirectory(cacheDir);

        var installerPath = Path.Combine(cacheDir, installerName);
        var partialPath = installerPath + ".partial";
        var checksumPath = installerPath + ".sha256";

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(10));

        var checksumText = await Client.GetStringAsync(checksumUri, timeout.Token);
        var expectedHash = ParseChecksum(checksumText, installerName);
        await File.WriteAllTextAsync(checksumPath, checksumText, timeout.Token);

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, installerUri);
            using var response = await Client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                timeout.Token);
            response.EnsureSuccessStatusCode();

            if (response.Content.Headers.ContentLength is long length && length > MaxReleaseBytes)
                throw new InvalidOperationException("The BabyAI installer is unexpectedly large.");

            await using var source = await response.Content.ReadAsStreamAsync(timeout.Token);
            await using var target = new FileStream(
                partialPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 131_072,
                useAsync: true);

            var buffer = new byte[131_072];
            long total = 0;
            while (true)
            {
                var read = await source.ReadAsync(buffer, timeout.Token);
                if (read == 0)
                    break;
                total += read;
                if (total > MaxReleaseBytes)
                    throw new InvalidOperationException("The BabyAI installer exceeded the download limit.");
                await target.WriteAsync(buffer.AsMemory(0, read), timeout.Token);
            }

            await target.FlushAsync(timeout.Token);
            await using var installer = File.OpenRead(partialPath);
            var actualHashBytes = await SHA256.HashDataAsync(installer, timeout.Token);
            var expectedHashBytes = Convert.FromHexString(expectedHash);
            if (!CryptographicOperations.FixedTimeEquals(actualHashBytes, expectedHashBytes))
                throw new InvalidOperationException("The downloaded BabyAI installer failed SHA-256 verification.");

            File.Move(partialPath, installerPath, overwrite: true);
            return new BabyAIDownloadedUpdate(
                version,
                installerPath,
                Convert.ToHexString(actualHashBytes).ToLowerInvariant());
        }
        finally
        {
            if (File.Exists(partialPath))
                File.Delete(partialPath);
        }
    }

    public static void LaunchInstaller(BabyAIDownloadedUpdate update)
    {
        if (!File.Exists(update.InstallerPath))
            throw new FileNotFoundException("The verified BabyAI installer is missing.", update.InstallerPath);

        Process.Start(new ProcessStartInfo(update.InstallerPath)
        {
            UseShellExecute = true,
        });
    }

    internal static bool TryParseReleaseVersion(string? tag, out Version version)
    {
        version = new Version(0, 0, 0);
        if (string.IsNullOrWhiteSpace(tag))
            return false;

        var text = tag.Trim();
        if (text.StartsWith('v') || text.StartsWith('V'))
            text = text[1..];

        var suffixIndex = text.IndexOfAny(['-', '+']);
        if (suffixIndex >= 0)
            text = text[..suffixIndex];

        var parts = text.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length is < 2 or > 4 || parts.Any(part => !int.TryParse(part, out _)))
            return false;

        if (parts.Length == 2)
            text += ".0";

        return Version.TryParse(text, out version!);
    }

    internal static string ParseChecksum(string text, string expectedFileName)
    {
        var line = text
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(item => item.Trim())
            .FirstOrDefault(item => item.Length > 0)
            ?? throw new InvalidOperationException("The release checksum file is empty.");
        var separator = line.IndexOf("  ", StringComparison.Ordinal);
        if (separator <= 0)
            throw new InvalidOperationException("The release checksum file has an invalid format.");

        var hash = line[..separator].Trim().ToLowerInvariant();
        var fileName = Path.GetFileName(line[(separator + 2)..].Trim());
        if (hash.Length != 64 || hash.Any(ch => !Uri.IsHexDigit(ch)))
            throw new InvalidOperationException("The release checksum is invalid.");
        if (!fileName.Equals(expectedFileName, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The release checksum refers to a different package.");
        return hash;
    }

    private static string? FindReleaseAssetUrl(JsonElement root, string expectedName)
    {
        if (!root.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
            return null;

        foreach (var asset in assets.EnumerateArray())
        {
            var name = asset.TryGetProperty("name", out var nameElement)
                ? nameElement.GetString()
                : null;
            if (!string.Equals(name, expectedName, StringComparison.OrdinalIgnoreCase))
                continue;
            var url = asset.TryGetProperty("browser_download_url", out var urlElement)
                ? urlElement.GetString()
                : null;
            return IsGitHubHttpsUrl(url) ? url : null;
        }

        return null;
    }

    private static Uri RequireGitHubHttpsUri(string? value)
    {
        if (!IsGitHubHttpsUrl(value) || !Uri.TryCreate(value, UriKind.Absolute, out var uri))
            throw new InvalidOperationException("The release installer URL is not a trusted GitHub HTTPS URL.");
        return uri;
    }

    private static bool IsGitHubHttpsUrl(string? value)
    {
        return Uri.TryCreate(value, UriKind.Absolute, out var uri)
            && uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            && uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase);
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("BabyAI-Desktop/0.1");
        return client;
    }

    private static string FormatVersion(Version version)
    {
        return version.Build >= 0
            ? $"{version.Major}.{version.Minor}.{version.Build}"
            : $"{version.Major}.{version.Minor}";
    }
}
