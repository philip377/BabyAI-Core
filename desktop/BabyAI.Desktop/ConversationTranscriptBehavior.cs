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
    private const string CodeFence = "```";

    private static readonly Regex MessagePattern = new(
        @"(?:\A|\r?\n\r?\n)(You|Вы|BabyAI|System|Система): (.*?)(?=(?:\r?\n\r?\n)(?:You|Вы|BabyAI|System|Система): |\z)",
        RegexOptions.Compiled | RegexOptions.Singleline);

    private static readonly Regex LanguagePattern = new(
        @"^[A-Za-z0-9+#_.-]{1,24}$",
        RegexOptions.Compiled);

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
            Margin = new Thickness(18, 34, 18, 20),
            Spacing = 6,
            Children =
            {
                new Border
                {
                    Width = 42,
                    Height = 42,
                    CornerRadius = new CornerRadius(21),
                    Background = Brush(124, 141, 255, 22),
                    BorderBrush = Brush(154, 174, 255, 42),
                    BorderThickness = new Thickness(1),
                    Child = new TextBlock
                    {
                        Text = "✦",
                        FontSize = 21,
                        Foreground = Brush(174, 190, 255, 235),
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center,
                    },
                },
                new TextBlock
                {
                    Text = "BabyAI готов",
                    FontSize = 14,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = Brush(248, 250, 255, 235),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Margin = new Thickness(0, 3, 0, 0),
                },
                new TextBlock
                {
                    Text = "Напиши сообщение — начнём с него",
                    FontSize = 11,
                    Foreground = Brush(255, 255, 255, 125),
                    HorizontalAlignment = HorizontalAlignment.Center,
                },
            },
        };
    }

    private static UIElement CreateMessageCard(string speaker, string text)
    {
        var isUser = speaker.Equals("You", StringComparison.OrdinalIgnoreCase)
            || speaker.Equals("Вы", StringComparison.OrdinalIgnoreCase);
        var isSystem = speaker.Equals("System", StringComparison.OrdinalIgnoreCase)
            || speaker.Equals("Система", StringComparison.OrdinalIgnoreCase);
        var alignment = isSystem
            ? HorizontalAlignment.Center
            : isUser ? HorizontalAlignment.Right : HorizontalAlignment.Left;

        var label = new TextBlock
        {
            Text = isUser ? "ВЫ" : isSystem ? "СИСТЕМА" : "BABYAI",
            FontSize = 9,
            CharacterSpacing = 95,
            FontWeight = FontWeights.SemiBold,
            Foreground = isUser
                ? Brush(184, 194, 255, 145)
                : isSystem ? Brush(255, 255, 255, 90) : Brush(184, 200, 255, 150),
            HorizontalAlignment = alignment,
            Margin = new Thickness(7, 0, 7, 0),
        };

        var bubble = new Border
        {
            MaxWidth = isSystem ? 320 : 352,
            Padding = isSystem ? new Thickness(11, 7, 11, 7) : new Thickness(14, 11, 14, 11),
            CornerRadius = isSystem
                ? new CornerRadius(13)
                : isUser ? new CornerRadius(19, 19, 6, 19) : new CornerRadius(19, 19, 19, 6),
            Background = isSystem
                ? Brush(255, 255, 255, 13)
                : isUser ? Brush(91, 112, 255, 150) : Brush(255, 255, 255, 30),
            BorderBrush = isSystem
                ? Brush(255, 255, 255, 18)
                : isUser ? Brush(151, 164, 255, 82) : Brush(171, 187, 255, 30),
            BorderThickness = new Thickness(1),
            Child = CreateMessageBody(text, isSystem),
            HorizontalAlignment = alignment,
        };

        var card = new StackPanel
        {
            MaxWidth = isSystem ? 330 : 362,
            HorizontalAlignment = alignment,
            Spacing = 4,
            Margin = new Thickness(0, 0, 0, 9),
        };
        card.Children.Add(label);
        card.Children.Add(bubble);

        if (!isUser && !isSystem)
            card.Children.Add(CreateCopyButton(text));

        return card;
    }

    private static UIElement CreateMessageBody(string text, bool isSystem)
    {
        if (!text.Contains(CodeFence, StringComparison.Ordinal))
            return CreateBodyText(text, isSystem);

        var stack = new StackPanel { Spacing = 8 };
        var parts = text.Split(CodeFence, StringSplitOptions.None);
        for (var index = 0; index < parts.Length; index++)
        {
            var part = parts[index];
            if (index % 2 == 0)
            {
                var normal = part.Trim();
                if (normal.Length > 0)
                    stack.Children.Add(CreateBodyText(normal, isSystem));
                continue;
            }

            var (language, code) = ParseCodeBlock(part);
            if (code.Length > 0)
                stack.Children.Add(CreateCodeBlock(language, code));
        }

        return stack.Children.Count == 0 ? CreateBodyText(text, isSystem) : stack;
    }

    private static TextBlock CreateBodyText(string text, bool isSystem)
    {
        return new TextBlock
        {
            Text = text,
            FontSize = isSystem ? 11 : 13,
            TextWrapping = TextWrapping.Wrap,
            IsTextSelectionEnabled = true,
            Foreground = Brush(248, 250, 255, isSystem ? (byte)170 : (byte)242),
            LineHeight = isSystem ? 17 : 20,
        };
    }

    private static (string Language, string Code) ParseCodeBlock(string raw)
    {
        var block = raw.Trim('\r', '\n');
        var lineBreak = block.IndexOf('\n');
        if (lineBreak <= 0)
            return (string.Empty, block);

        var candidate = block[..lineBreak].Trim().TrimEnd('\r');
        if (!LanguagePattern.IsMatch(candidate))
            return (string.Empty, block);

        return (candidate, block[(lineBreak + 1)..].TrimEnd('\r', '\n'));
    }

    private static UIElement CreateCodeBlock(string language, string code)
    {
        var content = new StackPanel { Spacing = 5 };
        if (!string.IsNullOrWhiteSpace(language))
        {
            content.Children.Add(new TextBlock
            {
                Text = language.ToUpperInvariant(),
                FontSize = 8,
                CharacterSpacing = 85,
                Foreground = Brush(170, 188, 255, 120),
            });
        }

        content.Children.Add(new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = new TextBlock
            {
                Text = code,
                FontFamily = new FontFamily("Consolas"),
                FontSize = 11,
                TextWrapping = TextWrapping.NoWrap,
                IsTextSelectionEnabled = true,
                Foreground = Brush(238, 242, 255, 230),
            },
        });

        return new Border
        {
            Background = Brush(7, 10, 18, 150),
            BorderBrush = Brush(156, 174, 255, 28),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(9, 7, 9, 8),
            Child = content,
        };
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
