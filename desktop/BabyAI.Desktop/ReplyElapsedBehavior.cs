using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace BabyAI.Desktop;

public static class ReplyElapsedBehavior
{
    public static readonly DependencyProperty SourceProperty = DependencyProperty.RegisterAttached(
        "Source",
        typeof(TextBlock),
        typeof(ReplyElapsedBehavior),
        new PropertyMetadata(null, OnSourceChanged));

    public static TextBlock? GetSource(DependencyObject element) => (TextBlock?)element.GetValue(SourceProperty);

    public static void SetSource(DependencyObject element, TextBlock? value) => element.SetValue(SourceProperty, value);

    private static void OnSourceChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs args)
    {
        if (dependencyObject is not TextBlock target || args.NewValue is not TextBlock statusText)
            return;

        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        DateTimeOffset? startedAt = null;

        void Refresh()
        {
            var status = statusText.Text.Trim().ToLowerInvariant();
            var active = status.Contains("thinking")
                || status.Contains("checking")
                || status.Contains("answering")
                || status.Contains("stopping")
                || status.Contains("дума")
                || status.Contains("отвеч")
                || status.Contains("выполня")
                || status.Contains("провер")
                || status.Contains("останав");

            if (!active)
            {
                timer.Stop();
                startedAt = null;
                target.Text = string.Empty;
                target.Visibility = Visibility.Collapsed;
                return;
            }

            startedAt ??= DateTimeOffset.UtcNow;
            if (!timer.IsEnabled)
                timer.Start();

            var seconds = Math.Max(0, (int)(DateTimeOffset.UtcNow - startedAt.Value).TotalSeconds);
            target.Text = $"{seconds} с";
            target.Visibility = Visibility.Visible;
        }

        timer.Tick += (_, _) => Refresh();
        statusText.RegisterPropertyChangedCallback(TextBlock.TextProperty, (_, _) => Refresh());
        target.Unloaded += (_, _) => timer.Stop();
        Refresh();
    }
}
