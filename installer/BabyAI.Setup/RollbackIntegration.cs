using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using IOPath = System.IO.Path;

namespace BabyAI.Setup;

internal static class RollbackIntegration
{
    private sealed record VersionPointer(string Version, string Path);
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    [ModuleInitializer]
    internal static void HandleRollbackCommand()
    {
        var args = Environment.GetCommandLineArgs().Skip(1).ToArray();
        if (!args.Any(x => x.Equals("--rollback", StringComparison.OrdinalIgnoreCase))) return;

        var installRoot = IOPath.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BabyAI");
        var restored = TryRollback(installRoot);
        System.Windows.MessageBox.Show(
            restored ? "BabyAI возвращён к предыдущей рабочей версии." : "Предыдущая версия BabyAI для отката не найдена.",
            "BabyAI",
            System.Windows.MessageBoxButton.OK,
            restored ? System.Windows.MessageBoxImage.Information : System.Windows.MessageBoxImage.Warning);
        Environment.Exit(restored ? 0 : 1);
    }

    public static void CommitVersionSwitch(string installRoot, string version, string versionDir)
    {
        ValidateVersion(versionDir);
        Directory.CreateDirectory(installRoot);

        var currentPath = IOPath.Combine(installRoot, "current.json");
        var previousPath = IOPath.Combine(installRoot, "previous.json");
        var current = ReadPointer(currentPath);
        if (current is not null && Directory.Exists(current.Path) && !PathsEqual(current.Path, versionDir))
        {
            WritePointerAtomic(previousPath, current);
        }

        WritePointerAtomic(currentPath, new VersionPointer(version, versionDir));
    }

    public static bool TryRollback(string installRoot)
    {
        var currentPath = IOPath.Combine(installRoot, "current.json");
        var previousPath = IOPath.Combine(installRoot, "previous.json");
        var previous = ReadPointer(previousPath);
        if (previous is null || !Directory.Exists(previous.Path)) return false;

        ValidateVersion(previous.Path);
        var current = ReadPointer(currentPath);
        WritePointerAtomic(currentPath, previous);
        if (current is not null && Directory.Exists(current.Path) && !PathsEqual(current.Path, previous.Path))
        {
            WritePointerAtomic(previousPath, current);
        }
        return true;
    }

    private static void ValidateVersion(string versionDir)
    {
        var desktop = IOPath.Combine(versionDir, "app", "BabyAI.Desktop.exe");
        var python = IOPath.Combine(versionDir, "python", "python.exe");
        var runtime = IOPath.Combine(versionDir, "runtime");
        if (!File.Exists(desktop)) throw new InvalidDataException("BabyAI.Desktop.exe не найден в версии.");
        if (!File.Exists(python)) throw new InvalidDataException("python.exe не найден в версии.");
        if (!Directory.Exists(runtime)) throw new InvalidDataException("Native runtime не найден в версии.");
    }

    private static VersionPointer? ReadPointer(string path)
    {
        if (!File.Exists(path)) return null;
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var root = document.RootElement;
        var version = root.TryGetProperty("version", out var versionElement) ? versionElement.GetString() : null;
        var pointerPath = root.TryGetProperty("path", out var pathElement) ? pathElement.GetString() : null;
        return string.IsNullOrWhiteSpace(version) || string.IsNullOrWhiteSpace(pointerPath)
            ? null
            : new VersionPointer(version, IOPath.GetFullPath(pointerPath));
    }

    private static void WritePointerAtomic(string path, VersionPointer pointer)
    {
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(new { version = pointer.Version, path = pointer.Path }, JsonOptions));
        File.Move(temp, path, true);
    }

    private static bool PathsEqual(string left, string right) =>
        IOPath.GetFullPath(left).TrimEnd(IOPath.DirectorySeparatorChar)
            .Equals(IOPath.GetFullPath(right).TrimEnd(IOPath.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);
}
