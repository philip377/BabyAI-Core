using System.Text.Json;
using BabyAI.Desktop;

internal static class ReviewTests
{
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private static async Task Refuses(Func<Task> action)
    {
        try { await action(); }
        catch (Exception e) when (e is InvalidOperationException or InvalidDataException or IOException) { return; }
        throw new Exception("Invalid rating operation accepted");
    }

    public static async Task Run()
    {
        var evidence = JsonSerializer.SerializeToElement(new {
            request = new { supplier = "Supplier", source = "Original product", lesson = "", candidates = new[] { "A", "B" } },
            result = new { candidate = (string?)null, explanation = "Needs review", comparisons = new[] {
                new { candidate = "A", attributes = new { identity = "unknown", volume = "match" } } }
            }
        });
        GingerChatAnswerReview Review() => new("chat-1", evidence);
        int saves = 0, revokes = 0;
        Task<GingerAnswerRatingReceipt> Save(JsonElement report, int points, string feedback)
        {
            saves++;
            Check(report.GetRawText() == evidence.GetRawText(), "Rating lost the original full request/answer");
            Check(points == 10 && feedback == "Верно", "Rating changed explicit feedback");
            return Task.FromResult(new GingerAnswerRatingReceipt("answer-1", true));
        }
        Task Revoke(string id, string reason)
        {
            revokes++;
            Check(id == "answer-1" && reason == "Ошибка", "Revocation lost identity or reason");
            return Task.CompletedTask;
        }

        var review = Review();
        Check(review.CanRate && !review.CanRevoke && saves == 0, "Creating a review must not save a rating");
        foreach (int? points in new int?[] { null, -1, 11 })
            await Refuses(() => review.RateAsync("chat-1", points, "Верно", Save));
        foreach (var feedback in new[] { "", "   ", new string('x', 4001) })
            await Refuses(() => review.RateAsync("chat-1", 10, feedback, Save));
        await Refuses(() => review.RateAsync("other-chat", 10, "Верно", Save));
        await Refuses(() => review.RevokeAsync("chat-1", "Ошибка", Revoke));
        Check(saves == 0 && revokes == 0, "Invalid review wrote evidence");
        await review.RateAsync("chat-1", 10, "  Верно  ", Save);
        Check(saves == 1 && !review.CanRate && review.CanRevoke, "Rating state not updated");
        await Refuses(() => review.RateAsync("chat-1", 10, "Верно", Save));
        await Refuses(() => review.RevokeAsync("other-chat", "Ошибка", Revoke));
        await Refuses(() => review.RevokeAsync("chat-1", "", Revoke));
        await Refuses(() => review.RevokeAsync("chat-1", "Ошибка", (_, _) => throw new IOException("failed")));
        Check(review.CanRevoke, "Failed revoke was treated as successful");
        await review.RevokeAsync("chat-1", "Ошибка", Revoke);
        Check(saves == 1 && revokes == 1 && !review.CanRate && !review.CanRevoke, "Revocation state incorrect");
        await Refuses(() => review.RevokeAsync("chat-1", "Ошибка", Revoke));

        var revokedInStorage = Review();
        var receipt = await revokedInStorage.RateAsync("chat-1", 10, "Верно",
            (_, _, _) => Task.FromResult(new GingerAnswerRatingReceipt("answer-1", false)));
        Check(!receipt.Active && !revokedInStorage.CanRate && !revokedInStorage.CanRevoke,
            "An old revoked rating was displayed as active");

        var failed = Review();
        await Refuses(() => failed.RateAsync("chat-1", 10, "Верно", (_, _, _) => throw new IOException("failed")));
        Check(failed.CanRate && failed.Rating is null, "Failed save was treated as successful");
        await failed.RateAsync("chat-1", 10, "Верно", Save);

        var closed = Review(); closed.Invalidate(); var savedBefore = saves;
        await Refuses(() => closed.RateAsync("chat-1", 10, "Верно", Save));
        Check(saves == savedBefore && !closed.CanRate && !closed.CanRevoke, "Closed view allowed rating");

        var concurrent = Review();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = concurrent.RateAsync("chat-1", 10, "Верно", async (data, score, comment) => {
            entered.SetResult(); await release.Task; return await Save(data, score, comment);
        });
        await entered.Task;
        await Refuses(() => concurrent.RateAsync("chat-1", 10, "Верно", Save));
        Check(!concurrent.CanRate && !concurrent.CanRevoke, "Controls enabled during save");
        savedBefore = saves; release.SetResult(); await first;
        Check(saves == savedBefore + 1, "Concurrent rating saved twice");

        GingerChatAnswerReview cloned;
        using (var document = JsonDocument.Parse(evidence.GetRawText()))
            cloned = new("chat-1", document.RootElement);
        await cloned.RateAsync("chat-1", 10, "Верно", Save);
        Check(cloned.Source == "Original product", "Evidence depends on disposed input document");
        foreach (var score in new[] { 0, 1, 10 })
            await Review().RateAsync("chat-1", score, "Явная оценка", (_, received, _) => {
                Check(received == score, "Score changed"); return Task.FromResult(new GingerAnswerRatingReceipt("id", true));
            });
        Console.WriteLine("Ginger chat ratings: complete evidence, explicit score, chat isolation, concurrency, failure and revocation passed.");
    }
}
