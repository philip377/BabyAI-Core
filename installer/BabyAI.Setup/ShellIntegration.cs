using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using IOPath = System.IO.Path;

namespace BabyAI.Setup;

internal static class ShellIntegration
{
    private static readonly Guid ShellLinkClassId = new("00021401-0000-0000-C000-000000000046");

    public static void CreateShortcuts(string desktopExe)
    {
        var workingDirectory = IOPath.GetDirectoryName(desktopExe)
            ?? throw new InvalidOperationException("Не удалось определить папку BabyAI Desktop.");

        var desktopDirectory = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        if (!string.IsNullOrWhiteSpace(desktopDirectory))
        {
            CreateShortcut(IOPath.Combine(desktopDirectory, "BabyAI.lnk"), desktopExe, workingDirectory);
        }

        var startMenu = Environment.GetFolderPath(Environment.SpecialFolder.Programs);
        if (!string.IsNullOrWhiteSpace(startMenu))
        {
            var babyAiMenu = IOPath.Combine(startMenu, "BabyAI");
            Directory.CreateDirectory(babyAiMenu);
            CreateShortcut(IOPath.Combine(babyAiMenu, "BabyAI.lnk"), desktopExe, workingDirectory);
        }

        UninstallIntegration.RegisterFromDesktop(desktopExe);
    }

    public static void RemoveShortcuts()
    {
        var desktopDirectory = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        if (!string.IsNullOrWhiteSpace(desktopDirectory))
        {
            DeleteIfExists(IOPath.Combine(desktopDirectory, "BabyAI.lnk"));
        }

        var startMenu = Environment.GetFolderPath(Environment.SpecialFolder.Programs);
        if (!string.IsNullOrWhiteSpace(startMenu))
        {
            var babyAiMenu = IOPath.Combine(startMenu, "BabyAI");
            DeleteIfExists(IOPath.Combine(babyAiMenu, "BabyAI.lnk"));
            if (Directory.Exists(babyAiMenu) && !Directory.EnumerateFileSystemEntries(babyAiMenu).Any())
            {
                Directory.Delete(babyAiMenu);
            }
        }
    }

    private static void DeleteIfExists(string path)
    {
        if (File.Exists(path)) File.Delete(path);
    }

    private static void CreateShortcut(string shortcutPath, string targetPath, string workingDirectory)
    {
        Directory.CreateDirectory(IOPath.GetDirectoryName(shortcutPath)!);

        var shellLinkType = Type.GetTypeFromCLSID(ShellLinkClassId)
            ?? throw new PlatformNotSupportedException("Windows Shell Link API недоступен.");
        var shellLinkObject = Activator.CreateInstance(shellLinkType)
            ?? throw new InvalidOperationException("Не удалось создать Windows Shell Link.");

        try
        {
            var shellLink = (IShellLinkW)shellLinkObject;
            shellLink.SetPath(targetPath);
            shellLink.SetWorkingDirectory(workingDirectory);
            shellLink.SetDescription("BabyAI");
            shellLink.SetIconLocation(targetPath, 0);
            ((IPersistFile)shellLink).Save(shortcutPath, true);
        }
        finally
        {
            Marshal.FinalReleaseComObject(shellLinkObject);
        }
    }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("000214F9-0000-0000-C000-000000000046")]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] char[] pszFile, int cch, IntPtr pfd, uint fFlags);
        void GetIDList(out IntPtr ppidl);
        void SetIDList(IntPtr pidl);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] char[] pszName, int cch);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] char[] pszDir, int cch);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] char[] pszArgs, int cch);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
        void GetHotkey(out short pwHotkey);
        void SetHotkey(short wHotkey);
        void GetShowCmd(out int piShowCmd);
        void SetShowCmd(int iShowCmd);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] char[] pszIconPath, int cch, out int piIcon);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, uint dwReserved);
        void Resolve(IntPtr hwnd, uint fFlags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
    }
}
