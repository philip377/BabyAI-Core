using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace BabyAI.Desktop;

public static class CompactBrainTextBehavior
{
    private static readonly ConditionalWeakTable<TextBlock, Subscription> Subscriptions = new();

    public static readonly DependencyProperty EnabledProperty = DependencyProperty.RegisterAttached(
        "Enabled",
        typeof(bool),
        typeof(CompactBrainTextBehavior),
        new PropertyMetadata(false, OnEnabledChanged));

    public static bool GetEnabled(DependencyObject element) => (bool)element.GetValue(EnabledProperty);

    public static void SetEnabled(DependencyObject element, bool value) => element.SetValue(EnabledProperty, value);

    private static void OnEnabledChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs args)
    {
        if (dependencyObject is not TextBlock text)
            return;

        if (Subscriptions.TryGetValue(text, out var previous))
        {
            text.UnregisterPropertyChangedCallback(TextBlock.TextProperty, previous.CallbackToken);
            Subscriptions.Remove(text);
        }

        if (args.NewValue is not true)
            return;

        var subscription = new Subscription();
        subscription.CallbackToken = text.RegisterPropertyChangedCallback(
            TextBlock.TextProperty,
            (_, _) => Compact(text, subscription));
        Subscriptions.Add(text, subscription);
        Compact(text, subscription);
    }

    private static void Compact(TextBlock text, Subscription subscription)
    {
        if (subscription.Updating)
            return;

        var compact = Format(text.Text);
        if (compact == text.Text)
            return;

        subscription.Updating = true;
        try
        {
            text.Text = compact;
        }
        finally
        {
            subscription.Updating = false;
        }
    }

    private static string Format(string value)
    {
        var text = value.Trim();
        var lower = text.ToLowerInvariant();

        if (lower.Contains("checking"))
            return "Проверяю…";
        if (lower.Contains("ready · native"))
            return "Готов · native";
        if (lower.Contains("ready · ollama"))
            return "Готов · ollama";
        if (lower.Contains("ready · echo"))
            return "Готов · echo";
        if (lower.Contains("native gguf model missing") || lower.Contains("model missing"))
            return "Модель не найдена";
        if (lower.Contains("native runtime missing"))
            return "Runtime не найден";
        if (lower.Contains("offline") || lower.Contains("status unavailable"))
            return "Мозг недоступен";
        if (lower.Contains("unsupported provider"))
            return "Провайдер ?";

        return text.Length <= 24 ? text : text[..23] + "…";
    }

    private sealed class Subscription
    {
        public long CallbackToken { get; set; }
        public bool Updating { get; set; }
    }
}
