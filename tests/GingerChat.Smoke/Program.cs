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
