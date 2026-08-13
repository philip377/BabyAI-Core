using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace BabyAI.Desktop;

public static class ReplyActivityBehavior
{
    private static readonly ConditionalWeakTable<ProgressRing, Subscription> Subscriptions = new();

    public static readonly DependencyProperty SourceProperty = DependencyProperty.RegisterAttached(
        "Source",
        typeof(TextBlock),
        typeof(ReplyActivityBehavior),
        new PropertyMetadata(null, OnSourceChanged));

    public static TextBlock? GetSource(DependencyObject element) =>
        (TextBlock?)element.GetValue(SourceProperty);

    public static void SetSource(DependencyObject element, TextBlock? value) =>
        element.SetValue(SourceProperty, value);

    private static void OnSourceChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs args)
    {
        if (dependencyObject is not ProgressRing ring)
            return;

        if (Subscriptions.TryGetValue(ring, out var previous))
        {
            previous.Source.UnregisterPropertyChangedCallback(TextBlock.TextProperty, previous.CallbackToken);
            Subscriptions.Remove(ring);
        }

        if (args.NewValue is not TextBlock source)
        {
            Update(ring, string.Empty);
            return;
        }

        var token = source.RegisterPropertyChangedCallback(
            TextBlock.TextProperty,
            (_, _) => Update(ring, source.Text));
        Subscriptions.Add(ring, new Subscription(source, token));
        Update(ring, source.Text);
    }

    private static void Update(ProgressRing ring, string status)
    {
        var value = status.Trim().ToLowerInvariant();
        var active = value.Contains("thinking")
            || value.Contains("checking")
            || value.Contains("stopping")
            || value.Contains("дума")
            || value.Contains("провер")
            || value.Contains("останав");

        ring.IsActive = active;
        ring.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
    }

    private sealed record Subscription(TextBlock Source, long CallbackToken);
}
