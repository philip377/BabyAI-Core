using BabyAI.Desktop;

internal static class ScanFlowTests
{
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    private static GingerChatScanRow Row(int position, int count, bool linked = false, params string[] candidates) =>
        new(new GingerChatItem("Supplier", "Document", position, "Product " + position, linked, candidates),
            count, 123, 456, Path.Combine(Path.GetTempPath(), "scan-observation-" + position));

    // This harness provides only observation, navigation, durable deferral and comparison.
    // It has no matching delegate, no driver and no live application access.
    private sealed class Harness(params GingerChatScanRow[] rows) : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "BabyAI-ScanFlow-" + Guid.NewGuid().ToString("N"));
        internal readonly List<string> Calls = [];
        internal readonly List<GingerChatStage> Stages = [];
        internal readonly List<string> Reports = [];
        internal Func<GingerChatScanRow, GingerChatScanRow>? FilterResult;
        internal Func<GingerChatScanRow, GingerChatScanRow>? NextResult;
        internal Action<GingerChatScanRow>? BeforeSave;
        internal Action<GingerChatScanRow>? AfterSave;
        internal Action<GingerChatItem>? OnCompare;
        internal Func<GingerChatItem, GingerChatComparison>? CompareResult;
        internal Action<GingerChatStage>? OnReport;
        internal GingerDeferredRows Store => new(_root);

        internal Task<GingerChatScanResult> Run(CancellationToken token = default) => new GingerChatScanFlow(
            _ => { Calls.Add("read"); return Task.FromResult(rows[0]); },
            (row, _) =>
            {
                Calls.Add("filter:" + row.Item.Position);
                return Task.FromResult(FilterResult?.Invoke(row) ?? row with
                { ObservationDirectory = row.ObservationDirectory + "-filtered" });
            },
            (row, _) =>
            {
                Calls.Add("next:" + row.Item.Position);
                Check(Calls.Count < 2_100, "Unbounded scan loop");
                Check(row.Item.Position < row.Count, "Navigation wrapped at end of price");
                if (!row.Item.Linked)
                    Check(Store.Read().Any(saved => saved.Position == row.Item.Position && saved.Title == row.Item.Title),
                        "Navigation preceded the durable reminder");
                return Task.FromResult(NextResult?.Invoke(row) ?? rows.Single(next => next.Item.Position == row.Item.Position + 1));
            },
            (row, _) =>
            {
                Calls.Add("save:" + row.Item.Position);
                BeforeSave?.Invoke(row);
                var saved = row.Item.Candidates.Length == 0
                    ? Store.Add(row.Item, "scan-chat", row.ObservationDirectory)
                    : Store.AddManualReview(row.Item, "scan-chat", row.ObservationDirectory);
                Check(Store.Read().Any(entry => entry.Id == saved.Id && entry.Title == row.Item.Title &&
                    entry.ObservationDirectory == row.ObservationDirectory), "Deferral was not durable before returning");
                AfterSave?.Invoke(row);
                return Task.CompletedTask;
            },
            (item, _) =>
            {
                Calls.Add("compare:" + item.Position);
                OnCompare?.Invoke(item);
                return Task.FromResult(CompareResult is null
                    ? new GingerChatComparison("proposal for " + item.Position, item.Candidates[0])
                    : CompareResult(item));
            },
            (stage, text) => { Stages.Add(stage); Reports.Add(text); OnReport?.Invoke(stage); }).RunAsync(token);

        public void Dispose()
        {
            var full = Path.GetFullPath(_root);
            Check(Path.GetDirectoryName(full) == Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())) &&
                Path.GetFileName(full).StartsWith("BabyAI-ScanFlow-", StringComparison.Ordinal), "Unsafe test cleanup path");
            if (Directory.Exists(full)) Directory.Delete(full, recursive: true);
        }
    }

    private static async Task Refuses<T>(Harness harness, CancellationToken token = default) where T : Exception
    {
        try { await harness.Run(token); }
        catch (T)
        {
            Check(harness.Stages.Last() == (typeof(T) == typeof(OperationCanceledException)
                ? GingerChatStage.Cancelled : GingerChatStage.Failed), "Failure did not produce its terminal stage");
            Check(!harness.Stages.Contains(GingerChatStage.Review) && !harness.Stages.Contains(GingerChatStage.Completed),
                "Failed scan reported success");
            return;
        }
        throw new Exception("Unsafe scan was accepted: " + typeof(T).Name);
    }

    internal static async Task Run()
    {
        await CheckSuccessfulScan();
        await CheckManualReviewScan();
        await CheckManualReviewFailures();
        await CheckInvalidComparisons();
        await CheckFullPriceBound();
        await CheckEndAndLinkedRows();
        await CheckFailuresAndStop();
        await CheckChangedObservations();
        CheckNavigationOnlyContract();
        Console.WriteLine("Ginger scan: empty and manual-review durable deferral before next, exact typed choices, full explanations, 500-row bound, failures, Stop, changed observations and no matching writer passed; Ginger was not started.");
    }

    private static async Task CheckManualReviewScan()
    {
        using (var scan = new Harness(Row(1, 3, false, "First", "Second"), Row(2, 3, false, "Pair"), Row(3, 3)))
        {
            scan.CompareResult = item => item.Position == 1
                ? new("No unique candidate: volume remains unknown.", null) : new("The next row has one proposal.", "Pair");
            var result = await scan.Run();
            Check(scan.Calls.SequenceEqual(["read", "compare:1", "save:1", "next:1", "compare:2"]),
                "Manual-review row did not save before advancing exactly once");
            Check(result.Row.Item.Position == 2 && result.Proposal == "The next row has one proposal." &&
                result.DeferredCount == 1 && !result.ReachedEnd, "Manual-review explanation replaced the final pair");
            var saved = scan.Store.Read().Single();
            Check(saved.Position == 1 && saved.Reason == GingerDeferredRows.ManualReviewReason,
                "Manual-review row was persisted as an empty result");
            Check(scan.Reports.Any(text => text.Contains("No unique candidate: volume remains unknown.", StringComparison.Ordinal)) &&
                scan.Stages.Count(stage => stage == GingerChatStage.Review) == 1,
                "The full deferred explanation was lost or shown as a final pair");
        }
        using (var scan = new Harness(Row(1, 2, false, "Possible"), Row(2, 2)))
        {
            scan.CompareResult = _ => new("Uncertain tester status.", null);
            var result = await scan.Run();
            Check(scan.Calls.SequenceEqual(["read", "compare:1", "save:1", "next:1", "filter:2", "save:2"]),
                "Mixed manual-review and empty traversal repeated or omitted a step");
            Check(result.ReachedEnd && result.Proposal is null && result.DeferredCount == 2 && result.AlreadyLinkedCount == 0,
                "End retained a stale proposal or lost mixed deferral counts");
            var saved = scan.Store.Read().OrderBy(row => row.Position).ToArray();
            Check(saved[0].Reason == GingerDeferredRows.ManualReviewReason && saved[1].Reason == GingerDeferredRows.NoCandidatesReason,
                "Mixed deferral reasons did not survive persistence");
            Check(!scan.Reports.Last().Contains("без кандидатов", StringComparison.Ordinal),
                "Mixed end summary described every saved row as empty");
        }
        using (var scan = new Harness(Row(1, 2, false, "Possible"), Row(2, 2, true)))
        {
            scan.CompareResult = _ => new("Manual review needed.", null);
            var result = await scan.Run();
            Check(result.ReachedEnd && result.Proposal is null && result.DeferredCount == 1 && result.AlreadyLinkedCount == 1,
                "A linked final row exposed an earlier manual-review proposal");
            Check(scan.Calls.SequenceEqual(["read", "compare:1", "save:1", "next:1"]),
                "A linked final row was filtered, compared or deferred");
        }
        using (var scan = new Harness(Row(3, 3, false, "Possible")))
        {
            var proposal = new string('x', 999_990) + "final-tail";
            scan.CompareResult = _ => new(proposal, null);
            var result = await scan.Run();
            Check(result.ReachedEnd && result.Proposal is null && result.DeferredCount == 1 &&
                scan.Calls.SequenceEqual(["read", "compare:3", "save:3"]), "Final manual-review row wrapped or was omitted");
            Check(scan.Reports.Any(text => text.EndsWith(proposal, StringComparison.Ordinal)),
                "Full bounded proposal was truncated before reaching the journal callback");
        }
    }

    private static async Task CheckManualReviewFailures()
    {
        using (var scan = new Harness(Row(1, 2, false, "Possible"), Row(2, 2)))
        {
            scan.CompareResult = _ => new("Needs review.", null);
            scan.BeforeSave = _ => throw new IOException("Disk full");
            await Refuses<IOException>(scan);
            Check(scan.Calls.SequenceEqual(["read", "compare:1", "save:1"]) && scan.Store.Read().Count == 0,
                "Failed manual-review save permitted navigation");
        }
        using (var stop = new CancellationTokenSource())
        using (var scan = new Harness(Row(1, 2, false, "Possible"), Row(2, 2)))
        {
            scan.CompareResult = _ => new("Needs review.", null);
            scan.AfterSave = _ => stop.Cancel();
            await Refuses<OperationCanceledException>(scan, stop.Token);
            Check(scan.Calls.SequenceEqual(["read", "compare:1", "save:1"]) && scan.Store.Read().Count == 1 &&
                scan.Reports.Last().Contains(GingerChatOutcomeText.Deferred(1), StringComparison.Ordinal),
                "Stop after manual-review save navigated or lost the durable count");
        }
        using (var stop = new CancellationTokenSource())
        using (var scan = new Harness(Row(1, 2, false, "Possible"), Row(2, 2)))
        {
            scan.CompareResult = _ => new("Late uncertain answer.", null);
            scan.OnCompare = _ => stop.Cancel();
            await Refuses<OperationCanceledException>(scan, stop.Token);
            Check(scan.Calls.SequenceEqual(["read", "compare:1"]) && scan.Store.Read().Count == 0,
                "Cancelled comparison was deferred as a valid unknown");
        }
        using (var scan = new Harness(Row(1, 2, false, "Possible"), Row(2, 2)))
        {
            scan.CompareResult = _ => new("Needs review.", null);
            scan.NextResult = _ => throw new IOException("Navigation result uncertain");
            await Refuses<IOException>(scan);
            Check(scan.Calls.SequenceEqual(["read", "compare:1", "save:1", "next:1"]) && scan.Store.Read().Count == 1,
                "Uncertain next after manual review replayed input or lost the reminder");
        }
        using (var scan = new Harness(Row(1, 1, false, "Possible")))
        {
            scan.CompareResult = _ => throw new TimeoutException("Model deadline");
            await Refuses<TimeoutException>(scan);
            Check(scan.Calls.SequenceEqual(["read", "compare:1"]) && scan.Store.Read().Count == 0,
                "Model timeout became a manual-review result");
        }
        using (var scan = new Harness(Row(1, 1, false, "Possible")))
        {
            scan.CompareResult = _ => throw new IOException("Model transport failed");
            await Refuses<IOException>(scan);
            Check(scan.Calls.SequenceEqual(["read", "compare:1"]) && scan.Store.Read().Count == 0,
                "Model transport error became a manual-review result");
        }
        using (var scan = new Harness(Row(1, 1, false, "Possible")))
        {
            scan.CompareResult = _ => throw new OperationCanceledException("Unknown interruption");
            await Refuses<OperationCanceledException>(scan);
            Check(scan.Calls.SequenceEqual(["read", "compare:1"]) && scan.Store.Read().Count == 0,
                "External interruption became a manual-review result");
        }
        Func<GingerChatScanRow, GingerChatScanRow>[] changes = [
            row => row with { ProcessId = 999 }, row => row with { TableHandle = 999 },
            row => row with { Count = 3 },
            row => row with { Item = row.Item with { Supplier = "Other supplier" } },
            row => row with { Item = row.Item with { Document = "Other document" } },
            row => row with { Item = row.Item with { Position = 1 } },
        ];
        foreach (var change in changes)
        {
            using var scan = new Harness(Row(1, 2, false, "Possible"), Row(2, 2, false, "Pair"));
            scan.CompareResult = _ => new("Needs review.", null);
            scan.NextResult = _ => change(Row(2, 2, false, "Pair"));
            await Refuses<InvalidDataException>(scan);
            Check(scan.Calls.SequenceEqual(["read", "compare:1", "save:1", "next:1"]) && scan.Store.Read().Count == 1,
                "Changed navigation result after manual review reached another comparison");
        }
    }

    private static async Task CheckInvalidComparisons()
    {
        GingerChatComparison?[] invalid = [null, new(null!, null), new("", null), new(" \t\r\n", null),
            new(new string('x', 1_000_001), null), new("Proposal", ""), new("Proposal", " "),
            new("Proposal", "Not in candidates"), new("Proposal", "candidate"), new("Proposal", " Candidate "),
            new("Proposal", "null")];
        foreach (var answer in invalid)
        {
            using var scan = new Harness(Row(1, 2, false, "Candidate"), Row(2, 2));
            scan.CompareResult = _ => answer!;
            await Refuses<InvalidDataException>(scan);
            Check(scan.Calls.SequenceEqual(["read", "compare:1"]) && scan.Store.Read().Count == 0,
                "Invalid comparison was saved or navigated");
        }
        using (var scan = new Harness(Row(1, 2, false, "Candidate", "Candidate"), Row(2, 2)))
        {
            scan.CompareResult = _ => new("Proposal", "Candidate");
            await Refuses<InvalidDataException>(scan);
            Check(scan.Calls.SequenceEqual(["read", "compare:1"]) && scan.Store.Read().Count == 0,
                "Duplicate exact candidate was treated as a unique pair or an uncertain result");
        }
    }

    private static async Task CheckFullPriceBound()
    {
        using var scan = new Harness(Enumerable.Range(1, 500).Select(position => Row(position, 500, false, "Possible")).ToArray());
        scan.CompareResult = item => new("Review row " + item.Position, null);
        var result = await scan.Run();
        Check(result.ReachedEnd && result.Proposal is null && result.Row.Item.Position == 500 && result.DeferredCount == 500 &&
            scan.Store.Read().Count == 500, "Maximum price did not finish with all reminders durable");
        Check(scan.Calls.Count(call => call.StartsWith("compare:", StringComparison.Ordinal)) == 500 &&
            scan.Calls.Count(call => call.StartsWith("save:", StringComparison.Ordinal)) == 500 &&
            scan.Calls.Count(call => call.StartsWith("next:", StringComparison.Ordinal)) == 499,
            "Maximum price repeated comparison, save or final navigation");
    }

    private static async Task CheckSuccessfulScan()
    {
        using (var scan = new Harness(Row(1, 3), Row(2, 3, false, "Candidate"), Row(3, 3)))
        {
            scan.NextResult = previous =>
            {
                var saved = scan.Store.Read().Single();
                Check(saved.Position == 1 && saved.ObservationDirectory.EndsWith("-filtered", StringComparison.Ordinal),
                    "Navigation preceded saving the exact filtered observation");
                return Row(2, 3, false, "Candidate");
            };
            scan.OnCompare = item => Check(item.Position == 2 && item.Candidates.SequenceEqual(["Candidate"]),
                "Comparison reused an empty or stale row");
            var result = await scan.Run();
            Check(scan.Calls.SequenceEqual(["read", "filter:1", "save:1", "next:1", "compare:2"]), "Wrong empty-row sequence");
            Check(result.Row.Item.Position == 2 && result.Proposal == "proposal for 2" &&
                result.DeferredCount == 1 && result.AlreadyLinkedCount == 0 && !result.ReachedEnd, "Wrong prepared-pair result");
        }
        using (var scan = new Harness(Row(1, 4), Row(2, 4), Row(3, 4), Row(4, 4, false, "Candidate")))
        {
            var result = await scan.Run();
            Check(result.DeferredCount == 3 && result.Row.Item.Position == 4 && !result.ReachedEnd, "Multiple empty rows were not traversed");
            Check(scan.Store.Read().Select(row => row.Position).Order().SequenceEqual([1, 2, 3]), "A skipped row was lost");
            Check(scan.Calls.Count(call => call.StartsWith("compare:", StringComparison.Ordinal)) == 1 &&
                scan.Calls.Count(call => call.StartsWith("next:", StringComparison.Ordinal)) == 3, "Comparison or transition repeated");
        }
        using (var scan = new Harness(Row(1, 2), Row(2, 2)))
        {
            scan.FilterResult = row => row with { Item = row.Item with { Candidates = ["Found by filters"] } };
            var result = await scan.Run();
            Check(scan.Calls.SequenceEqual(["read", "filter:1", "compare:1"]) && result.DeferredCount == 0,
                "Row with candidates discovered by filtering was skipped");
        }
    }

    private static async Task CheckEndAndLinkedRows()
    {
        using (var scan = new Harness(Row(1, 3), Row(2, 3), Row(3, 3)))
        {
            var result = await scan.Run();
            Check(result.ReachedEnd && result.Proposal is null && result.DeferredCount == 3 && result.Row.Item.Position == 3,
                "All-empty price did not end cleanly");
            Check(scan.Calls.SequenceEqual(["read", "filter:1", "save:1", "next:1", "filter:2", "save:2", "next:2", "filter:3", "save:3"]),
                "All-empty price wrapped, repeated or compared an empty row");
            Check(scan.Stages.Last() == GingerChatStage.Completed && scan.Store.Read().Count == 3, "End lost durable rows");
        }
        using (var scan = new Harness(Row(2, 2)))
        {
            var result = await scan.Run();
            Check(result.ReachedEnd && result.DeferredCount == 1 && scan.Calls.SequenceEqual(["read", "filter:2", "save:2"]),
                "Starting at final row wrapped or claimed preceding rows");
        }
        using (var scan = new Harness(Row(1, 3, true, "Old candidate"), Row(2, 3, true), Row(3, 3, false, "Candidate")))
        {
            var result = await scan.Run();
            Check(result.AlreadyLinkedCount == 2 && result.DeferredCount == 0 && scan.Store.Read().Count == 0,
                "Linked rows were deferred or counted as newly matched");
            Check(scan.Calls.SequenceEqual(["read", "next:1", "next:2", "compare:3"]), "Linked row was filtered or compared");
        }
        using (var scan = new Harness(Row(1, 2, true), Row(2, 2, true)))
        {
            var result = await scan.Run();
            Check(result.ReachedEnd && result.AlreadyLinkedCount == 2 && result.Proposal is null &&
                scan.Calls.SequenceEqual(["read", "next:1"]), "All-linked price repeated actions or wrapped");
        }
    }

    private static async Task CheckFailuresAndStop()
    {
        using (var scan = new Harness(Row(1, 2), Row(2, 2)))
        {
            scan.FilterResult = _ => throw new IOException("Filter failed");
            await Refuses<IOException>(scan);
            Check(scan.Calls.SequenceEqual(["read", "filter:1"]) && scan.Store.Read().Count == 0, "Filter failure was treated as no candidates");
        }
        using (var scan = new Harness(Row(1, 2), Row(2, 2)))
        {
            scan.BeforeSave = _ => throw new IOException("Disk full");
            await Refuses<IOException>(scan);
            Check(scan.Calls.SequenceEqual(["read", "filter:1", "save:1"]) && scan.Store.Read().Count == 0,
                "Failed save permitted next-row navigation");
        }
        using (var stop = new CancellationTokenSource())
        using (var scan = new Harness(Row(1, 2), Row(2, 2)))
        {
            scan.AfterSave = _ => stop.Cancel();
            await Refuses<OperationCanceledException>(scan, stop.Token);
            Check(scan.Calls.SequenceEqual(["read", "filter:1", "save:1"]) && scan.Store.Read().Count == 1,
                "Stop after durable save navigated or lost saved reminder");
        }
        using (var scan = new Harness(Row(1, 2), Row(2, 2)))
        {
            scan.NextResult = _ => throw new IOException("Navigation result uncertain");
            await Refuses<IOException>(scan);
            Check(scan.Calls.SequenceEqual(["read", "filter:1", "save:1", "next:1"]), "Uncertain navigation retried input");
        }
        using (var stop = new CancellationTokenSource())
        using (var scan = new Harness(Row(1, 2, true), Row(2, 2)))
        {
            scan.OnReport = stage => { if (stage == GingerChatStage.Skipping) stop.Cancel(); };
            await Refuses<OperationCanceledException>(scan, stop.Token);
            Check(scan.Calls.SequenceEqual(["read"]), "Stop after linked-row recognition navigated");
        }
        using (var stop = new CancellationTokenSource())
        using (var scan = new Harness(Row(1, 1, false, "Candidate")))
        {
            scan.OnCompare = _ => stop.Cancel();
            await Refuses<OperationCanceledException>(scan, stop.Token);
            Check(scan.Calls.SequenceEqual(["read", "compare:1"]), "Late Stop repeated comparison");
        }
    }

    private static async Task CheckChangedObservations()
    {
        Func<GingerChatScanRow, GingerChatScanRow>[] changes = [
            row => row with { Item = row.Item with { Supplier = "Other supplier" } },
            row => row with { Item = row.Item with { Document = "Other document" } },
            row => row with { ProcessId = 124 }, row => row with { TableHandle = 457 },
            row => row with { Count = 4 }, row => row with { Item = row.Item with { Position = 3 } },
        ];
        foreach (var change in changes.Concat(new Func<GingerChatScanRow, GingerChatScanRow>[] {
            row => row with { Item = row.Item with { Title = "Changed product" } },
            row => row with { Item = row.Item with { Linked = true } } }))
        {
            using var scan = new Harness(Row(1, 3), Row(2, 3), Row(3, 3));
            scan.FilterResult = change;
            await Refuses<InvalidDataException>(scan);
            Check(scan.Calls.SequenceEqual(["read", "filter:1"]) && scan.Store.Read().Count == 0,
                "Changed filtered row was saved or advanced");
        }
        foreach (var change in changes.Concat(new Func<GingerChatScanRow, GingerChatScanRow>[] {
            row => row with { Item = row.Item with { Position = 1 } } }))
        {
            using var scan = new Harness(Row(1, 3), Row(2, 3, false, "Candidate"), Row(3, 3));
            scan.NextResult = _ => change(Row(2, 3, false, "Candidate"));
            await Refuses<InvalidDataException>(scan);
            Check(scan.Calls.SequenceEqual(["read", "filter:1", "save:1", "next:1"]) && scan.Store.Read().Count == 1,
                "Changed document, skipped position or repeated row reached comparison");
        }
        using (var scan = new Harness(Row(1, 3), Row(2, 3), Row(3, 3)))
        {
            scan.NextResult = _ => Row(2, 3); // The second transition repeats row 2 forever unless rejected.
            await Refuses<InvalidDataException>(scan);
            Check(scan.Calls.SequenceEqual(["read", "filter:1", "save:1", "next:1", "filter:2", "save:2", "next:2"]),
                "Repeated navigation result did not stop after its first occurrence");
        }
        foreach (var bad in new[] { Row(1, 501), Row(0, 2), Row(1, 2) with { ProcessId = 0 },
            Row(1, 2) with { TableHandle = 0 }, Row(1, 2) with { ObservationDirectory = "" } })
        {
            using var scan = new Harness(bad);
            await Refuses<InvalidDataException>(scan);
            Check(scan.Calls.SequenceEqual(["read"]), "Invalid initial observation reached an action");
        }
    }

    private static void CheckNavigationOnlyContract()
    {
        var source = File.ReadAllText(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "../../../../../desktop/BabyAI.Desktop/GingerChatScanFlow.cs")));
        Check(!source.Contains("MatchAsync(", StringComparison.Ordinal) && !source.Contains("GingerChatMatchGate", StringComparison.Ordinal) &&
            !source.Contains("Process.Start(", StringComparison.Ordinal), "Scan acquired a matching writer or direct driver");
    }
}
