using BabyAI.Desktop;
using System.Security.Cryptography;
using System.Text;
using TestEnvironment = BabyAI.Desktop.Environment;

await RoutingTests.RunAsync();

internal static class RoutingTests
{
    private static int _checks;
    private static GingerChatTaskResult Result(GingerChatTaskState state = GingerChatTaskState.Failed,
        string summary = "Модель утверждала: всё успешно сопоставлено.") => new("chat-a", Guid.NewGuid().ToString("N"),
        new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero),
        new DateTimeOffset(2026, 1, 1, 12, 1, 0, TimeSpan.Zero), state, 2, summary);

    internal static async Task RunAsync()
    {
        await TypedFailureUsesActualRouter();
        await ExactResumeAndClarification();
        await StopAndStaleBridgeReplies();
        await ResolutionFailuresDoNotRestoreFocus();
        await CorruptDiskStaysConsumed();
        await HistoricResultIsNotConfirmation();
        await MissingResultFollowUpIsBounded();
        await ActualRecordKeepsFailedOutcomeWhenSaveFails();
        await ExistingGuardsAndHistoryFailure();
        Check(TestEnvironment.SyntheticRoot is null, "Synthetic directory override leaked outside fixture");
        Console.WriteLine($"PASS: {_checks} status-routing integration checks against linked production router/store/classifier; fake UI/bridge, isolated temp data only");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
        _checks++;
    }
    private static Task<bool> Dispatch(MainWindow window, string text) => window.DispatchAsync(text).WaitAsync(TimeSpan.FromSeconds(5));
    private static void NoNewWork(Fixture fixture)
    {
        Check(fixture.Window.FreshRuns.Count == 0, "Status route dispatched Ginger/driver work");
        Check(fixture.Bridge.ModelFallbackCalls == 0, "Status/failure fell through to model");
    }
    private static void Released(MainWindow window)
    {
        Check(!window.Busy && !window.HasCancellation, "Status route retained busy/cancellation state");
    }

    private static async Task TypedFailureUsesActualRouter()
    {
        using var fixture = new Fixture();
        fixture.Window.SeedMemory(Result());
        Check(await Dispatch(fixture.Window, "ну как?"), "Focused short status not consumed");
        Check(fixture.Window.Reply.Contains("из-за ошибки") && !fixture.Window.Reply.Contains("всё успешно"), "Model prose overrode actual typed Failed state");
        Check(fixture.Window.Reply.Contains("Новая проверка Ginger не запускалась"), "Status implied live execution");
        Check(fixture.Bridge.NavigationCalls.SequenceEqual(new[] { "chat.current", "chat.current" }), "Status issued unexpected bridge calls");
        Check(fixture.Bridge.HistoryCalls.Count == 1 && fixture.Bridge.HistoryCalls[0].ChatId == "chat-a", "Status history lost chat binding");
        Check(fixture.Window.Focus == "chat-a" && fixture.Window.Conversation.Count == 2, "Successful status lost conversation focus");
        Check(TestEnvironment.FolderRequests == 0, "Memory outcome unexpectedly read disk");
        NoNewWork(fixture);
        Released(fixture.Window);
    }

    private static async Task ExactResumeAndClarification()
    {
        using (var fixture = new Fixture())
        {
            fixture.Window.SeedMemory(Result());
            const string original = "  ПРОДОЛЖИ?!  ";
            Check(await Dispatch(fixture.Window, original), "Exact resume not consumed");
            Check(fixture.Window.FreshRuns.SequenceEqual(new[] { (original, (string?)"chat-a") }), "Resume changed original message, lost expected chat, or ran more than once");
            Check(fixture.Bridge.NavigationCalls.Count == 2 && fixture.Bridge.HistoryCalls.Count == 0 && fixture.Window.Conversation.Count == 0, "Resume replayed status/history actions");
            Check(fixture.Bridge.ModelFallbackCalls == 0 && TestEnvironment.FolderRequests == 0, "Exact resume invoked model/store");
            Released(fixture.Window);
        }
        using (var fixture = new Fixture())
        {
            fixture.Window.FocusChat("chat-a");
            Check(await Dispatch(fixture.Window, "сопоставление"), "Focused vague follow-up not consumed");
            Check(fixture.Window.Reply.Contains("свежего чтения") && fixture.Window.Reply.Contains("не повторяются"), "Clarification omitted fresh-start boundary");
            Check(TestEnvironment.FolderRequests == 0, "Clarification read disk");
            NoNewWork(fixture);
        }
        using (var fixture = new Fixture())
        {
            fixture.Window.FocusChat("chat-b");
            Check(!await Dispatch(fixture.Window, "продолжи"), "Another chat's focus authorized resume");
            Check(fixture.Window.FreshRuns.Count == 0 && fixture.Bridge.NavigationCalls.Count == 0, "Unfocused resume reached Ginger bridge");
        }
    }

    private static async Task StopAndStaleBridgeReplies()
    {
        foreach (var message in new[] { "ну как?", "продолжи" })
        foreach (var stage in new[] { 1, 2 })
        foreach (var change in new[] { "stop", "generation", "chat" })
        {
            using var fixture = new Fixture();
            fixture.Window.SeedMemory(Result());
            var pause = new PausedNavigation();
            if (stage == 2) fixture.Bridge.NavigationReplies.Enqueue(() => Task.FromResult(FakeBridge.Current("chat-a")));
            fixture.Bridge.NavigationReplies.Enqueue(pause.Invoke);
            var task = Dispatch(fixture.Window, message);
            await pause.Started;
            Check(!task.IsCompleted, "Synthetic await did not pause the real route");
            if (change == "stop") fixture.Window.Stop();
            else if (change == "generation") fixture.Window.AdvanceGeneration();
            else fixture.Window.SelectChat("chat-b");
            pause.Complete();
            Check(await task, "Stale/cancelled follow-up escaped to model");
            Check(fixture.Window.Focus is null, "Stale bridge result restored Ginger focus");
            NoNewWork(fixture);
            if (change == "stop")
                Check(fixture.Window.Reply.Contains("Запрос остановлен") && !fixture.Window.Reply.Contains("из-за ошибки"), "Cancelled await displayed stale status");
            else
                Check(fixture.Window.Conversation.Count == 0 && fixture.Bridge.HistoryCalls.Count == 0 && fixture.Window.Reply == "unchanged", "Stale result modified another turn/chat");
            Check(fixture.Bridge.NavigationCalls.Count == (stage == 1 && change == "stop" ? 1 : 2), "Stale path retried bridge calls");
            Released(fixture.Window);
        }
        using (var fixture = new Fixture())
        {
            fixture.Window.SeedMemory(Result());
            fixture.Bridge.NavigationReplies.Enqueue(() => Task.FromResult(FakeBridge.Current("chat-a")));
            fixture.Bridge.NavigationReplies.Enqueue(() => Task.FromResult(FakeBridge.Current("chat-b")));
            Check(await Dispatch(fixture.Window, "ну как?"), "Remote chat mismatch escaped");
            Check(fixture.Window.Focus is null && fixture.Window.Reply.Contains("Запрос остановлен"), "Remote chat mismatch applied stale result");
            NoNewWork(fixture);
        }
    }

    private static async Task ResolutionFailuresDoNotRestoreFocus()
    {
        using (var fixture = new Fixture())
        {
            fixture.Window.SeedMemory(Result() with { Summary = "" });
            Check(await Dispatch(fixture.Window, "ну как?"), "Invalid memory result fell through");
            Check(fixture.Window.Focus is null && fixture.Window.Reply.Contains("достоверный результат"), "Status validation failure restored focus");
            Check(fixture.Bridge.NavigationCalls.Count == 1 && StartupDiagnostics.Errors.Count == 1, "Invalid result continued/retried final navigation");
            Check(TestEnvironment.FolderRequests == 0, "Invalid current memory was replaced from disk");
            NoNewWork(fixture);
            Released(fixture.Window);
        }
        using (var fixture = new Fixture())
        {
            fixture.Window.SeedMemory(Result());
            fixture.Bridge.NavigationReplies.Enqueue(() => Task.FromResult(FakeBridge.Current("chat-a")));
            fixture.Bridge.NavigationReplies.Enqueue(() => Task.FromException<System.Text.Json.JsonElement>(new IOException("synthetic final nav failure")));
            Check(await Dispatch(fixture.Window, "ну как?"), "Final navigation failure fell through");
            Check(fixture.Window.Focus is null && fixture.Window.Reply.Contains("достоверный результат") && !fixture.Window.Reply.Contains("из-за ошибки"), "Resolved status survived final navigation failure");
            Check(fixture.Bridge.NavigationCalls.Count == 2, "Final navigation retried");
            NoNewWork(fixture);
            Released(fixture.Window);
        }
        using (var fixture = new Fixture())
        {
            fixture.Window.FocusChat("chat-a");
            fixture.Bridge.NavigationReplies.Enqueue(() => Task.FromResult(FakeBridge.Current(null)));
            Check(await Dispatch(fixture.Window, "продолжи"), "Missing active chat not consumed");
            Check(fixture.Window.Focus is null && fixture.Window.FreshRuns.Count == 0, "Missing active chat allowed resume");
            NoNewWork(fixture);
        }
    }

    private static async Task CorruptDiskStaysConsumed()
    {
        using var fixture = new Fixture();
        fixture.SaveDisk(Result(GingerChatTaskState.Matched));
        File.WriteAllText(fixture.RecordPath, "{broken", new UTF8Encoding(false));
        fixture.Window.FocusChat("chat-a");
        var priorReads = TestEnvironment.FolderRequests;
        Check(await Dispatch(fixture.Window, "ну как?"), "Corrupt persisted result fell through");
        Check(TestEnvironment.FolderRequests > priorReads, "Linked production store bypassed the synthetic Environment boundary");
        Check(fixture.Window.Focus is null && fixture.Window.Reply.Contains("достоверный результат"), "Corrupt disk result restored focus or claimed success");
        Check(File.ReadAllText(fixture.RecordPath) == "{broken", "Status read silently repaired/reset corrupt data");
        Check(fixture.Window.MemoryCount == 0 && fixture.Bridge.NavigationCalls.Count == 1, "Corrupt read cached a fake status or kept navigating");
        NoNewWork(fixture);
        Released(fixture.Window);
    }

    private static async Task HistoricResultIsNotConfirmation()
    {
        foreach (var state in new[] { GingerChatTaskState.PairReady, GingerChatTaskState.Matched })
        {
            using var fixture = new Fixture();
            fixture.SaveDisk(Result(state));
            var original = File.ReadAllBytes(fixture.RecordPath);
            var priorReads = TestEnvironment.FolderRequests;
            Check(fixture.Window.Focus is null && fixture.Window.Confirmation is null, "New fixture started with live permission");
            Check(await Dispatch(fixture.Window, "статус Ginger"), "Explicit historical status not consumed");
            Check(TestEnvironment.FolderRequests > priorReads, "Historical status did not exercise the real linked disk read");
            Check(fixture.Window.Reply.Contains("Последний известный результат") && fixture.Window.Reply.Contains("01.01.2026") && fixture.Window.Reply.Contains("Новая проверка Ginger не запускалась"), "Historic result presented as live state");
            if (state == GingerChatTaskState.PairReady)
                Check(fixture.Window.Reply.Contains("ещё не подтверждённое") && fixture.Window.Reply.Contains("действующая карточка"), "Historical pair became a matching permission");
            Check(fixture.Window.Confirmation is null && fixture.Window.ApprovalVisibility == Microsoft.UI.Xaml.Visibility.Collapsed, "History restored a confirmation card/permission");
            Check(original.SequenceEqual(File.ReadAllBytes(fixture.RecordPath)), "Status read rewrote persisted result");
            Check(fixture.Window.MemoryCount == 0, "History synthesized a new run in memory");
            NoNewWork(fixture);
            Released(fixture.Window);
        }
    }

    private static async Task MissingResultFollowUpIsBounded()
    {
        using var fixture = new Fixture();
        Check(await Dispatch(fixture.Window, "статус Ginger"), "Missing explicit status fell through");
        Check(fixture.Window.Reply == GingerChatTaskStatusFormatter.Missing && fixture.Window.Focus == "chat-a" && !fixture.Window.HasTaskFocus, "Missing status lost topic context or invented a resumable task");
        Check(await Dispatch(fixture.Window, "ну как?"), "Missing-result short follow-up fell through");
        Check(fixture.Window.Reply == GingerChatTaskStatusFormatter.Missing, "Missing result invented progress");
        Check(fixture.Bridge.NavigationCalls.Count == 4 && fixture.Bridge.HistoryCalls.Count == 2 && fixture.Window.Conversation.Count == 4, "Missing-result follow-up looped or retried work");
        Check(TestEnvironment.FolderRequests == 2 && !Directory.Exists(fixture.StoreRoot), "Read-only missing result created store files or bypassed sandbox path");
        Check(await Dispatch(fixture.Window, "продолжи"), "Short resume after missing result fell through");
        Check(fixture.Window.Reply.Contains("ещё нет результата задачи") && fixture.Window.Reply.Contains("продолжим сопоставление") && !fixture.Window.HasTaskFocus, "Missing result incorrectly authorized a short resume");
        Check(fixture.Bridge.NavigationCalls.Count == 6 && fixture.Bridge.HistoryCalls.Count == 3 && TestEnvironment.FolderRequests == 2, "Missing-result resume looped or re-read storage");
        NoNewWork(fixture);
        Released(fixture.Window);
    }

    private static async Task ActualRecordKeepsFailedOutcomeWhenSaveFails()
    {
        using var fixture = new Fixture();
        fixture.SaveDisk(Result(GingerChatTaskState.Matched));
        File.WriteAllText(fixture.RecordPath, "{corrupt old result", new UTF8Encoding(false));
        fixture.Window.RecordActualResult(Result(GingerChatTaskState.Failed));
        var beforeStatus = TestEnvironment.FolderRequests;
        Check(fixture.Window.MemoryCount == 1 && StartupDiagnostics.Errors.Count == 1, "Actual Record did not retain/log failed persistence");
        Check(await Dispatch(fixture.Window, "ну как?"), "In-memory failed save outcome fell through");
        Check(fixture.Window.Reply.Contains("из-за ошибки") && fixture.Window.Reply.Contains("сохранить его на диск не удалось"), "Failed save concealed actual latest Failed outcome");
        Check(TestEnvironment.FolderRequests == beforeStatus, "Latest failed outcome fell back to old disk result");
        Check(File.ReadAllText(fixture.RecordPath) == "{corrupt old result", "Failed save replaced corrupted previous entry");
        NoNewWork(fixture);
        Released(fixture.Window);
    }

    private static async Task ExistingGuardsAndHistoryFailure()
    {
        foreach (var blocker in new[] { "busy", "ginger", "ginger-dialog", "agent-dialog", "approval" })
        {
            using var fixture = new Fixture();
            fixture.Window.SeedMemory(Result());
            var permission = new object();
            fixture.Window.SetConfirmation(permission);
            fixture.Window.Block(blocker);
            Check(await Dispatch(fixture.Window, "продолжи"), "Busy/approval follow-up fell through");
            Check(fixture.Bridge.NavigationCalls.Count == 0 && fixture.Window.VoiceStops == 0 && TestEnvironment.FolderRequests == 0, "Guard performed status work");
            Check(ReferenceEquals(permission, fixture.Window.Confirmation), "Status guard revoked another confirmation");
            if (blocker == "approval") Check(fixture.Window.ApprovalVisibility == Microsoft.UI.Xaml.Visibility.Visible, "Status hid pending approval");
            NoNewWork(fixture);
        }
        using (var fixture = new Fixture())
        {
            fixture.Window.SeedMemory(Result());
            fixture.Bridge.HistoryFailure = new IOException("synthetic history unavailable");
            Check(await Dispatch(fixture.Window, "ну как?"), "History save failure fell through");
            Check(fixture.Window.Reply.Contains("из-за ошибки") && fixture.Window.Focus == "chat-a", "History failure discarded valid typed result");
            Check(StartupDiagnostics.Errors.Count == 1 && fixture.Bridge.HistoryCalls.Count == 1, "History failure was not logged or was retried");
            NoNewWork(fixture);
            Released(fixture.Window);
        }
    }

    private sealed class Fixture : IDisposable
    {
        private const string Prefix = "BabyAI-GingerChatStatusRouting-";
        private readonly string _temporaryParent;
        private readonly string _root;
        internal FakeBridge Bridge { get; } = new();
        internal MainWindow Window { get; }
        internal string StoreRoot => Path.Combine(_root, "BabyAI", "ginger-chat-task-status");
        internal string RecordPath => Path.Combine(StoreRoot, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("chat-a"))) + ".json");

        internal Fixture()
        {
            if (TestEnvironment.SyntheticRoot is not null) throw new Exception("Overlapping fixture roots");
            _temporaryParent = Path.GetFullPath(Path.GetTempPath());
            _root = Path.GetFullPath(Path.Combine(_temporaryParent, Prefix + Guid.NewGuid().ToString("N")));
            Directory.CreateDirectory(_root);
            TestEnvironment.SyntheticRoot = _root;
            TestEnvironment.FolderRequests = 0;
            StartupDiagnostics.Errors.Clear();
            Window = new MainWindow(Bridge);
        }
        internal void SaveDisk(GingerChatTaskResult result)
        {
            new GingerChatTaskStatusStore().Save(result);
            Check(TestEnvironment.FolderRequests > 0 && File.Exists(RecordPath), "Store did not use the test-only Environment path");
        }
        public void Dispose()
        {
            TestEnvironment.SyntheticRoot = null;
            var resolved = Path.GetFullPath(_root);
            if (!string.Equals(Path.GetDirectoryName(resolved)?.TrimEnd(Path.DirectorySeparatorChar), _temporaryParent.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase) ||
                !Path.GetFileName(resolved).StartsWith(Prefix, StringComparison.Ordinal) ||
                !Guid.TryParseExact(Path.GetFileName(resolved)[Prefix.Length..], "N", out _))
                throw new Exception("Refusing cleanup outside the exact synthetic fixture root");
            var pending = new Stack<string>();
            pending.Push(resolved);
            int entries = 0;
            while (pending.Count > 0)
            {
                var path = pending.Pop();
                var attributes = File.GetAttributes(path);
                if (++entries > 20 || (attributes & FileAttributes.ReparsePoint) != 0)
                    throw new Exception("Unexpected/reparse fixture entry; cleanup refused");
                if ((attributes & FileAttributes.Directory) != 0)
                    foreach (var child in Directory.EnumerateFileSystemEntries(path)) pending.Push(child);
            }
            Directory.Delete(resolved, recursive: true);
        }
    }
}
