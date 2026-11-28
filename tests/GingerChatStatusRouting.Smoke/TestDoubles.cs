using System.Text.Json;

// These three production files are linked verbatim. Only their external boundary
// types are replaced: controls, bridge and the OS data-directory lookup. In
// particular, no test can read/write the real LocalAppData path: an unconfigured
// Environment stub throws, and the real System.Environment is never consulted.
namespace Microsoft.UI.Xaml
{
    public enum Visibility { Visible, Collapsed }
}

namespace BabyAI.Desktop
{
    internal static class Environment
    {
        internal enum SpecialFolder { LocalApplicationData }
        internal static string? SyntheticRoot { get; set; }
        internal static int FolderRequests { get; set; }
        internal static string GetFolderPath(SpecialFolder folder)
        {
            if (folder != SpecialFolder.LocalApplicationData || SyntheticRoot is null)
                throw new InvalidOperationException("Real user directories are unavailable in this smoke test.");
            FolderRequests++;
            return SyntheticRoot;
        }
    }

    internal static class StartupDiagnostics
    {
        internal static readonly List<(string Message, Exception Error)> Errors = [];
        internal static void Log(string message, Exception error) => Errors.Add((message, error));
    }

    internal sealed class FakeControl
    {
        internal string Text { get; set; } = "unchanged";
        internal Microsoft.UI.Xaml.Visibility Visibility { get; set; } = Microsoft.UI.Xaml.Visibility.Collapsed;
    }

    internal sealed class FakeBridge
    {
        internal readonly Queue<Func<Task<JsonElement>>> NavigationReplies = [];
        internal readonly List<string> NavigationCalls = [];
        internal readonly List<(string ChatId, string Message, string Reply)> HistoryCalls = [];
        internal string ActiveChatId = "chat-a";
        internal Exception? HistoryFailure;
        internal int ModelFallbackCalls;

        internal Task<JsonElement> NavigationAsync(string command)
        {
            NavigationCalls.Add(command);
            if (command != "chat.current") throw new Exception("Unexpected bridge command: " + command);
            if (NavigationCalls.Count > 20) throw new Exception("Unbounded navigation loop");
            return NavigationReplies.Count > 0 ? NavigationReplies.Dequeue()() : Task.FromResult(Current(ActiveChatId));
        }
        internal Task RecordGingerChatAsync(string chatId, string message, string reply)
        {
            HistoryCalls.Add((chatId, message, reply));
            return HistoryFailure is null ? Task.CompletedTask : Task.FromException(HistoryFailure);
        }
        internal static JsonElement Current(string? chatId) => JsonSerializer.SerializeToElement(new { active_chat_id = chatId });
    }

    internal sealed class PausedNavigation
    {
        private readonly TaskCompletionSource<JsonElement> _reply = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Task Started => _started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        internal Task<JsonElement> Invoke()
        {
            _started.SetResult();
            return _reply.Task;
        }
        internal void Complete(string chatId = "chat-a") => _reply.SetResult(FakeBridge.Current(chatId));
    }

    public sealed partial class MainWindow
    {
        private string? _selectedChatId = "chat-a";
        private bool _busy, _gingerRunning, _gingerDialogOpen, _agentsDialogOpen;
        private CancellationTokenSource? _chatCancellation;
        private int _chatGeneration;
        private readonly FakeBridge _bridge;
        private readonly FakeControl ApprovalCard = new();
        private readonly FakeControl MessageBox = new();
        private readonly FakeControl ReplyText = new();
        // A status result must not create or replace a live confirmation. This
        // sentinel models the existing matching boundary without linking its UI.
        private object? _gingerChatConfirmation;
        internal readonly List<(string Role, string Text)> Conversation = [];
        internal readonly List<(string Message, string? ExpectedChatId)> FreshRuns = [];
        internal readonly List<(bool Busy, bool CanStop)> BusyChanges = [];
        internal int VoiceStops { get; private set; }
        internal string? Focus => _gingerFollowupChatId;
        internal bool HasTaskFocus => _gingerFollowupHasTask;
        internal string Reply => ReplyText.Text;
        internal bool Busy => _busy;
        internal bool HasCancellation => _chatCancellation is not null;
        internal int MemoryCount => _gingerTaskResults.Count;
        internal object? Confirmation => _gingerChatConfirmation;
        internal Microsoft.UI.Xaml.Visibility ApprovalVisibility => ApprovalCard.Visibility;

        internal MainWindow(FakeBridge bridge) => _bridge = bridge;
        internal void SeedMemory(GingerChatTaskResult result, bool focus = true)
        {
            _gingerTaskResults[result.ChatId] = result;
            if (focus) FocusChat(result.ChatId);
        }
        internal void FocusChat(string? chatId, bool hasTask = true)
        {
            _gingerFollowupChatId = chatId;
            _gingerFollowupHasTask = chatId is not null && hasTask;
        }
        internal void ClearFocus() => ClearGingerFollowUp();
        internal void Stop() => _chatCancellation?.Cancel();
        internal void AdvanceGeneration() => Interlocked.Increment(ref _chatGeneration);
        internal void SelectChat(string chatId) => _selectedChatId = chatId;
        internal void SetConfirmation(object? confirmation) => _gingerChatConfirmation = confirmation;
        internal void Block(string blocker)
        {
            switch (blocker)
            {
                case "busy": _busy = true; break;
                case "ginger": _gingerRunning = true; break;
                case "ginger-dialog": _gingerDialogOpen = true; break;
                case "agent-dialog": _agentsDialogOpen = true; break;
                case "approval": ApprovalCard.Visibility = Microsoft.UI.Xaml.Visibility.Visible; break;
                default: throw new ArgumentException("Unknown blocker");
            }
        }
        internal void RecordActualResult(GingerChatTaskResult result) => RecordGingerTaskResult(result.ChatId,
            result.RunId, result.StartedAt, result.State, result.DeferredCount, result.Summary);

        // Calls the actual private production router. The fallback counter is an
        // upstream sentinel, not an implementation of the route under test.
        internal async Task<bool> DispatchAsync(string message)
        {
            var consumed = await TryRouteGingerFollowUpAsync(message);
            if (!consumed) _bridge.ModelFallbackCalls++;
            return consumed;
        }
        private void StopVoiceListening(bool updateUi) => VoiceStops++;
        private void SetBusy(bool busy, bool canStop = false)
        {
            _busy = busy;
            BusyChanges.Add((busy, canStop));
        }
        private void AppendConversation(string role, string text) => Conversation.Add((role, text));
        private Task RunGingerFromChatAsync(string message, string? expectedChatId = null)
        {
            if (_busy) throw new Exception("Resume dispatched before releasing the status read");
            FreshRuns.Add((message, expectedChatId));
            return Task.CompletedTask;
        }
    }
}
