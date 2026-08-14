using System.Runtime.InteropServices;

namespace BabyAI.Desktop;

internal static class StartupDiagnostics
{
    private const uint MbOk = 0x00000000;
    private const uint MbIconError = 0x00000010;

    internal static string LogPath
    {
        get
        {
            var root = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "BabyAI",
                "logs");
            Directory.CreateDirectory(root);
            return Path.Combine(root, "desktop-startup.log");
        }
    }

    internal static void InstallGlobalHandlers()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception exception)
                Log("AppDomain.UnhandledException", exception);
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log("TaskScheduler.UnobservedTaskException", args.Exception);
        };
    }

    internal static void Log(string stage, Exception? exception = null)
    {
        try
        {
            var lines = new List<string>
            {
                $"[{DateTimeOffset.Now:O}] {stage}",
                $"os={Environment.OSVersion}",
                $"process_arch={RuntimeInformation.ProcessArchitecture}",
                $"framework={RuntimeInformation.FrameworkDescription}",
                $"base_dir={AppContext.BaseDirectory}",
            };
            if (exception is not null)
                lines.Add(exception.ToString());
            File.AppendAllLines(LogPath, lines);
        }
        catch
        {
            // Diagnostics must never become another startup failure.
        }
    }

    internal static void ShowFatal(string stage, Exception exception)
    {
        Log(stage, exception);
        try
        {
            MessageBoxW(
                IntPtr.Zero,
                $"BabyAI Desktop не смог запуститься.\n\n{exception.Message}\n\nДиагностика:\n{LogPath}",
                "BabyAI — ошибка запуска",
                MbOk | MbIconError);
        }
        catch
        {
            // If even native error UI is unavailable, the log is still best effort.
        }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);
}
