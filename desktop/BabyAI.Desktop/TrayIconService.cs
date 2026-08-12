using H.NotifyIcon;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace BabyAI.Desktop;

public sealed class TrayIconService : IDisposable
{
    private readonly MainWindow _window;
    private readonly TaskbarIcon _icon;

    public TrayIconService(MainWindow window)
    {
        _window = window;

        var menu = new MenuFlyout
        {
            AreOpenCloseAnimationsEnabled = false,
        };

        var show = new MenuFlyoutItem
        {
            Text = "Show BabyAI",
            Width = 160,
        };
        show.Click += (_, _) => _window.ShowFromTray();

        var hide = new MenuFlyoutItem
        {
            Text = "Hide BabyAI",
        };
        hide.Click += (_, _) => _window.HideToTray();

        var exit = new MenuFlyoutItem
        {
            Text = "Exit",
        };
        exit.Click += (_, _) => _window.RequestExit();

        menu.Items.Add(show);
        menu.Items.Add(hide);
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(exit);

        _icon = new TaskbarIcon
        {
            ToolTipText = "BabyAI",
            Visibility = Visibility.Visible,
            NoLeftClickDelay = true,
            ContextFlyout = menu,
            IconSource = new GeneratedIconSource
            {
                Text = "AI",
                FontSize = 34,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 255, 255, 255)),
                Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 93, 108, 255)),
            },
        };

        _icon.ForceCreate();
    }

    public void Dispose()
    {
        _icon.Dispose();
    }
}
