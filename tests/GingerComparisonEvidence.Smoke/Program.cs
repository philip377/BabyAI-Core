using BabyAI.Desktop;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

EvidenceTests.Run();

internal static class EvidenceTests
{
    private static int _checks;
    private const string FullReport = """
        {"request":{"supplier":"Поставщик","source":"Полное название 100 мл","lesson":"1. Урок поставщика\n2. Проверенное объяснение","candidates":["Кандидат A","Кандидат B"],"lessons":[{"id":"lesson-1","enabled":true,"text":"Полный урок"}]},"result":{"ok":true,"command":"ginger.teach.preview","id":14,"candidate":"Кандидат B","understanding":"Понял маркировку поставщика.","explanation":"Подходит второй кандидат.","comparisons":[{"index":1,"candidate":"Кандидат A","attributes":{"identity":"unknown","volume":"100ml"},"status":"rejected"},{"index":2,"candidate":"Кандидат B","attributes":{"identity":"verified","volume":"100ml"},"status":"matched"}],"identity_evidence":{"source":"Полное название","candidate":"Кандидат B","verified":true},"lesson_evidence":{"attributes":["identity"],"can_match":false}}}
        """;

    internal static void Run()
    {
        using var fixture = new Fixture();
        HappyAndImmutable(fixture);
        InvalidReportsDoNotWrite(fixture);
        SizeBounds(fixture);
        InvalidDirectories(fixture);
        ReparseDirectory(fixture);
        Console.WriteLine($"PASS: {_checks} full comparison-evidence checks; immutable atomic publication, strict failures, isolated temporary files only");
    }
    private static void Check(bool value, string message)
    {
        if (!value) throw new Exception(message);
        _checks++;
    }
    private static JsonElement Json(string text)
    {
        using var document = JsonDocument.Parse(text);
        return document.RootElement.Clone();
    }
    private static JsonElement Edited(Action<JsonObject> edit)
    {
        var report = JsonNode.Parse(FullReport)!.AsObject();
        edit(report);
        return Json(report.ToJsonString());
    }
    private static void Rejected(string directory, JsonElement report, string reason)
    {
        var before = Directory.Exists(directory) ? Directory.GetFiles(directory).Order().ToArray() : [];
        var returned = false;
        try { GingerChatComparisonEvidence.Save(directory, report); returned = true; }
        catch (Exception error) when (error is InvalidDataException or IOException or ArgumentException or UnauthorizedAccessException or ObjectDisposedException) { }
        Check(!returned, "Save returned a path for " + reason);
        if (Directory.Exists(directory))
            Check(Directory.GetFiles(directory).Order().SequenceEqual(before), "Rejected report created/removed evidence: " + reason);
    }

    private static void HappyAndImmutable(Fixture fixture)
    {
        var report = Json(FullReport);
        var before = report.GetRawText();
        var path = GingerChatComparisonEvidence.Save(fixture.Root, report);
        Check(Path.IsPathFullyQualified(path) && Path.GetDirectoryName(path) == fixture.Root, "Save returned an unrelated/relative path");
        var stem = Path.GetFileNameWithoutExtension(path);
        Check(stem.StartsWith("comparison-", StringComparison.Ordinal) && Guid.TryParseExact(stem[11..], "N", out _), "Unexpected evidence filename");
        var bytes = File.ReadAllBytes(path);
        Check(bytes.Length <= GingerChatComparisonEvidence.MaxReportBytes && !bytes.AsSpan().StartsWith(Encoding.UTF8.Preamble), "Evidence is too large or has a BOM");
        using var saved = JsonDocument.Parse(bytes);
        Check(JsonElement.DeepEquals(saved.RootElement, report), "Full model, lessons, transport or request fields were lost/reinterpreted");
        Check(report.GetRawText() == before, "Save mutated the caller's report");
        Check(saved.RootElement.GetProperty("result").GetProperty("candidate").GetString() == "Кандидат B", "Save reinterpreted the selected candidate");
        for (var index = 0; index < 4; index++)
        {
            var next = GingerChatComparisonEvidence.Save(fixture.Root, report);
            Check(next != path && File.Exists(next), "Repeated save reused an evidence path");
            Check(File.ReadAllBytes(path).SequenceEqual(bytes), "Repeated save replaced earlier evidence");
        }
        Check(Directory.GetFiles(fixture.Root, "*.pending").Length == 0, "Successful write left a pending file");
        var nullCandidate = Edited(root => root["result"]!["candidate"] = null);
        var nullPath = GingerChatComparisonEvidence.Save(fixture.Root, nullCandidate);
        Check(Json(File.ReadAllText(nullPath)).GetProperty("result").GetProperty("candidate").ValueKind == JsonValueKind.Null,
            "A valid no-match response was changed or rejected");
        var duplicateUnselected = Edited(root => { root["request"]!["candidates"] = new JsonArray("A", "A"); root["result"]!["candidate"] = null; });
        Check(File.Exists(GingerChatComparisonEvidence.Save(fixture.Root, duplicateUnselected)), "No-match was reinterpreted because unselected candidates share a title");
    }

    private static void InvalidReportsDoNotWrite(Fixture fixture)
    {
        foreach (var root in new[] { "null", "[]", "true", "\"text\"", "{}", "{\"request\":null,\"result\":null}" })
            Rejected(fixture.Root, Json(root), "invalid root " + root);
        Rejected(fixture.Root, default, "undefined root");
        foreach (var field in new[] { "request", "result" })
            Rejected(fixture.Root, Edited(root => root.Remove(field)), "missing " + field);
        foreach (var field in new[] { "supplier", "source", "lesson", "candidates" })
            Rejected(fixture.Root, Edited(root => root["request"]!.AsObject().Remove(field)), "missing request " + field);
        foreach (var field in new[] { "candidate", "explanation", "understanding" })
            Rejected(fixture.Root, Edited(root => root["result"]!.AsObject().Remove(field)), "missing result " + field);
        foreach (var field in new[] { "supplier", "source" })
        foreach (var value in new[] { JsonValue.Create(" \t"), JsonValue.Create(3), null })
            Rejected(fixture.Root, Edited(root => root["request"]![field] = value?.DeepClone()), "invalid request text " + field);
        foreach (var field in new[] { "explanation", "understanding" })
        foreach (var value in new[] { JsonValue.Create("\n"), JsonValue.Create(4), JsonValue.Create(new string('x', 4001)), null })
            Rejected(fixture.Root, Edited(root => root["result"]![field] = value?.DeepClone()), "invalid result text " + field);
        foreach (var value in new[] { "[]", "[null]", "[\" \"]", "[1]", "{}", "null" })
            Rejected(fixture.Root, Edited(root => root["request"]!["candidates"] = JsonNode.Parse(value)), "invalid candidate array");
        Rejected(fixture.Root, Edited(root => root["request"]!["candidates"] = new JsonArray(Enumerable.Range(0, 257).Select(i => JsonValue.Create(i.ToString())).ToArray())), "257 candidates");
        foreach (var value in new[] { JsonValue.Create("not listed"), JsonValue.Create("кандидат B"), JsonValue.Create("Кандидат B "), JsonValue.Create(1), JsonValue.Create(false) })
            Rejected(fixture.Root, Edited(root => root["result"]!["candidate"] = value!.DeepClone()), "inexact/invalid model candidate");
        Rejected(fixture.Root, Edited(root => root["request"]!["candidates"] = new JsonArray("Кандидат B", "Кандидат B")), "ambiguous chosen candidate");
        Rejected(fixture.Root, Edited(root => root["result"]!["error"] = null), "error response");
        Rejected(fixture.Root, Edited(root => root["result"]!["ok"] = false), "failed transport response");
        foreach (var field in new[] { "request", "result" })
        foreach (var mode in new[] { JsonValue.Create("source_only"), JsonValue.Create("unknown"), JsonValue.Create(1), null })
            Rejected(fixture.Root, Edited(root => { root[field]!["mode"] = mode?.DeepClone(); root["result"]!["candidate"] = null; }), "non-comparison mode");
        foreach (var invalid in new[] { JsonValue.Create(1), null })
            Rejected(fixture.Root, Edited(root => root["request"]!["lesson"] = invalid?.DeepClone()), "invalid lesson text");
        foreach (var command in new[] { JsonValue.Create("ginger.teach.source.preview"), JsonValue.Create("ginger.recognize.preview"), JsonValue.Create("other"), JsonValue.Create(1), null })
            Rejected(fixture.Root, Edited(root => root["result"]!["command"] = command?.DeepClone()), "mismatched command");
        foreach (var id in new[] { "-1", "1.5", "9223372036854775808", "null", "\"14\"", "true" })
            Rejected(fixture.Root, Edited(root => root["result"]!["id"] = JsonNode.Parse(id)), "invalid response id");
        foreach (var lesson in new[] { "", " \t\n" })
        {
            var recognize = Edited(root => { root["request"]!["lesson"] = lesson; root["result"]!["command"] = "ginger.recognize.preview"; });
            Check(File.Exists(GingerChatComparisonEvidence.Save(fixture.Root, recognize)), "Valid recognize command rejected");
            Rejected(fixture.Root, Edited(root => root["request"]!["lesson"] = lesson), "teach command with no lessons");
        }
        var core = Edited(root => { foreach (var name in new[] { "ok", "command", "id" }) root["result"]!.AsObject().Remove(name); });
        Check(File.Exists(GingerChatComparisonEvidence.Save(fixture.Root, core)), "Valid direct model result without transport rejected");
        foreach (var duplicate in new[]
        {
            FullReport.Replace("\"request\":", "\"request\":{},\"request\":"),
            FullReport.Replace("\"supplier\":", "\"supplier\":\"other\",\"supplier\":"),
            FullReport.Replace("\"candidate\":\"Кандидат B\",\"understanding\"", "\"candidate\":null,\"candidate\":\"Кандидат B\",\"understanding\""),
            FullReport.Replace("\"can_match\":false", "\"can_match\":true,\"can_match\":false"),
            FullReport.Replace("\"volume\":\"100ml\"", "\"volume\":\"50ml\",\"volume\":\"100ml\"")
        }) Rejected(fixture.Root, Json(duplicate), "duplicate field at root/request/result/nested object/array");
        var boundary = Edited(root =>
        {
            root["request"]!["candidates"] = new JsonArray(Enumerable.Range(0, 256).Select(i => JsonValue.Create(i.ToString())).ToArray());
            root["result"]!["candidate"] = "255";
            root["result"]!["understanding"] = new string('x', 4000);
            root["result"]!["explanation"] = new string('y', 4000);
        });
        Check(File.Exists(GingerChatComparisonEvidence.Save(fixture.Root, boundary)), "Valid field/count boundary rejected");
    }

    private static void SizeBounds(Fixture fixture)
    {
        var node = JsonNode.Parse(FullReport)!.AsObject();
        node["opaque_evidence"] = "";
        var emptySize = JsonSerializer.SerializeToUtf8Bytes(Json(node.ToJsonString())).Length;
        node["opaque_evidence"] = new string('x', GingerChatComparisonEvidence.MaxReportBytes - emptySize);
        var exact = Json(node.ToJsonString());
        var path = GingerChatComparisonEvidence.Save(fixture.Root, exact);
        Check(new FileInfo(path).Length == GingerChatComparisonEvidence.MaxReportBytes, "Exact UTF-8 byte limit rejected or changed");
        node["opaque_evidence"] = new string('x', GingerChatComparisonEvidence.MaxReportBytes - emptySize + 1);
        Rejected(fixture.Root, Json(node.ToJsonString()), "one byte oversized report");
        node["opaque_evidence"] = new string('я', 400_000);
        Rejected(fixture.Root, Json(node.ToJsonString()), "UTF-8 escaped output exceeds byte limit despite fewer characters");
    }

    private static void InvalidDirectories(Fixture fixture)
    {
        var report = Json(FullReport);
        Rejected(Path.Combine(fixture.Root, "missing"), report, "missing directory");
        Check(!Directory.Exists(Path.Combine(fixture.Root, "missing")), "Save created a missing observation directory");
        foreach (var path in new[] { "", " ", "relative-observation", ".", null! })
            Rejected(path, report, "non-full directory");
        var file = Path.Combine(fixture.Root, "not-directory");
        File.WriteAllText(file, "keep me");
        Rejected(file, report, "file as directory");
        Check(File.ReadAllText(file) == "keep me", "Directory validation damaged a file");
    }

    private static void ReparseDirectory(Fixture fixture)
    {
        var target = Directory.CreateDirectory(Path.Combine(fixture.Root, "link-target")).FullName;
        Directory.CreateDirectory(Path.Combine(target, "nested"));
        var link = Path.Combine(fixture.Root, "link");
        try { Directory.CreateSymbolicLink(link, target); }
        catch (Exception error) when (error is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            Console.WriteLine("Reparse fixture unavailable on this host: " + error.GetType().Name);
            return;
        }
        try
        {
            Rejected(link, Json(FullReport), "linked observation directory");
            Rejected(Path.Combine(link, "nested"), Json(FullReport), "linked ancestor");
            Check(Directory.GetFiles(target, "*", SearchOption.AllDirectories).Length == 0, "Reparse validation wrote through the link");
        }
        finally { Directory.Delete(link); }
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _temp = Path.GetFullPath(Path.GetTempPath());
        internal string Root { get; }
        internal Fixture()
        {
            Root = Path.GetFullPath(Path.Combine(_temp, "BabyAI-comparison-evidence-test-" + Guid.NewGuid().ToString("N")));
            Directory.CreateDirectory(Root);
        }
        public void Dispose()
        {
            var full = Path.GetFullPath(Root);
            if (Path.GetDirectoryName(full) != _temp.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) ||
                !Path.GetFileName(full).StartsWith("BabyAI-comparison-evidence-test-", StringComparison.Ordinal))
                throw new Exception("Refusing cleanup outside the isolated fixture");
            RequireOrdinaryTree(full);
            Directory.Delete(full, recursive: true);
        }

        private static void RequireOrdinaryTree(string path)
        {
            var attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
                throw new Exception("Refusing recursive cleanup through a reparse point");
            if ((attributes & FileAttributes.Directory) != 0)
                foreach (var child in Directory.EnumerateFileSystemEntries(path)) RequireOrdinaryTree(child);
        }
    }
}
