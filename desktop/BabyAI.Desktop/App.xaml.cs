using Microsoft.UI.Xaml;

namespace BabyAI.Desktop;

public partial class App : Application
{
    private Window? _window;

    internal MainWindow? MainWindow => _window as MainWindow;

    public App()
    {
        StartupDiagnostics.InstallGlobalHandlers();
        StartupDiagnostics.Log("App constructor entered");
        try
        {
            InitializeComponent();
            StartupDiagnostics.Log("App InitializeComponent completed");
        }
        catch (Exception ex)
        {
            StartupDiagnostics.ShowFatal("App InitializeComponent failed", ex);
            throw;
        }
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        StartupDiagnostics.Log("OnLaunched entered");
        try
        {
            InstalledRuntimeBootstrap.ApplyToCurrentProcess();
            StartupDiagnostics.Log("Installed runtime bootstrap applied");
            var mainWindow = new MainWindow();
            StartupDiagnostics.Log("MainWindow constructed");
            mainWindow.ApplyStartupUiSettings();
            mainWindow.ApplyGlassUi();
            _window = mainWindow;
            _window.Activate();
            StartupDiagnostics.Log("MainWindow activated");
        }
        catch (Exception ex)
        {
            StartupDiagnostics.ShowFatal("Desktop launch failed", ex);
            throw;
        }
    }
}
