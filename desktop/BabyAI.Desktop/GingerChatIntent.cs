using System.Text.Json;
using System.Text.RegularExpressions;

namespace BabyAI.Desktop;

internal enum GingerChatRoute { Other, Prepare, Clarify }

// Only the current user's chat input is eligible. An interpretation starts the
// existing preparation flow, never a match, saved lesson or permission change.
internal static class GingerChatIntent
{
    public static bool IsStart(string message) => message.Length <= 200 && Regex.IsMatch(message.Trim(),
        @"\A(?:(?:unix|юникс)[,\s]+)?(?:пожалуйста[,\s]+)?(?:надо бы |давай )?(?:продолжи(?:ть|м)?|начни|начать|запусти|запустить|возобнови)\s+сопоставление(?:\s+(?:в\s+)?(?:ginger|джинджер))?(?:[,\s]+пожалуйста)?[.!]*\z",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    // A broad domain filter avoids an extra model call for unrelated chat. It
    // does not decide whether a sentence is an instruction or authorize input.
    public static bool MayNeedReview(string message) => message.Length is > 0 and <= 500 &&
        Regex.IsMatch(message, @"\b(?:ginger|джинджер|с[ао]п[ао]став\w*|прайс\w*|кандидат\w*|номенклатур\w*|поставщик\w*|товар\w*)\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static async Task<GingerChatRoute> ResolveAsync(string message,
        Func<string, CancellationToken, Task<JsonElement>> review, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (IsStart(message)) return GingerChatRoute.Prepare;
        if (!MayNeedReview(message)) return GingerChatRoute.Other;
        var result = await review(message, token);
        token.ThrowIfCancellationRequested();
        return ParseReview(result, message);
    }

    internal static GingerChatRoute ParseReview(JsonElement result, string message)
    {
        // The response is bound to the original message and fixed workflow.
        // Never interpret arbitrary model-produced tool names or action text.
        if (result.ValueKind != JsonValueKind.Object || result.EnumerateObject().Count() != 6 ||
            !result.TryGetProperty("ok", out var ok) || ok.ValueKind != JsonValueKind.True ||
            !IsText(result, "command", "ginger.intent.preview") ||
            !IsText(result, "message", message) || !IsText(result, "scope", "current_row_preview") ||
            !result.TryGetProperty("can_match", out var canMatch) || canMatch.ValueKind != JsonValueKind.False ||
            !result.TryGetProperty("intent", out var intent) || intent.ValueKind != JsonValueKind.String)
            throw new InvalidDataException("Ответ о намерении не относится к текущему запросу.");
        return intent.GetString() switch
        {
            "prepare_matching" => GingerChatRoute.Prepare,
            "other" => GingerChatRoute.Other,
            "clarify" => GingerChatRoute.Clarify,
            _ => throw new InvalidDataException("Неизвестное намерение. Ginger не запущен.")
        };
    }

    private static bool IsText(JsonElement value, string name, string expected) =>
        value.TryGetProperty(name, out var field) && field.ValueKind == JsonValueKind.String && field.GetString() == expected;
}
