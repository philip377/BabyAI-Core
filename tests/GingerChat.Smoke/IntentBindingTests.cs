using BabyAI.Desktop;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

internal static class IntentBindingTests
{
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    private static void Refuses(string json, string message)
    {
        using var response = JsonDocument.Parse(json);
        try { GingerChatIntent.ParseReview(response.RootElement, message); }
        catch (InvalidDataException) { return; }
        throw new Exception("Unbound or malformed intent response accepted");
    }

    internal static async Task Run()
    {
        // Python verifies these envelopes against the real desktop_worker.serve
        // output, including its transport id and exact original Unicode text.
        var path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "../../../../../tests/fixtures/ginger_chat_intent_binding_cases.json"));
        using var fixtures = JsonDocument.Parse(File.ReadAllText(path));
        foreach (var fixture in fixtures.RootElement.EnumerateArray())
        {
            var message = fixture.GetProperty("message").GetString()!;
            var response = fixture.GetProperty("response");
            var expected = response.GetProperty("intent").GetString() switch
            {
                "prepare_matching" => GingerChatRoute.Prepare,
                "other" => GingerChatRoute.Other,
                "clarify" => GingerChatRoute.Clarify,
                _ => throw new Exception("Unknown fixture intent")
            };
            Check(!GingerChatIntent.IsStart(message), "Fixture bypassed semantic review");
            var calls = 0;
            var route = await GingerChatIntent.ResolveAsync(message, (text, _) =>
            {
                Check(text == message, "Review input was normalized or replaced");
                calls++;
                return Task.FromResult(response);
            }, default);
            Check(route == expected && calls == 1, "Worker envelope lost its bound route");

            var normalized = message.Normalize(NormalizationForm.FormC);
            if (normalized != message) Refuses(response.GetRawText(), normalized);
        }

        const string original = "продолжим сопоставление?";
        var valid = fixtures.RootElement[0].GetProperty("response").GetRawText();
        foreach (var staleMessage in new[] { "Продолжим сопоставление?", "продолжим сопоставление",
            "продолжим\u00a0сопоставление?", original + " ", "не " + original, "«" + original + "»",
            "Продолжим сопоставление другого прайса?" })
            Refuses(valid, staleMessage);

        foreach (var invalidId in new[] { "null", "true", "\"13\"", "-1", "1.5", "9223372036854775808" })
        {
            var response = JsonNode.Parse(valid)!;
            response["id"] = JsonNode.Parse(invalidId);
            Refuses(response.ToJsonString(), original);
        }
        foreach (var (name, value) in new[] { ("tool", "\"click\""), ("scope", "\"whole_price\""),
            ("can_match", "true"), ("command", "\"chat\""), ("ok", "false"), ("intent", "\"click\"") })
        {
            var response = JsonNode.Parse(valid)!;
            response[name] = JsonNode.Parse(value);
            Refuses(response.ToJsonString(), original);
        }
        Refuses(valid.Insert(1, "\"id\":13,"), original);
        Refuses(valid.Insert(1, "\"message\":\"other\","), original);
        var withoutScope = JsonNode.Parse(valid)!;
        withoutScope.AsObject().Remove("scope");
        Refuses(withoutScope.ToJsonString().Insert(1, "\"id\":13,"), original);

        Console.WriteLine("Ginger intent binding: real worker envelope, exact question, Unicode, stale input, strict fields and guarded routes passed.");
    }
}
