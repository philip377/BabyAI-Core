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
    private static JsonElement Reply(JsonElement request, string? word = "ALDHEYX", string role = "name")
    {
        var requestId = request.GetProperty("request_id").GetString();
        var reason = word is null ? "Qwen не предложила безопасное следующее слово. Нужна ручная проверка."
            : "Наблюдавшееся слово предложено для поиска; роль названия — оценка модели.";
        object? sourceEvidence = null;
        if (word is not null)
        {
            var source = request.GetProperty("source").GetString()!;
            var index = source.IndexOf(word, StringComparison.OrdinalIgnoreCase);
            if (index < 0) throw new InvalidOperationException("Fixture word is absent from source");
            sourceEvidence = new { origin = "observed_source", derived_by = "host", quote = source.Substring(index, word.Length) };
        }
        return JsonSerializer.SerializeToElement(new {
            ok = true, command = "ginger.search.word.preview", mode = "search_word",
            request_id = requestId, word, reason, can_match = false,
            search_evidence = new {
                version = 2, definitions = Array.Empty<object>(), blocked_reason = word is null ? reason : null,
                source_evidence = sourceEvidence,
                proposal = new { request_id = requestId, word, role = word is null ? "unknown" : role,
                    reason = "Непроверенное объяснение модели." }
            }
        });
    }

    private static async Task EvidenceContractCases(
        Func<Func<JsonElement, CancellationToken, Task<JsonElement>>, GingerSearchWordPlanner> planner,
        GingerDesktopObservation observed, string root)
    {
        static JsonObject Node(JsonElement request, string? word = "ALDHEYX") => JsonNode.Parse(Reply(request, word).GetRawText())!.AsObject();
        static JsonNode Evidence(JsonNode node) => node["search_evidence"]!;
        static JsonNode Proposal(JsonNode node) => Evidence(node)["proposal"]!;
        static JsonNode SourceEvidence(JsonNode node) => Evidence(node)["source_evidence"]!;
        static JsonObject Citation(string quote = "ALDHEYX") => new() {
            ["origin"] = "observed_source", ["derived_by"] = "host", ["quote"] = quote
        };
        static JsonObject Definition(string role = "name") => new() {
            ["term"] = "ALDHEYX", ["role"] = role, ["quote"] = "ALDHEYX — название аромата."
        };
        async Task Reject(string label, Func<JsonElement, JsonElement> reply)
        {
            var before = Directory.GetFiles(root, "search-decision-*").Length;
            var invalid = planner((request, _) => Task.FromResult(reply(request)));
            await Rejected(async () => { await invalid.ChooseAsync(observed, [], CancellationToken.None); }, label);
            Check(Directory.GetFiles(root, "search-decision-*").Length == before,
                "Malformed evidence was persisted: " + label);
        }
        var mutations = new List<(string Label, Action<JsonObject> Change)> {
            ("legacy response without evidence", node => node.Remove("search_evidence")),
            ("null evidence", node => node["search_evidence"] = null),
            ("array evidence", node => node["search_evidence"] = new JsonArray()),
            ("unknown evidence field", node => Evidence(node)["extra"] = true),
            ("missing evidence field", node => Evidence(node).AsObject().Remove("proposal")),
            ("v1 version", node => Evidence(node)["version"] = 1),
            ("future version", node => Evidence(node)["version"] = 3),
            ("boolean version", node => Evidence(node)["version"] = true),
            ("string version", node => Evidence(node)["version"] = "2"),
            ("fractional version", node => Evidence(node)["version"] = JsonNode.Parse("2.0")),
            ("null version", node => Evidence(node)["version"] = null),
            ("null definitions", node => Evidence(node)["definitions"] = null),
            ("object definitions", node => Evidence(node)["definitions"] = new JsonObject()),
            ("25 definitions", node => Evidence(node)["definitions"] = new JsonArray(
                Enumerable.Range(0, 25).Select(_ => (JsonNode)Definition()).ToArray())),
            ("null definition", node => Evidence(node)["definitions"] = new JsonArray((JsonNode?)null)),
            ("extra definition field", node => { var value = Definition(); value["extra"] = true;
                Evidence(node)["definitions"] = new JsonArray(value); }),
            ("missing definition field", node => { var value = Definition(); value.Remove("quote");
                Evidence(node)["definitions"] = new JsonArray(value); }),
            ("empty term", node => { var value = Definition(); value["term"] = " ";
                Evidence(node)["definitions"] = new JsonArray(value); }),
            ("oversized term", node => { var value = Definition(); value["term"] = new string('t', 201);
                Evidence(node)["definitions"] = new JsonArray(value); }),
            ("nonstring term", node => { var value = Definition(); value["term"] = 1;
                Evidence(node)["definitions"] = new JsonArray(value); }),
            ("empty definition quote", node => { var value = Definition(); value["quote"] = "";
                Evidence(node)["definitions"] = new JsonArray(value); }),
            ("oversized definition quote", node => { var value = Definition(); value["quote"] = new string('q', 1001);
                Evidence(node)["definitions"] = new JsonArray(value); }),
            ("null definition quote", node => { var value = Definition(); value["quote"] = null;
                Evidence(node)["definitions"] = new JsonArray(value); }),
            ("invalid definition role", node => Evidence(node)["definitions"] = new JsonArray(Definition("Name"))),
            ("missing proposal without veto", node => Evidence(node)["proposal"] = null),
            ("array proposal", node => Evidence(node)["proposal"] = new JsonArray()),
            ("unknown proposal field", node => Proposal(node)["extra"] = true),
            ("old model-authored source quote", node => Proposal(node)["source_quote"] = "ALDHEYX"),
            ("missing proposal field", node => Proposal(node).AsObject().Remove("role")),
            ("stale proposal id", node => Proposal(node)["request_id"] = new string('0', 32)),
            ("null proposal id", node => Proposal(node)["request_id"] = null),
            ("mismatched proposal word", node => Proposal(node)["word"] = "Another name"),
            ("null proposed word with selected envelope", node => { Proposal(node)["word"] = null;
                Proposal(node)["role"] = "unknown"; Evidence(node)["source_evidence"] = null; }),
            ("boolean proposal word", node => Proposal(node)["word"] = false),
            ("oversized proposal word", node => Proposal(node)["word"] = new string('w', 513)),
            ("invalid proposal role", node => Proposal(node)["role"] = "Name"),
            ("nonstring proposal role", node => Proposal(node)["role"] = false),
            ("missing source evidence field", node => Evidence(node).AsObject().Remove("source_evidence")),
            ("null source evidence for positive proposal", node => Evidence(node)["source_evidence"] = null),
            ("array source evidence", node => Evidence(node)["source_evidence"] = new JsonArray()),
            ("extra source evidence field", node => SourceEvidence(node)["extra"] = true),
            ("missing source evidence quote", node => SourceEvidence(node).AsObject().Remove("quote")),
            ("wrong source evidence origin", node => SourceEvidence(node)["origin"] = "lesson"),
            ("nonstring source evidence origin", node => SourceEvidence(node)["origin"] = 1),
            ("model-derived source evidence", node => SourceEvidence(node)["derived_by"] = "model"),
            ("null source evidence derivation", node => SourceEvidence(node)["derived_by"] = null),
            ("null source quote", node => SourceEvidence(node)["quote"] = null),
            ("empty source quote", node => SourceEvidence(node)["quote"] = ""),
            ("blank source quote", node => SourceEvidence(node)["quote"] = " \t"),
            ("oversized source quote", node => SourceEvidence(node)["quote"] = new string('q', 513)),
            ("quote has wrong source case", node => SourceEvidence(node)["quote"] = "aldheyx"),
            ("unrelated literal source quote", node => SourceEvidence(node)["quote"] = "White"),
            ("extended source phrase is not the word", node => SourceEvidence(node)["quote"] = "Label ALDHEYX"),
            ("empty proposal reason", node => Proposal(node)["reason"] = " "),
            ("oversized proposal reason", node => Proposal(node)["reason"] = new string('r', 601)),
            ("nonstring proposal reason", node => Proposal(node)["reason"] = 7),
            ("nonstring blocked reason", node => Evidence(node)["blocked_reason"] = false),
            ("blank blocked reason", node => Evidence(node)["blocked_reason"] = " "),
            ("oversized blocked reason", node => Evidence(node)["blocked_reason"] = new string('b', 601)),
            ("actionable envelope with veto", node => Evidence(node)["blocked_reason"] = node["reason"]!.DeepClone()),
            ("unbound veto reason", node => { node["word"] = null; Evidence(node)["blocked_reason"] = "Blocked"; }),
            ("positive proposal with unblocked null envelope", node => node["word"] = null)
        };
        foreach (var role in new[] { "packaging", "service", "tester", "gender", "unknown" })
            mutations.Add(("unsupported success role " + role, node => Proposal(node)["role"] = role));
        foreach (var (label, change) in mutations)
            await Reject(label, request => { var node = Node(request); change(node); return JsonSerializer.SerializeToElement(node); });

        foreach (var badField in new[] { "role", "source_evidence", "source_quote" })
            await Reject("null proposal word requires unknown/null evidence: " + badField, request => {
                var node = Node(request, null);
                if (badField == "source_evidence") Evidence(node)["source_evidence"] = Citation();
                else Proposal(node)[badField] = badField == "role" ? "name" : "";
                return JsonSerializer.SerializeToElement(node);
            });
        await Reject("model refusal without host veto", request => {
            var node = Node(request, null); Evidence(node)["blocked_reason"] = null;
            return JsonSerializer.SerializeToElement(node);
        });
        await Reject("null proposal and no blocked reason", request => {
            var node = Node(request, null); Evidence(node)["proposal"] = null; Evidence(node)["blocked_reason"] = null;
            return JsonSerializer.SerializeToElement(node);
        });
        await Reject("prechoice veto cannot contain a source citation", request => {
            var node = Node(request, null); Evidence(node)["proposal"] = null; Evidence(node)["source_evidence"] = Citation();
            return JsonSerializer.SerializeToElement(node);
        });
        await Reject("stale id in vetoed proposal", request => {
            var node = Node(request); node["word"] = null;
            Evidence(node)["blocked_reason"] = node["reason"]!.DeepClone();
            Proposal(node)["request_id"] = new string('0', 32);
            return JsonSerializer.SerializeToElement(node);
        });
        foreach (var quote in new string?[] { null, "", " \t", "White", "aldheyx" })
            await Reject("missing/invalid source citation in vetoed positive proposal", request => {
                var node = Node(request); node["word"] = null;
                Evidence(node)["blocked_reason"] = node["reason"]!.DeepClone();
                Evidence(node)["source_evidence"] = quote is null ? null : Citation(quote);
                return JsonSerializer.SerializeToElement(node);
            });
        await Reject("complete legacy v1 evidence", request => {
            var node = Node(request); Evidence(node)["version"] = 1;
            Evidence(node).AsObject().Remove("source_evidence"); Proposal(node)["source_quote"] = "ALDHEYX";
            return JsonSerializer.SerializeToElement(node);
        });

        foreach (var duplicate in new[] { "evidence", "proposal", "definition", "source evidence", "optional id" })
            await Reject("duplicate nested JSON: " + duplicate, request => {
                var node = Node(request); Evidence(node)["definitions"] = new JsonArray(Definition());
                var raw = node.ToJsonString();
                var invalid = duplicate switch {
                    "evidence" => raw.Replace("\"search_evidence\":{", "\"search_evidence\":{\"version\":2,", StringComparison.Ordinal),
                    "proposal" => raw.Replace("\"proposal\":{", "\"proposal\":{\"word\":\"ALDHEYX\",", StringComparison.Ordinal),
                    "definition" => raw.Replace("\"term\":\"ALDHEYX\"", "\"term\":\"ALDHEYX\",\"term\":\"ALDHEYX\"", StringComparison.Ordinal),
                    "source evidence" => raw.Replace("\"source_evidence\":{", "\"source_evidence\":{\"origin\":\"observed_source\",", StringComparison.Ordinal),
                    _ => "{\"id\":1,\"id\":1," + raw[1..]
                };
                Check(invalid != raw, "Duplicate fixture was not injected");
                return JsonDocument.Parse(invalid).RootElement.Clone();
            });

        foreach (var role in new[] { "brand", "name", "variant" })
        {
            JsonElement received = default;
            var valid = planner((request, _) => {
                var node = Node(request); node["id"] = long.MaxValue;
                Proposal(node)["role"] = role;
                node["reason"] = new string('h', 600);
                Proposal(node)["reason"] = new string('r', 600);
                var roles = new[] { "brand", "name", "variant", "packaging", "service", "concentration", "volume", "tester", "gender", "unknown" };
                Evidence(node)["definitions"] = new JsonArray(Enumerable.Range(0, 24).Select(index => (JsonNode)new JsonObject {
                    ["term"] = new string('t', 200), ["role"] = roles[index % roles.Length], ["quote"] = new string('q', 1000)
                }).ToArray());
                received = JsonSerializer.SerializeToElement(node);
                return Task.FromResult(received);
            });
            var choice = await valid.ChooseAsync(observed, [], CancellationToken.None);
            Check(choice.Word == "ALDHEYX" && choice.Reason == new string('h', 600),
                "Valid " + role + " proposal rejected or raw model reason promoted over host reason");
            var id = received.GetProperty("request_id").GetString();
            using var saved = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "search-decision-" + id + ".json")));
            Check(JsonElement.DeepEquals(saved.RootElement.GetProperty("result"), received),
                "Complete evidence, repeated terms, or conflicting roles lost in journal");
        }

        foreach (var (word, source) in new[] {
            ("aldheyx", "LM Aldheyx 100мл EDP"),
            ("листопад", "Avenor Листопад 30 мл EDT"),
            (new string('W', 512), new string('W', 512))
        })
        {
            var sourceCase = Observation(root, word: word, sourceTitle: source);
            JsonElement received = default;
            var valid = planner((request, _) => {
                received = Reply(request, word);
                return Task.FromResult(received);
            });
            var decision = await valid.ChooseAsync(sourceCase, [], CancellationToken.None);
            Check(decision.Word == word, "Literal source case or 512-character word was rejected");
            var sourceEvidence = received.GetProperty("search_evidence").GetProperty("source_evidence");
            Check(sourceEvidence.GetProperty("quote").GetString() == source.Substring(
                    source.IndexOf(word, StringComparison.OrdinalIgnoreCase), word.Length),
                "Host citation lost the literal source case");
        }

        foreach (var shape in new[] { "no-proposal", "blocked-positive-proposal", "blocked-null-proposal", "host-neutral-refusal" })
        {
            JsonElement received = default;
            var decisions = 0;
            var veto = planner((request, _) => {
                decisions++;
                var node = Node(request, shape == "blocked-positive-proposal" ? "ALDHEYX" : null);
                node["word"] = null;
                node["reason"] = shape == "host-neutral-refusal"
                    ? "Qwen не предложила безопасное следующее слово. Нужна ручная проверка."
                    : "Слово не подтверждено: противоречивые определения.";
                Evidence(node)["blocked_reason"] = node["reason"]!.DeepClone();
                if (shape == "host-neutral-refusal")
                    Proposal(node)["reason"] = "В уроках конфликт: название и упаковка несовместимы.";
                if (shape == "no-proposal") Evidence(node)["proposal"] = null;
                if (shape == "blocked-positive-proposal") Proposal(node)["role"] = "packaging";
                received = JsonSerializer.SerializeToElement(node);
                return Task.FromResult(received);
            });
            var fresh = Observation(Path.Combine(root, "fresh-" + shape));
            var refreshes = 0; var actions = 0;
            GingerSearchReviewRequiredException? review = null;
            try
            {
                await new GingerReasonedFilterFlow().PrepareAsync(observed, veto.ChooseAsync,
                    (before, _) => {
                        refreshes++;
                        var decisionId = received.GetProperty("request_id").GetString();
                        Check(File.Exists(Path.Combine(root, "search-decision-" + decisionId + ".json")),
                            "Refusal was refreshed before its evidence was durable: " + shape);
                        return Task.FromResult(fresh);
                    },
                    (before, _, _) => { actions++; return Task.FromResult(before); }, CancellationToken.None);
            }
            catch (GingerSearchReviewRequiredException error) { review = error; }
            Check(review is not null, "Validated refusal did not become a typed manual-review outcome: " + shape);
            Check(refreshes == 1 && actions == 0 && decisions == 1,
                "Veto/refusal did not perform exactly one fresh read without input or model retry: " + shape);
            Check(ReferenceEquals(review!.Observation, fresh) && review.EvidenceDirectory == observed.Directory,
                "Refusal lost the fresh observation or original evidence directory: " + shape);
            Check(review.Reason == received.GetProperty("reason").GetString() && review.Message == review.Reason,
                "Refusal changed the host reason or added an inference claim: " + shape);
            var id = received.GetProperty("request_id").GetString();
            using var saved = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "search-decision-" + id + ".json")));
            Check(JsonElement.DeepEquals(saved.RootElement.GetProperty("result"), received),
                "Veto/refusal evidence was not saved unchanged: " + shape);
            if (shape == "host-neutral-refusal")
                Check(review.Reason == "Qwen не предложила безопасное следующее слово. Нужна ручная проверка." &&
                    !review.Reason.Contains("В уроках конфликт", StringComparison.Ordinal) &&
                    saved.RootElement.GetProperty("result").GetProperty("search_evidence").GetProperty("proposal")
                        .GetProperty("reason").GetString() == "В уроках конфликт: название и упаковка несовместимы.",
                    "Host abstention promoted the untrusted model explanation or lost diagnostic evidence");
        }
    }
    private static async Task AttributeRoleCases(string root)
    {
        const string source = "Kenzo L*eau (L) 100ml edt";
        foreach (var (word, role, quote) in new[] { ("100", "volume", "100"), ("EDT", "concentration", "edt") })
        {
            var directory = Path.Combine(root, "attribute-" + role);
            Directory.CreateDirectory(directory);
            var observed = Observation(directory, word: word, sourceTitle: source);
            IReadOnlyList<GingerLearning.LanguageLesson> lessons = [];
            var current = true;
            GingerSearchWordPlanner Planner(Func<JsonElement, CancellationToken, Task<JsonElement>> consult) =>
                new(consult, _ => lessons, token => {
                    token.ThrowIfCancellationRequested();
                    if (!current) throw new OperationCanceledException("Synthetic changed chat");
                    return Task.CompletedTask;
                }, "attribute-chat", "attribute-run");

            JsonElement received = default;
            var valid = Planner((request, _) => {
                received = Reply(request, word, role);
                return Task.FromResult(received);
            });
            var decision = await valid.ChooseAsync(observed, [], CancellationToken.None);
            Check(decision.Word == word && decision.RequireCurrent is not null,
                "Host-validated " + role + " word lost its context guard");
            Check(received.GetProperty("search_evidence").GetProperty("source_evidence").GetProperty("quote").GetString() == quote,
                "Attribute citation no longer preserves the exact literal word/case inside the source");
            using (var saved = JsonDocument.Parse(File.ReadAllText(Directory.GetFiles(directory, "search-decision-*.json").Single())))
                Check(JsonElement.DeepEquals(saved.RootElement.GetProperty("result"), received) &&
                    saved.RootElement.GetProperty("request").GetProperty("source").GetString() == source,
                    "Attribute role or original request/evidence was rewritten");
            current = false;
            await Rejected(() => decision.RequireCurrent!(CancellationToken.None), role + " late chat change");
            current = true;
            lessons = [new(observed.Supplier, "Synthetic revised lesson", "Synthetic interpretation", true,
                DateTimeOffset.UtcNow, Guid.NewGuid().ToString("N"))];
            await Rejected(() => decision.RequireCurrent!(CancellationToken.None), role + " late lesson change");
            lessons = [];

            var mutations = new (string Label, Action<JsonObject> Change)[] {
                ("stale envelope", node => node["request_id"] = new string('0', 32)),
                ("stale proposal", node => node["search_evidence"]!["proposal"]!["request_id"] = new string('0', 32)),
                ("wrong quote", node => node["search_evidence"]!["source_evidence"]!["quote"] = role == "volume" ? "100ml" : "EDT"),
                ("unknown visible word", node => {
                    node["word"] = "Kenzo";
                    node["search_evidence"]!["proposal"]!["word"] = "Kenzo";
                    node["search_evidence"]!["source_evidence"]!["quote"] = "Kenzo";
                }),
                ("dishonest match permission", node => node["can_match"] = true),
                ("wrong origin", node => node["search_evidence"]!["source_evidence"]!["origin"] = "lesson"),
                ("model-authored citation", node => node["search_evidence"]!["source_evidence"]!["derived_by"] = "model")
            };
            foreach (var (label, change) in mutations)
            {
                var before = Directory.GetFiles(directory, "search-decision-*").Length;
                var invalid = Planner((request, _) => {
                    var node = JsonNode.Parse(Reply(request, word, role).GetRawText())!.AsObject();
                    change(node);
                    return Task.FromResult(JsonSerializer.SerializeToElement(node));
                });
                await Rejected(async () => { await invalid.ChooseAsync(observed, [], CancellationToken.None); }, role + " " + label);
                Check(Directory.GetFiles(directory, "search-decision-*").Length == before,
                    "Rejected attribute reply was saved: " + role + " " + label);
            }
            var duplicates = JsonSerializer.SerializeToNode(observed.Data)!;
            var secondWord = duplicates["words"]![0]!.DeepClone();
            secondWord["ObservedPosition"] = 2;
            duplicates["words"]!.AsArray().Add(secondWord);
            var duplicate = new GingerDesktopObservation(directory, JsonSerializer.SerializeToElement(duplicates));
            foreach (var (label, input, history) in new[] {
                ("checked word", Observation(directory, enabled: true, word: word, sourceTitle: source), Array.Empty<GingerSearchFilterEffect>()),
                ("duplicate word", duplicate, Array.Empty<GingerSearchFilterEffect>()),
                ("repeated word", observed, new[] { new GingerSearchFilterEffect(word, 300, 278) })
            })
            {
                var before = Directory.GetFiles(directory, "search-decision-*").Length;
                await Rejected(async () => { await valid.ChooseAsync(input, history, CancellationToken.None); }, role + " " + label);
                Check(Directory.GetFiles(directory, "search-decision-*").Length == before,
                    "Unchecked/unique/history guard failure saved an attribute decision");
            }
            var veto = Planner((request, _) => {
                var node = JsonNode.Parse(Reply(request, word, role).GetRawText())!;
                node["word"] = null;
                node["reason"] = "Host rejected ambiguous attribute; manual review required.";
                node["search_evidence"]!["blocked_reason"] = node["reason"]!.DeepClone();
                return Task.FromResult(JsonSerializer.SerializeToElement(node));
            });
            Check((await veto.ChooseAsync(observed, [], CancellationToken.None)).Word is null,
                "New attribute role bypassed an explicit host veto");
            var missing = Observation(Path.Combine(directory, "missing"), word: word, sourceTitle: source);
            await Rejected(async () => { await valid.ChooseAsync(missing, [], CancellationToken.None); }, role + " failed evidence save");
        }
    }

    private static GingerDesktopObservation Observation(string path, int count = 278, bool enabled = false,
        string word = "ALDHEYX", string? sourceTitle = null)
    {
        var body = JsonSerializer.SerializeToNode(new {
            status = count > 256 ? "needs_filters" : "observed", directory = path, search_completion_verified = false,
            process_id = 1234L, row_count = 9,
            source = new { Title = sourceTitle ?? "LM White Label ALDHEYX 100мл EDP", Supplier = "Synthetic supplier", DocumentNumber = "A",
                PriceText = "100", LinkedProductText = "<не указано>", TableHandle = 5678L, ObservedRowPosition = 3,
                IdentityVerified = false, CanMatch = false },
            words = new[] { new { Title = word, DescriptionText = word, Checked = enabled ? (bool?)true : null,
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
            await EvidenceContractCases(consult => Planner(consult), observed, root);
            await AttributeRoleCases(root);
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
