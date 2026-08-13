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
            Text = $"BabyAI · {provider}",
            IsEnabled = false,
            Width = 172,
        };

        var show = new MenuFlyoutItem
        {
            Text = "Открыть BabyAI",
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
            ToolTipText = $"BabyAI · {provider}",
            Visibility = Visibility.Visible,
            NoLeftClickDelay = true,
            ContextFlyout = menu,
            IconSource = new GeneratedIconSource
            {
                Text = "✦",
                FontSize = 36,
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
