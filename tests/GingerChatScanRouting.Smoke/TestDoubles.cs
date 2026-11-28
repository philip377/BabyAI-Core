using System.Text.Json;

// Link the production orchestration, flow, evidence writer, deferred store and
// review models verbatim. Replace only external UI/bridge/session boundaries.
// Production data-directory lookups fail unless a fixture supplies a temp root.
namespace Microsoft.UI.Xaml
{
    public enum Visibility { Visible, Collapsed }
    public readonly record struct Duration(TimeSpan Value);
}
namespace Microsoft.UI.Xaml.Media.Animation
{
    public readonly struct RepeatBehavior { public static RepeatBehavior Forever => new(); }
    public sealed class DoubleAnimation
    {
        public double From { get; set; }
        public double To { get; set; }
        public bool AutoReverse { get; set; }
        public Microsoft.UI.Xaml.Duration Duration { get; set; }
        public TimeSpan BeginTime { get; set; }
        public RepeatBehavior RepeatBehavior { get; set; }
    }
    public sealed class Storyboard
    {
        public List<DoubleAnimation> Children { get; } = [];
        public static void SetTarget(DoubleAnimation animation, object target) { }
        public static void SetTargetProperty(DoubleAnimation animation, string property) { }
        public void Begin() => throw new InvalidOperationException("Native animation is unavailable in this smoke test.");
        public void Stop() { }
    }
}
namespace Windows.UI.ViewManagement
{
    public sealed class UISettings { public bool AnimationsEnabled => false; }
}

namespace BabyAI.Desktop
{
    internal static class Environment
    {
        internal enum SpecialFolder { LocalApplicationData }
        internal static string? SyntheticRoot { get; set; }
        internal static string GetFolderPath(SpecialFolder folder) =>
            folder == SpecialFolder.LocalApplicationData && SyntheticRoot is not null ? SyntheticRoot
                : throw new InvalidOperationException("Real user directories are unavailable in this smoke test.");
    }
    internal static class StartupDiagnostics
    {
        internal static readonly List<(string Message, Exception Error)> Errors = [];
        internal static void Log(string message, Exception error) => Errors.Add((message, error));
    }
    internal sealed class FakeControl
    {
        internal string Text { get; set; } = "";
        internal Microsoft.UI.Xaml.Visibility Visibility { get; set; } = Microsoft.UI.Xaml.Visibility.Collapsed;
    }
    internal enum OrbState { Idle, Thinking, Executing, Error, Done }
    internal enum GingerCompanionPhase { Cancelled, Failed, Completed, Reviewing, Checking, Reading }
    internal enum GingerChatTaskState { Failed, Cancelled, TimedOut, Review, PairReady, Matched, End, AlreadyLinked, Rejected }
    internal sealed record GingerDesktopObservation(string Supplier, string Document, int Position, int Count,
        string Title, bool Linked, string[] Candidates, string Directory)
    {
        internal JsonElement Data => JsonSerializer.SerializeToElement(new { process_id = 1234L });
        internal JsonElement Source => JsonSerializer.SerializeToElement(new { TableHandle = 5678L });
    }
    internal sealed class Scenario
    {
        internal static Scenario? Current;
        internal readonly List<GingerDesktopObservation> Rows = [];
        internal readonly List<int> NextPositions = [];
        internal readonly List<GingerDesktopObservation> FilterInputs = [];
        internal readonly Queue<Func<GingerDesktopObservation, GingerDesktopObservation>> FilterReplies = [];
        internal readonly List<string> Journal = [];
        internal Action<GingerDesktopObservation>? BeforeNext;
        internal int StartReads, FilterReads, Disposals;
        internal GingerDeferredRow? Revisit;
        internal GingerDesktopSession? Session;
    }
    internal sealed class GingerDesktopSession : IDisposable
    {
        private readonly Scenario _scenario = Scenario.Current ?? throw new InvalidOperationException("Missing synthetic scenario");
        internal Action<string>? FilterProgress { get; set; }
        internal int? FilterCandidateTarget { get; init; }
        internal string DirectoryPath => Path.GetDirectoryName(_scenario.Rows[0].Directory)!;
        internal GingerDesktopSession() => _scenario.Session = this;
        internal void Record(string text) => _scenario.Journal.Add(text);
        internal Task<GingerDesktopObservation> PrepareFiltersAsync(GingerDesktopObservation observation, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            _scenario.FilterReads++;
            _scenario.FilterInputs.Add(observation);
            return Task.FromResult(_scenario.FilterReplies.Count == 0 ? observation : _scenario.FilterReplies.Dequeue()(observation));
        }
        internal Task<GingerDesktopObservation> ObserveAsync(CancellationToken token,
            GingerDesktopObservation? before = null, int? position = null, bool prepareFiltersOnOverflow = false)
        {
            token.ThrowIfCancellationRequested();
            if (before is null || position is null)
                throw new InvalidOperationException("Unexpected fresh/empty read in this nonempty-candidate test");
            _scenario.NextPositions.Add(position.Value);
            if (_scenario.NextPositions.Count > 3) throw new InvalidOperationException("Unbounded scan");
            _scenario.BeforeNext?.Invoke(before);
            return Task.FromResult(_scenario.Rows.Single(row => row.Position == position));
        }
        public void Dispose() => _scenario.Disposals++;
    }
    internal sealed class FakeBridge
    {
        internal readonly Queue<Func<JsonElement>> Replies = [];
        internal readonly List<(string Supplier, string Source, string Lesson, string[] Candidates)> Comparisons = [];
        internal readonly List<(string ChatId, string Message, string Reply)> History = [];
        internal Task<JsonElement> NavigationAsync(string command) => command == "chat.current"
            ? Task.FromResult(JsonSerializer.SerializeToElement(new { active_chat_id = "chat-current" }))
            : throw new InvalidOperationException("Unexpected navigation: " + command);
        internal Task<JsonElement> PreviewGingerLessonAsync(string supplier, string source, string lesson,
            IEnumerable<string> candidates, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Comparisons.Add((supplier, source, lesson, candidates.ToArray()));
            return Task.FromResult(Replies.Dequeue()());
        }
        internal Task<JsonElement> PreviewGingerIntentAsync(string message, CancellationToken token) =>
            throw new InvalidOperationException("Intent model is unavailable in this scan test");
        internal Task RecordGingerChatAsync(string chatId, string message, string reply)
        {
            History.Add((chatId, message, reply));
            return Task.CompletedTask;
        }
    }
    internal sealed class GingerProgressTurn
    {
        internal readonly List<(GingerChatStage Stage, string Text)> Stages = [];
        internal string Summary = "", Details = "";
        internal bool Finished;
        internal string Transcript => Summary + "\n" + Details;
        internal void Report(GingerChatStage stage, string text) => Stages.Add((stage, text));
        internal void Finish(string summary, string details, string? failureStatus)
        { Summary = summary; Details = details; Finished = true; }
    }
    internal sealed record FakeConfirmation(GingerDesktopSession Session, GingerDesktopObservation Observation,
        string ChatId, string Candidate);

    public sealed partial class MainWindow
    {
        private bool _busy, _gingerRunning;
        private bool _gingerDialogOpen = false, _agentsDialogOpen = false;
        private CancellationTokenSource? _chatCancellation, _gingerCancellation;
        private int _chatGeneration;
        private string? _selectedChatId = "chat-current";
        private readonly FakeBridge _bridge;
        private readonly FakeControl ApprovalCard = new(), MessageBox = new(), ReplyText = new(), CoreStatusText = new();
        private readonly FakeControl GingerChatStageText = new(), GingerChatActivity = new();
        private readonly FakeControl GingerStepDot1 = new(), GingerStepDot2 = new(), GingerStepDot3 = new();
        private FakeConfirmation? _gingerChatConfirmation;
        internal GingerChatAnswerReview? ShownReview;
        internal GingerChatLessonReview? ShownLesson;
        internal readonly List<GingerChatAnswerReview> ReviewShows = [];
        internal readonly List<GingerChatLessonReview> LessonShows = [];
        internal readonly List<FakeConfirmation> ConfirmationOffers = [];
        internal readonly List<(GingerChatTaskState State, int Deferred)> Outcomes = [];
        internal readonly List<GingerNotificationKind> Notifications = [];
        internal readonly List<(string Role, string Text)> Conversation = [];
        internal GingerProgressTurn Progress = new();
        internal bool CaptureMode;
        internal string? ResearchSource;
        internal bool Busy => _busy;
        internal bool Running => _gingerRunning;
        internal bool HasCancellation => _chatCancellation is not null || _gingerCancellation is not null;
        internal FakeConfirmation? Confirmation => _gingerChatConfirmation;
        internal MainWindow(FakeBridge bridge) => _bridge = bridge;
        internal Task RunAsync(GingerDeferredRow? revisit = null) => RunGingerFromChatAsync(
            "Продолжи сопоставление", expectedChatId: "chat-current", revisit: revisit);
        internal void SeedOldCards(GingerChatAnswerReview review, GingerChatLessonReview lesson)
        {
            ShownReview = review; ShownLesson = lesson;
            _gingerChatConfirmation = new(null!, Scenario.Current!.Rows[0], "old-chat", "Old candidate");
        }
        private void InvalidateGingerChatContext()
        {
            ShownReview?.Invalidate(); ShownLesson?.Invalidate();
            ShownReview = null; ShownLesson = null; _gingerChatConfirmation = null;
        }
        private void StopVoiceListening(bool updateUi) { }
        private void SetBusy(bool busy, bool canStop = false) => _busy = busy;
        private void AppendConversation(string role, string text) => Conversation.Add((role, text));
        private void ApplyState(OrbState state) { }
        private void SetGingerPhase(GingerCompanionPhase phase) { }
        private void SaveGingerLogLine(string text) { }
        private void SetGingerCaptureMode(bool enabled) => CaptureMode = enabled;
        private GingerProgressTurn CreateGingerProgressTurn() => Progress;
        private Task<bool> TryRouteGingerFollowUpAsync(string message) => Task.FromResult(false);
        private void ClearGingerFollowUp() { }
        private Task<GingerDesktopObservation> ReadGingerChatStartAsync(GingerDesktopSession session,
            GingerDeferredRow? revisit, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var scenario = Scenario.Current!;
            scenario.StartReads++;
            scenario.Revisit = revisit;
            return Task.FromResult(scenario.Rows[0]);
        }
        private void ShowBottleResearch(string chatId, string source) => ResearchSource = source;
        private void ShowGingerChatReview(GingerChatAnswerReview review)
        { ShownReview = review; ReviewShows.Add(review); }
        private void ShowGingerChatLesson(GingerChatLessonReview lesson)
        { ShownLesson = lesson; LessonShows.Add(lesson); }
        private string OfferGingerChatMatch(GingerDesktopSession session, GingerDesktopObservation observation,
            string chatId, string candidate)
        {
            _gingerChatConfirmation = new(session, observation, chatId, candidate);
            ConfirmationOffers.Add(_gingerChatConfirmation);
            return "Synthetic confirmation offered; no Ginger action or permission granted.";
        }
        private void RecordGingerTaskResult(string? chatId, string runId, DateTimeOffset started,
            GingerChatTaskState state, int deferred, string summary) => Outcomes.Add((state, deferred));
        private Task RefreshGingerDeferredRowsAsync() => Task.CompletedTask;
        private void NotifyGingerOutcome(GingerNotificationKind kind, string? chatId) => Notifications.Add(kind);
    }
}
