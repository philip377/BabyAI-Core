using System.Net;
using System.Reflection;
using System.Text.Json;

namespace BabyAI.Desktop;

public sealed record BabyAIUpdateInfo(
    string CurrentVersion,
    string? LatestVersion,
    string? ReleaseUrl,
    bool UpdateAvailable);

public static class BabyAIUpdateService
{
    private static readonly Uri LatestReleaseUri = new(
        "https://api.github.com/repos/philip377/BabyAI-Core/releases/latest");
    private static readonly HttpClient Client = CreateClient();

    public static Version CurrentVersion =>
        Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 1, 0);

    public static string CurrentVersionText => FormatVersion(CurrentVersion);

    public static async Task<BabyAIUpdateInfo> CheckAsync(CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, LatestReleaseUri);
        request.Headers.Accept.ParseAdd("application/vnd.github+json");

        using var response = await Client.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return new BabyAIUpdateInfo(
                CurrentVersionText,
                null,
                null,
                UpdateAvailable: false);
        }

        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
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

        return new BabyAIUpdateInfo(
            CurrentVersionText,
            FormatVersion(latest),
            releaseUrl,
            latest.CompareTo(CurrentVersion) > 0);
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

    private static HttpClient CreateClient()
    {
        var client = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(8),
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
