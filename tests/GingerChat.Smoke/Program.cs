using BabyAI.Desktop;

static void Require(bool condition, string name)
{
    if (!condition) throw new Exception(name);
}
foreach (var command in new[] { "Продолжи сопоставление", "надо бы продолжить сопоставление",
    "Юникс, давай продолжим сопоставление", "Запусти сопоставление в Ginger, пожалуйста!" })
    Require(GingerChatIntent.IsStart(command), "Missing explicit command: " + command);
foreach (var message in new[] { "не продолжай сопоставление", "как продолжить сопоставление?",
    "он сказал: продолжи сопоставление", "Продолжи сопоставление и проведи заказ",
    "Продолжи сопоставление?", "Расскажи о Ginger", "продолжи", new string('x', 201) })
    Require(!GingerChatIntent.IsStart(message), "Unsafe routing: " + message);

var source = new GingerChatItem("Supplier", "123", 2, "Product", false, []);
await IntentTests.Run();
await IntentBindingTests.Run();
DeferredRowsTests.Run();
DeferredSelectionTests.Run();
await ScanFlowTests.Run();
ProgressTranscriptTests.Run();
BottleTests.Run();
var stages = new List<GingerChatStage>();
int reads = 0, filters = 0, comparisons = 0;
void Report(GingerChatStage stage, string _) => stages.Add(stage);
Task<GingerChatItem> Read(CancellationToken _) { reads++; return Task.FromResult(source); }
Task<GingerChatItem> Filter(CancellationToken _) { filters++; return Task.FromResult(source with { Candidates = ["A"] }); }
Task<string> Compare(GingerChatItem item, CancellationToken _) {
    comparisons++; Require(item.Candidates.SequenceEqual(["A"]), "Compare received stale candidates");
    return Task.FromResult("proposal");
}
var result = await new GingerChatFlow(Read, Filter, Compare, Report).RunAsync(CancellationToken.None);
Require(result.Contains("proposal") && reads == 1 && filters == 1 && comparisons == 1, "Pipeline counts");
Require(stages.SequenceEqual(new[] { GingerChatStage.Started, GingerChatStage.Reading,
    GingerChatStage.Filtering, GingerChatStage.Comparing, GingerChatStage.Review }), "Event order");

stages.Clear(); comparisons = 0;
await new GingerChatFlow(Read, _ => Task.FromResult(source), Compare, Report).RunAsync(CancellationToken.None);
Require(comparisons == 0 && stages.Last() == GingerChatStage.Empty, "Empty search calls model");

stages.Clear(); filters = comparisons = 0;
await new GingerChatFlow(_ => Task.FromResult(source with { Candidates = ["A"] }), Filter, Compare, Report).RunAsync(CancellationToken.None);
Require(filters == 0 && comparisons == 1, "Unnecessary filter changes");

stages.Clear(); filters = comparisons = 0;
await new GingerChatFlow(_ => Task.FromResult(source with { Linked = true }), Filter, Compare, Report).RunAsync(CancellationToken.None);
Require(filters == 0 && comparisons == 0, "Linked row must not be reprocessed");

stages.Clear(); comparisons = 0;
try {
    await new GingerChatFlow(Read, _ => Task.FromResult(source with { Position = 3, Candidates = ["A"] }),
        Compare, Report).RunAsync(CancellationToken.None);
    throw new Exception("Changed row accepted");
} catch (InvalidDataException) { }
Require(comparisons == 0 && stages.Last() == GingerChatStage.Failed, "Changed row reached model");

using var cancellation = new CancellationTokenSource();
stages.Clear(); comparisons = filters = 0;
try {
    await new GingerChatFlow(_ => { cancellation.Cancel(); return Task.FromResult(source); },
        Filter, Compare, Report).RunAsync(cancellation.Token);
    throw new Exception("Cancelled read accepted");
} catch (OperationCanceledException) { }
Require(filters == 0 && comparisons == 0 && stages.Last() == GingerChatStage.Cancelled, "Cancellation reused observation");

using var modelCancellation = new CancellationTokenSource();
stages.Clear();
try {
    await new GingerChatFlow(_ => Task.FromResult(source with { Candidates = ["A"] }), Filter,
        (_, _) => { modelCancellation.Cancel(); return Task.FromResult("late answer"); }, Report)
        .RunAsync(modelCancellation.Token);
    throw new Exception("Late model answer accepted");
} catch (OperationCanceledException) { }
Require(!stages.Contains(GingerChatStage.Review) && stages.Last() == GingerChatStage.Cancelled, "Late success after stop");

stages.Clear(); filters = comparisons = 0;
try {
    await new GingerChatFlow(_ => throw new IOException("driver failed"), Filter, Compare, Report).RunAsync(CancellationToken.None);
    throw new Exception("Driver error swallowed");
} catch (IOException) { }
Require(filters == 0 && comparisons == 0 && stages.Last() == GingerChatStage.Failed, "Failure reported as completed");
Console.WriteLine("Ginger chat: explicit routing, stage order, cancellation, empty search and stale row guards passed.");

var snapshot = new GingerChatMatchSnapshot(123, 456, 20, "1500", "<не указано>",
    source with { Candidates = ["A", "B"] });
var matched = snapshot with { LinkedProduct = "A", Item = snapshot.Item with { Linked = true } };
var now = DateTimeOffset.Parse("2026-11-27T00:00:00Z");
GingerChatMatchGate Gate() => new(snapshot, "chat-1", "A", () => now);
var matchStages = new List<GingerChatMatchStage>();
int writes = 0;
Task<GingerChatMatchSnapshot> Write(CancellationToken _) { writes++; return Task.FromResult(matched); }
void MatchReport(GingerChatMatchStage stage, string _) => matchStages.Add(stage);
async Task Refused(Func<Task> run, string name)
{
    var refused = false;
    try { await run(); }
    catch (Exception ex) when (ex is InvalidOperationException or InvalidDataException or OperationCanceledException or IOException)
    { refused = true; }
    Require(refused, name);
}
async Task RejectBeforeWrite(GingerChatMatchGate gate, GingerChatMatchSnapshot fresh,
    string chat = "chat-1", bool reserved = false, CancellationToken token = default)
{
    writes = 0; matchStages.Clear();
    await Refused(() => gate.RunAsync(chat, _ => Task.FromResult(fresh), () => reserved, Write, MatchReport, token),
        "Unsafe match accepted");
    Require(writes == 0 && !matchStages.Contains(GingerChatMatchStage.Verified), "Rejected request wrote or reported success");
}

var valid = Gate();
await valid.RunAsync("chat-1", _ => Task.FromResult(snapshot), () => false, Write, MatchReport, CancellationToken.None);
Require(writes == 1 && matchStages.SequenceEqual(new[] { GingerChatMatchStage.Checking,
    GingerChatMatchStage.Matching, GingerChatMatchStage.Verified }), "Match stage order");
await RejectBeforeWrite(valid, snapshot); // A second press cannot repeat a successful write.
await RejectBeforeWrite(Gate(), snapshot, chat: "chat-2");
await RejectBeforeWrite(Gate(), snapshot, reserved: true);
foreach (var changed in new[] {
    snapshot with { ProcessId = 124 }, snapshot with { TableHandle = 457 },
    snapshot with { RowCount = 21 }, snapshot with { Price = "1600" },
    snapshot with { LinkedProduct = "other" },
    snapshot with { Item = snapshot.Item with { Supplier = "other" } },
    snapshot with { Item = snapshot.Item with { Document = "other" } },
    snapshot with { Item = snapshot.Item with { Position = 3 } },
    snapshot with { Item = snapshot.Item with { Title = "other" } },
    snapshot with { Item = snapshot.Item with { Linked = true } },
    snapshot with { Item = snapshot.Item with { Candidates = ["A", "C"] } },
    snapshot with { Item = snapshot.Item with { Candidates = ["B", "A"] } },
    snapshot with { Item = snapshot.Item with { Candidates = ["A", "A"] } } })
    await RejectBeforeWrite(Gate(), changed);

var expired = Gate(); now = now.AddMinutes(15);
await RejectBeforeWrite(expired, snapshot);
var expiresDuringRead = Gate(); writes = 0;
await Refused(() => expiresDuringRead.RunAsync("chat-1", _ => {
    now = now.AddMinutes(15); return Task.FromResult(snapshot);
}, () => false, Write, MatchReport, CancellationToken.None), "Expiry during read accepted");
Require(writes == 0, "Expired read wrote");
await RejectBeforeWrite(expiresDuringRead, snapshot); // Failure also consumes this confirmation.

using var beforeMatchCancellation = new CancellationTokenSource();
var cancelledGate = Gate(); writes = 0;
await Refused(() => cancelledGate.RunAsync("chat-1", _ => {
    beforeMatchCancellation.Cancel(); return Task.FromResult(snapshot);
}, () => false, Write, MatchReport, beforeMatchCancellation.Token), "Cancelled read accepted");
Require(writes == 0, "Cancellation before dispatch wrote");
await RejectBeforeWrite(Gate(), snapshot, token: beforeMatchCancellation.Token);

using var inFlightStop = new CancellationTokenSource();
matchStages.Clear(); writes = 0;
await Gate().RunAsync("chat-1", _ => Task.FromResult(snapshot), () => false, _ => {
    writes++; inFlightStop.Cancel(); return Task.FromResult(matched);
}, MatchReport, inFlightStop.Token);
Require(writes == 1 && matchStages.Last() == GingerChatMatchStage.Verified,
    "Stop hid a verified dispatched result");

foreach (var uncertain in new[] { snapshot, matched with { LinkedProduct = "wrong" },
    matched with { Item = matched.Item with { Position = 3 } }, matched with { ProcessId = 999 } })
{
    matchStages.Clear();
    var uncertainGate = Gate();
    await Refused(() => uncertainGate.RunAsync("chat-1", _ => Task.FromResult(snapshot), () => false,
        _ => Task.FromResult(uncertain), MatchReport, CancellationToken.None), "Wrong post-action result accepted");
    Require(!matchStages.Contains(GingerChatMatchStage.Verified), "Uncertain result reported as verified");
    await RejectBeforeWrite(uncertainGate, snapshot);
}
var failedWriter = Gate();
await Refused(() => failedWriter.RunAsync("chat-1", _ => Task.FromResult(snapshot), () => false,
    _ => throw new IOException("dispatch uncertain"), MatchReport, CancellationToken.None), "Writer failure swallowed");
await RejectBeforeWrite(failedWriter, snapshot);

var concurrent = Gate();
var readStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
var releaseRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
writes = 0;
var firstAttempt = concurrent.RunAsync("chat-1", async _ => {
    readStarted.SetResult(); await releaseRead.Task; return snapshot;
}, () => false, Write, (_, _) => { }, CancellationToken.None);
await readStarted.Task;
await Refused(() => concurrent.RunAsync("chat-1", _ => Task.FromResult(snapshot), () => false, Write,
    (_, _) => { }, CancellationToken.None), "Concurrent click accepted");
releaseRead.SetResult(); await firstAttempt;
Require(writes == 1, "Concurrent confirmation wrote twice");

foreach (var badCandidates in new[] { Array.Empty<string>(), new[] { "A", "A" }, new[] { "B" } })
    await Refused(() => { _ = new GingerChatMatchGate(snapshot with { Item = snapshot.Item with { Candidates = badCandidates } }, "chat-1", "A"); return Task.CompletedTask; },
        "Missing or duplicated proposed candidate accepted");
await Refused(() => { _ = new GingerChatMatchGate(matched, "chat-1", "A"); return Task.CompletedTask; }, "Linked proposal accepted");
var mutable = snapshot with { Item = snapshot.Item with { Candidates = ["A", "B"] } };
var frozenGate = new GingerChatMatchGate(mutable, "chat-1", "A");
mutable.Item.Candidates[1] = "C";
await RejectBeforeWrite(frozenGate, mutable);
Console.WriteLine("Ginger chat confirmation: one write, fresh identity, chat scope, expiry, prior claim, cancellation and verified result guards passed.");
await ReviewTests.Run();
await LessonTests.Run();
await NotificationTests.Run();
