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
            (Application.Current as App)?.MainWindow?.ApplyStartupFailureFromIndicator();
            StartRecovery(text);
        }
        finally
        {
            _refreshing = false;
        }
    }

    private static void UpdateRecoveryTimer(TextBlock text, BrainStatus brain)
    {
        if (brain.Ready
            || brain.State.Equals("unsupported_provider", StringComparison.OrdinalIgnoreCase)
            || brain.State.Equals("native_inference_pending", StringComparison.OrdinalIgnoreCase))
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
            "unavailable" => $"Brain: {brain.Provider} offline · start Ollama · auto-retry on",
            "model_missing" => $"Brain: model missing · run: ollama pull {brain.Model}",
            "native_model_missing" => "Brain: native GGUF model missing · configure BABYAI_NATIVE_MODEL",
            "native_runtime_missing" => "Brain: BabyAI native runtime missing · configure BABYAI_NATIVE_RUNTIME",
            "native_inference_pending" => "Brain: native files ready · inference wiring pending",
            "unsupported_provider" => $"Brain: unsupported provider · {brain.Provider}",
            _ => $"Brain: {brain.State} · auto-retry on · click to recheck",
        };
    }

    private static string BuildTooltip(BrainStatus brain)
    {
        var detail = Limit(brain.Detail);
        var guidance = brain.State switch
        {
            "unavailable" => "Start Ollama, then BabyAI will recheck automatically every 15 seconds. Click to recheck now.",
            "model_missing" => $"Install the configured model manually with: ollama pull {brain.Model}. BabyAI will recheck automatically every 15 seconds.",
            "native_model_missing" => "Point BABYAI_NATIVE_MODEL at a local GGUF file (or place babyai.gguf in ~/.babyai/models). BabyAI will recheck automatically.",
            "native_runtime_missing" => "Point BABYAI_NATIVE_RUNTIME at the BabyAI native runtime library. BabyAI will recheck automatically.",
            "native_inference_pending" => "The GGUF model and BabyAI native runtime are present. Generation stays disabled until the in-process inference path is implemented and tested.",
            "unsupported_provider" => "Choose a supported provider in the BabyAI launcher/configuration.",
            _ when brain.Ready => "Click to recheck.",
            _ => "BabyAI rechecks automatically every 15 seconds while unavailable. Click to recheck now.",
        };
        return string.IsNullOrWhiteSpace(detail)
            ? $"Provider: {brain.Provider}; model: {brain.Model}; state: {brain.State}. {guidance}"
            : $"{detail} {guidance}";
    }

    private static string Limit(string value)
    {
        value = value.Trim();
        return value.Length <= 240 ? value : value[..240] + "…";
    }
}
