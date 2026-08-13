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
    private static readonly DispatcherTimer RecoveryTimer = CreateRecoveryTimer();
    private static WeakReference<TextBlock>? _indicator;
    private static bool _refreshing;

    public static bool GetEnabled(DependencyObject element) => (bool)element.GetValue(EnabledProperty);

    public static void SetEnabled(DependencyObject element, bool value) => element.SetValue(EnabledProperty, value);

    private static DispatcherTimer CreateRecoveryTimer()
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
        timer.Tick += OnRecoveryTick;
        return timer;
    }

    private static void OnEnabledChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs args)
    {
        if (dependencyObject is not TextBlock text)
            return;

        text.Loaded -= OnLoaded;
        text.Unloaded -= OnUnloaded;
        text.Tapped -= OnTapped;

        if (args.NewValue is true)
        {
            text.Loaded += OnLoaded;
            text.Unloaded += OnUnloaded;
            text.Tapped += OnTapped;
        }
        else
        {
            StopRecoveryFor(text);
        }
    }

    private static async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not TextBlock text)
            return;

        _indicator = new WeakReference<TextBlock>(text);
        await RefreshAsync(text);
    }

    private static void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (sender is TextBlock text)
            StopRecoveryFor(text);
    }

    private static async void OnTapped(object sender, TappedRoutedEventArgs e)
    {
        if (sender is not TextBlock text)
            return;

        e.Handled = true;
        await RefreshAsync(text);
    }

    private static async void OnRecoveryTick(object? sender, object e)
    {
        if (_indicator is null || !_indicator.TryGetTarget(out var text) || text.XamlRoot is null)
        {
            RecoveryTimer.Stop();
            return;
        }

        await RefreshAsync(text, showChecking: false);
    }

    private static async Task RefreshAsync(TextBlock text, bool showChecking = true)
    {
        if (_refreshing)
            return;

        _refreshing = true;
        if (showChecking)
            text.Text = "Brain: checking…";

        try
        {
            var status = await Bridge.StatusAsync();
            text.Text = Format(status.Brain);
            ToolTipService.SetToolTip(text, BuildTooltip(status.Brain));
            (Application.Current as App)?.MainWindow?.ApplyBrainReadinessFromIndicator(status.Brain);
            UpdateRecoveryTimer(text, status.Brain);
        }
        catch (Exception ex)
        {
            text.Text = "Brain: status unavailable · retrying automatically · click to recheck";
            ToolTipService.SetToolTip(text, Limit(ex.Message));
            StartRecovery(text);
        }
        finally
        {
            _refreshing = false;
        }
    }

    private static void UpdateRecoveryTimer(TextBlock text, BrainStatus brain)
    {
        if (brain.Ready || brain.State.Equals("unsupported_provider", StringComparison.OrdinalIgnoreCase))
        {
            StopRecoveryFor(text);
            return;
        }

        StartRecovery(text);
    }

    private static void StartRecovery(TextBlock text)
    {
        _indicator = new WeakReference<TextBlock>(text);
        if (!RecoveryTimer.IsEnabled)
            RecoveryTimer.Start();
    }

    private static void StopRecoveryFor(TextBlock text)
    {
        if (_indicator is not null && _indicator.TryGetTarget(out var current) && !ReferenceEquals(current, text))
            return;

        RecoveryTimer.Stop();
        _indicator = null;
    }

    private static string Format(BrainStatus brain)
    {
        if (brain.Ready)
            return brain.Provider.Equals("echo", StringComparison.OrdinalIgnoreCase)
                ? "Brain: ready · echo"
                : $"Brain: ready · {brain.Provider} · {brain.Model}";

        return brain.State switch
        {
            "unavailable" => $"Brain: {brain.Provider} offline · auto-retry on · click to recheck",
            "model_missing" => $"Brain: model missing · {brain.Model} · auto-retry on · click to recheck",
            "unsupported_provider" => $"Brain: unsupported provider · {brain.Provider}",
            _ => $"Brain: {brain.State} · auto-retry on · click to recheck",
        };
    }

    private static string BuildTooltip(BrainStatus brain)
    {
        var detail = Limit(brain.Detail);
        var retry = brain.Ready || brain.State.Equals("unsupported_provider", StringComparison.OrdinalIgnoreCase)
            ? "Click to recheck."
            : "BabyAI rechecks automatically every 15 seconds while unavailable. Click to recheck now.";
        return string.IsNullOrWhiteSpace(detail)
            ? $"Provider: {brain.Provider}; model: {brain.Model}; state: {brain.State}. {retry}"
            : $"{detail} {retry}";
    }

    private static string Limit(string value)
    {
        value = value.Trim();
        return value.Length <= 240 ? value : value[..240] + "…";
    }
}
