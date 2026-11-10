using System.Diagnostics;
using System.Text;
using System.Text.Json;
namespace BabyAI.Desktop;

internal sealed record GingerDesktopObservation(string Directory, JsonElement Data)
{
    public JsonElement Source => Data.GetProperty("source");
    public string Title => Source.GetProperty("Title").GetString()!;
    public string Supplier => Source.GetProperty("Supplier").GetString()!;
    public string Document => Source.GetProperty("DocumentNumber").GetString()!;
    public int Position => Source.GetProperty("ObservedRowPosition").GetInt32();
    public int Count => Data.GetProperty("row_count").GetInt32();
    public bool Linked => Source.GetProperty("LinkedProductText").GetString() is not ("<не указано" or "<не указано>");
    public string[] Candidates => Data.GetProperty("candidates").EnumerateArray().Select(c => c.GetProperty("Title").GetString()!).ToArray();
}
internal sealed class GingerDesktopSession : IDisposable
{
    private readonly FileStream _lock;
    public string DirectoryPath { get; }
    public GingerDesktopSession()
    {
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BabyAI");
        Directory.CreateDirectory(root);
        _lock = new FileStream(Path.Combine(root, "ginger-desktop.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        DirectoryPath = Path.Combine(root, "ginger-sessions", DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(DirectoryPath);
    }
    public void Dispose() => _lock.Dispose();
    public bool HasPriorAttempt(GingerDesktopObservation observation) => GingerClaims.HasReservation(
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BabyAI", "ginger-matching-claims"),
        observation.Data.GetProperty("process_id").GetInt64(), observation.Source.GetProperty("TableHandle").GetInt64(),
        observation.Document, observation.Supplier, observation.Position, observation.Title);
    public void Record(string message) => File.AppendAllText(Path.Combine(DirectoryPath, "journal-ru.txt"), $"[{DateTimeOffset.Now:O}] {message}\n", Encoding.UTF8);
    private static ProcessStartInfo Command(string script, params string[] args)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Ginger", script);
        if (!File.Exists(path)) throw new FileNotFoundException("В сборке отсутствует драйвер Ginger.", path);
        var info = new ProcessStartInfo {
            FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "WindowsPowerShell", "v1.0", "powershell.exe"),
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
        foreach (var value in new[] { "-NoProfile", "-NonInteractive", "-STA", "-File", path }.Concat(args)) info.ArgumentList.Add(value);
        return info;
    }
    public async Task<GingerDesktopObservation> ObserveAsync(CancellationToken token,
        GingerDesktopObservation? previous = null, int? position = null, string? word = null)
    {
        var folder = Path.Combine(DirectoryPath, Guid.NewGuid().ToString("N"));
        var args = new List<string> { "-OutputDirectory", folder };
        if (previous is not null)
        {
            args.AddRange(["-ExpectedDirectory", previous.Directory, "-ConfirmNavigation"]);
            if (position.HasValue) args.AddRange(["-Operation", "Select", "-Position", position.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)]);
            else if (word is not null) args.AddRange(["-Operation", "Filter", "-Word", word, "-Enabled"]);
        }
        var json = await GingerDiagnostics.RunAsync(Command("ginger-desktop-step.ps1", args.ToArray()), TimeSpan.FromSeconds(60), token, DirectoryPath);
        using var document = JsonDocument.Parse(json);
        var observed = new GingerDesktopObservation(folder, document.RootElement.Clone());
        if (observed.Position < 1 || observed.Position > observed.Count || observed.Count > 500 || observed.Candidates.Length > 256)
            throw new InvalidDataException("Неверные размеры наблюдения Ginger.");
        if (previous is not null && (previous.Supplier != observed.Supplier || previous.Document != observed.Document))
            throw new InvalidDataException("Документ Ginger изменился.");
        Record($"Прочитана строка {observed.Position}/{observed.Count}: {observed.Title}. Кандидатов: {observed.Candidates.Length}.");
        return observed;
    }
    public async Task<GingerDesktopObservation> PrepareFiltersAsync(GingerDesktopObservation observed, CancellationToken token)
    {
        var python = Environment.GetEnvironmentVariable("BABYAI_PYTHON");
        if (string.IsNullOrWhiteSpace(python)) python = "python";
        var info = new ProcessStartInfo { FileName = python, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
        foreach (var arg in new[] { "-X", "utf8", Path.Combine(AppContext.BaseDirectory, "Ginger", "ginger-desktop-plan.py"), Path.Combine(observed.Directory, "search-context.json") }) info.ArgumentList.Add(arg);
        var json = await GingerDiagnostics.RunAsync(info, TimeSpan.FromSeconds(20), token, DirectoryPath);
        using var plan = JsonDocument.Parse(json);
        foreach (var step in plan.RootElement.GetProperty("steps").EnumerateArray())
        {
            token.ThrowIfCancellationRequested();
            var word = step.GetProperty("word").GetString()!;
            Record("Проверяю и включаю звёздочку слева: " + word);
            observed = await ObserveAsync(token, observed, word: word);
        }
        return observed;
    }
    public async Task<GingerDesktopObservation> MatchAsync(GingerDesktopObservation before, string candidate, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (before.Linked || before.Candidates.Count(c => c == candidate) != 1) throw new InvalidDataException("Пара не уникальна или строка уже сопоставлена.");
        var receipt = Path.Combine(DirectoryPath, "match-" + Guid.NewGuid().ToString("N"));
        Record($"Подтверждено сопоставление строки {before.Position}: {candidate}. Проверяю синее выделение.");
        // Once dispatch may have happened, Stop waits for reconciliation. It never kills the writer mid-flight.
        var json = await GingerDiagnostics.RunAsync(Command("ginger-match-once.ps1", "-ObservationDirectory", before.Directory,
            "-ReceiptDirectory", receipt, "-CandidateTitle", candidate, "-ConfirmMatch"), TimeSpan.FromSeconds(90), CancellationToken.None, DirectoryPath);
        using var result = JsonDocument.Parse(json);
        if (result.RootElement.GetProperty("status").GetString() != "matched_verified") throw new InvalidDataException("Результат не подтверждён. Повтор запрещён.");
        Record("Сопоставление подтверждено повторным чтением всей таблицы. Документ не закрывался.");
        return await ObserveAsync(CancellationToken.None);
    }
}
