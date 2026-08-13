namespace BabyAI.Desktop;

public sealed partial class MainWindow
{
    internal void ApplyBrainReadinessFromIndicator(BrainStatus brain)
    {
        StartupText.Text = FormatStartupReadiness(brain);

        if (_state is OrbState.Thinking or OrbState.Listening or OrbState.Approval)
            return;

        ApplyState(brain.Ready ? OrbState.Idle : OrbState.Error);
    }

    internal void ApplyStartupFailureFromIndicator()
    {
        StartupText.Text = "Startup: Core ✕ · Ollama ? · Model ?";
    }

    private static string FormatStartupReadiness(BrainStatus brain)
    {
        if (brain.Provider.Equals("echo", StringComparison.OrdinalIgnoreCase))
            return brain.Ready
                ? "Startup: Core ✓ · Brain echo ✓"
                : "Startup: Core ✓ · Brain echo ✕";

        if (!brain.Provider.Equals("ollama", StringComparison.OrdinalIgnoreCase))
            return $"Startup: Core ✓ · Provider {brain.Provider} ✕";

        return brain.State switch
        {
            "ready" => $"Startup: Core ✓ · Ollama ✓ · Model {brain.Model} ✓",
            "unavailable" => $"Startup: Core ✓ · Ollama ✕ · Model {brain.Model} ?",
            "model_missing" => $"Startup: Core ✓ · Ollama ✓ · Model {brain.Model} ✕",
            _ when brain.Ready => $"Startup: Core ✓ · Ollama ✓ · Model {brain.Model} ✓",
            _ => $"Startup: Core ✓ · Ollama ? · Model {brain.Model} ?",
        };
    }
}
