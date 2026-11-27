using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Markup;

namespace BabyAI.Desktop;

public partial class App : Application
{
    private Window? _window;

    internal MainWindow? MainWindow => _window as MainWindow;
    internal WindowsAppNotifications GingerNotifications { get; } = new();

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

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        StartupDiagnostics.Log("OnLaunched entered");
        try
        {
            if (!await GingerNotifications.PrepareLaunchAsync())
            {
                GingerNotifications.Dispose();
                Exit();
                return;
            }
            InstalledRuntimeBootstrap.ApplyToCurrentProcess();
            StartupDiagnostics.Log("Installed runtime bootstrap applied");

            try
            {
                var mainWindow = new MainWindow();
                StartupDiagnostics.Log("MainWindow constructed");
                mainWindow.AttachRuntimeBehaviors();
                mainWindow.ApplyStartupUiSettings();
                mainWindow.ApplyGlassUi();
                _window = mainWindow;
            }
            catch (XamlParseException ex)
            {
                StartupDiagnostics.Log($"MainWindow XAML failed; hresult=0x{ex.HResult:X8}; message={ex.Message}", ex);
                StartupDiagnostics.Log("MainWindow XAML failed; starting compatibility fallback");
                _window = new CompatibilityFallbackWindow(ex);
            }

            _window.Closed += (_, _) => GingerNotifications.Dispose();
            GingerNotifications.Initialize(OpenNotificationChat);
            _window.Activate();
            StartupDiagnostics.Log(_window is MainWindow
                ? "MainWindow activated"
                : "Compatibility fallback activated");
        }
        catch (Exception ex)
        {
            GingerNotifications.Dispose();
            StartupDiagnostics.ShowFatal("Desktop launch failed", ex);
            throw;
        }
    }

    private void OpenNotificationChat(string? chatId)
    {
        try
        {
            // WindowsAppNotifications dispatches this callback onto the existing UI thread.
            if (_window is MainWindow mainWindow)
                mainWindow.OpenGingerNotificationChat(chatId);
            else
                _window?.Activate();
        }
        catch (Exception ex) { StartupDiagnostics.Log("Notification chat navigation failed", ex); }
    }
}
