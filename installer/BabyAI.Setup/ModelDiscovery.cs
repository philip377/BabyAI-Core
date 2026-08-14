using System.IO;
using System.Text.Json;

namespace BabyAI.Setup;

internal static class ModelDiscovery
{
    private const long MinimumModelBytes = 64L * 1024L * 1024L;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static string? TryAdoptLocalModel(string installRoot)
    {
        var launchPath = Path.Combine(installRoot, "launch.json");
        var launch = ReadLaunchSettings(launchPath);
        if (!string.IsNullOrWhiteSpace(launch.Model) && File.Exists(launch.Model))
            return launch.Model;

        var candidates = FindCandidates(installRoot);
        if (candidates.Count == 0)
            return null;

        var preferred = candidates
            .Where(path => Path.GetFileName(path).Equals("babyai.gguf", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        string? selected = preferred.Count == 1
            ? preferred[0]
            : candidates.Count == 1
                ? candidates[0]
                : null;

        if (selected is null)
            return null;

        File.WriteAllText(launchPath, JsonSerializer.Serialize(new
        {
            provider = launch.Provider,
            acceleration = launch.Acceleration,
            model = selected
        }, JsonOptions));
        return selected;
    }

    private static List<string> FindCandidates(string installRoot)
    {
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            Path.Combine(installRoot, "models")
        };

        AddKnownFolder(roots, Environment.SpecialFolder.DesktopDirectory);
        AddKnownFolder(roots, Environment.SpecialFolder.MyDocuments);

        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(profile))
            roots.Add(Path.Combine(profile, "Downloads"));

        var results = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in roots)
            CollectFromRoot(root, results);

        return results.OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static void AddKnownFolder(HashSet<string> roots, Environment.SpecialFolder folder)
    {
        var path = Environment.GetFolderPath(folder);
        if (!string.IsNullOrWhiteSpace(path))
            roots.Add(path);
    }

    private static void CollectFromRoot(string root, HashSet<string> results)
    {
        if (!Directory.Exists(root))
            return;

        CollectDirectory(root, results);
        foreach (var child in SafeEnumerateDirectories(root))
            CollectDirectory(child, results);
    }

    private static void CollectDirectory(string directory, HashSet<string> results)
    {
        foreach (var file in SafeEnumerateFiles(directory, "*.gguf"))
        {
            try
            {
                var info = new FileInfo(file);
                if (info.Exists && info.Length >= MinimumModelBytes)
                    results.Add(info.FullName);
            }
            catch (IOException)
            {
                // Ignore files that disappear or cannot be inspected during discovery.
            }
            catch (UnauthorizedAccessException)
            {
                // Ignore folders/files the current user cannot inspect.
            }
        }
    }

    private static IEnumerable<string> SafeEnumerateFiles(string directory, string pattern)
    {
        try
        {
            return Directory.EnumerateFiles(directory, pattern, SearchOption.TopDirectoryOnly).ToArray();
        }
        catch (IOException)
        {
            return [];
        }
        catch (UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static IEnumerable<string> SafeEnumerateDirectories(string directory)
    {
        try
        {
            return Directory.EnumerateDirectories(directory, "*", SearchOption.TopDirectoryOnly).ToArray();
        }
        catch (IOException)
        {
            return [];
        }
        catch (UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static LaunchSettings ReadLaunchSettings(string path)
    {
        var defaults = new LaunchSettings("native", "auto", string.Empty);
        if (!File.Exists(path))
            return defaults;

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var root = document.RootElement;
            var provider = root.TryGetProperty("provider", out var providerElement)
                ? providerElement.GetString()
                : null;
            var acceleration = root.TryGetProperty("acceleration", out var accelerationElement)
                ? accelerationElement.GetString()
                : null;
            var model = root.TryGetProperty("model", out var modelElement)
                ? modelElement.GetString()
                : null;
            return new LaunchSettings(
                string.IsNullOrWhiteSpace(provider) ? defaults.Provider : provider,
                string.IsNullOrWhiteSpace(acceleration) ? defaults.Acceleration : acceleration,
                model ?? string.Empty);
        }
        catch (JsonException)
        {
            return defaults;
        }
        catch (IOException)
        {
            return defaults;
        }
    }

    private sealed record LaunchSettings(string Provider, string Acceleration, string Model);
}
