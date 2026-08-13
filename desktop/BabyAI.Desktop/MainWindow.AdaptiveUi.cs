using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using Windows.UI;

namespace BabyAI.Desktop;

public sealed partial class MainWindow
{
    private bool _applyingAdaptiveLayout;
    private bool _orbPresenceReady;
    private TextBlock? _elapsedText;
    private StackPanel? _quickPromptLayer;

    private void Panel_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!_expanded || _applyingAdaptiveLayout)
            return;

        CompactBrainTextBehavior.SetEnabled(BrainText, true);
        FriendlyDesktopTextBehavior.SetEnabled(TaskText, true);
        FriendlyDesktopTextBehavior.SetEnabled(ReplyText, true);
        EnsureElapsedIndicator();
        EnsureQuickPrompts();
        EnsureOrbPresence();
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

    private void EnsureQuickPrompts()
    {
        if (_quickPromptLayer is not null || ConversationScroller.Parent is not Border chatBorder)
            return;

        chatBorder.Child = null;
        var host = new Grid();
        host.Children.Add(ConversationScroller);

        _quickPromptLayer = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Bottom,
            HorizontalAlignment = HorizontalAlignment.Center,
            Spacing = 5,
            MaxWidth = 310,
            Margin = new Thickness(18, 0, 18, 12),
        };
        _quickPromptLayer.Children.Add(new TextBlock
        {
            Text = "МОЖНО НАЧАТЬ С ЭТОГО",
            FontSize = 9,
            CharacterSpacing = 80,
            Opacity = 0.42,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 2),
        });
        _quickPromptLayer.Children.Add(CreateQuickPromptButton("Кто ты и чем можешь помочь?"));
        _quickPromptLayer.Children.Add(CreateQuickPromptButton("Помоги мне разобрать задачу"));
        _quickPromptLayer.Children.Add(CreateQuickPromptButton("Что ты умеешь делать локально?"));

        host.Children.Add(_quickPromptLayer);
        chatBorder.Child = host;

        ConversationText.RegisterPropertyChangedCallback(
            TextBlock.TextProperty,
            (_, _) => UpdateQuickPromptVisibility());
        UpdateQuickPromptVisibility();
    }

    private Button CreateQuickPromptButton(string prompt)
    {
        var button = new Button
        {
            Content = prompt,
            FontSize = 11,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(11, 6, 11, 6),
            CornerRadius = new CornerRadius(12),
            Background = new SolidColorBrush(Color.FromArgb(20, 255, 255, 255)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(28, 255, 255, 255)),
            BorderThickness = new Thickness(1),
        };
        button.Click += (_, _) =>
        {
            MessageBox.Text = prompt;
            MessageBox.Focus(FocusState.Programmatic);
            MessageBox.Select(prompt.Length, 0);
        };
        return button;
    }

    private void UpdateQuickPromptVisibility()
    {
        if (_quickPromptLayer is null)
            return;

        var transcript = ConversationText.Text ?? string.Empty;
        var hasMessages = transcript.Contains("You: ", StringComparison.Ordinal)
            || transcript.Contains("BabyAI: ", StringComparison.Ordinal)
            || transcript.Contains("System: ", StringComparison.Ordinal);
        _quickPromptLayer.Visibility = hasMessages ? Visibility.Collapsed : Visibility.Visible;
    }

    private void EnsureOrbPresence()
    {
        if (_orbPresenceReady)
            return;

        _orbPresenceReady = true;
        StateGlyph.RegisterPropertyChangedCallback(
            TextBlock.TextProperty,
            (_, _) => UpdateOrbPresence());
        UpdateOrbPresence();
    }

    private void UpdateOrbPresence()
    {
        var label = StateGlyph.Text.Trim() switch
        {
            "≈" => "BabyAI · слушаю",
            "✦" => "BabyAI · думаю",
            "!" => "BabyAI · ждёт решения",
            "×" => "BabyAI · ошибка",
            _ => "BabyAI · готов",
        };

        ToolTipService.SetToolTip(OrbButton, label);
        AutomationProperties.SetName(OrbButton, label);
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
        var provider = ReadEnvironment("BABYAI_PROVIDER", "ollama").ToLowerInvariant();
        var details = new StackPanel
        {
            Spacing = 10,
            MinWidth = 300,
            MaxWidth = 370,
        };

        details.Children.Add(new TextBlock
        {
            Text = "Локальный мозг и состояние приложения",
            FontSize = 12,
            Opacity = 0.68,
            Margin = new Thickness(0, 0, 0, 2),
        });
        details.Children.Add(CreateDiagnosticRow("Состояние", BrainText.Text));
        details.Children.Add(CreateDiagnosticRow("Провайдер", provider));
        details.Children.Add(CreateDiagnosticRow("Core", CoreStatusText.Text));
        details.Children.Add(CreateDiagnosticRow("Runtime", RuntimeText.Text));
        details.Children.Add(CreateDiagnosticRow("Запуск", StartupText.Text));

        if (provider.Equals("native", StringComparison.OrdinalIgnoreCase))
        {
            details.Children.Add(CreateDiagnosticRow(
                "GGUF модель",
                ReadEnvironment("BABYAI_NATIVE_MODEL", "Путь не задан")));
            details.Children.Add(CreateDiagnosticRow(
                "Native runtime",
                ReadEnvironment("BABYAI_NATIVE_RUNTIME", "Путь не задан")));
        }
        else
        {
            details.Children.Add(CreateDiagnosticRow(
                "Модель",
                ReadEnvironment("BABYAI_MODEL", "qwen3:8b")));
        }

        details.Children.Add(new Border
        {
            Height = 1,
            Background = new SolidColorBrush(Color.FromArgb(24, 255, 255, 255)),
            Margin = new Thickness(0, 2, 0, 2),
        });
        details.Children.Add(CreateDiagnosticRow(
            "Управление",
            "Enter — отправить · Shift+Enter — новая строка · Stop — остановить генерацию"));
        details.Children.Add(CreateDiagnosticRow(
            "Окно",
            "Всегда поверх окон · закрытие сворачивает BabyAI в трей"));

        var dialog = new ContentDialog
        {
            XamlRoot = Root.XamlRoot,
            Title = "BabyAI · Диагностика",
            Content = new ScrollViewer
            {
                Content = details,
                MaxHeight = 470,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            },
            CloseButtonText = "Закрыть",
            DefaultButton = ContentDialogButton.Close,
        };

        await dialog.ShowAsync();
    }

    private static string ReadEnvironment(string name, string fallback)
    {
        var value = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
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
