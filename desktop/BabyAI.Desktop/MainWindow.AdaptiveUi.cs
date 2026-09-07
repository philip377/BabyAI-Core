using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Hosting;
using System.Numerics;
using Windows.UI.ViewManagement;
using Windows.Graphics;
using Windows.UI;

namespace BabyAI.Desktop;

public sealed partial class MainWindow
{
    private const int NavigationExpandedWidth = 224;
    private const int NavigationCollapsedWidth = 80;

    private bool _applyingAdaptiveLayout;
    private bool _composerDraftingReady;
    private bool _orbPresenceReady;
    private bool _statusPresentationReady;
    private TextBlock? _elapsedText;
    private StackPanel? _quickPromptLayer;
    private bool _navigationExpanded = true;

    private void Panel_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!_expanded || _applyingAdaptiveLayout)
            return;

        if (Panel.ActualWidth < 620 && _navigationExpanded)
        {
            _navigationExpanded = false;
            UpdateNavigationPresentation();
        }
        ApplyStoredUiSettings();
        CompactBrainTextBehavior.SetEnabled(BrainText, true);
        FriendlyDesktopTextBehavior.SetEnabled(TaskText, true);
        FriendlyDesktopTextBehavior.SetEnabled(ReplyText, true);
        EnsureElapsedIndicator();
        EnsureQuickPrompts();
        EnsureOrbPresence();
        EnsureComposerDrafting();
        EnsureStatusPresentation();
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
            "…" => "BabyAI · отвечаю",
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
        Panel.Width = double.NaN;
        Panel.Height = double.NaN;
        PanelColumn.Width = new GridLength(1, GridUnitType.Star);
    }

    private void NavigationToggleButton_Click(object sender, RoutedEventArgs e)
    {
        _navigationExpanded = !_navigationExpanded;
        UpdateNavigationPresentation();
        if (_expanded)
            ApplyAdaptiveExpandedLayout();

        // Animate only the visual surface: layout and hit targets settle immediately.
        // Restart safely on repeated toggles without changing the layout-owned Offset.
        ElementCompositionPreview.SetIsTranslationEnabled(NavigationSurface, true);
        var visual = ElementCompositionPreview.GetElementVisual(NavigationSurface);
        visual.StopAnimation("Opacity");
        visual.StopAnimation("Translation");
        visual.Opacity = 1;
        visual.Properties.InsertVector3("Translation", Vector3.Zero);
        if (!new UISettings().AnimationsEnabled)
            return;

        var fade = visual.Compositor.CreateScalarKeyFrameAnimation();
        fade.InsertKeyFrame(0, 0.65f);
        fade.InsertKeyFrame(1, 1);
        fade.Duration = TimeSpan.FromMilliseconds(160);
        visual.StartAnimation("Opacity", fade);
        var slide = visual.Compositor.CreateVector3KeyFrameAnimation();
        slide.InsertKeyFrame(0, new Vector3(_navigationExpanded ? -6 : 6, 0, 0));
        slide.InsertKeyFrame(1, Vector3.Zero);
        slide.Duration = TimeSpan.FromMilliseconds(160);
        visual.StartAnimation("Translation", slide);
    }

    private void UpdateNavigationPresentation()
    {
        UpdateNavigationItemLabels();
        NavigationColumn.Width = new GridLength(
            _navigationExpanded ? NavigationExpandedWidth : NavigationCollapsedWidth);

        var labelVisibility = _navigationExpanded
            ? Visibility.Visible
            : Visibility.Collapsed;
        NavigationBrandMark.Visibility = labelVisibility;
        NavigationHeaderLabel.Visibility = labelVisibility;
        NewChatLabel.Visibility = labelVisibility;
        ProjectsSectionLabel.Visibility = labelVisibility;

        RecentsSectionLabel.Visibility = labelVisibility;

        ProfileLabel.Visibility = labelVisibility;
        SettingsNavigationLabel.Visibility = labelVisibility;
        VoiceNavigationLabel.Visibility = labelVisibility;

        NavigationToggleButton.HorizontalAlignment = _navigationExpanded
            ? HorizontalAlignment.Right : HorizontalAlignment.Center;
        Grid.SetColumnSpan(NavigationToggleButton, _navigationExpanded ? 1 : 2);
        Grid.SetColumn(NavigationToggleButton, _navigationExpanded ? 1 : 0);
        NavigationToggleButton.Content = _navigationExpanded ? "‹" : "›";
        var tooltip = _navigationExpanded ? "Свернуть навигацию" : "Развернуть навигацию";
        ToolTipService.SetToolTip(NavigationToggleButton, tooltip);
        AutomationProperties.SetName(NavigationToggleButton, tooltip);
    }

    private async void NewChatButton_Click(object sender, RoutedEventArgs e)
    {
        await SwitchNavigationAsync("chat.create");
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
