namespace BabyAI.Desktop;

public sealed partial class MainWindow
{
    internal void AttachRuntimeBehaviors()
    {
        TryAttachBehavior(
            "BrainStatusBehavior",
            () => BrainStatusBehavior.SetEnabled(BrainText, true));

        TryAttachBehavior(
            "ReplyActivityBehavior",
            () => ReplyActivityBehavior.SetSource(ReplyActivityIndicator, ReplyText));

        TryAttachBehavior(
            "ConversationTranscriptBehavior",
            () => ConversationTranscriptBehavior.SetSource(ConversationStack, ConversationText));
    }

    private static void TryAttachBehavior(string name, Action attach)
    {
        try
        {
            attach();
            StartupDiagnostics.Log($"{name} attached from code");
        }
        catch (Exception ex)
        {
            StartupDiagnostics.Log($"{name} attach failed; continuing without it", ex);
        }
    }
}
