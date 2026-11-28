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
        await BoundedStartsDoNotTouchFilters();
        await BroadThenBoundedUsesFreshObservation();
        await StillBroadCannotCompareDeferOrAdvance();
        await ModelSearchRoutesThroughChat();
        await SearchDeclineDefersFreshRowThenShowsNextPair();
        await SearchDeclineAtEndHasNoPairCards();
        await SearchFailuresNeverBecomeDeferrals();
        await RevisitSearchDeclineStaysOnItsRow();
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

    private static GingerDesktopObservation ReplaceCandidates(Fixture fixture, int count)
    {
        var before = fixture.Scenario.Rows[0];
        var broad = Observation(before.Directory, before.Position, before.Count, before.Linked,
            Enumerable.Range(1, count).Select(index => "Unfiltered candidate " + index).ToArray());
        fixture.Scenario.Rows[0] = broad;
        return broad;
    }

    private static async Task BoundedStartsDoNotTouchFilters()
    {
        foreach (var count in new[] { 1, 10 })
        foreach (var revisit in new[] { false, true })
        {
            using var fixture = new Fixture(count: 2);
            var before = ReplaceCandidates(fixture, count);
            GingerDeferredRow? reminder = revisit ? fixture.Store.AddManualReview(Item(before), "saved-chat", before.Directory) : null;
            fixture.Scenario.FilterReplies.Enqueue(_ => throw new InvalidOperationException("Already bounded input must not touch filters"));
            fixture.Bridge.Replies.Enqueue(() => Result(before.Candidates[^1]));
            await fixture.Run(reminder);
            Check(fixture.Scenario.StartReads == 1 && fixture.Scenario.FilterReads == 0 &&
                fixture.Scenario.FilterInputs.Count == 0 && fixture.Scenario.NextPositions.Count == 0,
                "A directly bounded row caused unnecessary filter/navigation input");
            Check(fixture.Bridge.Comparisons.Single().Candidates.SequenceEqual(before.Candidates) &&
                ReferenceEquals(fixture.Window.Confirmation!.Observation, before), "Direct bounded comparison changed its observation");
            Check(fixture.Window.Outcomes.Single() == (GingerChatTaskState.PairReady, 0) &&
                !fixture.Window.Progress.Stages.Any(stage => stage.Stage == GingerChatStage.Filtering),
                "Direct bounded route failed or claimed filter preparation");
            _ = Evidence(before);
            Released(fixture);
        }
    }

    private static GingerDesktopObservation FilteredObservation(Fixture fixture, GingerDesktopObservation before, int count)
    {
        var directory = Path.Combine(fixture.Root, "filtered-observation");
        Directory.CreateDirectory(directory);
        return Observation(directory, before.Position, before.Count, before.Linked,
            Enumerable.Range(1, count).Select(index => "Fresh filtered candidate " + index).ToArray());
    }

    private static async Task BroadThenBoundedUsesFreshObservation()
    {
        foreach (var initialCount in new[] { 11, 256 })
        foreach (var finalCount in new[] { 1, 10 })
        foreach (var revisit in new[] { false, true })
        {
            using var fixture = new Fixture(count: 2);
            var before = ReplaceCandidates(fixture, initialCount);
            var fresh = FilteredObservation(fixture, before, finalCount);
            GingerDeferredRow? reminder = revisit ? fixture.Store.AddManualReview(Item(before), "saved-chat", before.Directory) : null;
            fixture.Scenario.FilterReplies.Enqueue(input =>
            {
                Check(ReferenceEquals(input, before), "Filtering used a stale/different observation");
                Check(fixture.Bridge.Comparisons.Count == 0 && fixture.Scenario.NextPositions.Count == 0,
                    "Broad row was compared or advanced before filter preparation");
                return fresh;
            });
            fixture.Bridge.Replies.Enqueue(() => Result(fresh.Candidates[^1], "fresh-filtered-result"));
            await fixture.Run(reminder);

            Check(fixture.Scenario.StartReads == 1 && fixture.Scenario.FilterReads == 1 &&
                fixture.Scenario.FilterInputs.Count == 1 && fixture.Scenario.NextPositions.Count == 0,
                "Broad-to-bounded route repeated reading/filtering or advanced the row");
            var comparison = fixture.Bridge.Comparisons.Single();
            Check(comparison.Supplier == fresh.Supplier && comparison.Source == fresh.Title &&
                comparison.Candidates.SequenceEqual(fresh.Candidates), "Bridge received stale/truncated unfiltered candidates");
            Check(Directory.GetFiles(before.Directory).Length == 0, "Fresh comparison was saved beside the old observation");
            var report = Evidence(fresh);
            Check(report.GetProperty("request").GetProperty("candidates").EnumerateArray().Select(value => value.GetString())
                .SequenceEqual(fresh.Candidates), "Full evidence did not retain fresh filtered candidates");
            Check(report.GetProperty("result").GetProperty("candidate").GetString() == fresh.Candidates[^1],
                "Filtered candidate was reinterpreted in evidence");
            Check(fixture.Window.ReviewShows.Count == 1 && fixture.Window.LessonShows.Count == 1 &&
                fixture.Window.ConfirmationOffers.Count == 1 && ReferenceEquals(fixture.Window.Confirmation!.Observation, fresh) &&
                fixture.Window.Confirmation.Candidate == fresh.Candidates[^1], "Review/confirmation retained the broad observation");
            Check(fixture.Store.Read().Count == (revisit ? 1 : 0) &&
                fixture.Window.Outcomes.Single() == (GingerChatTaskState.PairReady, 0), "Bounded pair was spuriously deferred");
            Check(fixture.Window.Progress.Stages.Count(stage => stage.Stage == GingerChatStage.Filtering) == 1 &&
                fixture.Window.Progress.Stages.Count(stage => stage.Stage == GingerChatStage.Comparing) == 1,
                "UI stages did not reflect one filter followed by one comparison");

            string[]? lessonCandidates = null;
            await fixture.Window.ShownLesson!.PreviewAsync("chat-current", "Synthetic lesson after filtering",
                (supplier, source, lesson, candidates, token) =>
                { lessonCandidates = candidates.ToArray(); return Task.FromResult(Result(null)); }, CancellationToken.None);
            Check(lessonCandidates!.SequenceEqual(fresh.Candidates), "Lesson card retained broad candidates after filtering");
            Released(fixture);
        }
    }

    private static async Task StillBroadCannotCompareDeferOrAdvance()
    {
        foreach (var initialCount in new[] { 11, 256 })
        foreach (var finalCount in new[] { 11, 256 })
        foreach (var revisit in new[] { false, true })
        {
            using var fixture = new Fixture(count: 2);
            var before = ReplaceCandidates(fixture, initialCount);
            var fresh = FilteredObservation(fixture, before, finalCount);
            GingerDeferredRow? reminder = revisit ? fixture.Store.AddManualReview(Item(before), "saved-chat", before.Directory) : null;
            var reminderPath = reminder is null ? null : Path.Combine(fixture.DeferredRoot, reminder.Id + ".json");
            var reminderBytes = reminderPath is null ? null : File.ReadAllBytes(reminderPath);
            fixture.Scenario.FilterReplies.Enqueue(input =>
            {
                Check(ReferenceEquals(input, before), "Still-broad filter received another row");
                return fresh;
            });
            // A valid queued answer ensures any accidental bridge call could succeed;
            // the regression must fail because comparison was invoked, not a fake error.
            fixture.Bridge.Replies.Enqueue(() => Result(fresh.Candidates[0]));
            await fixture.Run(reminder);

            Check(fixture.Scenario.StartReads == 1 && fixture.Scenario.FilterReads == 1 &&
                fixture.Scenario.FilterInputs.Count == 1, "Still-broad route replanned/replayed its filter budget");
            Check(fixture.Bridge.Comparisons.Count == 0 && fixture.Bridge.Replies.Count == 1 &&
                fixture.Scenario.NextPositions.Count == 0, "Still-broad route invoked model comparison or next-row input");
            Check(Directory.GetFiles(before.Directory).Length == 0 && Directory.GetFiles(fresh.Directory).Length == 0,
                "Still-broad route published comparison evidence without a bounded comparison");
            var saved = fixture.Store.Read();
            Check(saved.Count == (revisit ? 1 : 0), "Still-broad row became a new manual/empty deferral");
            if (reminder is not null)
                Check(saved.Single() == reminder && File.ReadAllBytes(reminderPath!).SequenceEqual(reminderBytes!),
                    "Still-broad revisit rewrote the existing reminder");
            Check(fixture.Window.Outcomes.Single() == (GingerChatTaskState.Failed, 0),
                "Still-broad route claimed completion or durable manual deferral");
            Check(fixture.Window.Progress.Stages.Count(stage => stage.Stage == GingerChatStage.Filtering) == 1 &&
                !fixture.Window.Progress.Stages.Any(stage => stage.Stage is GingerChatStage.Comparing or GingerChatStage.Skipping or GingerChatStage.Empty),
                "Still-broad progress entered comparison/deferral/empty state");
            Check(fixture.Window.Progress.Details.Contains($"осталось {finalCount} кандидатов", StringComparison.Ordinal) &&
                fixture.Window.Progress.Details.Contains("Сузьте поиск вручную", StringComparison.Ordinal),
                "Still-broad final transcript lost the actionable count/manual-search explanation");
            NoCards(fixture); Released(fixture);
        }
    }

    private static async Task ModelSearchRoutesThroughChat()
    {
        foreach (var mode in new[] { "selected", "manual", "blocked", "legacy", "v1", "bad-evidence", "bad-source", "changed-chat", "failed" })
        {
            using var fixture = new Fixture(count: 2);
            var before = ReplaceCandidates(fixture, 20);
            fixture.Scenario.UseSearchPlanner = true;
            var afterPath = Path.Combine(fixture.Root, "reasoned-observation");
            Directory.CreateDirectory(afterPath);
            var after = Observation(afterPath, before.Position, before.Count, false, ["Chosen candidate"], enabled: true);
            fixture.Scenario.FilterReplies.Enqueue(_ => after);
            fixture.Bridge.Replies.Enqueue(() => Result(mode is "manual" or "blocked"
                ? fixture.Scenario.Rows[1].Candidates[0] : "Chosen candidate"));
            fixture.Bridge.SearchReply = request => {
                if (mode == "failed") throw new InvalidDataException("Synthetic model failure");
                if (mode == "changed-chat") fixture.Bridge.CurrentChat = "another-chat";
                var requestId = request.GetProperty("request_id").GetString();
                if (mode == "legacy") return JsonSerializer.SerializeToElement(new {
                    ok = true, command = "ginger.search.word.preview", mode = "search_word",
                    request_id = requestId, word = "Synthetic", reason = "Legacy model rationale", can_match = false
                });
                if (mode == "v1") return JsonSerializer.SerializeToElement(new {
                    ok = true, command = "ginger.search.word.preview", mode = "search_word",
                    request_id = requestId, word = "Synthetic", reason = "Synthetic model rationale", can_match = false,
                    search_evidence = new { version = 1, definitions = Array.Empty<object>(), blocked_reason = (string?)null,
                        proposal = new { request_id = requestId, word = "Synthetic", role = "name",
                            source_quote = "Synthetic", reason = "Synthetic model rationale" } }
                });
                var reason = mode switch {
                    "blocked" => "Synthetic veto: conflicting definitions",
                    "manual" => "Synthetic host abstention",
                    _ => "Synthetic host explanation; role is a model estimate"
                };
                return JsonSerializer.SerializeToElement(new {
                    ok = true, command = "ginger.search.word.preview", mode = "search_word",
                    request_id = requestId, word = mode is "manual" or "blocked" ? null : "Synthetic", reason, can_match = false,
                    search_evidence = new {
                        version = 2, definitions = Array.Empty<object>(),
                        blocked_reason = mode is "manual" or "blocked" ? reason : null,
                        source_evidence = mode == "manual" ? null : new {
                            origin = "observed_source", derived_by = "host", quote = mode == "bad-source" ? "synthetic" : "Synthetic" },
                        proposal = new { request_id = mode == "bad-evidence" ? new string('0', 32) : requestId,
                            word = mode == "manual" ? null : "Synthetic", role = mode == "manual" ? "unknown" : "name",
                            reason = "Synthetic model rationale" }
                    }
                });
            };
            await fixture.Run();
            Check(fixture.Bridge.SearchRequests.Count == 1 && fixture.Scenario.FilterReads == 1,
                "Chat omitted or repeated Qwen search decision");
            if (mode == "selected")
            {
                Check(fixture.Scenario.FilterActions == 1 && fixture.Bridge.Comparisons.Count == 1 &&
                    ReferenceEquals(fixture.Window.Confirmation?.Observation, after), "Chat did not compare the newly filtered snapshot");
                Check(fixture.Window.Progress.Transcript.Contains("Synthetic host explanation; role is a model estimate") &&
                    !fixture.Window.Progress.Transcript.Contains("Synthetic model rationale"),
                    "Chat lost host explanation or promoted raw model rationale");
                Check(Directory.GetFiles(before.Directory, "search-decision-*.json").Length == 1, "Chat omitted search evidence");
            }
            else if (mode is "manual" or "blocked")
            {
                Check(fixture.Scenario.FilterActions == 0 && fixture.Scenario.FreshReads == 1 &&
                    fixture.Scenario.NextPositions.SequenceEqual(new[] { 2 }) && fixture.Store.Read().Count == 1,
                    "Valid semantic refusal did not defer after a fresh read and continue once");
                Check(fixture.Bridge.Comparisons.Single().Source == fixture.Scenario.Rows[1].Title &&
                    fixture.Window.Confirmation?.Observation == fixture.Scenario.Rows[1],
                    "Search refusal was compared or left stale confirmation on the skipped row");
                var saved = Directory.GetFiles(before.Directory, "search-decision-*.json");
                Check(saved.Length == 1, "Valid search refusal lost its decision journal");
                if (mode == "blocked")
                {
                    using var record = JsonDocument.Parse(File.ReadAllText(saved.Single()));
                    var result = record.RootElement.GetProperty("result");
                    Check(result.GetProperty("word").ValueKind == JsonValueKind.Null &&
                        result.GetProperty("search_evidence").GetProperty("proposal").GetProperty("word").GetString() == "Synthetic" &&
                        result.GetProperty("search_evidence").GetProperty("source_evidence").GetProperty("quote").GetString() == "Synthetic",
                        "Deferred veto lost the non-actionable raw proposal");
                }
            }
            else
            {
                Check(fixture.Scenario.FilterActions == 0 && fixture.Bridge.Comparisons.Count == 0 &&
                    fixture.Scenario.NextPositions.Count == 0 && fixture.Store.Read().Count == 0,
                    "Rejected model search clicked, compared, deferred or advanced");
                var saved = Directory.GetFiles(before.Directory, "search-decision-*.json");
                Check(saved.Length == 0, "Invalid or stale search response was published as decision evidence");
                NoCards(fixture);
            }
            Released(fixture);
        }
    }

    private static JsonElement SearchDecline(JsonElement request) => JsonSerializer.SerializeToElement(new {
        ok = true, command = "ginger.search.word.preview", mode = "search_word",
        request_id = request.GetProperty("request_id").GetString(), word = (string?)null,
        reason = "Synthetic unresolved fragment; manual search review required", can_match = false,
        search_evidence = new { version = 2, definitions = Array.Empty<object>(), proposal = (object?)null,
            source_evidence = (object?)null, blocked_reason = "Synthetic unresolved fragment; manual search review required" }
    });

    private static GingerDesktopObservation CopyObservation(GingerDesktopObservation row, string directory, long? overflow = null)
    {
        var json = System.Text.Json.Nodes.JsonNode.Parse(row.Data.GetRawText())!.AsObject();
        json["directory"] = directory;
        if (overflow is not null)
        {
            json["status"] = "needs_filters";
            json["candidate_count"] = overflow.Value;
            json.Remove("candidates");
        }
        return new(directory, JsonSerializer.SerializeToElement(json));
    }

    private static async Task SearchDeclineDefersFreshRowThenShowsNextPair()
    {
        foreach (var mode in new[] { "current213", "initial278", "next400" })
        {
            using var fixture = new Fixture(count: mode == "next400" ? 3 : 2);
            var index = mode == "next400" ? 1 : 0;
            if (index == 1)
            {
                var linked = fixture.Scenario.Rows[0];
                fixture.Scenario.Rows[0] = Observation(linked.Directory, linked.Position, linked.Count, true, linked.Candidates);
            }
            var original = fixture.Scenario.Rows[index];
            var broad = mode == "current213"
                ? ReplaceCandidates(fixture, 213)
                : CopyObservation(original, original.Directory, mode == "initial278" ? 278 : 400);
            fixture.Scenario.Rows[index] = broad;
            var freshDirectory = Path.Combine(fixture.Root, "fresh-search-refusal");
            Directory.CreateDirectory(freshDirectory);
            var fresh = CopyObservation(broad, freshDirectory);
            var final = fixture.Scenario.Rows[index + 1];
            fixture.Scenario.UseSearchPlanner = true;
            fixture.Scenario.RefreshReplies.Enqueue(input => {
                Check(ReferenceEquals(input, broad), "Semantic refusal refreshed a stale observation");
                return fresh;
            });
            fixture.Bridge.SearchReply = SearchDecline;
            fixture.Bridge.Replies.Enqueue(() => Result(final.Candidates[1], "final-pair-after-search-review"));
            var durableBeforeNext = false;
            fixture.Scenario.BeforeNext = before => {
                if (before.Position != broad.Position) return;
                Check(ReferenceEquals(before, fresh), "Next-row input did not use the fresh refused observation");
                var saved = fixture.Store.Read().Single();
                Check(saved.Title == fresh.Title && saved.Position == fresh.Position &&
                    saved.Supplier == fresh.Supplier && saved.Document == fresh.Document,
                    "Search refusal deferred another row or a fabricated empty-source identity");
                Check(saved.Reason != GingerDeferredRows.NoCandidatesReason && saved.Reason != GingerDeferredRows.ManualReviewReason,
                    "Semantic search refusal masqueraded as an empty list or a completed candidate comparison");
                Check(saved.ObservationDirectory == broad.Directory && saved.Reason == GingerDeferredRows.SearchReviewPrefix +
                    "Synthetic unresolved fragment; manual search review required",
                    "Search reminder lost its original decision folder or exact semantic refusal reason");
                Check(Directory.GetFiles(broad.Directory, "search-decision-*.json").Length == 1 &&
                    Directory.GetFiles(fresh.Directory, "comparison-*.json").Length == 0 && fixture.Bridge.Comparisons.Count == 0,
                    "Durable search evidence was missing, or broad/overflow candidates were compared");
                using var decision = JsonDocument.Parse(File.ReadAllText(Directory.GetFiles(broad.Directory, "search-decision-*.json").Single()));
                Check(decision.RootElement.GetProperty("request").GetProperty("candidate_count").GetInt64() == broad.CandidateCount &&
                    decision.RootElement.GetProperty("result").GetProperty("word").ValueKind == JsonValueKind.Null,
                    "Saved decision lost the true broad candidate count or semantic null");
                Check(fixture.Window.ReviewShows.Count == 0 && fixture.Window.LessonShows.Count == 0 && fixture.Window.ConfirmationOffers.Count == 0,
                    "Search-deferred row showed comparison/rating/lesson/pair cards");
                durableBeforeNext = true;
            };
            await fixture.Run();
            Check(durableBeforeNext && fixture.Scenario.FilterReads == 1 && fixture.Scenario.FilterActions == 0 && fixture.Scenario.FreshReads == 1 &&
                fixture.Bridge.SearchRequests.Count == 1, "Semantic refusal retried search or sent a filter action");
            Check(fixture.Scenario.NextPositions.SequenceEqual(index == 0 ? new[] { 2 } : new[] { 2, 3 }),
                "Overflow/current refusal skipped, retried, or remained on the wrong row");
            Check(fixture.Bridge.Comparisons.Single().Source == final.Title &&
                fixture.Bridge.Comparisons.Single().Candidates.SequenceEqual(final.Candidates), "Final comparison used refused-row candidates");
            Check(fixture.Window.ShownReview?.Source == final.Title && fixture.Window.ShownLesson?.Source == final.Title &&
                fixture.Window.Confirmation?.Observation == final && fixture.Window.Confirmation?.Candidate == final.Candidates[1],
                "Final review, lesson, or confirmation belongs to the refused row");
            JsonElement? rated = null;
            await fixture.Window.ShownReview!.RateAsync("chat-current", 9, "Synthetic callback only", (report, _, _) => {
                rated = report.Clone(); return Task.FromResult(new GingerAnswerRatingReceipt("synthetic", true));
            });
            Check(rated!.Value.GetProperty("request").GetProperty("source").GetString() == final.Title &&
                rated.Value.GetProperty("request").GetProperty("candidates").EnumerateArray().Select(value => value.GetString()).SequenceEqual(final.Candidates),
                "Rating retained source/candidates from the search-deferred row");
            string? lessonSource = null;
            string[]? lessonCandidates = null;
            await fixture.Window.ShownLesson!.PreviewAsync("chat-current", "Synthetic unsaved lesson", (supplier, source, lesson, candidates, token) => {
                lessonSource = source; lessonCandidates = candidates.ToArray(); return Task.FromResult(Result(null));
            }, CancellationToken.None);
            Check(lessonSource == final.Title && lessonCandidates!.SequenceEqual(final.Candidates), "Lesson editor retained overflow or old candidates");
            Check(fixture.Window.Outcomes.Single() == (GingerChatTaskState.PairReady, 1) &&
                fixture.Window.Notifications.Single() == GingerNotificationKind.PairReady,
                "Search deferral did not report the final pair and actual durable count");
            Check(!Directory.Exists(Path.Combine(fixture.Root, "BabyAI", "ginger-learning")), "Review callbacks saved learning without a save action");
            Released(fixture);
        }
    }

    private static async Task SearchDeclineAtEndHasNoPairCards()
    {
        foreach (var overflow in new[] { false, true })
        {
            using var fixture = new Fixture(count: 1);
            var before = ReplaceCandidates(fixture, 213);
            if (overflow) fixture.Scenario.Rows[0] = CopyObservation(before, before.Directory, 278);
            fixture.Scenario.UseSearchPlanner = true;
            fixture.Bridge.SearchReply = SearchDecline;
            await fixture.Run();
            Check(fixture.Store.Read().Single().Position == 1 && fixture.Scenario.NextPositions.Count == 0 &&
                fixture.Bridge.Comparisons.Count == 0 && fixture.Scenario.FilterActions == 0,
                "End-of-price semantic refusal was compared, clicked, or advanced");
            Check(fixture.Window.Outcomes.Single() == (GingerChatTaskState.End, 1) &&
                fixture.Window.Notifications.Single() == GingerNotificationKind.ManualReview,
                "End-of-price semantic refusal claimed a pair or lost the saved review count");
            NoCards(fixture); Released(fixture);
        }
    }

    private static async Task SearchFailuresNeverBecomeDeferrals()
    {
        foreach (var mode in new[] { "deferred-save", "evidence-save", "changed-source", "changed-chat", "cancel", "read-error", "read-timeout", "malformed" })
        {
            using var fixture = new Fixture(count: 2);
            var before = ReplaceCandidates(fixture, 213);
            fixture.Scenario.UseSearchPlanner = true;
            if (mode == "deferred-save")
            {
                Directory.CreateDirectory(Path.GetDirectoryName(fixture.DeferredRoot)!);
                File.WriteAllText(fixture.DeferredRoot, "Synthetic deferred-store obstruction");
            }
            fixture.Bridge.SearchReply = request => {
                if (mode == "evidence-save")
                {
                    Directory.Delete(before.Directory);
                    File.WriteAllText(before.Directory, "Synthetic decision-journal obstruction");
                }
                return mode == "malformed" ? Parse("{}") : SearchDecline(request);
            };
            fixture.Scenario.RefreshReplies.Enqueue(input => {
                if (mode == "changed-source") return fixture.Scenario.Rows[1];
                if (mode == "changed-chat") fixture.Bridge.CurrentChat = "another-chat";
                if (mode == "cancel") fixture.Window.CancelRun();
                if (mode == "read-error") throw new IOException("Synthetic fresh-read failure");
                if (mode == "read-timeout") throw new TimeoutException("Synthetic fresh-read timeout");
                return input;
            });
            await fixture.Run();
            Check(fixture.Scenario.NextPositions.Count == 0 && fixture.Scenario.FilterActions == 0 && fixture.Bridge.Comparisons.Count == 0,
                "Failed or stale semantic search caused a next/filter/compare operation: " + mode);
            if (mode == "deferred-save")
                Check(File.ReadAllText(fixture.DeferredRoot) == "Synthetic deferred-store obstruction", "Deferred-save failure overwrote its obstruction");
            else Check(fixture.Store.Read().Count == 0, "A search error was stored as an ordinary review: " + mode);
            var state = mode is "changed-chat" or "cancel" ? GingerChatTaskState.Cancelled
                : mode == "read-timeout" ? GingerChatTaskState.TimedOut : GingerChatTaskState.Failed;
            Check(fixture.Window.Outcomes.Single() == (state, 0), "Search failure was counted as a semantic deferral: " + mode);
            Check(fixture.Window.Notifications.Single() == (state == GingerChatTaskState.Cancelled
                ? GingerNotificationKind.Cancelled : GingerNotificationKind.Failed), "Search error notification claimed review/completion");
            if (mode == "evidence-save")
                Check(File.ReadAllText(before.Directory) == "Synthetic decision-journal obstruction" && fixture.Scenario.FreshReads == 0,
                    "Evidence write failure was ignored before fresh read");
            NoCards(fixture); Released(fixture);
        }
    }

    private static async Task RevisitSearchDeclineStaysOnItsRow()
    {
        using var fixture = new Fixture(count: 2);
        var before = ReplaceCandidates(fixture, 213);
        var reminder = fixture.Store.AddManualReview(Item(before), "saved-chat", before.Directory);
        var path = Path.Combine(fixture.DeferredRoot, reminder.Id + ".json");
        var original = File.ReadAllBytes(path);
        fixture.Scenario.UseSearchPlanner = true;
        fixture.Bridge.SearchReply = SearchDecline;
        await fixture.Run(reminder);
        Check(fixture.Scenario.NextPositions.Count == 0 && fixture.Bridge.Comparisons.Count == 0 && fixture.Scenario.FilterActions == 0,
            "Explicit revisit used a semantic refusal to advance/compare/filter");
        Check(fixture.Store.Read().Single() == reminder && File.ReadAllBytes(path).SequenceEqual(original),
            "Explicit revisit rewrote the original reminder");
        Check(fixture.Window.Outcomes.Single() == (GingerChatTaskState.Review, 0) &&
            fixture.Window.Notifications.Single() == GingerNotificationKind.ManualReview,
            "Explicit revisit refusal became a failed scan or a completed pair");
        Check(!fixture.Window.Progress.Stages.Any(stage => stage.Stage == GingerChatStage.Failed),
            "Explicit semantic review was briefly reported as a failure before the final review outcome");
        NoCards(fixture); Released(fixture);
    }

    private static GingerDesktopObservation Observation(string directory, int position, int count, bool linked, string[] candidates, bool enabled = false)
        => new(directory, JsonSerializer.SerializeToElement(new {
            status = "observed", directory, search_completion_verified = false, process_id = 1234L, row_count = count,
            source = new {
                Title = "Synthetic source " + position, Supplier = "Synthetic supplier", DocumentNumber = "Synthetic document",
                PriceText = "100", LinkedProductText = linked ? "Linked candidate" : "<не указано>",
                ObservedRowPosition = position, TableHandle = 5678L, IdentityVerified = false, CanMatch = false
            },
            words = new[] { new { Title = "Synthetic", DescriptionText = "", Checked = enabled ? (bool?)true : null, StateImage = enabled ? 2 : 0,
                ObservedPosition = 1, IdentityVerified = false } },
            candidates = candidates.Select((title, index) => new { Title = title, ObservedPosition = index + 1, IdentityVerified = false })
        }));

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
                Scenario.Rows.Add(Observation(directory, position, count, linkedLast && position == count,
                    ["Candidate " + position + "A", "Candidate " + position + "B"]));
            }
            Window = new MainWindow(Bridge);
        }
        internal async Task Run(GingerDeferredRow? revisit = null)
        {
            await Window.RunAsync(revisit).WaitAsync(TimeSpan.FromSeconds(10));
            Check(Scenario.Session?.FilterCandidateTarget == GingerChatPreparation.MaxCandidates,
                "Production chat session did not pass its comparison budget into word filtering");
            Check(Scenario.Session?.ChooseSearchWord is not null,
                "Production chat session omitted the model search planner");
        }
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
