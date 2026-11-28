using BabyAI.Desktop;
using System.Text.Json;

internal static class IntentTests
{
    public static async Task Run()
    {
        const string message = "Давай дальше по прайсу";
        static void Require(bool condition, string name)
        {
            if (!condition) throw new Exception(name);
        }
        static JsonElement Reply(string message, string intent = "prepare_matching") => JsonSerializer.SerializeToElement(new {
            ok = true, command = "ginger.intent.preview", message, intent,
            scope = "current_row_preview", can_match = false
        });
        foreach (var text in new[] { message, "Продолжай сапоставлять товары", "Вернись к сверке номенклатуры",
            "Можешь продолжить сопоставление?", "Подбери кандидата для текущей строки" })
            Require(GingerChatIntent.MayNeedReview(text), "Paraphrase missed by domain filter");

        foreach (var text in new[] { "продолжай", "привет", new string('x', 501) + " прайс" })
            Require(await GingerChatIntent.ResolveAsync(text, (_, _) => throw new Exception("Unrelated model call"), default)
                == GingerChatRoute.Other, "Unrelated input routed");
        Require(await GingerChatIntent.ResolveAsync("Продолжи сопоставление", (_, _) => throw new Exception("Fast command called model"), default)
            == GingerChatRoute.Prepare, "Fast command lost");

        foreach (var (intent, expected) in new[] { ("prepare_matching", GingerChatRoute.Prepare),
            ("other", GingerChatRoute.Other), ("clarify", GingerChatRoute.Clarify) })
        {
            var calls = 0;
            var result = await GingerChatIntent.ResolveAsync(message, (text, _) => {
                calls++; Require(text == message, "Classifier did not receive original user input");
                return Task.FromResult(Reply(text, intent));
            }, default);
            Require(result == expected && calls == 1, "Semantic route changed");
        }

        foreach (var change in new Dictionary<string, object?>[] {
            new() { ["ok"] = false }, new() { ["can_match"] = true },
            new() { ["scope"] = "whole_price" }, new() { ["message"] = "Другой прайс" },
            new() { ["intent"] = "click" }, new() { ["intent"] = null },
            new() { ["command"] = "chat" }, new() { ["tool"] = "click" } })
        {
            var fields = JsonSerializer.Deserialize<Dictionary<string, object?>>(Reply(message).GetRawText())!;
            foreach (var field in change) fields[field.Key] = field.Value;
            var rejected = false;
            try { _ = GingerChatIntent.ParseReview(JsonSerializer.SerializeToElement(fields), message); }
            catch (InvalidDataException) { rejected = true; }
            Require(rejected, "Malformed, stale or broadened intent accepted");
        }

        using var before = new CancellationTokenSource();
        before.Cancel();
        var stoppedBefore = false;
        try { await GingerChatIntent.ResolveAsync("Продолжи сопоставление", (_, _) => throw new Exception("Cancelled model call"), before.Token); }
        catch (OperationCanceledException) { stoppedBefore = true; }
        Require(stoppedBefore, "Pre-cancelled fast command accepted");

        using var late = new CancellationTokenSource();
        var stoppedAfter = false;
        try {
            await GingerChatIntent.ResolveAsync(message, (text, _) => {
                late.Cancel(); return Task.FromResult(Reply(text));
            }, late.Token);
        }
        catch (OperationCanceledException) { stoppedAfter = true; }
        Require(stoppedAfter, "Late model intent survived Stop");

        var failed = false;
        try { await GingerChatIntent.ResolveAsync(message, (_, _) => throw new IOException("worker failed"), default); }
        catch (IOException) { failed = true; }
        Require(failed, "Classifier failure silently turned into an executable route");
        Console.WriteLine("Ginger intent: paraphrase routing, input binding, scope, malformed output and cancellation guards passed.");
    }
}
