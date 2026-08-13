using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace BabyAI.Desktop;

public static class BrainStatusBehavior
{
    public static readonly DependencyProperty EnabledProperty = DependencyProperty.RegisterAttached(
        "Enabled",
        typeof(bool),
        typeof(BrainStatusBehavior),
        new PropertyMetadata(false, OnEnabledChanged));

    private static readonly BabyAIBridgeClient Bridge = new();
    private static bool _refreshing;

    public static bool GetEnabled(DependencyObject element) => (bool)element.GetValue(EnabledProperty);

    public static void SetEnabled(DependencyObject element, bool value) => element.SetValue(EnabledProperty, value);

    private static void OnEnabledChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs args)
    {
        if (dependencyObject is not TextBlock text)
            return;

        text.Loaded -= OnLoaded;
        text.Tapped -= OnTapped;

        if (args.NewValue is true)
        {
            text.Loaded += OnLoaded;
            text.Tapped += OnTapped;
        }
    }

    private static async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is TextBlock text)
            await RefreshAsync(text);
    }

    private static async void OnTapped(object sender, TappedRoutedEventArgs e)
    {
        if (sender is not TextBlock text)
            return;

        e.Handled = true;
        await RefreshAsync(text);
    }

    private static async Task RefreshAsync(TextBlock text)
    {
        if (_refreshing)
            return;

        _refreshing = true;
        text.Text = "Brain: checking…";

        try
        {
            var status = await Bridge.StatusAsync();
            text.Text = Format(status.Brain);
            ToolTipService.SetToolTip(text, BuildTooltip(status.Brain));
            (Application.Current as App)?.MainWindow?.ApplyBrainReadinessFromIndicator(status.Brain);
        }
        catch (Exception ex)
        {
            text.Text = "Brain: status unavailable · click to recheck";
            ToolTipService.SetToolTip(text, Limit(ex.Message));
        }
        finally
        {
            _refreshing = false;
        }
    }

    private static string Format(BrainStatus brain)
    {
        if (brain.Ready)
            return brain.Provider.Equals("echo", StringComparison.OrdinalIgnoreCase)
                ? "Brain: ready · echo"
                : $"Brain: ready · {brain.Provider} · {brain.Model}";

        return brain.State switch
        {
            "unavailable" => $"Brain: {brain.Provider} offline · click to recheck",
            "model_missing" => $"Brain: model missing · {brain.Model} · click to recheck",
            "unsupported_provider" => $"Brain: unsupported provider · {brain.Provider}",
            _ => $"Brain: {brain.State} · click to recheck",
        };
    }

    private static string BuildTooltip(BrainStatus brain)
    {
        var detail = Limit(brain.Detail);
        return string.IsNullOrWhiteSpace(detail)
            ? $"Provider: {brain.Provider}; model: {brain.Model}; state: {brain.State}. Click to recheck."
            : $"{detail} Click to recheck.";
    }

    private static string Limit(string value)
    {
        value = value.Trim();
        return value.Length <= 240 ? value : value[..240] + "…";
    }
}
