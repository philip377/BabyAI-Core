using Microsoft.Win32;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using IOPath = System.IO.Path;

namespace BabyAI.Setup;

internal static class UninstallIntegration
{
    private const string RegistryKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\BabyAI";
    private const int MoveFileDelayUntilReboot = 0x4;

    [ModuleInitializer]
    internal static void HandleUninstallCommand()
    {
        var args = Environment.GetCommandLineArgs().Skip(1).ToArray();
        if (!args.Any(x => x.Equals("--uninstall", StringComparison.OrdinalIgnoreCase))) return;

        var quiet = args.Any(x => x.Equals("--quiet", StringComparison.OrdinalIgnoreCase));
        var final = args.Any(x => x.Equals("--uninstall-final", StringComparison.OrdinalIgnoreCase));
        var installRoot = ResolveInstallRoot(args);

        if (!final && IsInsideInstallRoot(Environment.ProcessPath, installRoot))
        {
            RelaunchFromTemp(installRoot, quiet);
            Environment.Exit(0);
        }

        Uninstall(installRoot);
        if (final && Environment.ProcessPath is { } tempExe)
        {
            MoveFileEx(tempExe, null, MoveFileDelayUntilReboot);
        }

        if (!quiet)
        {
            System.Windows.MessageBox.Show(
                "BabyAI удалён. Ваши настройки, память и локальные модели сохранены.",
                "BabyAI",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Information);
        }
        Environment.Exit(0);
    }

    public static void RegisterFromDesktop(string desktopExe)
    {
        var appDirectory = IOPath.GetDirectoryName(desktopExe)
            ?? throw new InvalidOperationException("Не удалось определить папку BabyAI Desktop.");
        var versionDirectory = Directory.GetParent(appDirectory)
            ?? throw new InvalidOperationException("Не удалось определить версию BabyAI.");
        var versionsDirectory = versionDirectory.Parent
            ?? throw new InvalidOperationException("Не удалось определить versions root BabyAI.");
        var installRoot = versionsDirectory.Parent?.FullName
            ?? throw new InvalidOperationException("Не удалось определить install root BabyAI.");

        Register(installRoot, versionDirectory.Name, desktopExe);
    }

    private static void Register(string installRoot, string version, string displayIcon)
    {
        var sourceExe = Environment.ProcessPath
            ?? throw new InvalidOperationException("Не удалось определить путь установщика BabyAI.");
        var uninstaller = IOPath.Combine(installRoot, "BabyAI-Uninstall.exe");
        File.Copy(sourceExe, uninstaller, true);

        using var key = Registry.CurrentUser.CreateSubKey(RegistryKeyPath, true)
            ?? throw new InvalidOperationException("Не удалось зарегистрировать удаление BabyAI.");
        key.SetValue("DisplayName", "BabyAI");
        key.SetValue("DisplayVersion", version);
        key.SetValue("Publisher", "BabyAI");
        key.SetValue("DisplayIcon", $"\"{displayIcon}\"");
        key.SetValue("InstallLocation", installRoot);
        key.SetValue("UninstallString", $"\"{uninstaller}\" --uninstall");
        key.SetValue("QuietUninstallString", $"\"{uninstaller}\" --uninstall --quiet");
        key.SetValue("NoModify", 1, RegistryValueKind.DWord);
        key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
    }

    private static void Uninstall(string installRoot)
    {
        ShellIntegration.RemoveShortcuts();
        Registry.CurrentUser.DeleteSubKeyTree(RegistryKeyPath, false);

        var versions = IOPath.Combine(installRoot, "versions");
        if (Directory.Exists(versions)) Directory.Delete(versions, true);

        var current = IOPath.Combine(installRoot, "current.json");
        if (File.Exists(current)) File.Delete(current);

        var uninstaller = IOPath.Combine(installRoot, "BabyAI-Uninstall.exe");
        if (File.Exists(uninstaller) && !IOPath.GetFullPath(uninstaller).Equals(IOPath.GetFullPath(Environment.ProcessPath ?? ""), StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(uninstaller);
        }
    }

    private static string ResolveInstallRoot(string[] args)
    {
        var explicitRoot = args.FirstOrDefault(x => x.StartsWith("--install-root=", StringComparison.OrdinalIgnoreCase));
        if (explicitRoot is not null)
        {
            return IOPath.GetFullPath(explicitRoot["--install-root=".Length..].Trim('"'));
        }
        return IOPath.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BabyAI");
    }

    private static bool IsInsideInstallRoot(string? executable, string installRoot)
    {
        if (string.IsNullOrWhiteSpace(executable)) return false;
        var root = IOPath.GetFullPath(installRoot).TrimEnd(IOPath.DirectorySeparatorChar) + IOPath.DirectorySeparatorChar;
        return IOPath.GetFullPath(executable).StartsWith(root, StringComparison.OrdinalIgnoreCase);
    }

    private static void RelaunchFromTemp(string installRoot, bool quiet)
    {
        var sourceExe = Environment.ProcessPath
            ?? throw new InvalidOperationException("Не удалось определить путь uninstaller.");
        var tempExe = IOPath.Combine(IOPath.GetTempPath(), $"BabyAI-Uninstall-{Guid.NewGuid():N}.exe");
        File.Copy(sourceExe, tempExe, true);
        var arguments = $"--uninstall --uninstall-final --install-root=\"{installRoot}\"" + (quiet ? " --quiet" : "");
        Process.Start(new ProcessStartInfo(tempExe, arguments) { UseShellExecute = true });
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool MoveFileEx(string lpExistingFileName, string? lpNewFileName, int dwFlags);
}
