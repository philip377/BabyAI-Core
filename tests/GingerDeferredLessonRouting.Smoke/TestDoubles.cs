using System.Text.Json;

// The production deferred dialog and lesson factory are linked verbatim. Every
// external boundary is synthetic: controls, store, lessons, bridge and driver.
// No default OS data directory is read, and every driver operation throws.
namespace Microsoft.UI.Xaml
{
    public enum Visibility { Visible, Collapsed }
    public enum TextWrapping { NoWrap, Wrap }
    public sealed class RoutedEventArgs : EventArgs { }
    public class FrameworkElement
    {
        public Visibility Visibility { get; set; } = Visibility.Visible;
        public object XamlRoot { get; set; } = new();
        public double Height { get; set; }
        public double MaxHeight { get; set; }
        public double MaxWidth { get; set; }
        public bool IsEnabled { get; set; } = true;
    }
}

namespace Microsoft.UI.Xaml.Automation
{
    public static class AutomationProperties
    {
        public static void SetName(object control, string name) { }
    }
}

namespace Microsoft.UI.Xaml.Controls
{
    using Microsoft.UI.Xaml;
    public enum ListViewSelectionMode { Single }
    public enum ScrollBarVisibility { Disabled, Auto }
    public enum ContentDialogButton { None, Primary, Secondary, Close }
    public enum ContentDialogResult { None, Primary, Secondary }
    public sealed class SelectionChangedEventArgs : EventArgs { }
    public sealed class ContentDialogButtonClickEventArgs : EventArgs { public bool Cancel { get; set; } }
    public sealed class TextBlock : FrameworkElement
    {
        public string Text { get; set; } = "";
        public TextWrapping TextWrapping { get; set; }
        public bool IsTextSelectionEnabled { get; set; }
    }
    public sealed class StackPanel : FrameworkElement
    {
        public double Spacing { get; set; }
        public List<object> Children { get; } = [];
    }
    public sealed class ScrollViewer : FrameworkElement
    {
        public object? Content { get; set; }
        public ScrollBarVisibility HorizontalScrollBarVisibility { get; set; }
        public ScrollBarVisibility VerticalScrollBarVisibility { get; set; }
    }
    public sealed class ListView : FrameworkElement
    {
        public object? ItemsSource { get; set; }
        public string DisplayMemberPath { get; set; } = "";
        public ListViewSelectionMode SelectionMode { get; set; }
        public bool IsItemClickEnabled { get; set; }
        private object? _selected;
        public object? SelectedItem
        {
            get => _selected;
            set { _selected = value; SelectionChanged?.Invoke(this, new()); }
        }
        public event EventHandler<SelectionChangedEventArgs>? SelectionChanged;
    }
    public static class ToolTipService { public static void SetToolTip(object control, string text) { } }
    public sealed class ContentDialog : FrameworkElement
    {
        public object? Title { get; set; }
        public object? Content { get; set; }
        public string PrimaryButtonText { get; set; } = "";
        public string SecondaryButtonText { get; set; } = "";
        public string CloseButtonText { get; set; } = "";
        public bool IsPrimaryButtonEnabled { get; set; }
        public bool IsSecondaryButtonEnabled { get; set; }
        public ContentDialogButton DefaultButton { get; set; }
        public event EventHandler<ContentDialogButtonClickEventArgs>? PrimaryButtonClick;
        public event EventHandler<ContentDialogButtonClickEventArgs>? SecondaryButtonClick;
        internal static Func<ContentDialog, Task<ContentDialogResult>>? Show;
        internal static int Shows;
        internal ListView List => ((StackPanel)Content!).Children.OfType<ListView>().Single();
        internal string Status => ((StackPanel)Content!).Children.OfType<TextBlock>().Last().Text;
        public Task<ContentDialogResult> ShowAsync()
        {
            Shows++;
            return (Show ?? throw new Exception("Unconfigured synthetic dialog"))(this);
        }
        internal ContentDialogResult Click(ContentDialogResult button, bool bypassEnabled = false)
        {
            if (!bypassEnabled && (button == ContentDialogResult.Primary ? !IsPrimaryButtonEnabled : !IsSecondaryButtonEnabled))
                return ContentDialogResult.None;
            var args = new ContentDialogButtonClickEventArgs();
            if (button == ContentDialogResult.Primary) PrimaryButtonClick?.Invoke(this, args);
            else if (button == ContentDialogResult.Secondary) SecondaryButtonClick?.Invoke(this, args);
            return args.Cancel ? ContentDialogResult.None : button;
        }
    }
}

namespace BabyAI.Desktop
{
    using Microsoft.UI.Xaml;
    using Microsoft.UI.Xaml.Controls;

    internal static class StartupDiagnostics
    {
        internal static readonly List<(string Message, Exception Error)> Errors = [];
        internal static void Log(string message, Exception error) => Errors.Add((message, error));
    }
    internal sealed record GingerDeferredRow(string Id, string ChatId, string Supplier, string Document,
        int Position, string Title, string Reason, string ObservationDirectory, DateTimeOffset CreatedAt);
    internal sealed record GingerChatItem(string Supplier, string Document, int Position, string Title,
        bool Linked, string[] Candidates);
    internal sealed class GingerDeferredRows
    {
        internal static Func<IReadOnlyList<GingerDeferredRow>>? ReadRows;
        internal static int Reads;
        internal IReadOnlyList<GingerDeferredRow> Read()
        {
            Interlocked.Increment(ref Reads);
            return (ReadRows ?? throw new Exception("No in-memory deferred fixture configured"))();
        }
    }
    internal sealed class GingerLearning
    {
        internal sealed record LanguageLesson(string Supplier, string Text, string Understanding, bool Enabled,
            DateTimeOffset RecordedAt, string LessonId = "legacy");
        internal static Func<string, IReadOnlyList<LanguageLesson>>? ReadLessons;
        internal static readonly List<string> ReadSuppliers = [];
        internal IReadOnlyList<LanguageLesson> ReadLanguageLessons(string supplier)
        {
            ReadSuppliers.Add(supplier);
            return (ReadLessons ?? throw new Exception("No in-memory lesson fixture configured"))(supplier);
        }
        internal static string ComposeLanguageLessons(IEnumerable<LanguageLesson> lessons) => string.Join("\n", lessons.Select(x => x.Text));
        internal void SaveLanguageLessonIfUnchanged(LanguageLesson lesson, IReadOnlyList<LanguageLesson> expected)
            => throw new Exception("Opening a deferred lesson must never save or revoke a lesson");
    }
    internal sealed class FakeBridge
    {
        internal readonly Queue<Func<Task<JsonElement>>> Replies = [];
        internal readonly List<string> Calls = [];
        internal string Chat = "current-chat";
        internal Task<JsonElement> NavigationAsync(string command)
        {
            Calls.Add(command);
            if (command != "chat.current") throw new Exception("Unexpected bridge action: " + command);
            return Replies.Count == 0 ? Task.FromResult(Current(Chat)) : Replies.Dequeue()();
        }
        internal static JsonElement Current(string chat) => JsonSerializer.SerializeToElement(new { active_chat_id = chat });
    }
    internal sealed class GingerDesktopObservation
    {
        internal string Supplier => throw new Exception("Driver observation unavailable");
        internal string Document => throw new Exception("Driver observation unavailable");
        internal int Position => throw new Exception("Driver observation unavailable");
        internal string Title => throw new Exception("Driver observation unavailable");
        internal bool Linked => throw new Exception("Driver observation unavailable");
        internal string[] Candidates => throw new Exception("Driver observation unavailable");
        internal JsonElement Data => throw new Exception("Driver observation unavailable");
        internal JsonElement Source => throw new Exception("Driver observation unavailable");
        internal string Directory => throw new Exception("Driver observation unavailable");
        internal int Count => throw new Exception("Driver observation unavailable");
    }
    internal sealed class GingerDesktopSession
    {
        internal static int Calls;
        internal Task<GingerDesktopObservation> ObserveAsync(CancellationToken token, GingerDesktopObservation? previous = null,
            int position = 0, bool prepare = false, bool prepareFiltersOnOverflow = false)
        {
            Calls++;
            throw new Exception("Dialog routing must not invoke the Ginger driver");
        }
    }
    internal static class GingerDeferredSelection
    {
        internal static void ValidateBeforeSelection(GingerDeferredRow row, GingerChatItem item, JsonElement before,
            long process, long table, int count) => throw new Exception("Unexpected fresh selection validation");
        internal static void ValidateSelected(GingerDeferredRow row, GingerChatItem item) => throw new Exception("Unexpected driver selection");
    }

    public sealed partial class MainWindow
    {
        private bool _exitRequested, _busy, _gingerRunning, _gingerDialogOpen, _agentsDialogOpen;
        private string? _selectedChatId = "current-chat";
        private int _chatGeneration;
        private object? _gingerChatConfirmation;
        private readonly FrameworkElement Root = new();
        private readonly FrameworkElement GingerDeferredButton = new();
        private readonly TextBlock GingerDeferredCount = new();
        private readonly TextBlock ReplyText = new();
        private readonly FrameworkElement ApprovalCard = new() { Visibility = Visibility.Collapsed };
        private readonly FrameworkElement GingerChatConfirmationCard = new() { Visibility = Visibility.Collapsed };
        private readonly FakeBridge _bridge = new();
        internal FakeBridge Bridge => _bridge;
        internal readonly List<(string Message, string? ChatId, GingerDeferredRow? Row)> FreshRuns = [];
        internal readonly List<GingerChatLessonReview> ShownLessons = [];
        internal readonly List<(string Role, string Text)> Conversation = [];
        internal readonly List<string> Invalidations = [];
        internal Action<bool>? BusyChanged;
        internal bool Busy => _busy;
        internal bool DialogOpen => _gingerDialogOpen;
        internal string Reply => ReplyText.Text;
        internal object? Confirmation => _gingerChatConfirmation;
        internal bool Approval => ApprovalCard.Visibility == Visibility.Visible;
        internal void ChangeChat(string chat) => _selectedChatId = chat;
        internal void ChangeGeneration() => _chatGeneration++;
        internal void Block(string blocker)
        {
            switch (blocker)
            {
                case "exit": _exitRequested = true; break;
                case "busy": _busy = true; break;
                case "ginger": _gingerRunning = true; break;
                case "ginger-dialog": _gingerDialogOpen = true; break;
                case "agents-dialog": _agentsDialogOpen = true; break;
                case "approval": ApprovalCard.Visibility = Visibility.Visible; break;
                case "confirmation": _gingerChatConfirmation = new object(); break;
                case "confirmation-card": GingerChatConfirmationCard.Visibility = Visibility.Visible; break;
                default: throw new Exception("Unknown blocker: " + blocker);
            }
        }
        internal Task OpenDeferredAsync() => AsyncVoidCompletion.Run(() => GingerDeferredButton_Click(this, new()));
        internal Task RefreshDeferredAsync() => RefreshGingerDeferredRowsAsync();
        private void SetBusy(bool busy, bool canStop = false) { _busy = busy; BusyChanged?.Invoke(busy); }
        private Task RunGingerFromChatAsync(string message, string? expectedChatId = null, GingerDeferredRow? revisit = null)
        {
            if (_busy || _gingerDialogOpen) throw new Exception("Revisit dispatched before dialog reservation released");
            FreshRuns.Add((message, expectedChatId, revisit));
            return Task.CompletedTask;
        }
        private void ShowGingerChatLesson(GingerChatLessonReview lesson) => ShownLessons.Add(lesson);
        private void InvalidateGingerChatMatch() => Invalidations.Add("match");
        private void InvalidateGingerChatReview() => Invalidations.Add("review");
        private void InvalidateGingerChatLesson() => Invalidations.Add("lesson");
        private void ClearGingerFollowUp() => Invalidations.Add("followup");
        private void InvalidateGingerChatContext() => Invalidations.Add("context");
        private void StopVoiceListening(bool updateUi) => Invalidations.Add("voice");
        private void ShowGingerChatStep(string message, bool running) { }
        private void AppendConversation(string role, string text) => Conversation.Add((role, text));
    }

    // Observe completion of the actual async-void click handler, including its
    // posted exceptions. No polling, UI thread, window, dispatcher or native API.
    internal sealed class AsyncVoidCompletion : SynchronizationContext
    {
        private readonly TaskCompletionSource _done = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _operations, _posted, _returned;
        private Exception? _error;
        internal static Task Run(Action action)
        {
            var scope = new AsyncVoidCompletion();
            var previous = Current;
            SetSynchronizationContext(scope);
            try { action(); }
            catch (Exception error) { scope._error = error; }
            finally { SetSynchronizationContext(previous); Volatile.Write(ref scope._returned, 1); scope.TryComplete(); }
            return scope._done.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }
        public override void OperationStarted() => Interlocked.Increment(ref _operations);
        public override void OperationCompleted() { Interlocked.Decrement(ref _operations); TryComplete(); }
        public override void Post(SendOrPostCallback callback, object? state)
        {
            Interlocked.Increment(ref _posted);
            ThreadPool.QueueUserWorkItem(_ =>
            {
                var previous = Current;
                SetSynchronizationContext(this);
                try { callback(state); }
                catch (Exception error) { _error = error; }
                finally { SetSynchronizationContext(previous); Interlocked.Decrement(ref _posted); TryComplete(); }
            });
        }
        private void TryComplete()
        {
            if (Volatile.Read(ref _returned) == 0 || Volatile.Read(ref _operations) != 0 || Volatile.Read(ref _posted) != 0) return;
            if (_error is { } error) _done.TrySetException(error); else _done.TrySetResult();
        }
    }
}
