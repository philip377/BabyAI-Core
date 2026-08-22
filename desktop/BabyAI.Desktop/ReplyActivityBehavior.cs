using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace BabyAI.Desktop;

public static class ReplyActivityBehavior
{
    private static readonly ConditionalWeakTable<FrameworkElement, Subscription> Subscriptions = new();

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
        if (dependencyObject is not FrameworkElement indicator)
            return;

        if (Subscriptions.TryGetValue(indicator, out var previous))
        {
            previous.Source.UnregisterPropertyChangedCallback(TextBlock.TextProperty, previous.CallbackToken);
            Subscriptions.Remove(indicator);
        }

        if (args.NewValue is not TextBlock source)
        {
            Update(indicator, string.Empty);
            return;
        }

        var token = source.RegisterPropertyChangedCallback(
            TextBlock.TextProperty,
            (_, _) => Update(indicator, source.Text));
        Subscriptions.Add(indicator, new Subscription(source, token));
        Update(indicator, source.Text);
    }

    private static void Update(FrameworkElement indicator, string status)
    {
        var value = status.Trim().ToLowerInvariant();
        var active = value.Contains("thinking")
            || value.Contains("checking")
            || value.Contains("stopping")
            || value.Contains("дума")
            || value.Contains("выполня")
            || value.Contains("провер")
            || value.Contains("останав");

        indicator.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
    }

    private sealed record Subscription(TextBlock Source, long CallbackToken);
}
