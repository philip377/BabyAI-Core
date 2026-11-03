using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

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

            var gingerCard = new StackPanel { Spacing = 7 };
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
                Text = "Статус: доступен для локальной проверки",
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
