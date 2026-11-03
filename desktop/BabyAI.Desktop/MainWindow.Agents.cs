using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;

namespace BabyAI.Desktop;

public sealed partial class MainWindow
{
    private bool _agentsDialogOpen;
    private GingerCompanionWindow? _gingerCompanion;
    private GingerCompanionPhase _gingerPhase = GingerCompanionPhase.Idle;

    private void SetGingerPhase(GingerCompanionPhase phase)
    {
        _gingerPhase = phase;
        _gingerCompanion?.SetStatus(phase);
    }

    private void SetGingerCaptureMode(bool capturing) => _gingerCompanion?.SetCaptureMode(capturing);

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
                Text = GingerCompanionWindow.DescribePhase(_gingerPhase),
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
            var companionButton = new Button
            {
                Content = _gingerCompanion is null
                    ? "Показать питомца на рабочем столе"
                    : "Закрыть питомца"
            };
            companionButton.Click += (_, _) =>
            {
                if (_gingerCompanion is not null)
                {
                    _gingerCompanion.Close();
                    companionButton.Content = "Показать питомца на рабочем столе";
                    return;
                }

                var companion = new GingerCompanionWindow();
                companion.SetStatus(_gingerPhase);
                _gingerCompanion = companion;
                companion.Closed += (_, _) =>
                {
                    if (ReferenceEquals(_gingerCompanion, companion))
                    {
                        _gingerCompanion = null;
                        companionButton.Content = "Показать питомца на рабочем столе";
                    }
                };
                companion.Activate();
                companionButton.Content = "Закрыть питомца";
            };
            gingerCard.Children.Add(companionButton);
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
                Text = "Питомец: локальное окно · Статус: реальная проверка, без доступа к базе",
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
                Text = "Пузырь показывает фактический этап проверки Ginger, а не скрытые мысли ИИ. Окно пока непрозрачное.",
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
