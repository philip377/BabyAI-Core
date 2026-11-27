using System.Text.Json;
using BabyAI.Desktop;

internal static class LessonTests
{
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private static async Task Refuses(Func<Task> action)
    {
        try { await action(); }
        catch (Exception e) when (e is InvalidOperationException or InvalidDataException or IOException or OperationCanceledException) { return; }
        throw new Exception("Invalid lesson operation accepted");
    }
    private static Task Call(Action action) { action(); return Task.CompletedTask; }

    public static async Task Run()
    {
        var directory = Path.Combine(Path.GetTempPath(), "BabyAI-lesson-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try { await Test(directory); }
        finally { Directory.Delete(directory, true); }
        Console.WriteLine("Ginger chat lessons: explicit review, frozen evidence, scope, cancellation, storage failure, restart and revocation passed.");
    }

    private static async Task Test(string directory)
    {
        var learning = new GingerLearning(directory);
        var item = new GingerChatItem("Supplier", "Document", 1, "Source", false, ["A", "B"]);
        GingerChatLessonReview Review() => new("chat-1", item, learning);
        var explanation = "Верхний регистр выделяет полное название аромата.";
        var understanding = "Проверяю всё название аромата, включая PLATINUM и ULTRA BLUE.";
        var calls = 0;
        Task<JsonElement> Preview(string supplier, string source, string text, IEnumerable<string> candidates, CancellationToken token)
        {
            calls++;
            Check(supplier == "Supplier" && source == "Source" && text == explanation, "Wrong lesson scope or content");
            Check(candidates.SequenceEqual(new[] { "A", "B" }), "Preview lost frozen candidates");
            return Task.FromResult(JsonSerializer.SerializeToElement(new { understanding, candidate = (string?)null, explanation = "Нужна проверка." }));
        }
        var review = Review();
        await Refuses(() => Call(() => review.Save("chat-1", explanation)));
        await Refuses(() => Call(() => review.Revoke("chat-1")));
        await Refuses(() => review.PreviewAsync("wrong-chat", explanation, Preview, default));
        foreach (var text in new[] { "", "  ", new string('x', 4001) })
            await Refuses(() => review.PreviewAsync("chat-1", text, Preview, default));
        Check(calls == 0 && learning.ReadLanguageLesson("Supplier") is null, "Invalid input invoked model or saved a lesson");
        item.Candidates[1] = "Changed";
        var report = await review.PreviewAsync("chat-1", explanation, Preview, default);
        item.Candidates[1] = "B";
        Check(report.GetProperty("request").GetProperty("candidates")[1].GetString() == "B", "Evidence changed after reading");
        Check(review.CanSave && learning.ReadLanguageLesson("Supplier") is null, "Preview must not publish a lesson");
        await Refuses(() => Call(() => review.Save("other-chat", explanation)));
        await Refuses(() => Call(() => review.Save("chat-1", "Edited")));
        review.DiscardDraft();
        await Refuses(() => Call(() => review.Save("chat-1", explanation)));
        await review.PreviewAsync("chat-1", explanation, Preview, default);

        // Simulated interrupted publication is invisible to readers, and lock
        // contention must not report success or consume the reviewed draft.
        var folder = Path.Combine(directory, "language-lessons");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "interrupted.json.pending"), "{partial");
        using (var locked = new FileStream(Path.Combine(folder, ".write.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
            await Refuses(() => Call(() => review.Save("chat-1", explanation)));
        Check(review.CanSave && review.Saved is null, "Failed save consumed draft or reported success");
        review.Save("chat-1", explanation);
        var restarted = new GingerLearning(directory);
        Check(restarted.ReadLanguageLesson("Supplier") == review.Saved, "Lesson did not survive restart");
        Check(restarted.ReadLanguageLesson("other") is null && !review.CanSave && review.CanRevoke, "Wrong supplier or lesson state");
        await Refuses(() => Call(() => review.Save("chat-1", explanation)));

        var beforeRevoke = Directory.GetFiles(folder, "*.json").Length;
        using (var locked = new FileStream(Path.Combine(folder, ".write.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            await Refuses(() => Call(() => review.Revoke("chat-1")));
        Check(review.CanRevoke, "Failed revoke disabled the saved lesson");
        review.Revoke("chat-1");
        Check(restarted.ReadLanguageLesson("Supplier") is { Enabled: false }, "Revocation did not survive restart");
        Check(Directory.GetFiles(folder, "*.json").Length == beforeRevoke + 1, "Revocation overwrote lesson history");
        await Refuses(() => Call(() => review.Revoke("chat-1")));

        // A stale card cannot overwrite or revoke a more recent lesson.
        var stale = Review();
        await stale.PreviewAsync("chat-1", explanation, Preview, default);
        var newer = new GingerLearning.LanguageLesson("Supplier", "New rule", "New understanding", true, DateTimeOffset.UtcNow);
        learning.SaveLanguageLesson(newer);
        await Refuses(() => Call(() => stale.Save("chat-1", explanation)));
        Check(learning.ReadLanguageLesson("Supplier") == newer, "Stale save replaced a newer lesson");
        var staleRevoke = Review();
        learning.SaveLanguageLesson(newer with { Text = "Even newer", RecordedAt = DateTimeOffset.UtcNow });
        await Refuses(() => Call(() => staleRevoke.Revoke("chat-1")));
        Check(learning.ReadLanguageLesson("Supplier")?.Enabled == true, "Stale revoke disabled a newer lesson");

        var closed = Review(); closed.Invalidate();
        await Refuses(() => closed.PreviewAsync("chat-1", explanation, Preview, default));
        await Refuses(() => Call(() => closed.Revoke("chat-1")));
        Check(!closed.CanPreview && !closed.CanSave && !closed.CanRevoke, "Closed card remains actionable");

        foreach (var interruption in new[] { "cancel", "close", "edit" })
        {
            var pending = Review();
            using var cancellation = new CancellationTokenSource();
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var task = pending.PreviewAsync("chat-1", explanation, async (s, p, t, c, token) => {
                started.SetResult(); await release.Task; return await Preview(s, p, t, c, token);
            }, cancellation.Token);
            await started.Task;
            await Refuses(() => pending.PreviewAsync("chat-1", explanation, Preview, default));
            await Refuses(() => Call(() => pending.Revoke("chat-1")));
            if (interruption == "cancel") cancellation.Cancel();
            else if (interruption == "close") pending.Invalidate();
            else pending.DiscardDraft();
            release.SetResult();
            await Refuses(() => task);
            Check(pending.Draft is null && !pending.CanSave, "Late response survived cancellation or closed chat");
        }
        var invalid = Review();
        await invalid.PreviewAsync("chat-1", explanation, Preview, default);
        await Refuses(() => invalid.PreviewAsync("chat-1", explanation, (_, _, _, _, _) => throw new IOException("Model failed"), default));
        Check(!invalid.CanSave, "Failure left an earlier draft ready to save");
        await Refuses(() => invalid.PreviewAsync("chat-1", explanation, (_, _, _, _, _) => Task.FromResult(
            JsonSerializer.SerializeToElement(new { understanding, candidate = "A", explanation, error = "failed" })), default));
        Check(!invalid.CanSave, "Error response created a savable draft");
        foreach (var candidate in new[] { "Missing", "A" })
        {
            var invalidItem = candidate == "A" ? item with { Candidates = ["A", "A"] } : item;
            var bad = new GingerChatLessonReview("chat-1", invalidItem, learning);
            await Refuses(() => bad.PreviewAsync("chat-1", explanation, (_, _, _, _, _) => Task.FromResult(
                JsonSerializer.SerializeToElement(new { understanding, candidate, explanation })), default));
            Check(!bad.CanSave, "Unverifiable candidate created a savable lesson");
        }
        Check(!Directory.GetFiles(folder, "*.pending").Any(p => !p.EndsWith("interrupted.json.pending")), "Publication left a partial file");
    }
}
