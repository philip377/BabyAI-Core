using BabyAI.Desktop;

internal static class OutcomeTextTests
{
    internal static async Task Run()
    {
        Check(GingerChatOutcomeText.Deferred(0) == "Новых отложенных позиций нет.",
            "A zero count must not claim saved positions");
        foreach (var count in new[] { 0, 1, 27 })
        {
            var summaries = new[] { GingerChatOutcomeText.Cancelled(true, count),
                GingerChatOutcomeText.Cancelled(false, count), GingerChatOutcomeText.TimedOut(count), GingerChatOutcomeText.Failed(count) };
            foreach (var summary in summaries)
                Check(summary.Contains(GingerChatOutcomeText.Deferred(count), StringComparison.Ordinal) && summary.Length < 240,
                    "Final result lost its actual run count or became an oversized summary");
        }
        Check(GingerChatOutcomeText.Cancelled(true, 0).Contains("пользователем", StringComparison.Ordinal),
            "Explicit Stop must be distinguishable");
        Check(!GingerChatOutcomeText.Cancelled(false, 0).Contains("пользователем", StringComparison.Ordinal) &&
            !GingerChatOutcomeText.Cancelled(false, 0).Contains("время", StringComparison.Ordinal),
            "Unknown cancellation was falsely attributed to the user or a deadline");
        Check(GingerChatOutcomeText.TimedOut(0).Contains("Время ожидания", StringComparison.Ordinal) &&
            !GingerChatOutcomeText.TimedOut(0).Contains("пользователем", StringComparison.Ordinal),
            "A deadline was presented as explicit Stop");
        foreach (var call in new Action[] { () => GingerChatOutcomeText.Deferred(-1),
            () => GingerChatOutcomeText.Cancelled(true, -1), () => GingerChatOutcomeText.TimedOut(-1),
            () => GingerChatOutcomeText.Failed(-1) })
        {
            try { call(); }
            catch (ArgumentOutOfRangeException) { continue; }
            throw new Exception("Negative durable count accepted");
        }

        await CheckInitialInterruption(new TimeoutException("fixture: reading exceeded its deadline"), false);
        await CheckInitialInterruption(new OperationCanceledException("fixture: external interruption"), false);
        await CheckInitialInterruption(new OperationCanceledException(), true);
        await CheckStopAfterProcessedRow(linked: false);
        await CheckStopAfterProcessedRow(linked: true);
        await CheckStopAfterProcessedRow(linked: false, manualReview: true);
        Console.WriteLine("Ginger interruption UX: timeout versus explicit Stop, unclaimed cancellation, zero and durable counts passed; no Ginger or UI was started.");
    }

    private static async Task CheckInitialInterruption(Exception failure, bool userStop)
    {
        using var stop = new CancellationTokenSource();
        var reports = new List<(GingerChatStage Stage, string Text)>();
        var laterCalls = 0;
        var flow = new GingerChatScanFlow(_ =>
        {
            if (userStop) stop.Cancel();
            return Task.FromException<GingerChatScanRow>(failure);
        }, (row, _) => { laterCalls++; return Task.FromResult(row); },
            (row, _) => { laterCalls++; return Task.FromResult(row); },
            (_, _) => { laterCalls++; return Task.CompletedTask; },
            (_, _) => { laterCalls++; return Task.FromResult(new GingerChatComparison("unexpected", null)); },
            (stage, text) => reports.Add((stage, text)));
        try { await flow.RunAsync(stop.Token); }
        catch (Exception observed) when (ReferenceEquals(observed, failure))
        {
            Check(laterCalls == 0, "An interrupted initial read reached filtering, saving, navigation or comparison");
            if (failure is TimeoutException)
            {
                Check(reports.Last().Stage == GingerChatStage.Failed &&
                    reports.All(report => report.Stage != GingerChatStage.Cancelled), "Deadline became Cancelled");
                Check(reports.Last().Text.Contains(failure.Message, StringComparison.Ordinal), "Deadline cause was lost from the journal");
            }
            else
            {
                var text = reports.Last().Text;
                Check(reports.Last().Stage == GingerChatStage.Cancelled &&
                    text.Contains(GingerChatOutcomeText.Deferred(0), StringComparison.Ordinal),
                    "Cancellation before any save claimed stored positions");
                Check(text.Contains("пользователем", StringComparison.Ordinal) == userStop,
                    "Cancellation cause was guessed rather than taken from the outer token");
            }
            return;
        }
        throw new Exception("Initial interruption was swallowed or converted into another exception");
    }

    private static async Task CheckStopAfterProcessedRow(bool linked, bool manualReview = false)
    {
        using var stop = new CancellationTokenSource();
        var row = new GingerChatScanRow(new("Supplier", "Document", 1, "Product", linked,
            manualReview ? ["Possible candidate"] : []),
            2, 123, 456, "fresh-observation");
        var durable = 0;
        var nextCalls = 0;
        var reports = new List<string>();
        var flow = new GingerChatScanFlow(_ => Task.FromResult(row), (value, _) => Task.FromResult(value),
            (value, _) => { nextCalls++; return Task.FromResult(value); },
            (_, _) => { durable++; stop.Cancel(); return Task.CompletedTask; },
            (_, _) => Task.FromResult(new GingerChatComparison("No unique candidate.", null)), (stage, text) =>
            {
                reports.Add(text);
                if (linked && stage == GingerChatStage.Skipping) stop.Cancel();
            });
        try { await flow.RunAsync(stop.Token); }
        catch (OperationCanceledException)
        {
            var expected = linked ? 0 : 1;
            Check(durable == expected && nextCalls == 0 && reports.Last().Contains(GingerChatOutcomeText.Deferred(expected), StringComparison.Ordinal),
                "Stop counted a linked row as saved, lost an acknowledged save or navigated to the next row");
            return;
        }
        throw new Exception("Stop after saved row was ignored");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
