using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace BabyAI.Desktop;

public static class FriendlyDesktopTextBehavior
{
    private static readonly ConditionalWeakTable<TextBlock, Subscription> Subscriptions = new();

    public static readonly DependencyProperty EnabledProperty = DependencyProperty.RegisterAttached(
        "Enabled",
        typeof(bool),
        typeof(FriendlyDesktopTextBehavior),
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
            (_, _) => Apply(text, subscription));
        Subscriptions.Add(text, subscription);
        Apply(text, subscription);
    }

    private static void Apply(TextBlock text, Subscription subscription)
    {
        if (subscription.Updating)
            return;

        var replacement = Translate(text.Text);
        if (replacement == text.Text)
            return;

        subscription.Updating = true;
        try
        {
            text.Text = replacement;
        }
        finally
        {
            subscription.Updating = false;
        }
    }

    private static string Translate(string value) => value.Trim() switch
    {
        "No active task" => "Нет активной задачи",
        "Thinking…" => "Думаю…",
        "Response complete." => "Ответ готов.",
        "Generation stopped." => "Генерация остановлена.",
        "Stopping generation…" => "Останавливаю…",
        "Checking BabyAI Core…" => "Проверяю BabyAI…",
        "Core connection restored." => "Связь восстановлена.",
        "Lesson approved and saved to MEMORIA." => "Сохранено в памяти.",
        "Lesson rejected." => "Изменение отклонено.",
        _ => value,
    };

    private sealed class Subscription
    {
        public long CallbackToken { get; set; }
        public bool Updating { get; set; }
    }
}
