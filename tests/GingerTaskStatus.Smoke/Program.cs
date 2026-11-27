using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using BabyAI.Desktop;

var checks = 0;
void Require(bool condition, string message)
{
    checks++;
    if (!condition) throw new Exception(message);
}
void Reject(Action action, string message)
{
    checks++;
    try { action(); }
    catch (InvalidDataException) { return; }
    throw new Exception(message);
}
void IoReject(Action action, string message)
{
    checks++;
    try { action(); }
    catch (IOException) { return; }
    catch (UnauthorizedAccessException) { return; }
    throw new Exception(message);
}
var epoch = new DateTimeOffset(2026, 11, 28, 10, 0, 0, TimeSpan.Zero);
GingerChatTaskResult Result(string chat = "chat-a", int sequence = 0,
    GingerChatTaskState state = GingerChatTaskState.Failed, string summary = "Ошибка драйвера: exit 1.") =>
    new(chat, Guid.NewGuid().ToString("N"), epoch.AddMinutes(sequence * 2),
        epoch.AddMinutes(sequence * 2 + 1), state, sequence, summary);

using var temporary = new TempRoot();
string Area(string name) => Path.Combine(temporary.Path, name);
string RecordPath(string root) => Directory.GetFiles(root, "*.json").Single();

var absentRoot = Area("absent");
var absent = new GingerChatTaskStatusStore(absentRoot);
Require(!Directory.Exists(absentRoot), "Constructor touched the filesystem");
Require(absent.Read("chat-a") is null && !Directory.Exists(absentRoot), "Missing read created files");
Require(GingerChatTaskStatusFormatter.Format(null) == GingerChatTaskStatusFormatter.Missing,
    "Missing status was represented as success or a live check");

// Real filesystem round trips, a fresh store instance, and independent chat keys.
var root = Area("main");
var store = new GingerChatTaskStatusStore(root);
var first = Result();
store.Save(first);
Require(store.Read(first.ChatId) == first, "First save changed a field");
Require(new GingerChatTaskStatusStore(root).Read(first.ChatId) == first, "Restart lost persisted result");
Require(store.Read("chat-b") is null, "Another chat inherited the outcome");
var other = Result("chat-b", state: GingerChatTaskState.Matched);
store.Save(other);
Require(store.Read("chat-a") == first && store.Read("chat-b") == other, "Chat isolation failed");
store.Save(first);
Require(Directory.GetFiles(root, "*.json").Length == 2, "Duplicate created another result");
Reject(() => store.Save(first with { State = GingerChatTaskState.Matched }), "Same run changed into success");
var later = Result(sequence: 2, state: GingerChatTaskState.TimedOut);
store.Save(later);
Reject(() => store.Save(first), "Late old completion replaced a newer result");
Reject(() => store.Save(Result(sequence: 2)), "Ambiguous simultaneous start replaced a result");
Reject(() => store.Save(Result(sequence: 3) with { StartedAt = later.FinishedAt.AddTicks(-1) }),
    "Overlapping run replaced a result");
Require(store.Read("chat-a") == later, "Rejected save mutated latest result");

var expected = new Dictionary<GingerChatTaskState, string>
{
    [GingerChatTaskState.Failed] = "остановилась из-за ошибки",
    [GingerChatTaskState.Cancelled] = "была остановлена",
    [GingerChatTaskState.TimedOut] = "истекло время ожидания",
    [GingerChatTaskState.Review] = "требует ручной проверки",
    [GingerChatTaskState.PairReady] = "ещё не подтверждённое сопоставление",
    [GingerChatTaskState.Matched] = "подтверждено сопоставление одной пары",
    [GingerChatTaskState.End] = "не означает, что все позиции сопоставлены",
    [GingerChatTaskState.AlreadyLinked] = "уже имела привязку",
    [GingerChatTaskState.Rejected] = "пара была отклонена",
};
foreach (var state in Enum.GetValues<GingerChatTaskState>())
{
    var result = Result("enum-" + state, 7, state, "UNTRUSTED: проверил, сопоставления активны");
    store.Save(result);
    var loaded = new GingerChatTaskStatusStore(root).Read(result.ChatId);
    var text = GingerChatTaskStatusFormatter.Format(loaded);
    Require(loaded == result, "State did not round-trip: " + state);
    Require(text.Contains(expected[state]) && text.Contains("Последний известный результат") &&
        text.Contains("28.11.2026 10:15:00 UTC") && text.Contains("позиций: 7") &&
        text.Contains("Новая проверка Ginger не запускалась"), "Outcome semantics lost: " + state);
    Require(!text.Contains("UNTRUSTED") && !text.Contains("сопоставления активны"), "Summary became evidence");
    if (state != GingerChatTaskState.Matched)
        Require(!text.Contains("подтверждено сопоставление одной пары"), "Non-match became a verified match");
}
Require(GingerChatTaskStatusFormatter.Format(first).Contains("Новых отложенных позиций"), "Zero count invented saved rows");

// Reject invalid values before creating any directory or overwriting prior data.
var invalidResults = new[]
{
    first with { ChatId = "../outside" }, first with { ChatId = "" }, first with { ChatId = " " },
    first with { ChatId = new string('a', 129) }, first with { RunId = "not-a-run" },
    first with { RunId = Guid.Empty.ToString("N") }, first with { RunId = new string('A', 32) },
    first with { StartedAt = default }, first with { FinishedAt = default },
    first with { StartedAt = epoch.ToOffset(TimeSpan.FromHours(3)) },
    first with { FinishedAt = first.StartedAt.AddTicks(-1) },
    first with { State = (GingerChatTaskState)999 }, first with { DeferredCount = -1 },
    first with { DeferredCount = 1_000_001 }, first with { Summary = "" }, first with { Summary = "  " },
    first with { Summary = "\0" }, first with { Summary = "line\u0007" }, first with { Summary = "\uD800" },
    first with { Summary = new string('я', GingerChatTaskStatusStore.MaxSummaryLength + 1) },
};
var invalidRoot = Area("invalid-input");
foreach (var result in invalidResults)
{
    Reject(() => new GingerChatTaskStatusStore(invalidRoot).Save(result), "Invalid result persisted");
    Reject(() => GingerChatTaskStatusFormatter.Format(result), "Invalid result formatted");
}
Require(!Directory.Exists(invalidRoot), "Invalid input created storage");
var unicode = Result("unicode", summary: new string('я', 4_990) + "\n\t🌸 end");
store.Save(unicode);
Require(store.Read("unicode") == unicode, "Long Unicode summary changed or was silently shortened");

// Treat disk content as untrusted: exact schema, numeric types, duplicate keys,
// version, UTC timestamps, and chat binding are validated before use.
var malformedRoot = Area("malformed");
var malformedStore = new GingerChatTaskStatusStore(malformedRoot);
malformedStore.Save(first);
var malformedPath = RecordPath(malformedRoot);
var original = File.ReadAllText(malformedPath);
string Change(string key, JsonNode? value)
{
    var node = JsonNode.Parse(original)!.AsObject();
    node[key] = value;
    return node.ToJsonString();
}
var missing = JsonNode.Parse(original)!.AsObject(); missing.Remove("summary");
var duplicateWithMissing = missing.ToJsonString().Replace("\"state\":", "\"state\":\"matched\",\"state\":");
var malformed = new[]
{
    "", "{", "[]", "null", original + "{}", missing.ToJsonString(),
    Change("tool", "match"), Change("can_match", true), Change("version", 2), Change("version", "1"),
    Change("version", true), Change("state", "running"), Change("state", "Matched"), Change("state", 5),
    Change("state", null), Change("deferred_count", true), Change("deferred_count", 1.5),
    Change("deferred_count", "1"), Change("deferred_count", -1), Change("chat_id", "other-chat"),
    Change("run_id", ""), Change("summary", new JsonArray("not text")), Change("summary", null),
    Change("summary", new string('x', 5_001)), Change("summary", "\0"),
    Change("started_at", "2026-11-28"), Change("started_at", "2026-11-28T10:00:00.0000000+03:00"),
    Change("finished_at", "2026-11-28T09:00:00.0000000+00:00"),
    original.Replace("\"state\":", "\"state\":\"matched\",\"state\":"),
    original.Replace("\"state\":", "\"\\u0073tate\":\"matched\",\"state\":"),
    duplicateWithMissing,
};
foreach (var text in malformed)
{
    File.WriteAllText(malformedPath, text, new UTF8Encoding(false));
    Reject(() => malformedStore.Read("chat-a"), "Malformed stored result accepted: " + text[..Math.Min(70, text.Length)]);
    Reject(() => malformedStore.Save(Result(sequence: 10)), "Corrupt result silently replaced");
    Require(File.ReadAllText(malformedPath) == text, "Rejected operation altered corrupt evidence");
}
File.WriteAllBytes(malformedPath, [0x7b, 0x22, 0xff, 0x22, 0x3a, 0x30, 0x7d]);
Reject(() => malformedStore.Read("chat-a"), "Malformed UTF-8 accepted");
File.WriteAllBytes(malformedPath, new byte[GingerChatTaskStatusStore.MaxRecordBytes + 1]);
Reject(() => malformedStore.Read("chat-a"), "Oversized file was read as a result");
File.WriteAllText(malformedPath, original, new UTF8Encoding(false));

// An occupied lock and a failed atomic replacement preserve the previous file.
var failureRoot = Area("write-failure");
var failureStore = new GingerChatTaskStatusStore(failureRoot);
failureStore.Save(first);
var previousBytes = File.ReadAllBytes(RecordPath(failureRoot));
using (var blocked = new FileStream(Path.Combine(failureRoot, ".write.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
{
    IoReject(() => failureStore.Save(Result(sequence: 20)), "Contending save ignored its lock");
    IoReject(() => failureStore.Read("chat-a"), "Locked storage became a missing or stale success result");
}
Require(File.ReadAllBytes(RecordPath(failureRoot)).SequenceEqual(previousBytes), "Failed lock changed durable result");
if (OperatingSystem.IsWindows())
{
    using (var blocked = new FileStream(RecordPath(failureRoot), FileMode.Open, FileAccess.Read, FileShare.Read))
        IoReject(() => failureStore.Save(Result(sequence: 20)), "Replacement unexpectedly bypassed a Windows delete lock");
    Require(File.ReadAllBytes(RecordPath(failureRoot)).SequenceEqual(previousBytes), "Failed rename damaged old result");
    Require(Directory.GetFiles(failureRoot, "*.pending").Length == 0, "Failed replacement leaked staging content");
}
failureStore.Save(Result(sequence: 21));
Require(failureStore.Read("chat-a")!.State == GingerChatTaskState.Failed, "Recovery restored an older success");

var fileRoot = Area("not-directory"); File.WriteAllText(fileRoot, "occupied");
var noConstructorIo = new GingerChatTaskStatusStore(fileRoot);
Reject(() => noConstructorIo.Read("chat-a"), "Root file accepted as directory");
Reject(() => noConstructorIo.Save(first), "Root file overwritten");
var directoryRoot = Area("record-directory");
var directoryStore = new GingerChatTaskStatusStore(directoryRoot); directoryStore.Save(first);
var directoryPath = RecordPath(directoryRoot); File.Delete(directoryPath); Directory.CreateDirectory(directoryPath);
Reject(() => directoryStore.Read("chat-a"), "Directory accepted as JSON record");
Reject(() => directoryStore.Save(first), "Directory overwritten as result");

var quotaRoot = Area("quota"); Directory.CreateDirectory(quotaRoot);
for (var index = 0; index < GingerChatTaskStatusStore.MaxStoredChats; index++)
    File.WriteAllText(Path.Combine(quotaRoot, index + ".pending"), "");
Reject(() => new GingerChatTaskStatusStore(quotaRoot).Save(first), "Unbounded staging-file accumulation allowed");
Require(Directory.GetFiles(quotaRoot, "*.json").Length == 0, "Quota rejection published a result");

// Real concurrent reads see an entire committed old/new result or an explicit
// Windows sharing error. Unavailability must never become a guessed outcome.
var atomicRoot = Area("atomic");
var writer = new GingerChatTaskStatusStore(atomicRoot); writer.Save(first);
var reader = new GingerChatTaskStatusStore(atomicRoot);
var atomicResults = Enumerable.Range(1, 35).Select(index =>
    Result(sequence: index, summary: "Завершено: " + new string('я', 3_000))).Prepend(first)
    .ToDictionary(value => value.RunId);
var reads = 0;
var busyReads = 0;
using var ready = new ManualResetEventSlim();
var reading = Task.Run(() =>
{
    if (reader.Read("chat-a") != first) throw new Exception("Initial committed result absent");
    reads++;
    ready.Set();
    for (var index = 0; index < 300; index++)
    {
        try
        {
            var value = reader.Read("chat-a") ?? throw new Exception("Committed result disappeared");
            if (!atomicResults.TryGetValue(value.RunId, out var committed) || value != committed)
                throw new Exception("Reader observed torn record");
            reads++;
        }
        catch (IOException ex) when (OperatingSystem.IsWindows() && (ex.HResult & 0xffff) == 32) { busyReads++; }
    }
});
ready.Wait();
GingerChatTaskResult final = first;
foreach (var result in atomicResults.Values.Where(value => value != first).OrderBy(value => value.StartedAt))
{
    final = result;
    writer.Save(final);
}
await reading;
Require(reads > 0 && reads + busyReads == 301, "Concurrent reads neither completed nor failed explicitly");
Require(reader.Read("chat-a") == final, "Atomic replacement lost the final result");
Require(Directory.GetFiles(atomicRoot, "*.pending").Length == 0, "Successful saves leaked staging files");

Console.WriteLine($"PASS: {checks} task-status checks; strict persisted results, isolation, bounded data, failed/atomic saves and factual formatting; no UI/model/driver.");

sealed class TempRoot : IDisposable
{
    internal string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "babyai-ginger-status-" + Guid.NewGuid().ToString("N"));
    internal TempRoot() => Directory.CreateDirectory(Path);
    public void Dispose()
    {
        var resolved = System.IO.Path.GetFullPath(Path);
        var prefix = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(System.IO.Path.GetTempPath())) +
            System.IO.Path.DirectorySeparatorChar + "babyai-ginger-status-";
        if (!resolved.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
            (File.GetAttributes(resolved) & FileAttributes.ReparsePoint) != 0)
            throw new Exception("Unsafe temporary cleanup target");
        Directory.Delete(resolved, recursive: true);
    }
}
