using BabyAI.Desktop;
using Microsoft.UI.Xaml.Controls;
using System.Text.Json;

await DeferredLessonRoutingTests.RunAsync();

internal static class DeferredLessonRoutingTests
{
    private static int _checks;
    private static readonly string[] OpeningBlockers = ["exit", "busy", "ginger", "ginger-dialog", "agents-dialog",
        "approval", "confirmation", "confirmation-card"];
    private static readonly string[] ActionBlockers = ["exit", "ginger", "agents-dialog", "approval", "confirmation", "confirmation-card", "chat"];
    private static readonly string[] NavigationBlockers = ["exit", "ginger", "ginger-dialog", "agents-dialog",
        "approval", "confirmation", "confirmation-card", "chat", "generation"];

    internal static async Task RunAsync()
    {
        await SecondaryOpensFrozenSource();
        await PrimaryRemainsFreshRevisit();
        await NoSelectionAndCloseDoNothing();
        await InitialGuardsDoNotAcquireReservation();
        await DialogAndPostDialogGuards();
        await LateNavigationCannotOpenLesson();
        await BridgeAndStoreFailuresStayClosed();
        await BusySetterFailuresReleaseReservation();
        Check(GingerDesktopSession.Calls == 0, "Routing touched a driver boundary");
        Console.WriteLine($"PASS: {_checks} deferred-lesson routing checks against linked production dialog and source-only factory; in-memory UI/store/bridge, no desktop or user files");
    }
    private static void Check(bool value, string message)
    {
        if (!value) throw new Exception(message);
        _checks++;
    }
    private static void NoWork(Fixture fixture)
    {
        Check(fixture.Window.ShownLessons.Count == 0, "A blocked/cancelled route opened a lesson");
        Check(fixture.Window.FreshRuns.Count == 0, "A source-only/blocked route dispatched Ginger");
        Check(GingerDesktopSession.Calls == 0, "Routing invoked observation/selection");
        Check(fixture.Window.Invalidations.Count == 0, "A blocked route invalidated existing chat state");
    }
    private static void Released(MainWindow window)
    {
        Check(!window.Busy && !window.DialogOpen, "Dialog/lesson preparation retained its busy reservation");
    }
    private static void Change(MainWindow window, string blocker)
    {
        if (blocker == "chat") window.ChangeChat("another-chat");
        else if (blocker == "generation") window.ChangeGeneration();
        else window.Block(blocker);
    }

    private static async Task SecondaryOpensFrozenSource()
    {
        using var fixture = new Fixture();
        ContentDialog.Show = dialog =>
        {
            Check(dialog.PrimaryButtonText == "Открыть для разбора", "Existing primary action changed");
            Check(dialog.SecondaryButtonText == "Объяснить UNIX", "Source lesson action is missing");
            Check(dialog.DefaultButton == ContentDialogButton.Close, "Opening dialog pre-authorizes an action");
            Check(!dialog.IsPrimaryButtonEnabled && !dialog.IsSecondaryButtonEnabled, "Action enabled before selection");
            Check(((IEnumerable<GingerDeferredRow>)dialog.List.ItemsSource!).SequenceEqual([fixture.Newer, fixture.Row]), "Global queue ordering changed");
            dialog.List.SelectedItem = fixture.Row;
            Check(dialog.IsPrimaryButtonEnabled && dialog.IsSecondaryButtonEnabled, "Selection did not enable both explicit actions");
            return Task.FromResult(dialog.Click(ContentDialogResult.Secondary));
        };
        await fixture.Window.OpenDeferredAsync();
        Check(fixture.Window.ShownLessons.Count == 1, "Secondary did not open exactly one lesson");
        var lesson = fixture.Window.ShownLessons.Single();
        Check(lesson.SourceOnly && lesson.ChatId == "current-chat", "Secondary did not use the real source-only factory/current chat");
        Check(lesson.Supplier == fixture.Row.Supplier && lesson.Source == fixture.Row.Title, "Frozen source changed or was truncated");
        Check(lesson.ChatId != fixture.Row.ChatId, "Reminder's original chat displaced the currently selected chat");
        Check(lesson.CanPreview && !lesson.CanSave && lesson.Draft is null && lesson.Saved is null, "Opening a lesson published knowledge or created a reviewed draft");
        Check(fixture.Window.FreshRuns.Count == 0 && GingerDesktopSession.Calls == 0, "Source-only opening drove Ginger");
        Check(fixture.Window.Bridge.Calls.SequenceEqual(["chat.current"]), "Source opening issued model/history/driver calls");
        Check(GingerLearning.ReadSuppliers.SequenceEqual([fixture.Row.Supplier]), "Factory read another supplier's lessons");
        Check(fixture.Window.Invalidations.SequenceEqual(["context", "voice"]), "Context was cleared before validation or multiple times");
        Check(fixture.Rows.SequenceEqual([fixture.Row, fixture.Newer]), "Opening a lesson removed or rewrote a reminder");
        Check(fixture.Window.Reply.Contains("Проверить понимание"), "Opening implied that a lesson was already saved");
        Released(fixture.Window);
    }

    private static async Task PrimaryRemainsFreshRevisit()
    {
        using var fixture = new Fixture();
        fixture.Choose(ContentDialogResult.Primary);
        await fixture.Window.OpenDeferredAsync();
        var run = fixture.Window.FreshRuns.Single();
        Check(ReferenceEquals(run.Row, fixture.Row) && run.ChatId == "current-chat", "Primary lost exact reminder or expected chat");
        Check(run.Message == $"Разобрать отложенную позицию: строка {fixture.Row.Position}." && run.Message.Length <= 500,
            "Primary changed bounded synthetic request or included the full long source");
        Check(fixture.Window.ShownLessons.Count == 0 && fixture.Window.Bridge.Calls.Count == 0,
            "Primary was replaced with the source-only route");
        Check(GingerLearning.ReadSuppliers.Count == 0 && fixture.Window.Invalidations.Count == 0, "Primary executed lesson preparation");
        Released(fixture.Window);
    }

    private static async Task NoSelectionAndCloseDoNothing()
    {
        foreach (var action in new[] { ContentDialogResult.Primary, ContentDialogResult.Secondary, ContentDialogResult.None })
        {
            using var fixture = new Fixture();
            ContentDialog.Show = dialog => Task.FromResult(action == ContentDialogResult.None ? action : dialog.Click(action, bypassEnabled: true));
            await fixture.Window.OpenDeferredAsync();
            NoWork(fixture);
            Check(fixture.Window.Bridge.Calls.Count == 0, "No selection still reached the bridge");
            Released(fixture.Window);
        }
        using (var fixture = new Fixture())
        {
            // A result code alone is not a user selection captured by the event.
            ContentDialog.Show = _ => Task.FromResult(ContentDialogResult.Secondary);
            await fixture.Window.OpenDeferredAsync();
            NoWork(fixture);
        }
        using (var fixture = new Fixture(empty: true))
        {
            ContentDialog.Show = dialog =>
            {
                Check(!dialog.IsPrimaryButtonEnabled && !dialog.IsSecondaryButtonEnabled, "Empty queue offered work");
                return Task.FromResult(ContentDialogResult.None);
            };
            await fixture.Window.OpenDeferredAsync();
            NoWork(fixture);
            Released(fixture.Window);
        }
    }

    private static async Task InitialGuardsDoNotAcquireReservation()
    {
        foreach (var blocker in OpeningBlockers)
        {
            using var fixture = new Fixture();
            fixture.Window.Block(blocker);
            var confirmation = fixture.Window.Confirmation;
            await fixture.Window.OpenDeferredAsync();
            Check(ContentDialog.Shows == 0 && GingerDeferredRows.Reads == 0, "Opening guard read storage or showed a dialog: " + blocker);
            NoWork(fixture);
            Check(ReferenceEquals(confirmation, fixture.Window.Confirmation), "Opening guard cleared another confirmation");
            if (blocker == "approval") Check(fixture.Window.Approval, "Opening guard removed approval");
            if (blocker == "busy") Check(fixture.Window.Busy, "Opening guard released another operation's busy state");
            if (blocker == "ginger-dialog") Check(fixture.Window.DialogOpen, "Opening guard closed another dialog");
        }
    }

    private static async Task DialogAndPostDialogGuards()
    {
        foreach (var action in new[] { ContentDialogResult.Primary, ContentDialogResult.Secondary })
        foreach (var blocker in ActionBlockers)
        foreach (var moment in new[] { "click", "after-close" })
        {
            using var fixture = new Fixture();
            if (moment == "after-close") fixture.Window.BusyChanged = busy => { if (!busy) Change(fixture.Window, blocker); };
            ContentDialog.Show = dialog =>
            {
                dialog.List.SelectedItem = fixture.Row;
                if (moment == "click") Change(fixture.Window, blocker);
                return Task.FromResult(dialog.Click(action));
            };
            await fixture.Window.OpenDeferredAsync();
            NoWork(fixture);
            Check(fixture.Window.Bridge.Calls.Count == 0, "Stale dialog choice reached lesson navigation");
            if (blocker == "approval") Check(fixture.Window.Approval, "Dialog cleanup dismissed pending approval");
            if (blocker == "confirmation") Check(fixture.Window.Confirmation is not null, "Dialog cleanup dismissed pending confirmation");
            Released(fixture.Window);
        }
    }

    private static async Task LateNavigationCannotOpenLesson()
    {
        foreach (var blocker in NavigationBlockers)
        {
            using var fixture = new Fixture();
            fixture.Choose(ContentDialogResult.Secondary);
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var answer = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
            fixture.Window.Bridge.Replies.Enqueue(() => { started.SetResult(); return answer.Task; });
            var opening = fixture.Window.OpenDeferredAsync();
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Check(fixture.Window.Busy && !fixture.Window.DialogOpen, "Navigation did not reserve lesson preparation");
            Change(fixture.Window, blocker);
            answer.SetResult(FakeBridge.Current("current-chat"));
            await opening;
            NoWork(fixture);
            Check(fixture.Window.Bridge.Calls.Count == 1 && GingerLearning.ReadSuppliers.Count == 0,
                "Stale navigation response read lesson storage or retried navigation");
            if (blocker == "approval") Check(fixture.Window.Approval, "Navigation cleanup removed approval");
            if (blocker == "confirmation") Check(fixture.Window.Confirmation is not null, "Navigation cleanup removed confirmation");
            Check(!fixture.Window.Busy, "Late navigation retained its busy state");
        }
    }

    private static async Task BridgeAndStoreFailuresStayClosed()
    {
        foreach (var mode in new[] { "other-chat", "empty-chat", "missing-chat", "bridge-error", "lessons-error", "rows-error", "show-error" })
        {
            using var fixture = new Fixture();
            fixture.Choose(ContentDialogResult.Secondary);
            switch (mode)
            {
                case "other-chat": fixture.Window.Bridge.Chat = "other-chat"; break;
                case "empty-chat": fixture.Window.Bridge.Chat = ""; break;
                case "missing-chat": fixture.Window.Bridge.Replies.Enqueue(() => Task.FromResult(JsonSerializer.SerializeToElement(new { }))); break;
                case "bridge-error": fixture.Window.Bridge.Replies.Enqueue(() => Task.FromException<JsonElement>(new IOException("bridge unavailable"))); break;
                case "lessons-error": GingerLearning.ReadLessons = _ => throw new InvalidDataException("lessons corrupt"); break;
                case "rows-error": GingerDeferredRows.ReadRows = () => throw new InvalidDataException("reminders corrupt"); break;
                case "show-error": ContentDialog.Show = _ => Task.FromException<ContentDialogResult>(new InvalidOperationException("dialog failure")); break;
            }
            await fixture.Window.OpenDeferredAsync();
            NoWork(fixture);
            Released(fixture.Window);
            Check(fixture.Rows.Count == 2, "Failure removed deferred reminders");
            if (mode.EndsWith("error")) Check(StartupDiagnostics.Errors.Count == 1, "Failure was swallowed without diagnostics: " + mode);
        }
        using (var fixture = new Fixture())
        {
            fixture.Choose(ContentDialogResult.Secondary);
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var answer = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
            fixture.Window.Bridge.Replies.Enqueue(() => { started.SetResult(); return answer.Task; });
            var opening = fixture.Window.OpenDeferredAsync();
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            fixture.Window.ChangeChat("other-chat");
            answer.SetException(new IOException("late failure"));
            await opening;
            NoWork(fixture);
            Check(fixture.Window.Reply == "", "Late failure overwrote another chat's reply");
            Released(fixture.Window);
        }
    }

    private static async Task BusySetterFailuresReleaseReservation()
    {
        foreach (var throwAt in new[] { 1, 2, 3, 4 })
        {
            using var fixture = new Fixture();
            fixture.Choose(ContentDialogResult.Secondary);
            var updates = 0;
            fixture.Window.BusyChanged = _ => { if (++updates == throwAt) throw new InvalidOperationException("synthetic UI setter failed"); };
            await fixture.Window.OpenDeferredAsync();
            Released(fixture.Window);
            Check(StartupDiagnostics.Errors.Count >= 1, "UI setter failure lacked diagnostics");
            if (throwAt < 4) NoWork(fixture);
            else Check(fixture.Window.ShownLessons.Count == 1 && fixture.Window.FreshRuns.Count == 0, "Cleanup failure duplicated or replaced lesson opening");
        }
    }

    private sealed class Fixture : IDisposable
    {
        internal readonly MainWindow Window = new();
        internal readonly GingerDeferredRow Row = new("row-1", "original-chat", "Supplier", "Document", 7,
            "Source " + new string('x', 2000), "No candidates", "X:/synthetic-never-read-observation", DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
        internal readonly GingerDeferredRow Newer;
        internal readonly List<GingerDeferredRow> Rows;
        internal Fixture(bool empty = false)
        {
            Newer = Row with { Id = "row-2", Position = 8, Title = "Newer source", CreatedAt = Row.CreatedAt.AddMinutes(1) };
            Rows = empty ? [] : [Row, Newer];
            GingerDeferredRows.Reads = 0;
            GingerDeferredRows.ReadRows = () => Rows.ToArray();
            GingerLearning.ReadSuppliers.Clear();
            GingerLearning.ReadLessons = _ => [];
            ContentDialog.Shows = 0;
            ContentDialog.Show = _ => Task.FromResult(ContentDialogResult.None);
            StartupDiagnostics.Errors.Clear();
        }
        internal void Choose(ContentDialogResult action) => ContentDialog.Show = dialog =>
        {
            dialog.List.SelectedItem = Row;
            return Task.FromResult(dialog.Click(action));
        };
        public void Dispose()
        {
            GingerDeferredRows.ReadRows = null;
            GingerLearning.ReadLessons = null;
            ContentDialog.Show = null;
        }
    }
}
