using System.Text.Json;

namespace BabyAI.Desktop;

internal static class InstalledRuntimeBootstrap
{
    private sealed record LaunchSettings(string Provider, string Acceleration, string Model);

    public static void ApplyToCurrentProcess()
    {
        if (!TryResolveInstalledLayout(out var installRoot, out var versionRoot))
            return;

        var python = Path.Combine(versionRoot, "python", "python.exe");
        var cpuRuntime = Path.Combine(versionRoot, "runtime", "cpu", "babyai_native.dll");
        var vulkanRuntime = Path.Combine(versionRoot, "runtime", "vulkan", "babyai_native.dll");
        if (!File.Exists(python) || !File.Exists(cpuRuntime))
            return;

        var launch = ReadLaunchSettings(Path.Combine(installRoot, "launch.json"));
        Environment.SetEnvironmentVariable("BABYAI_PYTHON", python);
        Environment.SetEnvironmentVariable("BABYAI_PROVIDER", launch.Provider);
        Environment.SetEnvironmentVariable("BABYAI_NATIVE_ACCELERATION", launch.Acceleration);
        Environment.SetEnvironmentVariable("BABYAI_NATIVE_RUNTIME", cpuRuntime);
        Environment.SetEnvironmentVariable(
            "BABYAI_NATIVE_VULKAN_RUNTIME",
            File.Exists(vulkanRuntime) ? vulkanRuntime : null);
        Environment.SetEnvironmentVariable(
            "BABYAI_NATIVE_MODEL",
            !string.IsNullOrWhiteSpace(launch.Model) && File.Exists(launch.Model) ? launch.Model : null);
    }

    private static bool TryResolveInstalledLayout(out string installRoot, out string versionRoot)
    {
        installRoot = string.Empty;
        versionRoot = string.Empty;

        var appRoot = Path.GetFullPath(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var appDirectory = new DirectoryInfo(appRoot);
        if (!appDirectory.Name.Equals("app", StringComparison.OrdinalIgnoreCase))
            return false;

        var versionDirectory = appDirectory.Parent;
        var versionsDirectory = versionDirectory?.Parent;
        var rootDirectory = versionsDirectory?.Parent;
        if (versionDirectory is null || versionsDirectory is null || rootDirectory is null)
            return false;
        if (!versionsDirectory.Name.Equals("versions", StringComparison.OrdinalIgnoreCase))
            return false;

        installRoot = rootDirectory.FullName;
        versionRoot = versionDirectory.FullName;
        return true;
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
        catch
        {
            return defaults;
        }
    }
}
