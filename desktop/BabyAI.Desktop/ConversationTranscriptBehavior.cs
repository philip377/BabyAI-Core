using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.UI;

namespace BabyAI.Desktop;

public static class ConversationTranscriptBehavior
{
    private static readonly Regex MessagePattern = new(
        @"(?:\A|\r?\n\r?\n)(You|BabyAI|System): (.*?)(?=(?:\r?\n\r?\n)(?:You|BabyAI|System): |\z)",
        RegexOptions.Compiled | RegexOptions.Singleline);

    private static readonly ConditionalWeakTable<StackPanel, Subscription> Subscriptions = new();

    public static readonly DependencyProperty SourceProperty = DependencyProperty.RegisterAttached(
        "Source",
        typeof(TextBlock),
        typeof(ConversationTranscriptBehavior),
        new PropertyMetadata(null, OnSourceChanged));

    public static TextBlock? GetSource(DependencyObject element) =>
        (TextBlock?)element.GetValue(SourceProperty);

    public static void SetSource(DependencyObject element, TextBlock? value) =>
        element.SetValue(SourceProperty, value);

    private static void OnSourceChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs args)
    {
        if (dependencyObject is not StackPanel target)
            return;

        if (Subscriptions.TryGetValue(target, out var previous))
        {
            previous.Source.UnregisterPropertyChangedCallback(TextBlock.TextProperty, previous.CallbackToken);
            Subscriptions.Remove(target);
        }

        if (args.NewValue is not TextBlock source)
        {
            Render(target, string.Empty);
            return;
        }

        var token = source.RegisterPropertyChangedCallback(
            TextBlock.TextProperty,
            (_, _) => Render(target, source.Text));
        Subscriptions.Add(target, new Subscription(source, token));
        Render(target, source.Text);
    }

    private static void Render(StackPanel target, string transcript)
    {
        target.Children.Clear();
        var matches = MessagePattern.Matches(transcript ?? string.Empty);
        if (matches.Count == 0)
        {
            target.Children.Add(CreateEmptyState());
            return;
        }

        foreach (Match match in matches)
        {
            var speaker = match.Groups[1].Value;
            var text = match.Groups[2].Value.Trim();
            if (text.Length == 0)
                continue;
            target.Children.Add(CreateMessageCard(speaker, text));
        }
    }

    private static UIElement CreateEmptyState()
    {
        return new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(14, 24, 14, 14),
            Spacing = 4,
            Children =
            {
                new TextBlock
                {
                    Text = "✦",
                    FontSize = 22,
                    Foreground = Brush(154, 174, 255, 230),
                    HorizontalAlignment = HorizontalAlignment.Center,
                },
                new TextBlock
                {
                    Text = "BabyAI готов",
                    FontSize = 13,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = Brush(248, 250, 255, 230),
                    HorizontalAlignment = HorizontalAlignment.Center,
                },
                new TextBlock
                {
                    Text = "Напиши сообщение ниже",
                    FontSize = 11,
                    Foreground = Brush(255, 255, 255, 135),
                    HorizontalAlignment = HorizontalAlignment.Center,
                },
            },
        };
    }

    private static UIElement CreateMessageCard(string speaker, string text)
    {
        var isUser = speaker.Equals("You", StringComparison.OrdinalIgnoreCase);
        var isSystem = speaker.Equals("System", StringComparison.OrdinalIgnoreCase);
        var alignment = isSystem
            ? HorizontalAlignment.Center
            : isUser ? HorizontalAlignment.Right : HorizontalAlignment.Left;

        var label = new TextBlock
        {
            Text = isUser ? "ВЫ" : isSystem ? "СИСТЕМА" : "BABYAI",
            FontSize = 9,
            CharacterSpacing = 90,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brush(255, 255, 255, isSystem ? (byte)100 : (byte)125),
            HorizontalAlignment = alignment,
            Margin = new Thickness(6, 0, 6, 0),
        };

        var body = new TextBlock
        {
            Text = text,
            FontSize = isSystem ? 11 : 13,
            TextWrapping = TextWrapping.Wrap,
            IsTextSelectionEnabled = true,
            Foreground = Brush(248, 250, 255, isSystem ? (byte)175 : (byte)240),
        };

        var bubble = new Border
        {
            MaxWidth = isSystem ? 280 : 292,
            Padding = isSystem ? new Thickness(10, 6, 10, 6) : new Thickness(12, 9, 12, 9),
            CornerRadius = isSystem
                ? new CornerRadius(12)
                : isUser ? new CornerRadius(17, 17, 5, 17) : new CornerRadius(17, 17, 17, 5),
            Background = isSystem
                ? Brush(255, 255, 255, 14)
                : isUser ? Brush(91, 112, 255, 145) : Brush(255, 255, 255, 27),
            BorderBrush = isUser ? Brush(145, 160, 255, 75) : Brush(255, 255, 255, 24),
            BorderThickness = new Thickness(1),
            Child = body,
            HorizontalAlignment = alignment,
        };

        var card = new StackPanel
        {
            MaxWidth = 300,
            HorizontalAlignment = alignment,
            Spacing = 3,
            Margin = new Thickness(0, 0, 0, 7),
        };
        card.Children.Add(label);
        card.Children.Add(bubble);

        if (!isUser && !isSystem)
            card.Children.Add(CreateCopyButton(text));

        return card;
    }

    private static Button CreateCopyButton(string text)
    {
        var button = new Button
        {
            Content = "Копировать",
            FontSize = 9,
            Padding = new Thickness(6, 1, 6, 1),
            Margin = new Thickness(4, 0, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Left,
            Background = Brush(255, 255, 255, 0),
            BorderThickness = new Thickness(0),
            Foreground = Brush(255, 255, 255, 105),
        };
        ToolTipService.SetToolTip(button, "Копировать ответ");
        button.Click += (_, _) =>
        {
            var package = new DataPackage();
            package.SetText(text);
            Clipboard.SetContent(package);
            button.Content = "Скопировано";
        };
        return button;
    }

    private static SolidColorBrush Brush(byte red, byte green, byte blue, byte alpha) =>
        new(Color.FromArgb(alpha, red, green, blue));

    private sealed record Subscription(TextBlock Source, long CallbackToken);
}
