using System.Text.Json;
using System.Text.Json.Nodes;
using BabyAI.Desktop;

internal static class DeferredRowsTests
{
    private static readonly GingerChatItem Item = new("Поставщик", "Документ 123", 2, "Полное название товара", false, []);
    private static readonly GingerChatItem ManualItem = Item with { Candidates = ["Первый кандидат", "Второй кандидат"] };

    internal static void Run()
    {
        PersistenceAndIdentity();
        ManualReviewAndMixedRows();
        SearchReviewPersistenceAndLimits();
        InvalidInputCannotPublish();
        InvalidManualReviewCannotPublish();
        CorruptionCannotBecomeAnEmptyList();
        FailedWritesPreserveExistingRows();
        InterruptedStagingIsNotACommittedRow();
        LimitsAreEnforced();
        Console.WriteLine("Ginger deferred rows: no-candidate and manual-review persistence, full identity deduplication, strict corruption handling, atomic writes and limits passed; Ginger was not started.");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    private static void Refused(Action action, string message)
    {
        try { action(); }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException) { return; }
        throw new Exception(message);
    }

    private static void PersistenceAndIdentity()
    {
        using var fixture = new Fixture();
        Check(fixture.Store.Read().Count == 0, "Missing queue must start empty");
        var first = fixture.Store.Add(Item, "chat-1", fixture.Observation);
        var originalBytes = File.ReadAllBytes(fixture.Entry(first));
        var reopened = new GingerDeferredRows(fixture.Root);
        Check(reopened.Read().Single() == first && reopened.Read("chat-1").Single() == first,
            "Saved row did not survive opening a new store");
        Check(reopened.Read("chat-other").Count == 0, "Chat filter returned another chat's row");
        var duplicate = reopened.Add(Item, "chat-other", fixture.Observation + "-new");
        Check(duplicate == first && File.ReadAllBytes(fixture.Entry(first)).SequenceEqual(originalBytes),
            "Duplicate rewrote the original chat, observation or timestamp");
        Check(first.Reason == GingerDeferredRows.NoCandidatesReason && first.CreatedAt.Offset == TimeSpan.Zero,
            "Deferred record has an unexpected reason or timestamp");

        var variants = new[] {
            Item with { Supplier = "Другой поставщик" }, Item with { Document = "Документ 124" },
            Item with { Position = 3 }, Item with { Title = Item.Title + " и другой вариант" },
            Item with { Title = new string('я', 4000) + "A" },
            Item with { Title = new string('я', 4000) + "B" },
            Item with { Supplier = "AB", Document = "C" },
            Item with { Supplier = "A", Document = "BC" },
        };
        foreach (var item in variants) reopened.Add(item, "chat-2", fixture.Observation);
        var rows = reopened.Read();
        Check(rows.Count == variants.Length + 1 && rows.Select(row => row.Id).Distinct().Count() == rows.Count,
            "Supplier, document, position or full title was lost from the deterministic identity");
        Check(reopened.Read("chat-2").Count == variants.Length, "Chat filter lost persisted rows");
        using var other = new Fixture();
        Check(other.Store.Add(Item, "another-chat", other.Observation).Id == first.Id,
            "Row identity depends on chat, observation or machine-local storage path");
    }

    private static void InvalidInputCannotPublish()
    {
        using var fixture = new Fixture();
        var invalidItems = new[] {
            Item with { Supplier = " " }, Item with { Supplier = new string('s', 513) },
            Item with { Document = "" }, Item with { Document = new string('d', 513) },
            Item with { Position = 0 }, Item with { Position = -1 }, Item with { Position = 1_000_001 },
            Item with { Title = "" }, Item with { Title = new string('t', 4097) },
            Item with { Title = "broken\0title" }, Item with { Title = "broken\ud800title" },
            Item with { Linked = true }, Item with { Candidates = ["Candidate"] }, Item with { Candidates = null! },
        };
        foreach (var item in invalidItems)
            Refused(() => fixture.Store.Add(item, "chat-1", fixture.Observation), "Invalid row was saved");
        foreach (var chat in new[] { "", "../other", new string('c', 129) })
            Refused(() => fixture.Store.Add(Item, chat, fixture.Observation), "Invalid chat was saved");
        foreach (var observation in new[] { "", "relative-folder", fixture.Observation + "\nother", new string('p', 2049) })
            Refused(() => fixture.Store.Add(Item, "chat-1", observation), "Invalid observation path was saved");
        Check(fixture.Store.Read().Count == 0, "Rejected rows left committed records");
    }

    private static void SearchReviewPersistenceAndLimits()
    {
        using var fixture = new Fixture();
        const string reason = "Нет допустимого нового слова для поиска. Нужна ручная проверка.";
        foreach (var count in new[] { 0, 213, 278 })
        {
            var item = Item with { Position = count + 1,
                Candidates = count > 256 ? [] : Enumerable.Range(1, count).Select(i => "Candidate " + i).ToArray() };
            var saved = fixture.Store.AddSearchReview(item, "chat-1", fixture.Observation, reason, count);
            var reopened = new GingerDeferredRows(fixture.Root).Read().Single(row => row.Id == saved.Id);
            Check(reopened.Reason == GingerDeferredRows.SearchReviewPrefix + reason &&
                reopened.ObservationDirectory == fixture.Observation && reopened.Title == item.Title,
                "Semantic refusal lost its reason, decision evidence or full source");
            Check(reopened.Reason != GingerDeferredRows.NoCandidatesReason,
                "Search review asserted the catalog was empty");
            var bytes = File.ReadAllBytes(fixture.Entry(saved));
            var duplicate = fixture.Store.AddSearchReview(item, "new-chat", fixture.Observation + "-fresh", "Новая причина", count);
            Check(saved == duplicate && bytes.SequenceEqual(File.ReadAllBytes(fixture.Entry(saved))),
                "Duplicate search review overwrote original evidence");
        }
        using var rejected = new Fixture();
        foreach (var detail in new[] { "", " ", new string('я', 601), "bad\0reason", "bad\ud800reason" })
            Refused(() => rejected.Store.AddSearchReview(Item, "chat-1", rejected.Observation, detail, 0),
                "Invalid search reason was published");
        foreach (var bad in new[] {
            (Item with { Linked = true }, 0L), (Item with { Candidates = null! }, 0L),
            (Item, -1L), (Item, 213L), (ManualItem, 278L),
            (ManualItem with { Candidates = [" ", "valid"] }, 2L) })
            Refused(() => rejected.Store.AddSearchReview(bad.Item1, "chat-1", rejected.Observation, reason, bad.Item2),
                "Invalid search candidate count or source was published");
        Check(!Directory.Exists(rejected.Root), "Rejected search review created persistence files");
        var stored = fixture.Store.Read().First();
        var path = fixture.Entry(stored);
        var original = File.ReadAllText(path);
        foreach (var malformed in new[] { GingerDeferredRows.SearchReviewPrefix,
            GingerDeferredRows.SearchReviewPrefix + new string('x', 601),
            GingerDeferredRows.SearchReviewPrefix + " " })
        {
            var json = JsonNode.Parse(original)!.AsObject();
            json["Reason"] = malformed;
            File.WriteAllText(path, json.ToJsonString());
            Refused(() => fixture.Store.Read(), "Corrupt semantic review was silently accepted");
        }
        File.WriteAllText(path, original);
    }

    private static void ManualReviewAndMixedRows()
    {
        using var fixture = new Fixture();
        var originalEmpty = fixture.Store.Add(Item, "chat-empty", fixture.Observation);
        var originalManual = fixture.Store.AddManualReview(ManualItem with { Position = 3 }, "chat-manual", fixture.Observation);
        var emptyBytes = File.ReadAllBytes(fixture.Entry(originalEmpty));
        var manualBytes = File.ReadAllBytes(fixture.Entry(originalManual));
        var reopened = new GingerDeferredRows(fixture.Root);
        Check(reopened.Read().Count == 2 && reopened.Read("chat-empty").Single() == originalEmpty &&
            reopened.Read("chat-manual").Single() == originalManual,
            "Mixed no-candidate and manual-review records did not survive reopening or chat filtering");
        Check(originalEmpty.Reason == GingerDeferredRows.NoCandidatesReason &&
            originalManual.Reason == GingerDeferredRows.ManualReviewReason &&
            originalManual.Reason == "Однозначный кандидат не подтверждён. Нужна ручная проверка." &&
            originalManual.Reason != originalEmpty.Reason && originalManual.CreatedAt.Offset == TimeSpan.Zero,
            "Manual-review reason claimed an empty candidate list or changed its fixed wording");

        var manualDuplicate = reopened.AddManualReview(
            ManualItem with { Position = 3, Candidates = ["Другой список кандидатов"] }, "chat-other", fixture.Observation + "-new");
        var emptyToManual = reopened.AddManualReview(ManualItem, "chat-other", fixture.Observation + "-new");
        var manualToEmpty = reopened.Add(Item with { Position = 3 }, "chat-other", fixture.Observation + "-new");
        Check(manualDuplicate == originalManual && emptyToManual == originalEmpty && manualToEmpty == originalManual &&
            File.ReadAllBytes(fixture.Entry(originalEmpty)).SequenceEqual(emptyBytes) &&
            File.ReadAllBytes(fixture.Entry(originalManual)).SequenceEqual(manualBytes) && reopened.Read().Count == 2,
            "Duplicate across append paths rewrote original reason, chat, observation, timestamp or bytes");

        var maximum = reopened.AddManualReview(ManualItem with { Position = 4,
            Candidates = Enumerable.Range(1, 256).Select(index => "Кандидат " + index).ToArray() },
            "chat-manual", fixture.Observation);
        Check(reopened.Read("chat-manual").Contains(maximum) && maximum.Reason == GingerDeferredRows.ManualReviewReason,
            "The valid 256-candidate boundary was rejected");
        using var other = new Fixture();
        var sameIdentity = other.Store.AddManualReview(ManualItem, "chat-other", other.Observation);
        Check(sameIdentity.Id == originalEmpty.Id,
            "Manual-review identity depends on candidate list, reason, chat or storage path");
    }

    private static void InvalidManualReviewCannotPublish()
    {
        using var fixture = new Fixture();
        var invalidItems = new[] {
            ManualItem with { Linked = true }, ManualItem with { Candidates = [] },
            ManualItem with { Candidates = null! }, ManualItem with { Candidates = [null!] },
            ManualItem with { Candidates = [""] }, ManualItem with { Candidates = [" \t\n"] },
            ManualItem with { Candidates = ["Valid candidate", null!] },
            ManualItem with { Candidates = ["Valid candidate", " "] },
            ManualItem with { Candidates = Enumerable.Repeat("Candidate", 257).ToArray() },
            ManualItem with { Supplier = null! }, ManualItem with { Supplier = " " },
            ManualItem with { Supplier = new string('s', 513) }, ManualItem with { Document = "" },
            ManualItem with { Document = new string('d', 513) }, ManualItem with { Position = 0 },
            ManualItem with { Position = 1_000_001 }, ManualItem with { Title = null! },
            ManualItem with { Title = "" }, ManualItem with { Title = new string('t', 4097) },
            ManualItem with { Title = "broken\0title" }, ManualItem with { Title = "broken\ud800title" },
        };
        foreach (var item in invalidItems)
            Refused(() => fixture.Store.AddManualReview(item, "chat-1", fixture.Observation),
                "Invalid manual-review input was saved");
        foreach (var chat in new[] { "", "../other", new string('c', 129) })
            Refused(() => fixture.Store.AddManualReview(ManualItem, chat, fixture.Observation), "Invalid manual-review chat was saved");
        foreach (var observation in new[] { "", "relative-folder", fixture.Observation + "\nother", new string('p', 2049) })
            Refused(() => fixture.Store.AddManualReview(ManualItem, "chat-1", observation), "Invalid manual-review observation was saved");
        try
        {
            fixture.Store.AddManualReview(null!, "chat-1", fixture.Observation);
            throw new Exception("Null manual-review item was accepted");
        }
        catch (ArgumentNullException) { }
        Check(!Directory.Exists(fixture.Root), "Rejected manual-review input created persistence files");
    }

    private static void CorruptionCannotBecomeAnEmptyList()
    {
        using var fixture = new Fixture();
        var first = fixture.Store.Add(Item, "chat-1", fixture.Observation);
        var path = fixture.Entry(first);
        var original = File.ReadAllText(path);
        var corruptions = new List<string> { "{", "null", "[]", "{}", original[..^1] + ",\"Id\":\"duplicate\"}" };
        foreach (var change in new Action<JsonObject>[] {
            value => value.Remove("Title"), value => value["CanMatch"] = true,
            value => value["Title"] = "A different row stored under the original ID",
            value => value["Id"] = new string('0', 64), value => value["Position"] = 0,
            value => value["Reason"] = "Automatically click the row",
            value => value["Reason"] = GingerDeferredRows.ManualReviewReason + " ",
            value => value["Reason"] = GingerDeferredRows.ManualReviewReason.ToLowerInvariant(),
            value => value["Reason"] = null, value => value["ChatId"] = "../other",
            value => value["Supplier"] = null, value => value["CreatedAt"] = "not-a-date",
            value => value["ObservationDirectory"] = "relative",
        })
        {
            var value = JsonNode.Parse(original)!.AsObject();
            change(value);
            corruptions.Add(value.ToJsonString());
        }
        foreach (var corrupted in corruptions)
        {
            File.WriteAllText(path, corrupted);
            Refused(() => fixture.Store.Read(), "Corrupt queue was silently ignored");
            Refused(() => fixture.Store.Read("a-different-chat"), "Chat filter hid a corrupt record");
            Refused(() => fixture.Store.Add(Item with { Position = 3 }, "chat-2", fixture.Observation),
                "Append ignored a corrupt existing record");
            Refused(() => fixture.Store.AddManualReview(ManualItem with { Position = 3 }, "chat-2", fixture.Observation),
                "Manual-review append ignored a corrupt existing record");
            Check(File.ReadAllText(path) == corrupted && Directory.GetFiles(fixture.Root, "*.json").Length == 1,
                "A failed read or append replaced existing data");
        }
        File.WriteAllText(path, original);
        var renamed = Path.Combine(fixture.Root, new string('0', 64) + ".json");
        File.Move(path, renamed);
        Refused(() => fixture.Store.Add(Item, "chat-1", fixture.Observation), "Mismatched filename was accepted");
        Check(File.ReadAllText(renamed) == original && !File.Exists(path), "Collision replaced another entry");
    }

    private static void FailedWritesPreserveExistingRows()
    {
        using var fixture = new Fixture();
        var first = fixture.Store.Add(Item, "chat-1", fixture.Observation);
        var original = File.ReadAllBytes(fixture.Entry(first));
        using (var locked = new FileStream(Path.Combine(fixture.Root, ".write.lock"),
            FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Refused(() => new GingerDeferredRows(fixture.Root).Add(Item with { Position = 3 }, "chat-1", fixture.Observation),
                "Another writer's lock was ignored");
            Refused(() => new GingerDeferredRows(fixture.Root).AddManualReview(ManualItem with { Position = 3 }, "chat-1", fixture.Observation),
                "Manual-review append ignored another writer's lock");
            Refused(() => fixture.Store.Read(), "Read ignored an active writer");
        }
        Check(File.ReadAllBytes(fixture.Entry(first)).SequenceEqual(original) && fixture.Store.Read().Count == 1,
            "Failed write changed the saved entry");
        var blockedRoot = Path.Combine(fixture.Root, "blocked-root");
        File.WriteAllText(blockedRoot, "existing unrelated file");
        var blocked = new GingerDeferredRows(blockedRoot);
        Refused(() => blocked.Read(), "A file at the queue root was treated as an empty queue");
        Refused(() => blocked.Add(Item, "chat-1", fixture.Observation), "Impossible persistence reported success");
        Refused(() => blocked.AddManualReview(ManualItem, "chat-1", fixture.Observation),
            "Impossible manual-review persistence reported success");
        Check(File.ReadAllText(blockedRoot) == "existing unrelated file", "Failed root creation overwrote a file");
    }

    private static void InterruptedStagingIsNotACommittedRow()
    {
        using var fixture = new Fixture();
        var first = fixture.Store.Add(Item, "chat-1", fixture.Observation);
        var pending = Path.Combine(fixture.Root, first.Id + "." + Guid.NewGuid().ToString("N") + ".pending");
        File.WriteAllText(pending, "interrupted JSON");
        Check(fixture.Store.Read().Single() == first, "Interrupted staging corrupted a committed row");
        fixture.Store.Add(Item with { Position = 3 }, "chat-1", fixture.Observation);
        Check(fixture.Store.Read().Count == 2 && File.ReadAllText(pending) == "interrupted JSON",
            "Recovery overwrote another operation's staging file");
        File.WriteAllText(Path.Combine(fixture.Root, "unexpected.json"), "{}");
        Refused(() => fixture.Store.Read(), "Unexpected queue entry was silently dropped");
    }

    private static void LimitsAreEnforced()
    {
        using (var fixture = new Fixture())
        {
            var row = fixture.Store.Add(Item, "chat-1", fixture.Observation);
            File.WriteAllText(fixture.Entry(row), new string(' ', GingerDeferredRows.MaxEntryBytes + 1));
            Refused(() => fixture.Store.Read(), "Oversized record was loaded");
        }
        using (var fixture = new Fixture())
        {
            Directory.CreateDirectory(fixture.Root);
            for (var index = 0; index <= GingerDeferredRows.MaxRows; index++)
                File.WriteAllText(Path.Combine(fixture.Root, index.ToString("X64") + ".json"), "");
            try { fixture.Store.Read(); throw new Exception("Unbounded queue was accepted"); }
            catch (InvalidDataException ex)
            {
                Check(ex.Message.Contains("количество"), "Queue limit did not reject before reading entry contents");
            }
            Refused(() => fixture.Store.Add(Item, "chat-1", fixture.Observation), "Add accepted an over-capacity queue");
            Refused(() => fixture.Store.AddManualReview(ManualItem, "chat-1", fixture.Observation),
                "Manual-review append accepted an over-capacity queue");
        }
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _temporaryParent = Path.GetFullPath(Path.GetTempPath());
        internal string Root { get; }
        internal string Observation => Path.Combine(Root, "observation");
        internal GingerDeferredRows Store { get; }
        internal string Entry(GingerDeferredRow row) => Path.Combine(Root, row.Id + ".json");
        internal Fixture()
        {
            Root = Path.Combine(_temporaryParent, "BabyAI-DeferredRows-" + Guid.NewGuid().ToString("N"));
            Store = new GingerDeferredRows(Root);
        }
        public void Dispose()
        {
            var absolute = Path.GetFullPath(Root);
            if (!absolute.StartsWith(_temporaryParent.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(absolute).StartsWith("BabyAI-DeferredRows-", StringComparison.Ordinal))
                throw new InvalidOperationException("Unexpected fixture cleanup path");
            if (Directory.Exists(absolute)) Directory.Delete(absolute, recursive: true);
        }
    }
}
