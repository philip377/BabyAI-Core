using System.Text.Json;
using BabyAI.Desktop;

internal static class ReadinessTests
{
    private const string Msaa = "ginger-msaa-probe.ps1";
    private const string Listview = "ginger-listview-probe.ps1";

    internal static void Run()
    {
        GingerProbeResult Parse(string json)
        {
            using var doc = JsonDocument.Parse(json);
            return GingerReadiness.Parse(Msaa, doc.RootElement);
        }
        string Report(int count, bool truncated = false) => JsonSerializer.Serialize(new
        {
            source = "Ginger4 Win32 HWND and MSAA read-only inspection", process_name = "Ginger4",
            names_included = false, listview_count = count, truncated,
            listviews = Enumerable.Range(0, Math.Max(0, count)).Select(_ => new { class_name = "SysListView32" }),
        });
        void Check(string expected, params GingerProbeResult[] reports)
        {
            var result = GingerReadiness.Assess(reports);
            if (result.State != expected || result.CanWrite || result.SelectionVerified ||
                result.CanRunOcr != (expected == "tables_detected_read_only"))
                throw new Exception("Incorrect readiness: " + result);
        }
        Check("diagnostics_incomplete");
        Check("diagnostics_incomplete", new GingerProbeResult(Msaa, "timeout"));
        Check("diagnostics_incomplete", Parse(Report(2, true)));
        Check("matching_tables_not_found", Parse(Report(0)));
        Check("matching_tables_incomplete", Parse(Report(1)));
        Check("tables_detected_read_only", Parse(Report(2)));
        Check("inconsistent_tables", Parse(Report(0)), new(Listview, "collected", 2, true));
        foreach (var json in new[] {
            "{}", "[]", Report(-1), Report(0).Replace("Ginger4", "NotGinger"),
            Report(0).Replace("\"names_included\":false", "\"names_included\":true"),
            Report(0).Replace("\"listview_count\":0", "\"listview_count\":2"),
            Report(0).Replace("\"listview_count\":0", "\"listview_count\":\"0\""),
            Report(2).Replace("SysListView32", "Edit"),
            Report(0).Replace("\"truncated\":false", "\"truncated\":null"),
        })
        {
            var rejected = false;
            try { Parse(json); } catch (InvalidDataException) { rejected = true; }
            if (!rejected) throw new Exception("Malformed metadata accepted: " + json);
        }
        Console.WriteLine("PASS: missing/partial/conflicting tables, read-only readiness, malformed reports");
    }
}
