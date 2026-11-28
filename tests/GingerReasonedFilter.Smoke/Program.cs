using BabyAI.Desktop;
using System.Text.Json;
using System.Text.Json.Nodes;

await ReasonedFilterTests.RunAsync();

// Pure orchestration tests: no filesystem, process, environment lookup, UI or model.
// The real observation parser and both production filter validators are linked.
internal static class ReasonedFilterTests
{
    private static int _checks, _snapshot;
    private static readonly string[] Words = ["White", "Label", "ALDHEYX", "100", "EDP", "мл"];

    internal static async Task RunAsync()
    {
        await AlreadyBounded();
        await OneActionAndRealNullState();
        await AdaptiveHistory();
        await EmptyIsTerminal();
        await InitialEmptyMayChoose();
        await BudgetAcrossCalls();
        await InvalidDecisions();
        await StaleRefresh();
        await RequireCurrentIsLastGuard();
        await CancellationBoundaries();
        await FailedActionsCannotReplay();
        await ConcurrentCallsAreRejected();
        Console.WriteLine($"PASS: {_checks} reasoned-filter checks; linked production flow/observations, adaptive history, four-action budget, fresh guards and terminal zero; no UI/model/files/processes");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
        _checks++;
    }

    private static async Task<Exception> Fails(Func<Task> action, string message)
    {
        try { await action(); }
        catch (Exception error) when (error is InvalidDataException or IOException or OperationCanceledException or InvalidOperationException)
        { _checks++; return error; }
        throw new Exception("Expected failure: " + message);
    }

    private static GingerDesktopObservation Observation(int candidates, bool realNullState = false)
    {
        var data = JsonSerializer.SerializeToNode(new {
            status = "observed", directory = "synthetic", row_count = 20, process_id = 1234L,
            search_completion_verified = false,
            source = new { Title = "Synthetic full item 100 мл", Supplier = "Synthetic supplier", DocumentNumber = "Invoice 7",
                ObservedRowPosition = 3, TableHandle = 5678L, PriceText = "100.00", LinkedProductText = "<не указано>",
                IdentityVerified = false, CanMatch = false },
            words = Words.Select((word, index) => new { Title = word, DescriptionText = "Visible " + word,
                ObservedPosition = index + 1, StateImage = realNullState ? 0 : 1, Checked = realNullState ? (bool?)null : false,
                IdentityVerified = false }).ToArray()
        })!.AsObject();
        SetCandidates(data, candidates);
        return Snapshot(data);
    }

    private static void SetCandidates(JsonObject data, int count)
    {
        data.Remove("candidate_count"); data.Remove("candidates");
        data["status"] = count > 256 ? "needs_filters" : "observed";
        if (count > 256) data["candidate_count"] = count;
        else data["candidates"] = JsonSerializer.SerializeToNode(Enumerable.Range(1, count)
            .Select(index => new { Title = $"Candidate {count}:{index}", ObservedPosition = index, IdentityVerified = false }).ToArray());
    }

    private static GingerDesktopObservation Snapshot(JsonObject data)
    {
        var directory = "synthetic-observation-" + Interlocked.Increment(ref _snapshot);
        data["directory"] = directory;
        return new(directory, JsonSerializer.SerializeToElement(data));
    }

    private static GingerDesktopObservation Change(GingerDesktopObservation before, Action<JsonObject>? change = null)
    {
        var data = JsonNode.Parse(before.Data.GetRawText())!.AsObject();
        change?.Invoke(data);
        return Snapshot(data);
    }

    private static GingerDesktopObservation Applied(GingerDesktopObservation before, string word, int count) => Change(before, data =>
    {
        var selected = data["words"]!.AsArray().Single(row => row!["Title"]!.GetValue<string>() == word)!;
        selected["Checked"] = true; selected["StateImage"] = 2;
        SetCandidates(data, count);
    });

    private static async Task AlreadyBounded()
    {
        foreach (var count in new[] { 1, 10 })
        {
            var harness = new Harness();
            var observed = Observation(count);
            Check(ReferenceEquals(await harness.Run(observed), observed), "Bounded snapshot was replaced");
            Check(harness.ChooseCalls == 0 && harness.RefreshCalls == 0 && harness.ApplyCalls == 0,
                "A bounded list reached model/refresh/input");
        }
    }

    private static async Task OneActionAndRealNullState()
    {
        foreach (var realNullState in new[] { false, true })
        {
            var harness = new Harness();
            harness.Decisions.Enqueue(new("White", "Название исключает лишние варианты."));
            harness.AfterCounts.Enqueue(10);
            var before = Observation(278, realNullState);
            var after = await harness.Run(before);
            Check(after.CandidateCount == 10 && !after.NeedsFilters, "278→10 did not produce a complete bounded result");
            Check(harness.ChooseCalls == 1 && harness.RefreshCalls == 1 && harness.ApplyCalls == 1,
                "278→10 did not stop after one decision/read/input");
            Check(ReferenceEquals(harness.ApplyInputs.Single(), harness.Refreshed.Single()) &&
                !ReferenceEquals(harness.ApplyInputs.Single(), before), "Input used old observation instead of refreshed snapshot");
            Check(harness.Messages.Single() == "Qwen выбрала «White»: Название исключает лишние варианты.", "Decision reason was not reported before input");
            Check(after.Words[0].GetProperty("Checked").ValueKind == JsonValueKind.True && after.Words[0].GetProperty("StateImage").GetInt32() == 2,
                "Real null/0 or false/1 unchecked state did not become checked/2");
        }
    }

    private static async Task AdaptiveHistory()
    {
        foreach (var intermediate in new[] { 25, 300 })
        {
            var harness = new Harness();
            harness.Decisions.Enqueue(new("White", "Сначала название."));
            harness.Decisions.Enqueue(new("100", "Теперь уточняю объём по новому списку."));
            harness.AfterCounts.Enqueue(intermediate); harness.AfterCounts.Enqueue(8);
            var after = await harness.Run(Observation(278));
            Check(after.CandidateCount == 8 && harness.ChooseCalls == 2 && harness.ApplyWords.SequenceEqual(new[] { "White", "100" }),
                "Adaptive second decision was not applied exactly once");
            Check(ReferenceEquals(harness.ChooseInputs[1], harness.AppliedResults[0]) &&
                harness.ChooseInputs[1].CandidateCount == intermediate, "Second Qwen request used a stale candidate snapshot");
            Check(harness.Histories[0].Count == 0 && harness.Histories[1].SequenceEqual(new[] { new GingerSearchFilterEffect("White", 278, intermediate) }),
                "Qwen did not receive exactly the verified prior effect");
            Check(harness.Histories[0].Count == 0, "Retained first history changed after a later action");
            var immutable = false;
            try { ((IList<GingerSearchFilterEffect>)harness.Histories[1]).Add(new("invented", 1, 2)); }
            catch (NotSupportedException) { immutable = true; }
            Check(immutable, "Callback could mutate action history");
        }
    }

    private static async Task EmptyIsTerminal()
    {
        var harness = new Harness();
        harness.Decisions.Enqueue(new("White", "Проверяю название.")); harness.AfterCounts.Enqueue(0);
        var empty = await harness.Run(Observation(278));
        Check(empty.CandidateCount == 0 && harness.ChooseCalls == 1 && harness.ApplyCalls == 1, "Zero did not stop immediately after verified input");
        var freshEmpty = Change(empty);
        Check(ReferenceEquals(await harness.Run(freshEmpty), freshEmpty), "Terminal zero returned a stale snapshot");
        Check(harness.ChooseCalls == 1 && harness.RefreshCalls == 1 && harness.ApplyCalls == 1, "Repeated zero restarted the model or input");
        await Fails(() => harness.Run(Change(empty, data => SetCandidates(data, 30))), "terminal zero changed to broad");
        Check(harness.ChooseCalls == 1 && harness.ApplyCalls == 1, "Changed terminal row restarted model decisions");
    }

    private static async Task InitialEmptyMayChoose()
    {
        var harness = new Harness();
        harness.Decisions.Enqueue(new("100", "Уточняю исходный пустой поиск.")); harness.AfterCounts.Enqueue(2);
        Check((await harness.Run(Observation(0))).CandidateCount == 2 && harness.ChooseCalls == 1 && harness.ApplyCalls == 1,
            "Initial zero was confused with a verified terminal zero");
    }

    private static async Task BudgetAcrossCalls()
    {
        var harness = new Harness();
        var current = Observation(278);
        for (var index = 0; index < 4; index++)
        {
            harness.Decisions.Enqueue(new(Words[index], "Следующее отдельное уточнение."));
            harness.AfterCounts.Enqueue(40 - index);
            // An empty fake queue throws on the following model request. It leaves
            // the successfully verified action in this same session's budget.
            await Fails(() => harness.Run(current), "continue on a later call");
            current = harness.AppliedResults[^1];
            Check(harness.ApplyCalls == index + 1, "Action count was reset across calls");
        }
        var chooseBefore = harness.ChooseCalls;
        var limit = await Fails(() => harness.Run(current), "exhausted cross-call budget");
        Check(harness.ChooseCalls == chooseBefore && harness.ApplyCalls == 4 && limit.Message.Contains("4", StringComparison.Ordinal),
            "Exhausted budget asked the model or sent a fifth action");
        Check(harness.Histories.Last().Count == 3, "Fourth action lost verified history from previous calls");
        var independent = Change(Observation(278), data => data["source"]!["DocumentNumber"] = "Another invoice");
        harness.Decisions.Enqueue(new("White", "Новый документ.")); harness.AfterCounts.Enqueue(1);
        Check((await harness.Run(independent)).CandidateCount == 1 && harness.Histories.Last().Count == 0,
            "Independent source inherited another row's budget/history");
    }

    private static async Task InvalidDecisions()
    {
        foreach (var decision in new GingerSearchWordDecision?[] {
            null, new(null, "Нельзя надёжно сузить этот список."), new("", "reason"), new(" ", "reason"),
            new(new string('w', 513), "reason"), new("White", ""), new("White", " \n"),
            new("White", null!), new("White", new string('r', 601)), new("missing", "reason"),
            new("white", "reason"), new("White ", "reason") })
        {
            var harness = new Harness();
            harness.Decisions.Enqueue(decision!);
            var error = await Fails(() => harness.Run(Observation(278)), "invalid/null decision");
            Check(harness.RefreshCalls == 0 && harness.ApplyCalls == 0 && harness.ChooseCalls == 1,
                "Invalid/null decision caused refresh/input/fallback");
            if (decision is { Word: null }) Check(error.Message.Contains(decision.Reason, StringComparison.Ordinal), "Manual exception lost Qwen's reason");
        }
        foreach (var transform in new Action<JsonObject>[] {
            data => { data["words"]![0]!["Checked"] = true; data["words"]![0]!["StateImage"] = 2; },
            data => data["words"]![1]!["Title"] = "White"
        })
        {
            var harness = new Harness(); harness.Decisions.Enqueue(new("White", "reason"));
            await Fails(() => harness.Run(Change(Observation(278), transform)), "checked/duplicate word");
            Check(harness.ApplyCalls == 0 && harness.RefreshCalls == 0, "Checked/duplicate word reached input");
        }
        var failed = new Harness { ChooseOverride = (_, _, _) => throw new IOException("Synthetic Qwen failure") };
        await Fails(() => failed.Run(Observation(278)), "model exception");
        Check(failed.ChooseCalls == 1 && failed.RefreshCalls == 0 && failed.ApplyCalls == 0, "Model error triggered retry/fallback");
    }

    private static async Task StaleRefresh()
    {
        var changes = new List<(string Name, int Count, Action<JsonObject> Change)> {
            ("process",278,data => data["process_id"] = 9999),
            ("table",278,data => data["source"]!["TableHandle"] = 9999),
            ("supplier",278,data => data["source"]!["Supplier"] = "Other"),
            ("document",278,data => data["source"]!["DocumentNumber"] = "Other"),
            ("position",278,data => data["source"]!["ObservedRowPosition"] = 4),
            ("title",278,data => data["source"]!["Title"] = "Other item"),
            ("price",278,data => data["source"]!["PriceText"] = "101.00"),
            ("linked",278,data => data["source"]!["LinkedProductText"] = "Now linked"),
            ("row count",278,data => data["row_count"] = 21),
            ("overflow count",278,data => SetCandidates(data, 279)),
            ("status",278,data => SetCandidates(data, 256)),
            ("candidate count",12,data => SetCandidates(data, 13)),
            ("candidate title",12,data => data["candidates"]![0]!["Title"] = "Changed candidate"),
            ("candidate order",12,data => { var a = data["candidates"]![0]!["Title"]!.GetValue<string>();
                data["candidates"]![0]!["Title"] = data["candidates"]![1]!["Title"]!.GetValue<string>(); data["candidates"]![1]!["Title"] = a; }),
            ("word title",278,data => data["words"]![1]!["Title"] = "Other"),
            ("word description",278,data => data["words"]![0]!["DescriptionText"] = "Changed meaning"),
            ("word state",278,data => { data["words"]![0]!["Checked"] = null; data["words"]![0]!["StateImage"] = 0; }),
            ("other word checked",278,data => { data["words"]![1]!["Checked"] = true; data["words"]![1]!["StateImage"] = 2; }),
            ("word order",278,data => { var a = data["words"]![0]!.DeepClone(); var b = data["words"]![1]!.DeepClone();
                a["ObservedPosition"] = 2; b["ObservedPosition"] = 1; data["words"]![0] = b; data["words"]![1] = a; }),
            ("word extra field",278,data => data["words"]![0]!["unexpected"] = "changed")
        };
        foreach (var change in changes)
        {
            var harness = new Harness(); harness.Decisions.Enqueue(new("White", "reason"));
            harness.RefreshOverride = (before, _) => Task.FromResult(Change(before, change.Change));
            await Fails(() => harness.Run(Observation(change.Count)), "stale " + change.Name);
            Check(harness.ChooseCalls == 1 && harness.RefreshCalls == 1 && harness.ApplyCalls == 0 && harness.Messages.Count == 0,
                "Stale " + change.Name + " reached report/input or retried");
        }
    }

    private static async Task RequireCurrentIsLastGuard()
    {
        var harness = new Harness();
        harness.Decisions.Enqueue(new("White", "reason", _ => { harness.Timeline.Add("guard"); return Task.CompletedTask; }));
        harness.AfterCounts.Enqueue(10);
        await harness.Run(Observation(278));
        Check(harness.Timeline.SequenceEqual(new[] { "choose", "refresh", "report", "guard", "apply" }),
            "Chat/lesson guard was not between report/fresh validation and input");
        foreach (var revokeAt in new[] { "refresh", "report" })
        {
            var revoked = false;
            var guarded = new Harness();
            guarded.Decisions.Enqueue(new("White", "reason", _ => revoked
                ? Task.FromException(new InvalidOperationException("Synthetic lesson/chat was revoked")) : Task.CompletedTask));
            if (revokeAt == "refresh") guarded.RefreshOverride = (before, _) => { revoked = true; return Task.FromResult(Change(before)); };
            else guarded.ReportHook = _ => revoked = true;
            await Fails(() => guarded.Run(Observation(278)), "revoked during " + revokeAt);
            Check(guarded.ApplyCalls == 0 && guarded.RefreshCalls == 1 && guarded.Messages.Count == 1,
                "Revoked context reached input after " + revokeAt);
        }
    }

    private static async Task CancellationBoundaries()
    {
        foreach (var boundary in new[] { "before", "choose", "refresh", "report", "guard", "apply" })
        {
            using var cancellation = new CancellationTokenSource();
            var harness = new Harness();
            var decision = new GingerSearchWordDecision("White", "reason", _ =>
            { if (boundary == "guard") cancellation.Cancel(); return Task.CompletedTask; });
            harness.Decisions.Enqueue(decision); harness.AfterCounts.Enqueue(0);
            if (boundary == "before") cancellation.Cancel();
            if (boundary == "choose") harness.ChooseOverride = (_, _, _) => { cancellation.Cancel(); return Task.FromResult(decision); };
            if (boundary == "refresh") harness.RefreshOverride = (before, _) => { cancellation.Cancel(); return Task.FromResult(Change(before)); };
            if (boundary == "report") harness.ReportHook = _ => cancellation.Cancel();
            if (boundary == "apply") harness.ApplyOverride = (before, word, _) => { cancellation.Cancel(); return Task.FromResult(Applied(before, word, 0)); };
            var error = await Fails(() => harness.Run(Observation(278), cancellation.Token), "cancel at " + boundary);
            Check(error is OperationCanceledException && harness.ApplyCalls == (boundary == "apply" ? 1 : 0), "Cancellation sent/replayed an input at " + boundary);
            if (boundary == "before") Check(harness.ChooseCalls == 0 && harness.RefreshCalls == 0, "Already cancelled call reached model");
        }
    }

    private static async Task FailedActionsCannotReplay()
    {
        foreach (var failure in new[] { "exception", "target unchecked", "other filter", "source changed", "words changed" })
        {
            var harness = new Harness();
            var original = Observation(278);
            harness.Decisions.Enqueue(new("White", "reason"));
            harness.ApplyOverride = (before, word, _) => failure switch {
                "exception" => throw new IOException("Synthetic uncertain driver failure"),
                "target unchecked" => Task.FromResult(Change(before, data => SetCandidates(data, 0))),
                "other filter" => Task.FromResult(Change(Applied(before, word, 0), data => { data["words"]![1]!["Checked"] = true; data["words"]![1]!["StateImage"] = 2; })),
                "source changed" => Task.FromResult(Change(Applied(before, word, 0), data => data["source"]!["Title"] = "Wrong item")),
                _ => Task.FromResult(Change(Applied(before, word, 0), data => data["words"]![1]!["DescriptionText"] = "Changed word"))
            };
            await Fails(() => harness.Run(original), "bad action result " + failure);
            Check(harness.ApplyCalls == 1 && harness.ChooseCalls == 1, "Bad transition retried or returned zero early");
            harness.Decisions.Enqueue(new("White", "must not replay"));
            await Fails(() => harness.Run(original), "repeat uncertain word " + failure);
            Check(harness.ApplyCalls == 1 && harness.RefreshCalls == 1 && harness.Histories[^1].Count == 0,
                "Failed input was replayed or advertised as a verified effect");
        }
        var budget = new Harness { ApplyOverride = (_, _, _) => throw new IOException("Synthetic input failure") };
        var observed = Observation(278);
        foreach (var word in Words.Take(4))
        {
            budget.Decisions.Enqueue(new(word, "separate attempt"));
            await Fails(() => budget.Run(observed), "uncertain input consumes attempt");
        }
        var chooseBefore = budget.ChooseCalls;
        await Fails(() => budget.Run(observed), "failed inputs exhaust budget");
        Check(budget.ApplyCalls == 4 && budget.ChooseCalls == chooseBefore, "Failed inputs did not consume the four-action budget");
    }

    private static async Task ConcurrentCallsAreRejected()
    {
        var gate = new TaskCompletionSource<GingerSearchWordDecision>(TaskCreationOptions.RunContinuationsAsynchronously);
        var harness = new Harness { ChooseOverride = (_, _, _) => gate.Task };
        harness.AfterCounts.Enqueue(10);
        var observed = Observation(278);
        var pending = harness.Run(observed);
        await Fails(() => harness.Run(observed), "concurrent call");
        Check(harness.ChooseCalls == 1 && harness.ApplyCalls == 0, "Concurrent call duplicated the model/action");
        gate.SetResult(new("White", "reason"));
        Check((await pending).CandidateCount == 10 && harness.ApplyCalls == 1, "Rejected concurrent call disturbed active call");
    }

    private sealed class Harness
    {
        internal readonly GingerReasonedFilterFlow Flow = new();
        internal readonly Queue<GingerSearchWordDecision> Decisions = [];
        internal readonly Queue<int> AfterCounts = [];
        internal readonly List<GingerDesktopObservation> ChooseInputs = [], Refreshed = [], ApplyInputs = [], AppliedResults = [];
        internal readonly List<IReadOnlyList<GingerSearchFilterEffect>> Histories = [];
        internal readonly List<string> ApplyWords = [], Messages = [], Timeline = [];
        internal int ChooseCalls, RefreshCalls, ApplyCalls;
        internal Func<GingerDesktopObservation, IReadOnlyList<GingerSearchFilterEffect>, CancellationToken, Task<GingerSearchWordDecision>>? ChooseOverride;
        internal Func<GingerDesktopObservation, CancellationToken, Task<GingerDesktopObservation>>? RefreshOverride;
        internal Func<GingerDesktopObservation, string, CancellationToken, Task<GingerDesktopObservation>>? ApplyOverride;
        internal Action<string>? ReportHook;
        internal Task<GingerDesktopObservation> Run(GingerDesktopObservation observed, CancellationToken token = default) =>
            Flow.PrepareAsync(observed, Choose, Refresh, Apply, token, text => { Timeline.Add("report"); Messages.Add(text); ReportHook?.Invoke(text); })
                .WaitAsync(TimeSpan.FromSeconds(5));
        private Task<GingerSearchWordDecision> Choose(GingerDesktopObservation observed, IReadOnlyList<GingerSearchFilterEffect> effects, CancellationToken token)
        {
            ChooseCalls++; Timeline.Add("choose"); ChooseInputs.Add(observed); Histories.Add(effects);
            return ChooseOverride?.Invoke(observed, effects, token) ?? Task.FromResult(Decisions.Dequeue());
        }
        private async Task<GingerDesktopObservation> Refresh(GingerDesktopObservation before, CancellationToken token)
        {
            RefreshCalls++; Timeline.Add("refresh");
            var fresh = RefreshOverride is null ? Change(before) : await RefreshOverride(before, token);
            Refreshed.Add(fresh); return fresh;
        }
        private async Task<GingerDesktopObservation> Apply(GingerDesktopObservation before, string word, CancellationToken token)
        {
            ApplyCalls++; Timeline.Add("apply"); ApplyInputs.Add(before); ApplyWords.Add(word);
            var result = ApplyOverride is null ? Applied(before, word, AfterCounts.Dequeue()) : await ApplyOverride(before, word, token);
            AppliedResults.Add(result); return result;
        }
    }
}
