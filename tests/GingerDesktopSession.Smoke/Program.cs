using BabyAI.Desktop;
using System.Text.Json;
using System.Text.Json.Nodes;

// Pure orchestration fixtures. No Session, subprocess, model, native UI or user data.
await FilterFlowTests.RunAsync();

internal static class FilterFlowTests
{
    private static int _checks;

    internal static async Task RunAsync()
    {
        ObservationContract();
        await OnePlanAndFreshChain();
        await CandidateTargetStopsEarly();
        await CandidateTargetBoundaries();
        await InvalidCandidateTargets();
        await CandidateTargetCancellation();
        await OrdinaryAndOptInBoundaries();
        await Exhaustion();
        await InvalidPlans();
        await Cancellation();
        await ChangedSourceAndFilters();
        await MaximumBound();
        Console.WriteLine($"PASS: {_checks} Ginger desktop observation/filter checks; synthetic only, no UI or processes");
    }

    private static JsonObject Fixture(string directory, int candidates, string[]? words = null, string[]? enabled = null)
    {
        words ??= ["Alpha", "Beta"];
        enabled ??= [];
        var value = new JsonObject
        {
            ["status"] = candidates > 256 ? "needs_filters" : "observed",
            ["directory"] = directory,
            ["process_id"] = 123,
            ["row_count"] = 165,
            ["search_completion_verified"] = false,
            ["source"] = new JsonObject
            {
                ["TableHandle"] = 42,
                ["ObservedRowPosition"] = 3,
                ["Title"] = "Alpha Beta 100 ml",
                ["Supplier"] = "Synthetic supplier",
                ["DocumentNumber"] = "D-123",
                ["PriceText"] = "10,00",
                ["LinkedProductText"] = "<не указано>",
                ["IdentityVerified"] = false,
                ["CanMatch"] = false
            },
            ["words"] = new JsonArray(words.Select((word, i) => (JsonNode)new JsonObject
            {
                ["ObservedPosition"] = i + 1,
                ["Title"] = word,
                ["DescriptionText"] = "Поиск: synthetic",
                ["StateImage"] = enabled.Contains(word) ? 2 : 1,
                ["Checked"] = enabled.Contains(word),
                ["IdentityVerified"] = false
            }).ToArray())
        };
        if (candidates > 256) value["candidate_count"] = candidates;
        else value["candidates"] = new JsonArray(Enumerable.Range(1, candidates).Select(i => (JsonNode)new JsonObject
        {
            ["ObservedPosition"] = i,
            ["Title"] = "Full candidate " + i,
            ["DescriptionText"] = "Unabridged details " + i,
            ["IdentityVerified"] = false
        }).ToArray());
        return value;
    }

    private static GingerDesktopObservation Read(JsonObject fixture)
    {
        using var json = JsonDocument.Parse(fixture.ToJsonString());
        return new GingerDesktopObservation(fixture["directory"]!.GetValue<string>(), json.RootElement);
    }
    private static GingerDesktopObservation Observe(string directory, int candidates, string[]? words = null, string[]? enabled = null)
        => Read(Fixture(directory, candidates, words, enabled));
    private static string Plan(params string[] words) => JsonSerializer.Serialize(new { steps = words.Select(word => new { word, enabled = true }) });
    private static void Check(bool value, string reason)
    {
        if (!value) throw new Exception(reason);
        _checks++;
    }
    private static T Throws<T>(Action action, string reason) where T : Exception
    {
        try { action(); }
        catch (T error) { _checks++; return error; }
        throw new Exception("Expected " + typeof(T).Name + ": " + reason);
    }
    private static async Task<T> ThrowsAsync<T>(Func<Task> action, string reason) where T : Exception
    {
        try { await action(); }
        catch (T error) { _checks++; return error; }
        throw new Exception("Expected " + typeof(T).Name + ": " + reason);
    }

    private static void ObservationContract()
    {
        var overflow = Observe("initial", 278);
        Check(overflow.NeedsFilters && overflow.CandidateCount == 278, "Overflow lost its actual count");
        Throws<InvalidDataException>(() => _ = overflow.Candidates, "Overflow must not masquerade as empty candidates");
        Throws<InvalidDataException>(overflow.RequireComplete, "Match/complete boundary accepted overflow");
        var full = Observe("full", 256);
        Check(!full.NeedsFilters && full.Candidates.Length == 256 && full.Candidates[^1] == "Full candidate 256", "Complete candidates were truncated");
        Check(full.Data.GetProperty("candidates")[255].GetProperty("DescriptionText").GetString() == "Unabridged details 256", "Full JSON was reduced");
        var zero = Observe("zero", 0);
        Check(!zero.NeedsFilters && zero.Candidates.Length == 0, "Real complete zero is distinct from overflow");

        void Reject(Action<JsonObject> mutate, int count = 278)
        {
            var fixture = Fixture("bad", count);
            mutate(fixture);
            Throws<InvalidDataException>(() => Read(fixture), "Malformed observation");
        }
        Reject(x => x["status"] = "empty");
        Reject(x => x["candidates"] = new JsonArray());
        Reject(x => x["candidate_count"] = 256);
        Reject(x => x["candidate_count"] = 278.5);
        Reject(x => x.Remove("candidate_count"));
        Reject(x => x.Remove("words"));
        Reject(x => x["process_id"] = 0);
        Reject(x => x["row_count"] = 501);
        Reject(x => x["source"]!["ObservedRowPosition"] = 166);
        Reject(x => x["source"]!["Title"] = "");
        Reject(x => x["source"]!["CanMatch"] = true);
        Reject(x => x["search_completion_verified"] = true);
        Reject(x => x["words"]![0]!["StateImage"] = 2);
        Reject(x => x["words"]![0]!["Checked"] = "false");
        Reject(x => x["words"]![0]!["ObservedPosition"] = 2);
        Reject(x => x.Remove("candidates"), 2);
        Reject(x => x["candidate_count"] = 2, 2);
        Reject(x => x["status"] = "observed", 278);
        Reject(x => x["candidates"]![0]!["Title"] = "", 2);
        Reject(x => x["candidates"]![1]!["ObservedPosition"] = 1, 2);
        using var doc = JsonDocument.Parse(Fixture("real", 2).ToJsonString());
        Throws<InvalidDataException>(() => new GingerDesktopObservation("other", doc.RootElement), "Wrong context directory");
        using var duplicate = JsonDocument.Parse(Fixture("dup", 278).ToJsonString().Replace("\"process_id\":123", "\"process_id\":123,\"process_id\":456"));
        Throws<InvalidDataException>(() => new GingerDesktopObservation("dup", duplicate.RootElement), "Duplicate identity field");
    }

    private static async Task OnePlanAndFreshChain()
    {
        var initial = Observe("selected", 278);
        var middle = Observe("after-alpha", 300, enabled: ["Alpha"]);
        var final = Observe("after-beta", 2, enabled: ["Alpha", "Beta"]);
        var trace = new List<string>();
        var progress = new List<string>();
        var result = await GingerDesktopFilterFlow.ObserveAsync(
            _ => { trace.Add("Select once"); return Task.FromResult(initial); },
            (before, _) => { trace.Add("Plan " + before.Directory); return Task.FromResult(Plan("Alpha", "Beta")); },
            (before, word, _) =>
            {
                trace.Add("Filter " + before.Directory + ":" + word);
                return Task.FromResult(word == "Alpha" ? middle : final);
            }, CancellationToken.None, prepareFiltersOnOverflow: true, report: progress.Add);
        Check(trace.SequenceEqual(new[] { "Select once", "Plan selected", "Filter selected:Alpha", "Filter after-alpha:Beta" }), "Replanned, replayed Select/Filter, or reused stale expected directory");
        Check(ReferenceEquals(result, final) && result.Candidates.SequenceEqual(new[] { "Full candidate 1", "Full candidate 2" }), "Final full snapshot not returned intact");
        Check(progress.Count == 3 && progress[0].Contains("278") && progress[1].Contains("Alpha") && progress[2].Contains("Beta"), "Filtering progress lost");
    }

    private static async Task CandidateTargetStopsEarly()
    {
        foreach (var initialCount in new[] { 278, 11, 256, 0 })
        foreach (var finalCount in new[] { 1, 10 })
        {
            var initial = Observe("before-target", initialCount);
            var fresh = Observe("after-first-filter", finalCount, enabled: ["Alpha"]);
            var trace = new List<string>();
            var progress = new List<string>();
            Task<string> GetPlan(GingerDesktopObservation before, CancellationToken _)
            {
                Check(ReferenceEquals(before, initial), "Target planner received a substituted observation");
                trace.Add("plan");
                return Task.FromResult(Plan("Alpha", "Beta"));
            }
            Task<GingerDesktopObservation> Filter(GingerDesktopObservation before, string word, CancellationToken _)
            {
                trace.Add(word);
                Check(ReferenceEquals(before, initial) && word == "Alpha", "Target preparation applied a destructive extra filter");
                return Task.FromResult(fresh);
            }
            // Ordinary complete rows reach explicit preparation from ChatFlow;
            // overflow reaches it through the existing opt-in Observe route.
            var result = initialCount > 256
                ? await GingerDesktopFilterFlow.ObserveAsync(_ => { trace.Add("read"); return Task.FromResult(initial); },
                    GetPlan, Filter, CancellationToken.None, prepareFiltersOnOverflow: true, report: progress.Add, candidateTarget: 10)
                : await GingerDesktopFilterFlow.PrepareFiltersAsync(initial, GetPlan, Filter, CancellationToken.None,
                    report: progress.Add, candidateTarget: 10);
            Check(trace.SequenceEqual(initialCount > 256 ? ["read", "plan", "Alpha"] : ["plan", "Alpha"]),
                "Target preparation repeated or continued the validated plan after reaching its budget");
            Check(ReferenceEquals(result, fresh) && result.Candidates.Length == finalCount &&
                result.Data.GetProperty("candidates")[finalCount - 1].GetProperty("DescriptionText").GetString() == "Unabridged details " + finalCount,
                "Target result did not preserve the exact complete fresh snapshot");
            Check(!progress.Any(message => message.Contains("Beta", StringComparison.Ordinal)),
                "Progress claimed an extra filter after a successful bounded result");
        }
    }

    private static async Task CandidateTargetBoundaries()
    {
        foreach (var (target, count) in new[] { (1, 1), (10, 1), (10, 10), (256, 256) })
        {
            var initial = Observe("already-suitable", count);
            var result = await GingerDesktopFilterFlow.PrepareFiltersAsync(initial,
                (_, _) => throw new Exception("Already-suitable result must not request a plan"),
                (_, _, _) => throw new Exception("Already-suitable result must not apply a filter"),
                CancellationToken.None, candidateTarget: target);
            Check(ReferenceEquals(result, initial), "Already-suitable result was replaced or truncated");
        }
        foreach (var (target, firstCount, finalCount) in new[] { (10, 11, 10), (10, 0, 10), (10, 300, 1), (1, 2, 1), (256, 278, 256) })
        {
            var words = new[] { "Alpha", "Beta", "Gamma" };
            var initial = Observe("chain-start", 278, words);
            var first = Observe("chain-first", firstCount, words, ["Alpha"]);
            var final = Observe("chain-final", finalCount, words, ["Alpha", "Beta"]);
            var calls = new List<string>();
            var result = await GingerDesktopFilterFlow.PrepareFiltersAsync(initial,
                (_, _) => { calls.Add("plan"); return Task.FromResult(Plan(words)); },
                (before, word, _) =>
                {
                    calls.Add(word);
                    Check(word == "Alpha" ? ReferenceEquals(before, initial) : word == "Beta" && ReferenceEquals(before, first),
                        "Continuation used stale context or exceeded the first suitable result");
                    return Task.FromResult(word == "Alpha" ? first : final);
                }, CancellationToken.None, candidateTarget: target);
            Check(calls.SequenceEqual(["plan", "Alpha", "Beta"]) && ReferenceEquals(result, final),
                "Zero/above-target intermediate result stopped early, replanned, or reached an extra filter");
        }
        int defaultSteps = 0;
        var defaultFinal = Observe("default-after-beta", 0, enabled: ["Alpha", "Beta"]);
        var defaultResult = await GingerDesktopFilterFlow.PrepareFiltersAsync(Observe("default-start", 278),
            (_, _) => Task.FromResult(Plan("Alpha", "Beta")), (_, word, _) =>
            {
                defaultSteps++;
                return Task.FromResult(word == "Alpha" ? Observe("default-after-alpha", 10, enabled: ["Alpha"]) : defaultFinal);
            }, CancellationToken.None);
        Check(defaultSteps == 2 && ReferenceEquals(defaultResult, defaultFinal),
            "Default-null preparation unexpectedly acquired the chat-only early-stop policy");

        await ThrowsAsync<InvalidDataException>(() => GingerDesktopFilterFlow.ObserveAsync(
            _ => Task.FromResult(Observe("read-only-target", 278)),
            (_, _) => throw new Exception("Candidate target must not grant overflow filter permission"),
            (_, _, _) => throw new Exception("Read-only target dispatched input"),
            CancellationToken.None, candidateTarget: 10), "Target bypassed explicit overflow opt-in");
    }

    private static async Task InvalidCandidateTargets()
    {
        foreach (var target in new[] { int.MinValue, -1, 0, 257, int.MaxValue })
        {
            int reads = 0, plans = 0, filters = 0;
            var initial = Observe("invalid-target", 1);
            Task<string> GetPlan(GingerDesktopObservation _, CancellationToken token) { plans++; return Task.FromResult(Plan("Alpha")); }
            Task<GingerDesktopObservation> Filter(GingerDesktopObservation before, string _, CancellationToken token) { filters++; return Task.FromResult(before); }
            await ThrowsAsync<ArgumentOutOfRangeException>(() => GingerDesktopFilterFlow.ObserveAsync(
                _ => { reads++; return Task.FromResult(initial); }, GetPlan, Filter, CancellationToken.None,
                prepareFiltersOnOverflow: true, candidateTarget: target), "Invalid observation target accepted");
            await ThrowsAsync<ArgumentOutOfRangeException>(() => GingerDesktopFilterFlow.PrepareFiltersAsync(initial,
                GetPlan, Filter, CancellationToken.None, candidateTarget: target), "Invalid preparation target accepted");
            Check(reads == 0 && plans == 0 && filters == 0, "Invalid target reached an external delegate");
        }
    }

    private static async Task CandidateTargetCancellation()
    {
        using (var stop = new CancellationTokenSource())
        {
            stop.Cancel();
            await ThrowsAsync<OperationCanceledException>(() => GingerDesktopFilterFlow.PrepareFiltersAsync(Observe("cancel-suitable", 10),
                (_, _) => throw new Exception("Cancelled target planned"), (_, _, _) => throw new Exception("Cancelled target filtered"),
                stop.Token, candidateTarget: 10), "Already-suitable target ignored prior Stop");
        }
        using (var stop = new CancellationTokenSource())
            await ThrowsAsync<OperationCanceledException>(() => GingerDesktopFilterFlow.ObserveAsync(
                _ => { stop.Cancel(); return Task.FromResult(Observe("cancel-read", 10)); },
                (_, _) => throw new Exception("Cancelled observation planned"), (_, _, _) => throw new Exception("Cancelled observation filtered"),
                stop.Token, prepareFiltersOnOverflow: true, candidateTarget: 10), "Suitable read ignored Stop");
        using (var stop = new CancellationTokenSource())
        {
            int filters = 0;
            await ThrowsAsync<OperationCanceledException>(() => GingerDesktopFilterFlow.PrepareFiltersAsync(Observe("cancel-filter", 278),
                (_, _) => Task.FromResult(Plan("Alpha", "Beta")), (_, _, token) =>
                {
                    Check(token == stop.Token, "Target filter token lost");
                    filters++;
                    stop.Cancel();
                    return Task.FromResult(Observe("cancelled-but-suitable", 10, enabled: ["Alpha"]));
                }, stop.Token, candidateTarget: 10), "Suitable filter result ignored Stop");
            Check(filters == 1, "Stop with a suitable result dispatched another filter");
        }
    }

    private static async Task OrdinaryAndOptInBoundaries()
    {
        int plans = 0, filters = 0;
        Task<string> GetPlan(GingerDesktopObservation _, CancellationToken token) { plans++; return Task.FromResult(Plan()); }
        Task<GingerDesktopObservation> Filter(GingerDesktopObservation before, string _, CancellationToken token) { filters++; return Task.FromResult(before); }
        var complete = Observe("complete", 2);
        foreach (bool optIn in new[] { false, true })
        {
            var result = await GingerDesktopFilterFlow.ObserveAsync(_ => Task.FromResult(complete), GetPlan, Filter, CancellationToken.None, optIn);
            Check(ReferenceEquals(result, complete) && plans == 0 && filters == 0, "Ordinary complete Observe changed behavior");
        }
        var exception = await ThrowsAsync<InvalidDataException>(() => GingerDesktopFilterFlow.ObserveAsync(
            _ => Task.FromResult(Observe("pre-match-read", 278)), GetPlan, Filter, CancellationToken.None), "Read-only caller must opt in to filters");
        Check(plans == 0 && filters == 0 && exception.Message.Contains("278"), "Read-only overflow planned or dispatched input");
        var zero = Observe("zero", 0);
        var noSteps = await GingerDesktopFilterFlow.PrepareFiltersAsync(zero, GetPlan, Filter, CancellationToken.None);
        Check(ReferenceEquals(noSteps, zero) && plans == 1 && filters == 0, "Complete empty result with empty plan rejected");

        var current = Observe("full-start", 2);
        int reads = 0;
        var prepared = await GingerDesktopFilterFlow.PrepareFiltersAsync(current, (_, _) => Task.FromResult(Plan("Alpha", "Beta")),
            (before, word, _) => Task.FromResult(++reads == 1 ? Observe("overflow-mid", 300, enabled: ["Alpha"]) : Observe("full-end", 2, enabled: ["Alpha", "Beta"])), CancellationToken.None);
        Check(reads == 2 && prepared.Directory == "full-end", "Public preparation did not reuse the bounded plan across overflow");
    }

    private static async Task Exhaustion()
    {
        foreach (bool noSteps in new[] { true, false })
        {
            int plans = 0, filters = 0;
            var error = await ThrowsAsync<InvalidDataException>(() => GingerDesktopFilterFlow.PrepareFiltersAsync(Observe("wide", 278),
                (_, _) => { plans++; return Task.FromResult(noSteps ? Plan() : Plan("Alpha", "Beta")); },
                (_, word, _) => { filters++; return Task.FromResult(Observe("wide-" + filters, 300, enabled: word == "Alpha" ? ["Alpha"] : ["Alpha", "Beta"])); },
                CancellationToken.None), "Unresolved overflow must stop for manual narrowing");
            Check(plans == 1 && filters == (noSteps ? 0 : 2), "Exhaustion triggered replanning/replay");
            Check(error.Message.Contains(noSteps ? "278" : "300") && error.Message.Contains("вручную"), "Manual error omitted latest count/action");
        }
    }

    private static async Task InvalidPlans()
    {
        var plans = new[]
        {
            Plan("Alpha", "Alpha"), Plan("Alpha", "Missing"),
            "{\"steps\":[{\"word\":\"Alpha\",\"enabled\":false}]}",
            "{\"steps\":[{\"word\":\"Alpha\",\"enabled\":\"true\"}]}",
            "{\"steps\":[{\"word\":\"Alpha\"}]}",
            "{\"steps\":[{\"word\":\"Alpha\",\"enabled\":true,\"position\":4}]}",
            "{\"steps\":[{\"word\":\"Alpha\",\"word\":\"Beta\",\"enabled\":true}]}",
            "{\"steps\":{},\"status\":\"plan_proposed\"}",
            Plan(Enumerable.Range(0, 33).Select(i => "W" + i).ToArray())
        };
        foreach (var plan in plans)
        foreach (var target in new int?[] { null, 10 })
        {
            int filters = 0;
            await ThrowsAsync<InvalidDataException>(() => GingerDesktopFilterFlow.PrepareFiltersAsync(Observe("bad-plan", 278),
                (_, _) => Task.FromResult(plan), (_, _, _) => { filters++; throw new Exception("Must validate the entire plan first"); }, CancellationToken.None,
                candidateTarget: target), "Invalid plan accepted");
            Check(filters == 0, "Invalid plan dispatched a prefix");
        }
        foreach (var fixture in new[] { Observe("duplicate-word", 278, words: ["Alpha", "Alpha"]), Observe("already-set", 278, enabled: ["Alpha"]) })
            await ThrowsAsync<InvalidDataException>(() => GingerDesktopFilterFlow.PrepareFiltersAsync(fixture,
                (_, _) => Task.FromResult(Plan("Alpha")), (_, _, _) => throw new Exception("Must not dispatch ambiguous/repeated input"), CancellationToken.None), "Ambiguous or already-enabled word accepted");
    }

    private static async Task Cancellation()
    {
        using (var cts = new CancellationTokenSource())
        {
            cts.Cancel();
            await ThrowsAsync<OperationCanceledException>(() => GingerDesktopFilterFlow.ObserveAsync(
                _ => throw new Exception("Cancelled before initial read"), (_, _) => throw new Exception("Cancelled before plan"),
                (_, _, _) => throw new Exception("Cancelled before filter"), cts.Token, true), "Cancelled initial read ran");
        }
        using (var cts = new CancellationTokenSource())
        {
            await ThrowsAsync<OperationCanceledException>(() => GingerDesktopFilterFlow.PrepareFiltersAsync(Observe("cancel-plan", 278),
                (_, token) => { Check(token == cts.Token, "Planner token lost"); cts.Cancel(); return Task.FromResult(Plan("Alpha")); },
                (_, _, _) => throw new Exception("Cancelled plan dispatched input"), cts.Token), "Cancellation after planner ignored");
        }
        using (var cts = new CancellationTokenSource())
        {
            int filters = 0;
            await ThrowsAsync<OperationCanceledException>(() => GingerDesktopFilterFlow.PrepareFiltersAsync(Observe("cancel-step", 278),
                (_, _) => Task.FromResult(Plan("Alpha", "Beta")), (_, _, token) =>
                {
                    filters++;
                    Check(token == cts.Token, "Filter token lost");
                    cts.Cancel();
                    return Task.FromResult(Observe("after-cancel", 300, enabled: ["Alpha"]));
                }, cts.Token), "Cancellation after first filter ignored");
            Check(filters == 1, "Second filter ran after Stop");
        }
        using (var cts = new CancellationTokenSource())
        {
            await ThrowsAsync<OperationCanceledException>(() => GingerDesktopFilterFlow.PrepareFiltersAsync(Observe("cancel-progress", 278),
                (_, _) => Task.FromResult(Plan("Alpha")), (_, _, _) => throw new Exception("Progress-triggered Stop dispatched input"),
                cts.Token, report: message => { if (message.Contains("Alpha")) cts.Cancel(); }), "Stop at step progress ignored");
        }
    }

    private static async Task ChangedSourceAndFilters()
    {
        var mutations = new Action<JsonObject>[]
        {
            x => x["process_id"] = 456,
            x => x["row_count"] = 164,
            x => x["source"]!["TableHandle"] = 43,
            x => x["source"]!["Supplier"] = "Changed supplier",
            x => x["source"]!["DocumentNumber"] = "Other document",
            x => x["source"]!["ObservedRowPosition"] = 4,
            x => x["source"]!["Title"] = "Other full title",
            x => x["source"]!["PriceText"] = "20,00",
            x => x["source"]!["LinkedProductText"] = "Unexpected link",
            x => x["words"]![0]!["Title"] = "Other word",
            x => x["words"]![0]!["DescriptionText"] = "Changed search mode",
            x => { x["words"]![0]!["Checked"] = false; x["words"]![0]!["StateImage"] = 1; },
            x => { x["words"]![1]!["Checked"] = true; x["words"]![1]!["StateImage"] = 2; },
            x => x["words"]!.AsArray().RemoveAt(1)
        };
        foreach (var mutate in mutations)
        foreach (var target in new int?[] { null, 10 })
        {
            int filters = 0;
            var fixture = Fixture("changed", target.HasValue ? 10 : 300, enabled: ["Alpha"]);
            mutate(fixture);
            await ThrowsAsync<InvalidDataException>(() => GingerDesktopFilterFlow.PrepareFiltersAsync(Observe("before-change", 278),
                (_, _) => Task.FromResult(Plan("Alpha", "Beta")), (_, _, _) => { filters++; return Task.FromResult(Read(fixture)); },
                CancellationToken.None, candidateTarget: target), "Changed source/filter accepted");
            Check(filters == 1, "Changed source/filter caused another input");
        }
        int attempts = 0;
        await ThrowsAsync<IOException>(() => GingerDesktopFilterFlow.PrepareFiltersAsync(Observe("read-failure", 278),
            (_, _) => Task.FromResult(Plan("Alpha", "Beta")), (_, _, _) => { attempts++; throw new IOException("Uncertain input/read failure"); },
            CancellationToken.None), "Read failure swallowed");
        Check(attempts == 1, "Uncertain filter was retried");
        var before = Observe("source", 2);
        var selected = Fixture("selected", 2);
        selected["source"]!["ObservedRowPosition"] = 4;
        selected["source"]!["Title"] = "New target";
        before.RequireSameSource(Read(selected), selectedPosition: 4);
        _checks++;
        Throws<InvalidDataException>(() => before.RequireSameSource(Read(selected), selectedPosition: 5), "Wrong Select result accepted");
    }

    private static async Task MaximumBound()
    {
        var words = Enumerable.Range(0, 32).Select(i => "W" + i).ToArray();
        int plans = 0, filters = 0;
        var result = await GingerDesktopFilterFlow.PrepareFiltersAsync(Observe("max", 278, words),
            (_, _) => { plans++; return Task.FromResult(Plan(words)); }, (before, word, _) =>
            {
                Check(word == words[filters], "Step order changed");
                filters++;
                return Task.FromResult(Observe("max-" + filters, filters == 32 ? 2 : 300, words, words.Take(filters).ToArray()));
            }, CancellationToken.None);
        Check(plans == 1 && filters == 32 && result.CandidateCount == 2, "Bounded 32-step plan failed or replanned");
    }
}
