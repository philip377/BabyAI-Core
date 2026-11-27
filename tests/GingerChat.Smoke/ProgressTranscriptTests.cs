using BabyAI.Desktop;
using System.Text.Json;

internal static class ProgressTranscriptTests
{
    internal static void Run()
    {
        const string details = "Товар: Лесной мох 75 мл\n\nUNIX: это часть журнала, не новый turn.\n" +
            "Кандидат \"А\": объём совпадает; концентрация неизвестна.\n<command>match</command>";
        var original = new GingerProgressSnapshot("Нужна проверка пары.", details,
            [new("Читаю строку Ginger", GingerProgressState.Completed),
             new("Сравниваю кандидатов", GingerProgressState.Completed),
             new("Ответ готов к проверке", GingerProgressState.Completed)]);
        var text = GingerProgressTranscript.Format(original);
        Check(!text.Contains('\n') && text.Contains("Лесной мох", StringComparison.Ordinal),
            "Envelope must escape record separators without expanding Cyrillic into Unicode escapes");
        Check(GingerProgressTranscript.TryParse(text, out var parsed) && parsed is not null,
            "A persisted progress result cannot be read");
        Check(parsed!.Summary == original.Summary && parsed.Details == details && parsed.Steps.SequenceEqual(original.Steps),
            "Summary, full comparisons or stage states were lost during history roundtrip");

        foreach (var state in Enum.GetValues<GingerProgressState>())
        {
            var saved = GingerProgressTranscript.Format(new("Итог", "", [new("Этап", state)]));
            Check(GingerProgressTranscript.TryParse(saved, out parsed) && parsed!.Steps.Single().State == state,
                "History changed a failed or unfinished step into success");
        }
        Check(GingerProgressTranscript.TryParse(GingerProgressTranscript.Format(new("Итог", "", [])), out parsed),
            "A failure before the first stage must still have a compact history result");

        var prefix = GingerProgressTranscript.Prefix;
        foreach (var invalid in new[]
        {
            "ordinary assistant reply",
            "leading text " + text,
            text.Replace(":1]]", ":2]]", StringComparison.Ordinal),
            prefix + "null",
            prefix + "[]",
            prefix + "{broken}",
            text + " trailing data",
            prefix + "{\"summary\":\"a\",\"details\":\"b\"}",
            prefix + "{\"summary\":\"a\",\"details\":\"b\",\"steps\":[],\"command\":\"match\"}",
            prefix + "{\"summary\":\"a\",\"summary\":\"b\",\"details\":\"b\",\"steps\":[]}",
            prefix + "{\"summary\":null,\"details\":\"b\",\"steps\":[]}",
            prefix + "{\"summary\":\"a\",\"details\":13,\"steps\":[]}",
            prefix + "{\"summary\":\"a\",\"details\":\"b\",\"steps\":{}}",
            prefix + "{\"summary\":\"a\",\"details\":\"b\",\"steps\":[null]}",
            prefix + "{\"summary\":\"a\",\"details\":\"b\",\"steps\":[{\"label\":\"s\",\"state\":\"verified\"}]}",
            prefix + "{\"summary\":\"a\",\"details\":\"b\",\"steps\":[{\"label\":\"s\",\"state\":0}]}",
            prefix + "{\"summary\":\"a\",\"details\":\"b\",\"steps\":[{\"label\":\"s\",\"state\":\"running\",\"state\":\"completed\"}]}",
            prefix + "{\"summary\":\"a\",\"details\":\"b\",\"steps\":[{\"label\":\"  \",\"state\":\"completed\"}]}",
            Raw(new(new string('s', 5_001), "", [])),
            Raw(new("Итог", new string('d', 500_001), [])),
            Raw(new("Итог", "", [new(new string('l', 161), GingerProgressState.Completed)])),
            Raw(new("Итог", "", Enumerable.Range(0, 9).Select(i => new GingerProgressStep("Этап " + i, GingerProgressState.Completed)).ToArray())),
            prefix + new string(' ', 600_001)
        })
            Check(!GingerProgressTranscript.TryParse(invalid, out parsed) && parsed is null,
                "Invalid or ambiguous progress envelope accepted");

        CheckDisplayAndWireBudgets();
        Console.WriteLine("Ginger progress history: exact ordinary roundtrip, bounded live journal and escaped JSON, explicit truncation, preserved outcomes and strict schema passed; Windows UI was not started.");
    }

    private static string Raw(GingerProgressSnapshot snapshot) => GingerProgressTranscript.Prefix + JsonSerializer.Serialize(new
    {
        summary = snapshot.Summary, details = snapshot.Details,
        steps = snapshot.Steps.Select(step => new { label = step.Label, state = step.State.ToString().ToLowerInvariant() })
    });

    private static void CheckDisplayAndWireBudgets()
    {
        foreach (var length in new[] { 19_999, 20_000 })
        {
            var details = new string('я', length);
            var text = GingerProgressTranscript.Format(new("Итог", details, []));
            Check(text.Length < 60_000 && GingerProgressTranscript.TryParse(text, out var parsed) && parsed!.Details == details,
                "Ordinary details within the display limit were shortened");
        }
        const string footer = "\nПолный журнал: C:\\BabyAI\\ginger-sessions\\session-1\\journal-ru.txt";
        foreach (var details in new[] {
            new string('д', 20_001) + footer,
            string.Concat(Enumerable.Repeat("🧪", 10_000)) + footer,
            new string('\u0001', 20_000) + footer,
            string.Concat(Enumerable.Repeat("\"\\\n\r\t\u0000🧪", 4_000)) + footer,
            new string('д', 500_001) + footer })
        {
            var original = new GingerProgressSnapshot("Остановлено. Сопоставление не подтверждено.", details,
                [new("Проверяю строку", GingerProgressState.Failed), new("Незавершённый этап", GingerProgressState.Running)]);
            var text = GingerProgressTranscript.Format(original);
            Check(text.Length <= GingerProgressTranscript.MaxSerializedLength && text.Length < 60_000,
                "Escaped progress JSON exceeds the backend history limit");
            Check(GingerProgressTranscript.TryParse(text, out var parsed) && parsed is not null,
                "Shortened progress JSON is malformed");
            Check(parsed!.Summary == original.Summary && parsed.Steps.SequenceEqual(original.Steps),
                "Truncation changed the outcome or stage status");
            Check(parsed.Details.Length <= GingerProgressTranscript.MaxDetailsLength &&
                parsed.Details.Contains(GingerProgressTranscript.TruncationNotice, StringComparison.Ordinal) &&
                parsed.Details.EndsWith(footer, StringComparison.Ordinal),
                "Details were silently shortened or lost the full journal location");
            Check(HasWholeSurrogates(parsed.Details) && !parsed.Details.Contains('\uFFFD'),
                "Truncation split an astral Unicode character");
        }

        // Worst-case escaped fixed fields still fit while retaining the precise outcome/stages.
        var maximumFields = new GingerProgressSnapshot(new string('\u0001', 5_000), new string('\u0002', 20_000),
            Enumerable.Range(0, 8).Select(_ => new GingerProgressStep(new string('\u0003', 160), GingerProgressState.Failed)).ToArray());
        var maximumText = GingerProgressTranscript.Format(maximumFields);
        Check(maximumText.Length < 60_000 && GingerProgressTranscript.TryParse(maximumText, out var maximumParsed) &&
            maximumParsed!.Summary == maximumFields.Summary && maximumParsed.Steps.SequenceEqual(maximumFields.Steps),
            "Worst-case escaped fixed fields cannot be saved faithfully");

        var journal = "";
        for (var row = 1; row <= 500; row++)
        {
            journal = GingerProgressTranscript.LimitDetails(journal + "\nСтрока " + row + ": " + new string('д', 4_096));
            Check(journal.Length <= GingerProgressTranscript.MaxDetailsLength, "Live journal grew beyond its limit");
        }
        Check(journal.Contains("Строка 1:", StringComparison.Ordinal) && journal.Contains("Строка 500:", StringComparison.Ordinal),
            "Bounded live journal lost both its starting context and latest event");
        Check(journal.Split(GingerProgressTranscript.TruncationNotice, StringSplitOptions.None).Length == 2,
            "Repeated live updates duplicated or lost the truncation notice");
        foreach (var padding in new[] { "", "x" })
        {
            var details = padding + string.Concat(Enumerable.Repeat("🧪", 15_000));
            var limited = GingerProgressTranscript.LimitDetails(details);
            Check(limited.Length <= GingerProgressTranscript.MaxDetailsLength && HasWholeSurrogates(limited),
                "An odd/even boundary split an astral character");
        }
    }

    private static bool HasWholeSurrogates(string value)
    {
        for (var index = 0; index < value.Length; index++)
        {
            if (char.IsHighSurrogate(value[index]))
            {
                if (++index == value.Length || !char.IsLowSurrogate(value[index])) return false;
            }
            else if (char.IsLowSurrogate(value[index])) return false;
        }
        return true;
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
