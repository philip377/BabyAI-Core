using Microsoft.UI.Xaml;

namespace BabyAI.Desktop;

public partial class App : Application
{
    private Window? _window;

    internal MainWindow? MainWindow => _window as MainWindow;

    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        InstalledRuntimeBootstrap.ApplyToCurrentProcess();
        var mainWindow = new MainWindow();
        mainWindow.ApplyStartupUiSettings();
        mainWindow.ApplyGlassUi();
        _window = mainWindow;
        _window.Activate();
    }
}
