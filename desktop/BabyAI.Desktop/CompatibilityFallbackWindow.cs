using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace BabyAI.Desktop;

internal sealed class CompatibilityFallbackWindow : Window
{
    internal CompatibilityFallbackWindow(Exception startupError)
    {
        Title = "BabyAI — режим совместимости";

        var root = new Grid
        {
            Background = new SolidColorBrush(Color.FromArgb(255, 11, 13, 24)),
            Padding = new Thickness(28),
        };

        var stack = new StackPanel
        {
            Spacing = 14,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            MaxWidth = 620,
        };

        stack.Children.Add(new TextBlock
        {
            Text = "BabyAI запущен в режиме совместимости",
            FontSize = 26,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Colors.White),
            TextWrapping = TextWrapping.Wrap,
            HorizontalAlignment = HorizontalAlignment.Center,
        });

        stack.Children.Add(new TextBlock
        {
            Text = "Основной интерфейс не смог загрузиться на этой версии Windows. Само приложение и встроенный runtime установлены; мы сохранили техническую диагностику и не завершаем BabyAI молча.",
            FontSize = 15,
            Foreground = new SolidColorBrush(Color.FromArgb(255, 190, 195, 218)),
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
        });

        stack.Children.Add(new TextBlock
        {
            Text = $"Диагностика: {StartupDiagnostics.LogPath}",
            FontSize = 12,
            Foreground = new SolidColorBrush(Color.FromArgb(255, 145, 153, 190)),
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
        });

        stack.Children.Add(new TextBlock
        {
            Text = startupError.Message,
            FontSize = 12,
            Foreground = new SolidColorBrush(Color.FromArgb(255, 238, 166, 178)),
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            MaxHeight = 90,
        });

        var close = new Button
        {
            Content = "Закрыть",
            HorizontalAlignment = HorizontalAlignment.Center,
            Padding = new Thickness(22, 10, 22, 10),
        };
        close.Click += (_, _) => Close();
        stack.Children.Add(close);

        root.Children.Add(stack);
        Content = root;

        AppWindow.Resize(new Windows.Graphics.SizeInt32(720, 430));
    }
}
