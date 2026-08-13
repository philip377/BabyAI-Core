using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Graphics;

namespace BabyAI.Desktop;

public sealed partial class MainWindow
{
    private bool _applyingAdaptiveLayout;
    private TextBlock? _elapsedText;

    private void Panel_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!_expanded || _applyingAdaptiveLayout)
            return;

        CompactBrainTextBehavior.SetEnabled(BrainText, true);
        EnsureElapsedIndicator();
        ApplyAdaptiveExpandedLayout();
    }

    private void EnsureElapsedIndicator()
    {
        if (_elapsedText is not null || ReplyText.Parent is not Grid statusGrid)
            return;

        statusGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(RetryButton, 3);

        _elapsedText = new TextBlock
        {
            FontSize = 10,
            Opacity = 0.52,
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = Visibility.Collapsed,
        };
        Grid.SetColumn(_elapsedText, 2);
        ReplyElapsedBehavior.SetSource(_elapsedText, ReplyText);
        statusGrid.Children.Add(_elapsedText);
    }

    private void ApplyAdaptiveExpandedLayout()
    {
        _applyingAdaptiveLayout = true;
        try
        {
            var displayArea = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Nearest);
            var work = displayArea.WorkArea;
            var outer = displayArea.OuterBounds;

            var panelWidth = Math.Clamp(work.Width - 220, 340, 430);
            var windowWidth = panelWidth + 154;
            var windowHeight = Math.Clamp(work.Height - 120, 460, 580);
            var panelHeight = windowHeight - 20;

            Panel.Width = panelWidth;
            Panel.Height = panelHeight;
            PanelColumn.Width = new GridLength(panelWidth + 12);

            var current = AppWindow.Position;
            var localX = current.X - outer.X;
            var localY = current.Y - outer.Y;
            var maxX = Math.Max(work.X, work.X + work.Width - windowWidth);
            var maxY = Math.Max(work.Y, work.Y + work.Height - windowHeight);
            var x = Math.Clamp(localX, work.X, maxX);
            var y = Math.Clamp(localY, work.Y, maxY);

            AppWindow.MoveAndResize(
                new RectInt32(x, y, windowWidth, windowHeight),
                displayArea);
        }
        finally
        {
            _applyingAdaptiveLayout = false;
        }
    }

    private async void DetailsButton_Click(object sender, RoutedEventArgs e)
    {
        var details = new StackPanel
        {
            Spacing = 10,
            MinWidth = 300,
        };

        details.Children.Add(CreateDiagnosticRow("Состояние", BrainText.Text));
        details.Children.Add(CreateDiagnosticRow("Core", CoreStatusText.Text));
        details.Children.Add(CreateDiagnosticRow("Runtime", RuntimeText.Text));
        details.Children.Add(CreateDiagnosticRow("Запуск", StartupText.Text));
        details.Children.Add(new TextBlock
        {
            Text = "Технические детали вынесены сюда, чтобы основной чат оставался чистым.",
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.62,
            FontSize = 11,
            Margin = new Thickness(0, 4, 0, 0),
        });

        var dialog = new ContentDialog
        {
            XamlRoot = Root.XamlRoot,
            Title = "BabyAI · Диагностика",
            Content = details,
            PrimaryButtonText = "Обновить статус",
            SecondaryButtonText = "Очистить чат",
            CloseButtonText = "Закрыть",
            DefaultButton = ContentDialogButton.Close,
        };

        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Primary)
        {
            try
            {
                SetBusy(true);
                ReplyText.Text = "Проверяю состояние…";
                await RefreshStatusAsync();
                ReplyText.Text = "Статус обновлён.";
            }
            catch (Exception ex)
            {
                ShowBridgeError(ex);
            }
            finally
            {
                SetBusy(false);
            }
        }
        else if (result == ContentDialogResult.Secondary)
        {
            _conversation.Clear();
            ConversationText.Text = string.Empty;
            ReplyText.Text = "Чат очищен.";
        }
    }

    private static UIElement CreateDiagnosticRow(string label, string value)
    {
        var panel = new StackPanel { Spacing = 2 };
        panel.Children.Add(new TextBlock
        {
            Text = label.ToUpperInvariant(),
            FontSize = 9,
            CharacterSpacing = 80,
            Opacity = 0.5,
        });
        panel.Children.Add(new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(value) ? "—" : value,
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            IsTextSelectionEnabled = true,
        });
        return panel;
    }
}
