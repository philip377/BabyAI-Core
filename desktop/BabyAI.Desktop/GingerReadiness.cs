using System.Text.Json;

namespace BabyAI.Desktop;

internal sealed record GingerProbeResult(string Probe, string Status, int? TableCount = null,
    bool Complete = false);

internal sealed record GingerReadiness(string State, bool CanRunOcr, string Message)
{
    // Metadata can permit read-only OCR, never linking or identity verification.
    public bool SelectionVerified => false;
    public bool CanWrite => false;

    internal static GingerProbeResult Parse(string probe, JsonElement report)
    {
        var expectedSource = probe switch
        {
            "ginger-uia-probe.ps1" => "Ginger UI Automation read-only inspection",
            "ginger-msaa-probe.ps1" => "Ginger4 Win32 HWND and MSAA read-only inspection",
            "ginger-listview-probe.ps1" => "Ginger4 native list-view read-only inspection",
            _ => throw new InvalidDataException("Unknown probe"),
        };
        if (report.ValueKind != JsonValueKind.Object ||
            Text(report, "source") != expectedSource ||
            Text(report, "process_name") != "Ginger4")
            throw new InvalidDataException("Unexpected report identity");
        if (probe == "ginger-uia-probe.ps1")
        {
            if (Flag(report, "names_included") ||
                !report.TryGetProperty("views", out var views) || views.ValueKind != JsonValueKind.Array)
                throw new InvalidDataException("Unexpected UIA report");
            return new(probe, "collected");
        }
        if (probe == "ginger-msaa-probe.ps1" && Flag(report, "names_included"))
            throw new InvalidDataException("Names must not be collected");
        if (!report.TryGetProperty("listview_count", out var count) ||
            count.ValueKind != JsonValueKind.Number || !count.TryGetInt32(out var number) || number < 0 || number > 5000 ||
            !report.TryGetProperty("listviews", out var tables) ||
            tables.ValueKind != JsonValueKind.Array || tables.GetArrayLength() != number)
            throw new InvalidDataException("Invalid table count");
        foreach (var table in tables.EnumerateArray())
        {
            if (table.ValueKind != JsonValueKind.Object ||
                Text(table, "class_name") is not { } className ||
                !System.Text.RegularExpressions.Regex.IsMatch(className, @"(^|[.])SysListView32([.]|$)"))
                throw new InvalidDataException("Invalid table metadata");
        }
        var truncated = Flag(report, probe == "ginger-msaa-probe.ps1" ? "truncated" : "truncated_by_visited_limit");
        return new(probe, "collected", number, !truncated);
    }

    internal static GingerReadiness Assess(IEnumerable<GingerProbeResult> reports)
    {
        var completed = reports.Where(r => r.Status == "collected" && r.Complete && r.TableCount.HasValue).ToArray();
        if (completed.Length == 0)
            return new("diagnostics_incomplete", false,
                "Не удалось проверить доступность таблиц Ginger. Проверка товара приостановлена; изменения запрещены.");
        // Reports are sequential: disagreement can mean that the screen changed.
        if (completed.Select(r => r.TableCount).Distinct().Count() > 1)
            return new("inconsistent_tables", false,
                "Способы чтения видят разные таблицы. Возможно, экран изменился. Проверка товара приостановлена.");
        if (completed[0].TableCount == 0)
            return new("matching_tables_not_found", false,
                "Ginger открыт, но таблицы сопоставления не обнаружены. Открой прайс поставщика и раздел сопоставления; товары не менялись.");
        if (completed[0].TableCount < 2)
            return new("matching_tables_incomplete", false,
                "Найдена только одна таблица. Не могу определить обе области сопоставления; проверка товара приостановлена.");
        return new("tables_detected_read_only", true,
            "Обнаружены таблицы. Продолжаю только чтение через OCR; выделенная строка и ID товара пока не подтверждены.");
    }

    private static string? Text(JsonElement item, string key) =>
        item.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static bool Flag(JsonElement item, string key)
    {
        if (!item.TryGetProperty(key, out var value) ||
            value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw new InvalidDataException("Missing report flag");
        return value.GetBoolean();
    }
}
