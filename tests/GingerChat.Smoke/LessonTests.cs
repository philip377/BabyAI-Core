using System.Text.Json;
using System.Text.Json.Nodes;
using BabyAI.Desktop;

internal static class LessonTests
{
    private static readonly GingerChatItem Item = new("Supplier", "Document", 1, "Source", false, ["A", "B"]);
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private static async Task Refuses(Func<Task> action)
    {
        try { await action(); }
        catch (Exception e) when (e is InvalidOperationException or InvalidDataException or IOException or OperationCanceledException) { return; }
        throw new Exception("Invalid lesson operation accepted");
    }
    private static Task Call(Action action) { action(); return Task.CompletedTask; }
    private static GingerChatLessonReview Review(GingerLearning learning) => new("chat-1", Item, learning);
    private static JsonElement Result() => JsonSerializer.SerializeToElement(new {
        understanding = "Учитываю объяснения поставщика и проверяю все характеристики.",
        candidate = (string?)null, explanation = "Нужна проверка."
    });
    private static Task<JsonElement> Accept(string supplier, string source, string text,
        IEnumerable<string> candidates, CancellationToken token) => Task.FromResult(Result());
    private static JsonElement SourceResult() => JsonSerializer.SerializeToElement(new {
        mode = "source_only", understanding = "Учитываю объяснение только для этого поставщика.",
        candidate = (string?)null, explanation = "Проверено понимание исходной строки. Пара не выбиралась."
    });
    private static Task<JsonElement> AcceptSource(string supplier, string source, string text,
        IEnumerable<string> candidates, CancellationToken token) => Task.FromResult(SourceResult());
    private static GingerChatLessonReview SourceReview(GingerLearning learning) =>
        GingerChatLessonReview.ForDeferredSource("chat-1", new GingerDeferredRow("saved-row", "original-chat",
            "Supplier", "Document", 1, "Source", GingerDeferredRows.NoCandidatesReason,
            Path.GetTempPath(), DateTimeOffset.UtcNow), learning);
    private static async Task<GingerLearning.LanguageLesson> Add(GingerLearning learning, string text)
    {
        var review = Review(learning);
        await review.PreviewAsync("chat-1", text, Accept, default);
        review.Save("chat-1", text);
        return review.Saved ?? throw new Exception("Saved lesson was not selected");
    }

    public static async Task Run()
    {
        var directory = Path.Combine(Path.GetTempPath(), "BabyAI-lesson-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            await TestReviewAndPublication(Path.Combine(directory, "publication"));
            await TestAccumulation(Path.Combine(directory, "accumulation"));
            await TestStaleBundles(Path.Combine(directory, "stale"));
            await TestInterruptedPreview(Path.Combine(directory, "interrupted"));
            await TestLimits(Path.Combine(directory, "limits"));
            await TestLegacyHistory(Path.Combine(directory, "legacy"));
            await TestSourceOnlyPublication(Path.Combine(directory, "source-publication"));
            await TestSourceOnlyResponses(Path.Combine(directory, "source-responses"));
            await TestSourceOnlyInterruptions(Path.Combine(directory, "source-interruptions"));
        }
        finally { Directory.Delete(directory, true); }
        Console.WriteLine("Ginger chat lessons: independent lessons, source-only frozen examples/strict results, scoped publication/revocation, stale bundles, cancellation and atomic publication passed.");
    }

    private static async Task TestReviewAndPublication(string directory)
    {
        var learning = new GingerLearning(directory);
        var item = Item with { Candidates = ["A", "B"] };
        var review = new GingerChatLessonReview("chat-1", item, learning);
        Check(!review.SourceOnly, "Ordinary comparison became a source-only review");
        const string explanation = "Верхний регистр выделяет полное название аромата.";
        var calls = 0;
        Task<JsonElement> Preview(string supplier, string source, string text, IEnumerable<string> candidates, CancellationToken token)
        {
            calls++;
            Check(supplier == "Supplier" && source == "Source" && text == explanation, "Wrong lesson scope or content");
            Check(candidates.SequenceEqual(new[] { "A", "B" }), "Preview lost frozen candidates");
            return Task.FromResult(Result());
        }
        await Refuses(() => Call(() => review.Save("chat-1", explanation)));
        await Refuses(() => Call(() => review.Revoke("chat-1")));
        await Refuses(() => review.PreviewAsync("wrong-chat", explanation, Preview, default));
        foreach (var text in new[] { "", "  ", new string('x', 4001) })
            await Refuses(() => review.PreviewAsync("chat-1", text, Preview, default));
        Check(calls == 0 && learning.ReadLanguageLessons("Supplier").Count == 0, "Invalid input invoked model or saved a lesson");
        item.Candidates[1] = "Changed";
        var report = await review.PreviewAsync("chat-1", explanation, Preview, default);
        Check(report.GetProperty("request").GetProperty("candidates")[1].GetString() == "B", "Evidence changed after reading");
        Check(review.CanSave && learning.ReadLanguageLessons("Supplier").Count == 0, "Preview must not publish a lesson");
        await Refuses(() => Call(() => review.Save("other-chat", explanation)));
        await Refuses(() => Call(() => review.Save("chat-1", "Edited")));
        review.DiscardDraft();
        await Refuses(() => Call(() => review.Save("chat-1", explanation)));
        await review.PreviewAsync("chat-1", explanation, Preview, default);

        // Incomplete writes stay invisible; a busy store leaves the reviewed draft retryable.
        var folder = Path.Combine(directory, "language-lessons");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "interrupted.json.pending"), "{partial");
        using (var locked = new FileStream(Path.Combine(folder, ".write.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
            await Refuses(() => Call(() => review.Save("chat-1", explanation)));
        Check(review.CanSave && review.Saved is null, "Failed save consumed draft or reported success");
        review.Save("chat-1", explanation);
        var restarted = new GingerLearning(directory);
        Check(restarted.ReadLanguageLessons("Supplier").Single() == review.Saved, "Lesson did not survive restart");
        Check(restarted.ReadLanguageLessons("other").Count == 0 && !review.CanSave && review.CanRevoke, "Wrong supplier or lesson state");
        Check(review.Lessons.Single() == review.Saved, "Saved lesson is missing from the review list");
        await Refuses(() => Call(() => review.Save("chat-1", explanation)));
        await Refuses(() => Call(() => review.Revoke("other-chat")));

        var beforeRevoke = Directory.GetFiles(folder, "*.json").Length;
        using (var locked = new FileStream(Path.Combine(folder, ".write.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            await Refuses(() => Call(() => review.Revoke("chat-1")));
        Check(review.CanRevoke, "Failed revoke disabled the saved lesson");
        review.Revoke("chat-1");
        Check(restarted.ReadLanguageLessons("Supplier").Single() is { Enabled: false }, "Revocation did not survive restart");
        Check(restarted.LessonForReview("Supplier", "Source") is null, "Revoked lesson still reaches matching");
        Check(Directory.GetFiles(folder, "*.json").Length == beforeRevoke + 1, "Revocation overwrote lesson history");
        await Refuses(() => Call(() => review.Revoke("chat-1")));
        Check(!Directory.GetFiles(folder, "*.pending").Any(p => !p.EndsWith("interrupted.json.pending")), "Publication left a partial file");
    }

    private static async Task TestAccumulation(string directory)
    {
        var learning = new GingerLearning(directory);
        const string firstText = "Немного подмят означает тестер.";
        const string secondText = "Название аромата записано заглавными буквами.";
        const string thirdText = "Не игнорировать PLATINUM и ULTRA BLUE.";
        var first = await Add(learning, firstText);
        var review = Review(learning);
        Check(review.Saved is null && !review.CanRevoke && review.Lessons.Single() == first,
            "Opening a card implicitly selected an existing lesson for replacement");
        string? previewText = null;
        Task<JsonElement> Capture(string supplier, string source, string text, IEnumerable<string> candidates, CancellationToken token)
        { previewText = text; return Task.FromResult(Result()); }
        await review.PreviewAsync("chat-1", secondText, Capture, default);
        Check(previewText!.Contains(firstText) && previewText.Contains(secondText), "Preview omitted an existing lesson or the new explanation");
        Check(review.Draft?.Text == secondText && learning.LessonForReview("Supplier", "Source")?.Text == firstText,
            "Preview replaced persistent rules or stored the composed context as a single draft");
        review.Save("chat-1", secondText);
        var second = review.Saved!;
        Check(first.LessonId != second.LessonId && review.Lessons.Count == 2, "Adding a lesson replaced the previous lesson");
        Check(learning.LessonForReview("Supplier", "Source")?.Text == previewText, "Matching context differs from the context the user reviewed");
        review.SelectLesson(null);
        await review.PreviewAsync("chat-1", thirdText, Capture, default);
        review.Save("chat-1", thirdText);
        var third = review.Saved!;
        Check(review.Lessons.Count == 3 && new GingerLearning(directory).LessonForReview("Supplier", "Source")?.Text == previewText,
            "Accumulated rules did not survive restart");

        review.SelectLesson(second.LessonId);
        Check(review.Saved == second && review.Draft is null && review.CanRevoke, "Explicit selection picked the wrong lesson");
        const string changedText = "Верхний регистр выделяет полное название, но не доказывает совпадение.";
        await review.PreviewAsync("chat-1", changedText, Capture, default);
        Check(previewText!.Contains(firstText) && previewText.Contains(changedText) && previewText.Contains(thirdText)
            && !previewText.Contains(secondText), "Editing preview did not replace only the selected lesson");
        review.Save("chat-1", changedText);
        var afterEdit = new GingerLearning(directory).ReadLanguageLessons("Supplier");
        Check(afterEdit.Count == 3 && afterEdit.Single(x => x.LessonId == first.LessonId) == first
            && afterEdit.Single(x => x.LessonId == third.LessonId) == third
            && review.Saved?.LessonId == second.LessonId, "Edit changed another lesson or created a new identity");
        Check(learning.LessonForReview("Supplier", "Source")?.Text == previewText, "Edited matching context differs from preview");

        await review.PreviewAsync("chat-1", "Unsaved edit", Capture, default);
        review.SelectLesson(first.LessonId);
        Check(!review.CanSave && review.Draft is null && review.Saved == first, "Changing selection retained the previous lesson draft");
        await Refuses(() => Call(() => review.Save("chat-1", "Unsaved edit")));
        await Refuses(() => Call(() => review.SelectLesson("missing-lesson")));
        review.SelectLesson(second.LessonId);
        review.Revoke("chat-1");
        var restarted = new GingerLearning(directory);
        var retained = restarted.ReadLanguageLessons("Supplier");
        Check(retained.Count == 3 && retained.Single(x => x.LessonId == second.LessonId).Enabled == false
            && retained.Single(x => x.LessonId == first.LessonId) == first && retained.Single(x => x.LessonId == third.LessonId) == third,
            "Revoking one lesson changed other saved lessons");
        var context = restarted.LessonForReview("Supplier", "Source")!.Text;
        Check(context.Contains(firstText) && context.Contains(thirdText) && !context.Contains(changedText), "Revoked lesson leaked into matching context");
        Check(Review(restarted).Lessons.Count == 3 && restarted.LessonForReview("Other supplier", "Source") is null,
            "Revoked lessons disappeared from management or crossed supplier scope");
    }

    private static async Task TestStaleBundles(string directory)
    {
        var learning = new GingerLearning(directory);
        var first = await Add(learning, "First rule");
        var second = await Add(learning, "Second rule");
        var stale = Review(learning);
        stale.SelectLesson(first.LessonId);
        await stale.PreviewAsync("chat-1", "First rule edited", Accept, default);
        var editor = Review(learning);
        editor.SelectLesson(second.LessonId);
        await editor.PreviewAsync("chat-1", "Second rule edited", Accept, default);
        editor.Save("chat-1", "Second rule edited");
        var expected = learning.ReadLanguageLessons("Supplier").ToArray();
        await Refuses(() => Call(() => stale.Save("chat-1", "First rule edited")));
        Check(learning.ReadLanguageLessons("Supplier").SequenceEqual(expected), "A stale composed preview overwrote a newer rule");

        var stalePreview = Review(learning);
        await Add(learning, "Third rule");
        var calls = 0;
        await Refuses(() => stalePreview.PreviewAsync("chat-1", "Fourth rule", (s, p, t, c, token) => {
            calls++; return Task.FromResult(Result());
        }, default));
        Check(calls == 0 && !stalePreview.CanSave, "A stale card reviewed an incomplete lesson set");

        var staleRevoke = Review(learning);
        staleRevoke.SelectLesson(first.LessonId);
        var fresh = Review(learning);
        fresh.SelectLesson(first.LessonId);
        await fresh.PreviewAsync("chat-1", "First rule updated elsewhere", Accept, default);
        fresh.Save("chat-1", "First rule updated elsewhere");
        await Refuses(() => Call(() => staleRevoke.Revoke("chat-1")));
        Check(learning.ReadLanguageLessons("Supplier").Single(x => x.LessonId == first.LessonId).Enabled,
            "A stale card revoked a newer explanation");
    }

    private static async Task TestInterruptedPreview(string directory)
    {
        var learning = new GingerLearning(directory);
        var first = await Add(learning, "Existing explanation");
        var closed = Review(learning); closed.Invalidate();
        await Refuses(() => closed.PreviewAsync("chat-1", "Explanation", Accept, default));
        await Refuses(() => Call(() => closed.SelectLesson(first.LessonId)));
        await Refuses(() => Call(() => closed.Revoke("chat-1")));
        Check(!closed.CanPreview && !closed.CanSave && !closed.CanRevoke, "Closed card remains actionable");

        foreach (var interruption in new[] { "cancel", "close", "edit", "other-window" })
        {
            var pending = Review(learning);
            using var cancellation = new CancellationTokenSource();
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var before = learning.ReadLanguageLessons("Supplier").ToArray();
            var task = pending.PreviewAsync("chat-1", "New explanation", async (s, p, t, c, token) => {
                started.SetResult(); await release.Task; return Result();
            }, cancellation.Token);
            await started.Task;
            await Refuses(() => pending.PreviewAsync("chat-1", "New explanation", Accept, default));
            await Refuses(() => Call(() => pending.Revoke("chat-1")));
            await Refuses(() => Call(() => pending.SelectLesson(first.LessonId)));
            if (interruption == "cancel") cancellation.Cancel();
            else if (interruption == "close") pending.Invalidate();
            else if (interruption == "edit") pending.DiscardDraft();
            else await Add(learning, "An explanation from another window");
            release.SetResult();
            await Refuses(() => task);
            Check(pending.Draft is null && !pending.CanSave, "Late response survived cancellation, edit, closed chat or changed lesson set");
            if (interruption != "other-window")
                Check(learning.ReadLanguageLessons("Supplier").SequenceEqual(before), "Interrupted preview published a lesson");
        }
        var invalid = Review(learning);
        await invalid.PreviewAsync("chat-1", "Explanation", Accept, default);
        await Refuses(() => invalid.PreviewAsync("chat-1", "Explanation", (_, _, _, _, _) => throw new IOException("Model failed"), default));
        Check(!invalid.CanSave, "Failure left an earlier draft ready to save");
        await Refuses(() => invalid.PreviewAsync("chat-1", "Explanation", (_, _, _, _, _) => Task.FromResult(
            JsonSerializer.SerializeToElement(new { understanding = "Rule", candidate = "A", explanation = "Review", error = "failed" })), default));
        Check(!invalid.CanSave, "Error response created a savable draft");
        foreach (var candidate in new[] { "Missing", "A" })
        {
            var invalidItem = candidate == "A" ? Item with { Candidates = ["A", "A"] } : Item;
            var bad = new GingerChatLessonReview("chat-1", invalidItem, learning);
            await Refuses(() => bad.PreviewAsync("chat-1", "Explanation", (_, _, _, _, _) => Task.FromResult(
                JsonSerializer.SerializeToElement(new { understanding = "Rule", candidate, explanation = "Review" })), default));
            Check(!bad.CanSave, "Unverifiable candidate created a savable lesson");
        }
    }

    private static async Task TestLimits(string directory)
    {
        var learning = new GingerLearning(directory);
        await Add(learning, "Do not ignore packaging");
        var duplicate = Review(learning);
        var calls = 0;
        Task<JsonElement> Count(string s, string p, string t, IEnumerable<string> c, CancellationToken token)
        { calls++; return Task.FromResult(Result()); }
        await Refuses(() => duplicate.PreviewAsync("chat-1", "  Do not ignore packaging  ", Count, default));
        Check(calls == 0 && !duplicate.CanSave, "Duplicate lesson reached the model or became savable");
        await Add(learning, new string('a', 2500));
        var overflowing = Review(learning);
        await Refuses(() => overflowing.PreviewAsync("chat-1", new string('b', 1600), Count, default));
        Check(calls == 0 && !overflowing.CanSave && learning.ReadLanguageLessons("Supplier").Count == 2,
            "Oversized combined context was truncated, sent to the model or persisted");
    }

    private static async Task TestLegacyHistory(string directory)
    {
        var folder = Path.Combine(directory, "language-lessons");
        Directory.CreateDirectory(folder);
        void Write(string filename, string text) => File.WriteAllText(Path.Combine(folder, filename),
            JsonSerializer.Serialize(new { Supplier = "Supplier", Text = text, Understanding = "Legacy understanding",
                Enabled = true, RecordedAt = DateTimeOffset.UtcNow }));
        Write("0001.json", "Obsolete legacy explanation");
        Write("0002.json", "Latest legacy explanation");
        var learning = new GingerLearning(directory);
        var legacy = learning.ReadLanguageLessons("Supplier").Single();
        Check(legacy.Text == "Latest legacy explanation" && legacy.LessonId == "legacy", "Old files became separate active lessons or lost their legacy identity");
        var added = await Add(learning, "Independent new explanation");
        var restarted = new GingerLearning(directory);
        Check(restarted.ReadLanguageLessons("Supplier").Count == 2 && added.LessonId != legacy.LessonId,
            "Adding a new lesson replaced the legacy explanation");
        var context = restarted.LessonForReview("Supplier", "Source")!.Text;
        Check(context.Contains(legacy.Text) && context.Contains(added.Text) && !context.Contains("Obsolete"),
            "Legacy history leaked obsolete explanations into the composed context");
        var review = Review(restarted);
        review.SelectLesson(legacy.LessonId);
        review.Revoke("chat-1");
        Check(restarted.LessonForReview("Supplier", "Source")?.Text == added.Text,
            "Revoking the legacy lesson removed the new independent explanation");
        Check(File.ReadAllText(Path.Combine(folder, "0001.json")).Contains("Obsolete"), "Migration overwrote original history");
    }

    private static async Task TestSourceOnlyPublication(string directory)
    {
        var learning = new GingerLearning(Path.Combine(directory, "learning"));
        var first = await Add(learning, "Existing supplier rule");
        await Refuses(() => Call(() => _ = new GingerChatLessonReview("chat-1", Item with { Candidates = [] }, learning)));
        var inbox = new GingerDeferredRows(Path.Combine(directory, "deferred"));
        var original = inbox.Add(Item with { Candidates = [] }, "original-chat", Path.GetFullPath(directory));
        var row = inbox.Read().Single();
        var review = GingerChatLessonReview.ForDeferredSource("chat-1", row, learning);
        row = row with { Supplier = "Changed", Title = "Different source" };
        Check(review.SourceOnly && review.ChatId == "chat-1" && review.Supplier == original.Supplier
            && review.Source == original.Title && !review.CanSave && !review.CanRevoke,
            "Source-only factory lost its frozen source/current-chat binding or granted save authority");
        const string explanation = "Supplier code describes concentration.";
        string? combined = null;
        var calls = 0;
        Task<JsonElement> Preview(string supplier, string source, string lesson, IEnumerable<string> candidates, CancellationToken token)
        {
            calls++;
            Check(supplier == "Supplier" && source == "Source" && !candidates.Any(), "Source-only preview acquired candidates or a changed source");
            combined = lesson;
            return Task.FromResult(SourceResult());
        }
        await Refuses(() => Call(() => review.Save("chat-1", explanation)));
        await Refuses(() => review.PreviewAsync("other-chat", explanation, Preview, default));
        Check(calls == 0, "Another chat invoked source-only preview");
        var report = await review.PreviewAsync("chat-1", explanation, Preview, default);
        var request = report.GetProperty("request");
        Check(request.GetProperty("mode").GetString() == "source_only" && request.GetProperty("candidates").GetArrayLength() == 0
            && request.GetProperty("source").GetString() == original.Title && report.GetProperty("result").GetProperty("candidate").ValueKind == JsonValueKind.Null,
            "Source-only report became an ordinary candidate comparison");
        Check(combined!.Contains(first.Text) && combined.Contains(explanation) && review.Draft?.Text == explanation
            && learning.ReadLanguageLessons("Supplier").Count == 1, "Source-only preview omitted rules or persisted before explicit save");
        await Refuses(() => Call(() => _ = new GingerChatAnswerReview("chat-1", report)));
        await Refuses(() => Call(() => review.Save("chat-1", "Edited explanation")));
        await Refuses(() => Call(() => review.Save("other-chat", explanation)));
        var folder = Path.Combine(directory, "learning", "language-lessons");
        using (var locked = new FileStream(Path.Combine(folder, ".write.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
            await Refuses(() => Call(() => review.Save("chat-1", explanation)));
        Check(review.CanSave && review.Saved is null, "Failed source-only save consumed its reviewed draft");
        review.Save("chat-1", explanation);
        var second = review.Saved!;
        Check(second.LessonId != first.LessonId && review.CanRevoke && !review.CanSave
            && new GingerLearning(Path.Combine(directory, "learning")).LessonForReview("Supplier", "Source")?.Text == combined,
            "Explicit source-only save did not retain the reviewed combined supplier rules");
        review.SelectLesson(first.LessonId);
        await review.PreviewAsync("chat-1", "Existing rule revised from a saved source", Preview, default);
        Check(combined!.Contains(explanation) && !combined.Contains(first.Text), "Source-only edit replaced the wrong lesson");
        review.Save("chat-1", "Existing rule revised from a saved source");
        Check(review.Saved!.LessonId == first.LessonId && review.Lessons.Single(x => x.LessonId == second.LessonId) == second,
            "Source-only edit changed another saved rule");
        await Refuses(() => Call(() => review.Revoke("other-chat")));
        review.Revoke("chat-1");
        Check(learning.ReadLanguageLessons("Supplier").Single(x => x.LessonId == first.LessonId).Enabled == false
            && learning.LessonForReview("Supplier", "Source")?.Text == explanation
            && learning.ReadLanguageLessons("Other supplier").Count == 0,
            "Source-only revocation changed another rule or supplier");
        Check(inbox.Read().Single() == original, "Teaching changed or consumed the deferred source");
    }

    private static async Task TestSourceOnlyResponses(string directory)
    {
        var learning = new GingerLearning(directory);
        var review = SourceReview(learning);
        foreach (var wrappers in new[] { 0, 1, 2 })
        {
            var result = JsonNode.Parse(SourceResult().GetRawText())!.AsObject();
            if (wrappers > 0) { result["ok"] = true; result["command"] = "ginger.teach.source.preview"; }
            if (wrappers > 1) result["id"] = long.MaxValue;
            await review.PreviewAsync("chat-1", "Explanation", (_, _, _, _, _) => Task.FromResult(JsonSerializer.SerializeToElement(result)), default);
            Check(review.CanSave && learning.ReadLanguageLessons("Supplier").Count == 0, "Valid source-only transport failed or saved a rule");
            review.DiscardDraft();
        }
        var invalid = new List<JsonElement> { Result(), JsonSerializer.SerializeToElement<object?>(null),
            JsonSerializer.SerializeToElement(new[] { "not an object" }) };
        foreach (var mutate in new Action<JsonObject>[] {
            x => x.Remove("mode"), x => x["mode"] = "comparison", x => x["mode"] = true,
            x => x.Remove("candidate"), x => x["candidate"] = "A", x => x["candidate"] = "null", x => x["candidate"] = 0,
            x => x.Remove("understanding"), x => x["understanding"] = " ", x => x["understanding"] = new JsonArray("rule"),
            x => x["understanding"] = new string('u', 4001), x => x["explanation"] = new string('e', 4001),
            x => x.Remove("explanation"), x => x["explanation"] = null,
            x => x["error"] = null, x => x["can_match"] = false, x => x["permission"] = "match",
            x => x["ok"] = true, x => x["command"] = "ginger.teach.source.preview", x => x["id"] = 1,
            x => { x["ok"] = false; x["command"] = "ginger.teach.source.preview"; },
            x => { x["ok"] = true; x["command"] = "ginger.teach.preview"; },
        })
        {
            var result = JsonNode.Parse(SourceResult().GetRawText())!.AsObject(); mutate(result);
            invalid.Add(JsonSerializer.SerializeToElement(result));
        }
        foreach (var id in new JsonNode?[] { JsonValue.Create(-1), JsonValue.Create(1.5), JsonValue.Create("1"), JsonValue.Create(true), null })
        {
            var result = JsonNode.Parse(SourceResult().GetRawText())!.AsObject();
            result["ok"] = true; result["command"] = "ginger.teach.source.preview"; result["id"] = id;
            invalid.Add(JsonSerializer.SerializeToElement(result));
        }
        var raw = SourceResult().GetRawText();
        foreach (var suffix in new[] { ",\"mode\":\"source_only\"", ",\"candidate\":null", ",\"explanation\":\"duplicate\"",
            ",\"ok\":true,\"command\":\"ginger.teach.source.preview\",\"id\":1,\"id\":1" })
        {
            using var duplicate = JsonDocument.Parse(raw[..^1] + suffix + "}"); invalid.Add(duplicate.RootElement.Clone());
        }
        foreach (var result in invalid)
        {
            await review.PreviewAsync("chat-1", "Previous draft", AcceptSource, default);
            await Refuses(() => review.PreviewAsync("chat-1", "Explanation", (_, _, _, _, _) => Task.FromResult(result), default));
            Check(review.Draft is null && !review.CanSave && review.CanPreview, "Invalid source-only response preserved a savable draft or kept the review busy");
        }
        var boundary = JsonSerializer.SerializeToElement(new { mode = "source_only", candidate = (string?)null,
            understanding = new string('u', 4000), explanation = new string('e', 4000) });
        await review.PreviewAsync("chat-1", "Explanation", (_, _, _, _, _) => Task.FromResult(boundary), default);
        Check(review.CanSave && review.Draft?.Understanding.Length == 4000, "Valid source-only result at the text limit was rejected");
        var ordinary = Review(learning);
        await Refuses(() => ordinary.PreviewAsync("chat-1", "Explanation", AcceptSource, default));
        Check(!ordinary.CanSave && !ordinary.SourceOnly, "Source-only result was accepted as an ordinary candidate comparison");
    }

    private static async Task TestSourceOnlyInterruptions(string directory)
    {
        var learning = new GingerLearning(directory);
        var first = await Add(learning, "Original rule");
        foreach (var interruption in new[] { "cancel", "close", "edit", "other-window" })
        {
            var review = SourceReview(learning);
            using var cancellation = new CancellationTokenSource();
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var before = learning.ReadLanguageLessons("Supplier").ToArray();
            var task = review.PreviewAsync("chat-1", "Source-only explanation", async (_, _, _, candidates, _) => {
                Check(!candidates.Any(), "Interrupted source-only review received candidates");
                started.SetResult(); await release.Task; return SourceResult();
            }, cancellation.Token);
            await started.Task;
            await Refuses(() => review.PreviewAsync("chat-1", "Another draft", AcceptSource, default));
            await Refuses(() => Call(() => review.Save("chat-1", "Source-only explanation")));
            await Refuses(() => Call(() => review.SelectLesson(first.LessonId)));
            await Refuses(() => Call(() => review.Revoke("chat-1")));
            if (interruption == "cancel") cancellation.Cancel();
            else if (interruption == "close") review.Invalidate();
            else if (interruption == "edit") review.DiscardDraft();
            else await Add(learning, "Rule added during source-only preview");
            release.SetResult();
            await Refuses(() => task);
            Check(review.Draft is null && !review.CanSave, "Late source-only response survived cancellation/invalidation/stale lessons");
            if (interruption != "other-window")
                Check(learning.ReadLanguageLessons("Supplier").SequenceEqual(before), "Interrupted source-only preview changed persistent lessons");
        }
        var stale = SourceReview(learning);
        stale.SelectLesson(first.LessonId);
        await stale.PreviewAsync("chat-1", "Changed original", AcceptSource, default);
        await Add(learning, "New external rule");
        await Refuses(() => Call(() => stale.Save("chat-1", "Changed original")));
        await Refuses(() => Call(() => stale.Revoke("chat-1")));
        var calls = 0;
        await Refuses(() => stale.PreviewAsync("chat-1", "Retry", (_, _, _, _, _) => {
            calls++; return Task.FromResult(SourceResult());
        }, default));
        Check(calls == 0 && !stale.CanSave && learning.ReadLanguageLessons("Supplier").Single(x => x.LessonId == first.LessonId) == first,
            "Stale source-only card changed lessons or invoked preview with an outdated snapshot");
        var failed = SourceReview(learning);
        await failed.PreviewAsync("chat-1", "Explanation", AcceptSource, default);
        await Refuses(() => failed.PreviewAsync("chat-1", "Explanation", (_, _, _, _, _) => throw new IOException("Preview failed"), default));
        Check(!failed.CanSave && failed.CanPreview, "Failed source-only preview retained its draft or working lock");
    }
}
