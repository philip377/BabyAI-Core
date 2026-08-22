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
    private bool _composerDraftingReady;
    private bool _orbPresenceReady;
    private bool _statusPresentationReady;
    private TextBlock? _elapsedText;
    private StackPanel? _quickPromptLayer;

    private void Panel_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!_expanded || _applyingAdaptiveLayout)
            return;

        ApplyStoredUiSettings();
        CompactBrainTextBehavior.SetEnabled(BrainText, true);
        FriendlyDesktopTextBehavior.SetEnabled(TaskText, true);
        FriendlyDesktopTextBehavior.SetEnabled(ReplyText, true);
        EnsureElapsedIndicator();
        EnsureQuickPrompts();
        EnsureOrbPresence();
        EnsureComposerDrafting();
        EnsureStatusPresentation();
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
        var hasMessages = transcript.Contains("Вы: ", StringComparison.Ordinal)
            || transcript.Contains("You: ", StringComparison.Ordinal)
            || transcript.Contains("BabyAI: ", StringComparison.Ordinal)
            || transcript.Contains("Система: ", StringComparison.Ordinal)
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
            "›" => "BabyAI · выполняю",
            "!" => "BabyAI · ждёт решения",
            "✓" => "BabyAI · готово",
            "×" => "BabyAI · ошибка",
            _ => "BabyAI · готов",
        };

        ToolTipService.SetToolTip(OrbButton, label);
        AutomationProperties.SetName(OrbButton, label);
    }

    private void EnsureComposerDrafting()
    {
        if (_composerDraftingReady)
            return;

        _composerDraftingReady = true;
        MessageBox.RegisterPropertyChangedCallback(
            Control.IsEnabledProperty,
            (_, _) => KeepComposerWritable());
        KeepComposerWritable();
        AutomationProperties.SetName(MessageBox, "Сообщение BabyAI");
        AutomationProperties.SetHelpText(
            MessageBox,
            "Можно готовить следующее сообщение, пока BabyAI думает. Enter отправляет, когда текущий ответ завершён.");
    }

    private void KeepComposerWritable()
    {
        if (!MessageBox.IsEnabled)
            MessageBox.IsEnabled = true;
    }

    private void EnsureStatusPresentation()
    {
        if (_statusPresentationReady)
            return;

        _statusPresentationReady = true;
        BrainText.RegisterPropertyChangedCallback(
            TextBlock.TextProperty,
            (_, _) => UpdateBrainPill());
        UpdateBrainPill();

        AutomationProperties.SetName(SendButton, "Отправить сообщение");
        AutomationProperties.SetHelpText(SendButton, "Enter");
        AutomationProperties.SetName(StopButton, "Остановить генерацию");
        AutomationProperties.SetName(RetryButton, "Повторить проверку BabyAI");
        AutomationProperties.SetName(DetailsButton, "Открыть настройки BabyAI");
        ToolTipService.SetToolTip(DetailsButton, "Настройки");
    }

    private void UpdateBrainPill()
    {
        if (BrainText.Parent is not Border pill)
            return;

        var status = BrainText.Text.Trim().ToLowerInvariant();

        if (status.Contains("готов") || status.Contains("ready"))
        {
            pill.Background = new SolidColorBrush(Color.FromArgb(24, 76, 212, 145));
            pill.BorderBrush = new SolidColorBrush(Color.FromArgb(54, 108, 236, 173));
            BrainText.Foreground = new SolidColorBrush(Color.FromArgb(225, 218, 255, 238));
            return;
        }

        if (status.Contains("не найден") || status.Contains("missing"))
        {
            pill.Background = new SolidColorBrush(Color.FromArgb(26, 236, 169, 72));
            pill.BorderBrush = new SolidColorBrush(Color.FromArgb(58, 255, 196, 92));
            BrainText.Foreground = new SolidColorBrush(Color.FromArgb(230, 255, 233, 190));
            return;
        }

        if (status.Contains("ошиб")
            || status.Contains("недоступ")
            || status.Contains("offline")
            || status.Contains("unavailable"))
        {
            pill.Background = new SolidColorBrush(Color.FromArgb(28, 232, 82, 104));
            pill.BorderBrush = new SolidColorBrush(Color.FromArgb(62, 255, 112, 130));
            BrainText.Foreground = new SolidColorBrush(Color.FromArgb(232, 255, 216, 222));
            return;
        }

        pill.Background = new SolidColorBrush(Color.FromArgb(22, 124, 141, 255));
        pill.BorderBrush = new SolidColorBrush(Color.FromArgb(42, 160, 174, 255));
        BrainText.Foreground = new SolidColorBrush(Color.FromArgb(220, 240, 244, 255));
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
        SettingsButton_Click(sender, e);
        await Task.CompletedTask;
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
