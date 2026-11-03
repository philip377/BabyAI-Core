using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace BabyAI.Desktop;

// Fixed read-only entry point; never accepts a mode, script or command from the model.
internal static class GingerCandidateReader
{
    internal static int Validate(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Text(root, "mode", 64, "ReadMatchingCandidates");
        False(root, "catalogue_changed");
        False(root, "row_identity_verified");
        var actions = Field(root, "actions", JsonValueKind.Array);
        if (actions.GetArrayLength() != 0 || Number(root, "process_id") <= 0)
            throw new InvalidDataException("Unexpected navigation or process");
        var snapshot = Field(root, "candidate_snapshot", JsonValueKind.Object);
        False(snapshot, "IdentityVerified");
        False(snapshot, "CanMatch");
        False(snapshot, "SearchCompletionVerified");
        if (Number(snapshot, "PanelHandle") <= 0) throw new InvalidDataException("Missing panel");
        var source = Field(snapshot, "Source", JsonValueKind.Object);
        False(source, "IdentityVerified");
        False(source, "CanMatch");
        if (Number(source, "TableHandle") <= 0 || Number(source, "ObservedRowPosition") is < 1 or > 500)
            throw new InvalidDataException("Invalid source observation");
        foreach (var name in new[] { "DocumentNumber", "Supplier", "Title", "PriceText", "LinkedProductText" })
            Text(source, name, 512);
        var candidates = Field(snapshot, "Candidates", JsonValueKind.Array);
        if (candidates.GetArrayLength() > 256) throw new InvalidDataException("Too many candidates");
        int index = 0;
        foreach (var candidate in candidates.EnumerateArray())
        {
            if (Number(candidate, "ObservedPosition") != ++index) throw new InvalidDataException("Invalid candidate order");
            Text(candidate, "Title", 512);
            var description = Field(candidate, "DescriptionText", JsonValueKind.String).GetString()!;
            if (description.Length > 2048) throw new InvalidDataException("Oversized description");
            False(candidate, "IdentityVerified");
            if (!candidate.TryGetProperty("Selected", out var selected) ||
                selected.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                throw new InvalidDataException("Missing selection state");
        }
        return index;
    }

    private static JsonElement Field(JsonElement parent, string name, JsonValueKind kind)
    {
        if (parent.ValueKind != JsonValueKind.Object || !parent.TryGetProperty(name, out var value) || value.ValueKind != kind)
            throw new InvalidDataException("Invalid observation field: " + name);
        return value;
    }
    private static void False(JsonElement parent, string name) => Field(parent, name, JsonValueKind.False);
    private static long Number(JsonElement parent, string name)
    {
        if (!Field(parent, name, JsonValueKind.Number).TryGetInt64(out var number))
            throw new InvalidDataException("Invalid observation number");
        return number;
    }
    private static void Text(JsonElement parent, string name, int limit, string? expected = null)
    {
        var value = Field(parent, name, JsonValueKind.String).GetString()!;
        if (string.IsNullOrWhiteSpace(value) || value == "(null)" || value.Length > limit || (expected != null && value != expected))
            throw new InvalidDataException("Invalid observation text");
    }

    internal static async Task<bool> TryReadAsync(Action<string> log, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var script = Path.Combine(AppContext.BaseDirectory, "Ginger", "ginger-navigation.ps1");
        log("Читаю выделенную позицию и кандидатов через интерфейс Ginger. Данные останутся локально.");
        try
        {
            if (!File.Exists(script)) throw new FileNotFoundException("Bundled reader missing");
            var info = new ProcessStartInfo
            {
                FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                    "System32", "WindowsPowerShell", "v1.0", "powershell.exe"),
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
            };
            foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass",
                "-File", script, "-Mode", "ReadMatchingCandidates" }) info.ArgumentList.Add(argument);
            var json = await GingerDiagnostics.RunAsync(info, TimeSpan.FromSeconds(30), token);
            var count = Validate(json);
            token.ThrowIfCancellationRequested();
            var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "BabyAI", "diagnostics", "ginger-candidates-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            await File.WriteAllTextAsync(Path.Combine(folder, "observation.json"), json, Encoding.UTF8, token);
            log($"Позиция прочитана, кандидатов в панели: {count}. Повторные снимки совпали.");
            log("Это ещё не подтверждение завершения поиска или идентичности товаров. Сопоставление запрещено.");
            log("Локальный отчёт: " + folder);
            return true;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException or
            System.ComponentModel.Win32Exception or JsonException or OperationCanceledException)
        {
            log(ex is OperationCanceledException
                ? "Чтение не ответило за 30 секунд. Перехожу к диагностике интерфейса."
                : "Выделенную позицию и кандидатов не удалось проверить. Перехожу к диагностике интерфейса.");
            return false;
        }
    }
}
