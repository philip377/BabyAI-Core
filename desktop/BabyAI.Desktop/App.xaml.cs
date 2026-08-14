using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Markup;

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

            try
            {
                var mainWindow = new MainWindow();
                StartupDiagnostics.Log("MainWindow constructed");
                mainWindow.ApplyStartupUiSettings();
                mainWindow.ApplyGlassUi();
                _window = mainWindow;
            }
            catch (XamlParseException ex)
            {
                StartupDiagnostics.Log("MainWindow XAML failed; starting compatibility fallback", ex);
                _window = new CompatibilityFallbackWindow(ex);
            }

            _window.Activate();
            StartupDiagnostics.Log(_window is MainWindow
                ? "MainWindow activated"
                : "Compatibility fallback activated");
        }
        catch (Exception ex)
        {
            StartupDiagnostics.ShowFatal("Desktop launch failed", ex);
            throw;
        }
    }
}
