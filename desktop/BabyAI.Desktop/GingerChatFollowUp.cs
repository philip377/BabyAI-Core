using System.Text.RegularExpressions;

namespace BabyAI.Desktop;

internal enum GingerChatFollowUpKind { None, Status, Resume, Clarify }

// Classifies only the current message. Context ownership/expiry and any action
// remain with the caller; a Resume result is never permission to replay input.
internal static class GingerChatFollowUp
{
    private static readonly HashSet<string> ShortStatus = new(StringComparer.Ordinal)
    {
        "ну как", "ну и как", "как дела", "готово", "ну что", "что там", "как там",
        "давай проверим текущее состояние"
    };

    private static readonly HashSet<string> ShortResume = new(StringComparer.Ordinal)
    {
        "продолжи", "продолжай", "продолжим", "давай продолжим", "повтори"
    };

    private static readonly Regex ExplicitStatus = new(
        @"\A(?:" +
        @"что (?:у нас )?(?:там )?с (?:сопоставлением|сопоставлениями)(?: в (?:ginger|джинджер))?|" +
        @"как (?:идет|идут|продвигается|продвигаются) (?:сопоставление|сопоставления)(?: в (?:ginger|джинджер))?|" +
        @"(?:какой |каков )?(?:текущий )?статус (?:ginger|джинджер|сопоставления|сопоставлений)|" +
        @"(?:покажи|проверь) (?:(?:текущий )?статус|(?:текущее )?состояние) (?:ginger|джинджер|сопоставления|сопоставлений)|" +
        @"(?:есть сопоставление|все сопоставлено|сопоставление (?:готово|завершено))" +
        @")\z", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    internal static GingerChatFollowUpKind Classify(string? message, bool sameChatContext)
    {
        if (string.IsNullOrWhiteSpace(message) || message.Length > 500)
            return GingerChatFollowUpKind.None;
        // Do not interpret quoted text, multiple lines, control characters or
        // a formatted payload as a short instruction, even with recent focus.
        if (message.Any(c => char.IsControl(c) && c != '\t') ||
            message.IndexOfAny(['"', '\'', '`', '«', '»', '“', '”', '„', '‘', '’', '<', '>']) >= 0)
            return GingerChatFollowUpKind.None;

        var text = Regex.Replace(message.Trim().ToLowerInvariant().Replace('ё', 'е'), @"[ \t\u00a0]+", " ");
        var end = text.Length;
        while (end > 0 && text[end - 1] is '.' or '!' or '?') end--;
        if (text.Length - end > 3) return GingerChatFollowUpKind.None;
        text = text[..end].TrimEnd();

        if (ExplicitStatus.IsMatch(text)) return GingerChatFollowUpKind.Status;
        if (ShortResume.Contains(text))
            return sameChatContext ? GingerChatFollowUpKind.Resume : GingerChatFollowUpKind.None;
        if (!sameChatContext) return GingerChatFollowUpKind.None;
        if (ShortStatus.Contains(text)) return GingerChatFollowUpKind.Status;
        if (text is "сопоставление" or "сопоставления") return GingerChatFollowUpKind.Clarify;
        return GingerChatFollowUpKind.None;
    }
}
