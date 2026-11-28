using System.Text.Json;
using System.Text.Json.Nodes;
using BabyAI.Desktop;

internal static class Program
{
    private static int _checks;
    private static void Check(bool value, string message)
    {
        _checks++;
        if (!value) throw new Exception(message);
    }
    private static async Task Rejected(Func<Task> action, string label)
    {
        try { await action(); }
        catch (Exception error) when (error is InvalidDataException or OperationCanceledException or IOException or JsonException or InvalidOperationException)
        { _checks++; return; }
        throw new Exception("Accepted " + label);
    }
    private static JsonElement Reply(JsonElement request, string? word = "ALDHEYX") => JsonSerializer.SerializeToElement(new {
        ok = true, command = "ginger.search.word.preview", mode = "search_word",
        request_id = request.GetProperty("request_id").GetString(), word,
        reason = "Название аромата точнее, чем обозначение упаковки.", can_match = false
    });
    private static GingerDesktopObservation Observation(string path, int count = 278, bool enabled = false)
    {
        var body = JsonSerializer.SerializeToNode(new {
            status = count > 256 ? "needs_filters" : "observed", directory = path, search_completion_verified = false,
            process_id = 1234L, row_count = 9,
            source = new { Title = "LM White Label ALDHEYX 100мл EDP", Supplier = "Synthetic supplier", DocumentNumber = "A",
                PriceText = "100", LinkedProductText = "<не указано>", TableHandle = 5678L, ObservedRowPosition = 3,
                IdentityVerified = false, CanMatch = false },
            words = new[] { new { Title = "ALDHEYX", DescriptionText = "ALDHEYX", Checked = enabled ? (bool?)true : null,
                StateImage = enabled ? 2 : 0, ObservedPosition = 1, IdentityVerified = false } }
        })!;
        if (count > 256) body["candidate_count"] = count;
        else body["candidates"] = JsonSerializer.SerializeToNode(Enumerable.Range(1, count).Select(i => new {
            Title = "Candidate " + i, ObservedPosition = i, IdentityVerified = false
        }));
        return new(path, JsonSerializer.SerializeToElement(body));
    }
    public static async Task Main()
    {
        var root = Path.Combine(Path.GetTempPath(), "BabyAI-SearchPlanner-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var observed = Observation(root);
            var learning = new GingerLearning(Path.Combine(root, "lessons"));
            var lesson = new GingerLearning.LanguageLesson(observed.Supplier, "White Label — обозначение серии упаковки.",
                "Обозначение упаковки не является названием аромата.", true, DateTimeOffset.UtcNow, Guid.NewGuid().ToString("N"));
            learning.SaveLanguageLesson(lesson);
            var supplied = new List<JsonElement>();
            Task Guard(CancellationToken token) { token.ThrowIfCancellationRequested(); return Task.CompletedTask; }
            GingerSearchWordPlanner Planner(Func<JsonElement, CancellationToken, Task<JsonElement>> consult,
                Func<CancellationToken, Task>? guard = null) => new(consult, learning.ReadLanguageLessons, guard ?? Guard, "chat", "run");
            var planner = Planner((request, token) => { supplied.Add(request.Clone()); return Task.FromResult(Reply(request)); });
            var decision = await planner.ChooseAsync(observed, [], CancellationToken.None);
            Check(decision.Word == "ALDHEYX" && decision.RequireCurrent is not null, "Decision lost word/context guard");
            Check(supplied.Single().GetProperty("lesson").GetString()!.Contains(lesson.Text), "Confirmed lesson omitted");
            Check(supplied[0].GetProperty("words")[0].GetProperty("checked").ValueKind == JsonValueKind.Null,
                "Real Ginger null checkbox was changed");
            var evidence = Directory.GetFiles(root, "search-decision-*.json").Single();
            var report = JsonDocument.Parse(File.ReadAllText(evidence)).RootElement;
            Check(report.GetProperty("chat_id").GetString() == "chat" && report.GetProperty("run_id").GetString() == "run" &&
                report.GetProperty("request").GetRawText() == supplied[0].GetRawText(), "Evidence lost actual request binding");
            Check(report.GetProperty("lessons").GetArrayLength() == 1, "Evidence omitted lesson revision set");
            await decision.RequireCurrent!(CancellationToken.None);
            learning.SaveLanguageLesson(lesson with { Enabled = false, RecordedAt = lesson.RecordedAt.AddSeconds(1) });
            await Rejected(() => decision.RequireCurrent(CancellationToken.None), "revocation after response");

            foreach (var field in new[] { "request_id", "can_match", "word", "mode", "command", "ok", "reason", "extra", "duplicate" })
            {
                var beforeFiles = Directory.GetFiles(root, "search-decision-*.json").Length;
                var invalid = Planner((request, token) => {
                    var node = JsonNode.Parse(Reply(request).GetRawText())!;
                    switch (field)
                    {
                        case "request_id": node[field] = new string('0', 32); break;
                        case "can_match": node[field] = true; break;
                        case "word": node[field] = "invented"; break;
                        case "mode": node[field] = "match"; break;
                        case "command": node[field] = "ginger.teach.preview"; break;
                        case "ok": node[field] = false; break;
                        case "reason": node[field] = " "; break;
                        case "extra": node[field] = "untrusted"; break;
                        case "duplicate":
                            return Task.FromResult(JsonDocument.Parse(node.ToJsonString().TrimEnd('}') + ",\"word\":\"ALDHEYX\"}").RootElement.Clone());
                    }
                    return Task.FromResult(JsonSerializer.SerializeToElement(node));
                });
                await Rejected(async () => { await invalid.ChooseAsync(observed, [], CancellationToken.None); }, field);
                Check(Directory.GetFiles(root, "search-decision-*.json").Length == beforeFiles, "Invalid answer saved as valid evidence");
            }
            var revision = lesson with { Enabled = true, RecordedAt = lesson.RecordedAt.AddSeconds(2) };
            var staleLesson = Planner((request, token) => {
                learning.SaveLanguageLesson(revision);
                return Task.FromResult(Reply(request));
            });
            await Rejected(async () => { await staleLesson.ChooseAsync(observed, [], CancellationToken.None); }, "revocation during inference");
            var current = true;
            var changedRun = Planner((request, token) => { current = false; return Task.FromResult(Reply(request)); },
                token => current ? Task.CompletedTask : throw new OperationCanceledException("New chat"));
            await Rejected(async () => { await changedRun.ChooseAsync(observed, [], CancellationToken.None); }, "changed chat");
            await Rejected(async () => { await planner.ChooseAsync(observed, [new("ALDHEYX", 278, 25)], CancellationToken.None); }, "repeated word");
            var nullPlanner = Planner((request, token) => Task.FromResult(Reply(request, null)));
            var nullDecision = await nullPlanner.ChooseAsync(observed, [], CancellationToken.None);
            Check(nullDecision.Word is null, "Manual-review decision became an action");
            var nonexistent = Observation(Path.Combine(root, "missing-directory"));
            await Rejected(async () => { await planner.ChooseAsync(nonexistent, [], CancellationToken.None); }, "failed evidence save");

            var requestsBefore = supplied.Count;
            var refreshes = 0; var inputs = 0;
            var flow = new GingerReasonedFilterFlow();
            var result = await flow.PrepareAsync(observed, planner.ChooseAsync,
                (before, token) => { refreshes++; return Task.FromResult(before); },
                (before, word, token) => { inputs++; return Task.FromResult(Observation(root, 10, true)); }, CancellationToken.None);
            Check(result.CandidateCount == 10 && inputs == 1 && refreshes == 1 && supplied.Count == requestsBefore + 1,
                "Planner+flow did not stop after 278 to 10");
            var lessonChangedDuringRefresh = new GingerReasonedFilterFlow();
            await Rejected(async () => {
                await lessonChangedDuringRefresh.PrepareAsync(observed, planner.ChooseAsync,
                    (before, token) => {
                        learning.SaveLanguageLesson(revision with { Enabled = false, RecordedAt = revision.RecordedAt.AddSeconds(1) });
                        return Task.FromResult(before);
                    }, (before, word, token) => { inputs++; return Task.FromResult(Observation(root, 10, true)); }, CancellationToken.None);
            }, "lesson changed during UI refresh");
            Check(inputs == 1, "Revoked lesson dispatched UI input");
            Console.WriteLine($"PASS: {_checks} search planner binding/evidence checks");
        }
        finally
        {
            var full = Path.GetFullPath(root);
            var parent = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!full.StartsWith(parent, StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(full).StartsWith("BabyAI-SearchPlanner-"))
                throw new InvalidOperationException("Unexpected fixture directory");
            Directory.Delete(full, recursive: true);
        }
    }
}
