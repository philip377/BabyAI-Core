using H.NotifyIcon;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace BabyAI.Desktop;

public sealed class TrayIconService : IDisposable
{
    private readonly MainWindow _window;
    private readonly TaskbarIcon _icon;
    private bool _created;
    private bool _createAttempted;

    public TrayIconService(MainWindow window)
    {
        _window = window;

        var provider = Environment.GetEnvironmentVariable("BABYAI_PROVIDER");
        if (string.IsNullOrWhiteSpace(provider))
            provider = "ollama";
        provider = provider.Trim().ToLowerInvariant();

        var menu = new MenuFlyout
        {
            AreOpenCloseAnimationsEnabled = false,
        };

        var runtime = new MenuFlyoutItem
        {
            Text = $"UNIX · {provider}",
            IsEnabled = false,
            Width = 172,
        };

        var show = new MenuFlyoutItem
        {
            Text = "Открыть UNIX",
        };
        show.Click += (_, _) => _window.ShowFromTray();

        var hide = new MenuFlyoutItem
        {
            Text = "Скрыть",
        };
        hide.Click += (_, _) => _window.HideToTray();

        var exit = new MenuFlyoutItem
        {
            Text = "Выход",
        };
        exit.Click += (_, _) => _window.RequestExit();

        menu.Items.Add(runtime);
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(show);
        menu.Items.Add(hide);
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(exit);

        _icon = new TaskbarIcon
        {
            ToolTipText = $"UNIX · {provider}",
            Visibility = Visibility.Visible,
            NoLeftClickDelay = true,
            ContextFlyout = menu,
        };

        _window.Activated += Window_Activated;
    }

    private void Window_Activated(object sender, WindowActivatedEventArgs args)
    {
        if (_createAttempted)
            return;

        _createAttempted = true;
        try
        {
            // Load the same multi-size asset embedded in the executable. The
            // TaskbarIcon owns/disposes Icon; no async URI decoding is needed.
            _icon.Icon = new System.Drawing.Icon(System.IO.Path.Combine(
                AppContext.BaseDirectory, "Assets", "Unix.ico"), 32, 32);
            _icon.ForceCreate(enablesEfficiencyMode: false);
            _created = true;
        }
        catch
        {
            // Tray integration is optional. Never take down the Orb because the
            // Windows shell tray path is unavailable on a particular system.
        }
    }

    public void SetUpdateAvailable(string? version)
    {
        if (_created && !string.IsNullOrWhiteSpace(version))
            _icon.ToolTipText = $"UNIX · доступно обновление {version}";
    }

    public void Dispose()
    {
        _window.Activated -= Window_Activated;
        _icon.Dispose();
    }
}
