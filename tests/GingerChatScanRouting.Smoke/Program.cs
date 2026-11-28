using System.Text.Json;
using BabyAI.Desktop;
using TestEnvironment = BabyAI.Desktop.Environment;

await ScanRoutingTests.RunAsync();

internal static class ScanRoutingTests
{
    private static int _checks;
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
        _checks++;
    }
    private static JsonElement Result(string? candidate, string marker = "completed") => JsonSerializer.SerializeToElement(new {
        understanding = "Synthetic complete comparison", candidate, explanation = "Synthetic comparison explanation",
        extra_evidence = new { marker, detail = new string('e', 25000) }
    });
    private static JsonElement Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
    private static GingerChatItem Item(GingerDesktopObservation row) =>
        new(row.Supplier, row.Document, row.Position, row.Title, row.Linked, row.Candidates);
    private static JsonElement Report(GingerDesktopObservation row, JsonElement result) => JsonSerializer.SerializeToElement(new {
        request = new { supplier = row.Supplier, source = row.Title, lesson = "", candidates = row.Candidates }, result
    });
    private static JsonElement Evidence(GingerDesktopObservation row)
    {
        var files = Directory.GetFiles(row.Directory, "comparison-*.json");
        Check(files.Length == 1, "Comparison was not saved exactly once before continuing");
        using var json = JsonDocument.Parse(File.ReadAllText(files[0]));
        return json.RootElement.Clone();
    }

    internal static async Task RunAsync()
    {
        // These linked constructors must hit our throwing namespace-local shim
        // before any store read/write can occur. They never receive real AppData.
        TestEnvironment.SyntheticRoot = null;
        foreach (var constructor in new Action[] { () => _ = new GingerDeferredRows(), () => _ = new GingerLearning() })
        {
            var refused = false;
            try { constructor(); }
            catch (InvalidOperationException ex) when (ex.Message.Contains("Real user directories")) { refused = true; }
            Check(refused, "Production default storage bypassed the temp-only Environment boundary");
        }
        await ManualThenPairUsesSecondObservation();
        await ManualThenEndDoesNotShowPreviousCards();
        await MalformedAnswersCannotDeferOrAdvance();
        await EvidenceWriteFailureCannotDeferOrAdvance();
        await DeferredWriteFailureCannotAdvance();
        await LaterFailureCannotRestoreFirstReview();
        await RevisitManualStaysOnItsRow();
        Check(TestEnvironment.SyntheticRoot is null && Scenario.Current is null, "Fixture isolation leaked");
        Console.WriteLine($"PASS: {_checks} scan-routing integration checks against linked MainWindow/flow/store/evidence/reviews; fake UI/bridge/session, temp data only; no Ginger or model started");
    }

    private static void Released(Fixture fixture)
    {
        Check(!fixture.Window.Busy && !fixture.Window.Running && !fixture.Window.HasCancellation,
            "Actual finally retained busy/running/cancellation state");
        Check(!fixture.Window.CaptureMode && fixture.Scenario.Disposals == 1 && fixture.Window.Progress.Finished,
            "Actual finally retained capture/session or failed to finish the turn");
        Check(fixture.Bridge.History.Count == 1 && fixture.Bridge.History[0].ChatId == "chat-current",
            "Final history was not bound to the originating chat exactly once");
    }
    private static void NoCards(Fixture fixture)
    {
        Check(fixture.Window.ShownReview is null && fixture.Window.ShownLesson is null && fixture.Window.Confirmation is null,
            "A skipped or failed row retained visible review/lesson/confirmation");
        Check(fixture.Window.ReviewShows.Count == 0 && fixture.Window.LessonShows.Count == 0 &&
            fixture.Window.ConfirmationOffers.Count == 0, "Skipped or failed row was offered as a final pair");
    }

    private static async Task ManualThenPairUsesSecondObservation()
    {
        using var fixture = new Fixture(count: 2);
        var first = fixture.Scenario.Rows[0];
        var second = fixture.Scenario.Rows[1];
        fixture.Bridge.Replies.Enqueue(() => Result(null, "first-full-answer"));
        fixture.Bridge.Replies.Enqueue(() => Result(second.Candidates[1], "second-full-answer"));
        var nextCheckRan = false;
        fixture.Scenario.BeforeNext = before =>
        {
            Check(before == first, "Next-row call was not bound to the compared first observation");
            var report = Evidence(first);
            Check(report.GetProperty("result").GetProperty("extra_evidence").GetProperty("detail").GetString()!.Length == 25000,
                "Saved evidence was reduced to UI text");
            var deferred = fixture.Store.Read().Single();
            Check(deferred.Position == 1 && deferred.Title == first.Title && deferred.Reason == GingerDeferredRows.ManualReviewReason,
                "Next-row input preceded durable manual-review storage");
            Check(fixture.Window.ReviewShows.Count == 0 && fixture.Window.ConfirmationOffers.Count == 0,
                "Skipped row's review or confirmation was shown during transition");
            nextCheckRan = true;
        };
        await fixture.Run();
        Check(nextCheckRan && fixture.Scenario.NextPositions.SequenceEqual(new[] { 2 }) && fixture.Scenario.StartReads == 1,
            "Scan retried or did not visit the next row exactly once");
        Check(fixture.Bridge.Comparisons.Select(call => call.Source).SequenceEqual(new[] { first.Title, second.Title }),
            "Comparison used stale source after advancing");
        Check(fixture.Bridge.Comparisons[1].Candidates.SequenceEqual(second.Candidates), "Second comparison used first candidates");
        Check(fixture.Window.ReviewShows.Count == 1 && fixture.Window.ShownReview!.Source == second.Title &&
            fixture.Window.ShownReview.ChatId == "chat-current", "Final answer review belongs to the wrong row/chat");
        Check(fixture.Window.LessonShows.Count == 1 && fixture.Window.ShownLesson!.Source == second.Title &&
            !fixture.Window.ShownLesson.SourceOnly, "Final lesson editor belongs to the wrong row or mode");
        var confirmation = fixture.Window.Confirmation!;
        Check(fixture.Window.ConfirmationOffers.Count == 1 && ReferenceEquals(confirmation.Observation, second) &&
            ReferenceEquals(confirmation.Session, fixture.Scenario.Session) && confirmation.Candidate == second.Candidates[1] &&
            confirmation.ChatId == "chat-current", "Final confirmation used a stale observation/session/candidate/chat");
        var savedSecond = Evidence(second);
        Check(savedSecond.GetProperty("request").GetProperty("source").GetString() == second.Title &&
            savedSecond.GetProperty("result").GetProperty("extra_evidence").GetProperty("marker").GetString() == "second-full-answer",
            "Second evidence reused the first report");
        Check(fixture.Window.Outcomes.Single() == (GingerChatTaskState.PairReady, 1) &&
            fixture.Window.Notifications.Single() == GingerNotificationKind.PairReady, "Final outcome did not represent second pair readiness");
        Check(fixture.Window.ResearchSource == second.Title, "Research panel stayed on skipped source");

        JsonElement? rated = null;
        await fixture.Window.ShownReview!.RateAsync("chat-current", 8, "Synthetic rating callback only", (report, _, _) =>
        { rated = report.Clone(); return Task.FromResult(new GingerAnswerRatingReceipt("synthetic", true)); });
        Check(rated!.Value.GetProperty("request").GetProperty("candidates").EnumerateArray().Select(v => v.GetString())
            .SequenceEqual(second.Candidates), "Review rating callback retained stale candidate evidence");
        string? lessonSource = null;
        string[]? lessonCandidates = null;
        await fixture.Window.ShownLesson!.PreviewAsync("chat-current", "Synthetic unsaved lesson", (supplier, source, lesson, candidates, token) =>
        { lessonSource = source; lessonCandidates = candidates.ToArray(); return Task.FromResult(Result(null)); }, CancellationToken.None);
        Check(lessonSource == second.Title && lessonCandidates!.SequenceEqual(second.Candidates),
            "Lesson preview retained first row's frozen example or candidates");
        Check(!Directory.Exists(Path.Combine(fixture.Root, "BabyAI", "ginger-learning")), "Review/preview saved a lesson without a save action");
        Released(fixture);
    }

    private static async Task ManualThenEndDoesNotShowPreviousCards()
    {
        foreach (var linkedLast in new[] { false, true })
        {
            using var fixture = new Fixture(count: linkedLast ? 2 : 1, linkedLast: linkedLast);
            var first = fixture.Scenario.Rows[0];
            var oldReview = new GingerChatAnswerReview("old-chat", Report(first, Result(first.Candidates[0])));
            var oldLesson = new GingerChatLessonReview("old-chat", Item(first), new GingerLearning());
            fixture.Window.SeedOldCards(oldReview, oldLesson);
            fixture.Bridge.Replies.Enqueue(() => Result(null));
            await fixture.Run();
            NoCards(fixture);
            Check(!oldReview.CanRate && !oldLesson.CanPreview, "Starting scan did not invalidate old review cards");
            Check(fixture.Store.Read().Single().Position == 1 && fixture.Store.Read().Single().Reason == GingerDeferredRows.ManualReviewReason,
                "End-of-list scan lost the manual-review row or deferred an already-linked row");
            Check(fixture.Scenario.NextPositions.Count == (linkedLast ? 1 : 0) && fixture.Bridge.Comparisons.Count == 1,
                "End/linked row caused extra comparison or navigation");
            Check(fixture.Window.Outcomes.Single() == (GingerChatTaskState.End, 1) &&
                fixture.Window.Notifications.Single() == GingerNotificationKind.ManualReview,
                "End with a deferred manual row claimed a ready pair");
            Released(fixture);
        }
    }

    private static async Task MalformedAnswersCannotDeferOrAdvance()
    {
        foreach (var raw in new[] {
            "{}", "{\"candidate\":false,\"understanding\":\"u\",\"explanation\":\"e\"}",
            "{\"candidate\":null,\"understanding\":\"\",\"explanation\":\"e\"}",
            "{\"candidate\":\"invented\",\"understanding\":\"u\",\"explanation\":\"e\"}",
            "{\"candidate\":null,\"candidate\":null,\"understanding\":\"u\",\"explanation\":\"e\"}",
            "{\"candidate\":null,\"understanding\":\"u\",\"explanation\":\"e\",\"error\":\"failed\"}",
            "{\"candidate\":null,\"understanding\":\"u\",\"explanation\":\"e\",\"mode\":\"source_only\"}"
        })
        {
            using var fixture = new Fixture(count: 2);
            fixture.Bridge.Replies.Enqueue(() => Parse(raw));
            await fixture.Run();
            Check(fixture.Scenario.NextPositions.Count == 0 && fixture.Store.Read().Count == 0,
                "Malformed reply became a durable ambiguity or next-row input: " + raw);
            Check(Directory.GetFiles(fixture.Scenario.Rows[0].Directory).Length == 0, "Malformed reply was published as complete evidence");
            Check(fixture.Window.Outcomes.Single() == (GingerChatTaskState.Failed, 0), "Malformed reply did not fail the actual route");
            NoCards(fixture); Released(fixture);
        }
    }

    private static async Task EvidenceWriteFailureCannotDeferOrAdvance()
    {
        using var fixture = new Fixture(count: 2);
        var directory = fixture.Scenario.Rows[0].Directory;
        Directory.Delete(directory);
        File.WriteAllText(directory, "Synthetic obstruction; preserve me");
        fixture.Bridge.Replies.Enqueue(() => Result(null));
        await fixture.Run();
        Check(File.ReadAllText(directory) == "Synthetic obstruction; preserve me", "Evidence failure overwrote its obstruction");
        Check(fixture.Scenario.NextPositions.Count == 0 && fixture.Store.Read().Count == 0,
            "Evidence persistence failure permitted deferral or next-row input");
        Check(fixture.Window.Outcomes.Single() == (GingerChatTaskState.Failed, 0), "Evidence write failure became manual review");
        NoCards(fixture); Released(fixture);
    }

    private static async Task DeferredWriteFailureCannotAdvance()
    {
        using var fixture = new Fixture(count: 2);
        Directory.CreateDirectory(Path.GetDirectoryName(fixture.DeferredRoot)!);
        File.WriteAllText(fixture.DeferredRoot, "Synthetic queue obstruction");
        fixture.Bridge.Replies.Enqueue(() => Result(null));
        await fixture.Run();
        Check(Evidence(fixture.Scenario.Rows[0]).GetProperty("result").GetProperty("candidate").ValueKind == JsonValueKind.Null,
            "Completed comparison was lost when deferred persistence failed");
        Check(File.ReadAllText(fixture.DeferredRoot) == "Synthetic queue obstruction" && fixture.Scenario.NextPositions.Count == 0,
            "Failed deferred write overwrote data or permitted next-row input");
        Check(fixture.Window.Outcomes.Single() == (GingerChatTaskState.Failed, 0), "Failed deferred write incremented durable count");
        NoCards(fixture); Released(fixture);
    }

    private static async Task LaterFailureCannotRestoreFirstReview()
    {
        using var fixture = new Fixture(count: 2);
        fixture.Bridge.Replies.Enqueue(() => Result(null));
        fixture.Bridge.Replies.Enqueue(() => throw new IOException("Synthetic second comparison failure"));
        await fixture.Run();
        Check(fixture.Store.Read().Single().Position == 1 && fixture.Scenario.NextPositions.SequenceEqual(new[] { 2 }),
            "Second-row error lost first durable deferral or retried navigation");
        Check(fixture.Window.Outcomes.Single() == (GingerChatTaskState.Failed, 1), "Later error lost actual durable count");
        NoCards(fixture); Released(fixture);
    }

    private static async Task RevisitManualStaysOnItsRow()
    {
        using var fixture = new Fixture(count: 2);
        var first = fixture.Scenario.Rows[0];
        var reminder = fixture.Store.AddManualReview(Item(first), "saved-chat", first.Directory);
        var reminderPath = Path.Combine(fixture.DeferredRoot, reminder.Id + ".json");
        var before = File.ReadAllBytes(reminderPath);
        fixture.Bridge.Replies.Enqueue(() => Result(null));
        await fixture.Run(reminder);
        Check(ReferenceEquals(fixture.Scenario.Revisit, reminder) && fixture.Scenario.StartReads == 1 &&
            fixture.Scenario.NextPositions.Count == 0, "Explicit revisit entered automatic scan navigation");
        Check(fixture.Store.Read().Single() == reminder && File.ReadAllBytes(reminderPath).SequenceEqual(before),
            "Revisit rewrote the reminder instead of staying on its row");
        Check(fixture.Window.ShownReview?.Source == first.Title && fixture.Window.ShownLesson?.Source == first.Title &&
            fixture.Window.Confirmation is null && fixture.Window.ConfirmationOffers.Count == 0,
            "Manual revisit lost its own review or created a pair");
        Check(fixture.Window.Outcomes.Single() == (GingerChatTaskState.Review, 0), "Revisit incorrectly counted a new deferral/end");
        _ = Evidence(first);
        Released(fixture);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _temporaryParent = Path.GetFullPath(Path.GetTempPath());
        internal string Root { get; }
        internal string DeferredRoot => Path.Combine(Root, "BabyAI", "ginger-deferred-rows");
        internal GingerDeferredRows Store => new();
        internal Scenario Scenario { get; } = new();
        internal FakeBridge Bridge { get; } = new();
        internal MainWindow Window { get; }
        internal Fixture(int count, bool linkedLast = false)
        {
            if (TestEnvironment.SyntheticRoot is not null) throw new Exception("Overlapping synthetic roots");
            Root = Path.Combine(_temporaryParent, "BabyAI-ScanRouting-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
            TestEnvironment.SyntheticRoot = Root;
            BabyAI.Desktop.Scenario.Current = Scenario;
            StartupDiagnostics.Errors.Clear();
            for (var position = 1; position <= count; position++)
            {
                var directory = Path.Combine(Root, "observation-" + position);
                Directory.CreateDirectory(directory);
                Scenario.Rows.Add(new("Synthetic supplier", "Synthetic document", position, count,
                    "Synthetic source " + position, linkedLast && position == count,
                    ["Candidate " + position + "A", "Candidate " + position + "B"], directory));
            }
            Window = new MainWindow(Bridge);
        }
        internal Task Run(GingerDeferredRow? revisit = null) => Window.RunAsync(revisit).WaitAsync(TimeSpan.FromSeconds(10));
        public void Dispose()
        {
            TestEnvironment.SyntheticRoot = null;
            BabyAI.Desktop.Scenario.Current = null;
            var full = Path.GetFullPath(Root);
            if (!full.StartsWith(_temporaryParent.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(full).StartsWith("BabyAI-ScanRouting-", StringComparison.Ordinal))
                throw new InvalidOperationException("Unexpected fixture cleanup path");
            if (Directory.Exists(full)) Directory.Delete(full, recursive: true);
        }
    }
}
