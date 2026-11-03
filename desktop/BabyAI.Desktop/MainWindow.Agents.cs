using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;

namespace BabyAI.Desktop;

public sealed partial class MainWindow
{
    private bool _agentsDialogOpen;

    private async void AgentsNavigationButton_Click(object sender, RoutedEventArgs e)
    {
        if (_agentsDialogOpen || _busy || _gingerRunning || _gingerDialogOpen)
            return;

        _agentsDialogOpen = true;
        try
        {
            var content = new StackPanel { Spacing = 14, MinWidth = 390 };
            content.Children.Add(new TextBlock
            {
                Text = "Специализированные агенты UNIX. Управление ими не требует отдельного чата.",
                TextWrapping = TextWrapping.Wrap,
                Opacity = 0.8
            });

            var gingerCard = new StackPanel { Spacing = 9 };
            // Visual companion only: never expose hidden model reasoning as thoughts.
            var avatar = new Grid { Width = 76, Height = 76, HorizontalAlignment = HorizontalAlignment.Left };
            avatar.Children.Add(new Ellipse
            {
                Width = 68, Height = 68,
                Fill = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 126, 112, 221)),
                Stroke = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 180, 168, 255)),
                StrokeThickness = 2
            });
            avatar.Children.Add(new TextBlock
            {
                Text = "• ᴗ •",
                FontSize = 23,
                Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 255, 255, 255)),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            });
            gingerCard.Children.Add(avatar);

            var thoughtText = new TextBlock
            {
                Text = "Отдыхаю. Поручишь мне проверку товаров?",
                TextWrapping = TextWrapping.Wrap,
                FontSize = 12
            };
            gingerCard.Children.Add(new Border
            {
                Padding = new Thickness(10),
                CornerRadius = new CornerRadius(12),
                Background = new SolidColorBrush(Windows.UI.Color.FromArgb(35, 255, 255, 255)),
                Child = thoughtText
            });
            var thoughts = new[]
            {
                "Отдыхаю. Поручишь мне проверку товаров?",
                "Если варианты ароматов различаются, я попрошу ручную проверку.",
                "Без разрешения я не меняю данные Ginger.",
                "Когда подключим события агента, здесь будет настоящий статус задачи."
            };
            var thoughtIndex = 0;
            var thoughtButton = new Button { Content = "Что думает питомец?" };
            thoughtButton.Click += (_, _) =>
            {
                thoughtIndex = (thoughtIndex + 1) % thoughts.Length;
                thoughtText.Text = thoughts[thoughtIndex];
            };
            gingerCard.Children.Add(thoughtButton);
            gingerCard.Children.Add(new TextBlock
            {
                Text = "Ginger Agent",
                FontSize = 18,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
            });
            gingerCard.Children.Add(new TextBlock
            {
                Text = "Проверка товаров, кандидатов сопоставления и журнал действий. Сейчас — тестовый режим без записи в Ginger.",
                TextWrapping = TextWrapping.Wrap
            });
            gingerCard.Children.Add(new TextBlock
            {
                Text = "Питомец: прототип · Агент: доступен для локальной проверки",
                FontSize = 12,
                Opacity = 0.7
            });
            content.Children.Add(new Border
            {
                Padding = new Thickness(14),
                CornerRadius = new CornerRadius(12),
                Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(35, 127, 145, 232)),
                Child = gingerCard
            });

            content.Children.Add(new TextBlock
            {
                Text = "Реплики пока демонстрационные. Настоящие события агента и питомцы на рабочем столе появятся позже.",
                TextWrapping = TextWrapping.Wrap,
                FontSize = 11,
                Opacity = 0.7
            });

            var dialog = new ContentDialog
            {
                XamlRoot = Root.XamlRoot,
                Title = "UNIX · Агенты",
                Content = content,
                PrimaryButtonText = "Открыть Ginger Agent",
                CloseButtonText = "Закрыть",
                DefaultButton = ContentDialogButton.Close
            };

            var result = await dialog.ShowAsync();
            _agentsDialogOpen = false;
            if (result == ContentDialogResult.Primary)
                GingerNavigationButton_Click(sender, e);
        }
        finally
        {
            _agentsDialogOpen = false;
        }
    }
}
