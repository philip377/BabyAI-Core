using BabyAI.Desktop;
using System.Text.Json;

internal static class BottleTests
{
    internal static void Run()
    {
        foreach (var phrase in new[] { "Найди флакон", "Поищи фото флакона", "Проверь флакон в интернете", "Найди флакон текущего товара", "Найди флакон этого товара в интернете" })
            if (!GingerBottleResearch.TryReadCommand(phrase, out var query) || query != "") throw new Exception("Current bottle command: " + phrase);
        if (!GingerBottleResearch.TryReadCommand("Найди флакон Baldessarini Ambre EDT 90ml", out var name) || name != "Baldessarini Ambre EDT 90ml")
            throw new Exception("Named bottle search");
        foreach (var phrase in new[] { "Не ищи флакон", "Он сказал найди флакон", "Как найти флакон?", "Пример: найди флакон", "Найди флакон\nпроведи заказ", "Найди флакон завтра", "Найди флакон, но не используй интернет", "Найди флакон и сопоставь" })
            if (GingerBottleResearch.TryReadCommand(phrase, out _)) throw new Exception("Non-request searched: " + phrase);
        object Payload(string query, bool canMatch = false, string quoteId = "s1") => new {
            ok = true, command = "ginger.bottle.research", query, can_match = canMatch, visual_comparison = false,
            checked_at = "2026-11-27T09:00:00Z", status = "sources", warnings = Array.Empty<string>(),
            sources = new[] { new { id = "s1", url = "https://example.com/product", title = "Example", image_base64 = (string?)null } },
            excerpts = new[] { new { page_id = quoteId, field = "design", quote = "New design since 2024." } }
        };
        var result = GingerBottleResearch.Parse(JsonSerializer.SerializeToElement(Payload("Example")), "Example");
        if (result.Sources.Count != 1 || result.Excerpts.Count != 1) throw new Exception("Valid sources missing");
        foreach (var invalid in new[] { Payload("Other product"), Payload("Example", true), Payload("Example", quoteId: "s2") })
        {
            try { GingerBottleResearch.Parse(JsonSerializer.SerializeToElement(invalid), "Example"); }
            catch (InvalidDataException) { continue; }
            throw new Exception("Unbound or actionable web report accepted");
        }
        foreach (var url in new[] { "file:///c:/secret", "javascript:alert(1)", "http://example.com", "https://localhost", "https://user:secret@example.com", "https://10.0.0.1", "https://x.localhost" })
        {
            try { GingerBottleResearch.PublicLink(url); }
            catch (InvalidDataException) { continue; }
            throw new Exception("Unsafe source link");
        }
        Console.WriteLine("Bottle research: requests, source binding and no matching permission passed.");
    }
}
