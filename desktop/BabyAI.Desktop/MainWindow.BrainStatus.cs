namespace BabyAI.Desktop;

public sealed partial class MainWindow
{
    internal void ApplyBrainReadinessFromIndicator(BrainStatus brain)
    {
        if (_state is OrbState.Thinking or OrbState.Listening or OrbState.Approval)
            return;

        ApplyState(brain.Ready ? OrbState.Idle : OrbState.Error);
    }
}
