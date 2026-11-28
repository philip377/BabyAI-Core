using System.Diagnostics;
using System.Text;
using System.Text.Json;
namespace BabyAI.Desktop;

internal sealed class GingerDesktopSession : IDisposable
{
    private readonly FileStream _lock;
    public string DirectoryPath { get; }
    public Action<string>? FilterProgress { get; set; }
    public int? FilterCandidateTarget { get; init; }
    public Func<GingerDesktopObservation, IReadOnlyList<GingerSearchFilterEffect>, CancellationToken, Task<GingerSearchWordDecision>>? ChooseSearchWord { get; set; }
    private readonly GingerReasonedFilterFlow _reasonedFilters = new();
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
        GingerDesktopObservation? previous = null, int? position = null, string? word = null, bool prepare = false,
        bool prepareFiltersOnOverflow = false)
    {
        if (ChooseSearchWord is not null)
        {
            var observed = await ReadOnceAsync(token, previous, position, word, prepare);
            token.ThrowIfCancellationRequested();
            if (!prepareFiltersOnOverflow) observed.RequireComplete();
            return observed.NeedsFilters ? await PrepareFiltersAsync(observed, token) : observed;
        }
        return await GingerDesktopFilterFlow.ObserveAsync(
            readToken => ReadOnceAsync(readToken, previous, position, word, prepare),
            PlanFiltersAsync, ReadFilterOnceAsync, token, prepareFiltersOnOverflow, ReportFilter, FilterCandidateTarget);
    }

    // This is deliberately raw: a filter may still yield an overflow and must not
    // recursively plan, replay the original Select/Prepare, or repeat that filter.
    private Task<GingerDesktopObservation> ReadFilterOnceAsync(GingerDesktopObservation previous, string word, CancellationToken token)
        => ReadOnceAsync(token, previous, word: word);

    private async Task<GingerDesktopObservation> ReadOnceAsync(CancellationToken token,
        GingerDesktopObservation? previous = null, int? position = null, string? word = null, bool prepare = false)
    {
        token.ThrowIfCancellationRequested();
        if ((position.HasValue || word is not null) && previous is null || position.HasValue && word is not null)
            throw new InvalidOperationException("Выбор строки и фильтр требуют отдельного свежего наблюдения.");
        if (position.HasValue && (position.Value < 1 || position.Value > previous!.Count))
            throw new InvalidDataException("Неверная позиция строки Ginger.");
        var folder = Path.Combine(DirectoryPath, Guid.NewGuid().ToString("N"));
        var args = new List<string> { "-OutputDirectory", folder };
        if (prepare)
        {
            if (previous is not null || position.HasValue || word is not null)
                throw new InvalidOperationException("Подготовка экрана не совмещается с изменением строки или фильтра.");
            args.AddRange(["-Operation", "Prepare", "-ConfirmNavigation"]);
            Record("Подготавливаю панель сопоставления открытого прайса. Уже открытую панель не переключаю.");
        }
        if (previous is not null)
        {
            args.AddRange(["-ExpectedDirectory", previous.Directory, "-ConfirmNavigation"]);
            if (position.HasValue) args.AddRange(["-Operation", "Select", "-Position", position.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)]);
            else if (word is not null)
            {
                args.AddRange(["-Operation", "Filter", "-Word", word, "-Enabled"]);
                if (ChooseSearchWord is not null) args.Add("-RequireFreshFilterContext");
            }
        }
        // Preparation and the two complete candidate contexts can exceed a minute
        // on the user's price list. Keep a finite budget and immediate user Stop;
        // a deadline is a failure, never an instruction to retry navigation.
        var json = await GingerDiagnostics.RunAsync(Command("ginger-desktop-step.ps1", args.ToArray()), TimeSpan.FromSeconds(120), token, DirectoryPath);
        token.ThrowIfCancellationRequested();
        using var document = JsonDocument.Parse(json);
        var observed = new GingerDesktopObservation(folder, document.RootElement);
        previous?.RequireSameSource(observed, position);
        Record($"Прочитана строка {observed.Position}/{observed.Count}: {observed.Title}. Кандидатов: {observed.CandidateCount}" +
            (observed.NeedsFilters ? "; требуется сужение, содержимое кандидатов не прочитано." : "."));
        return observed;
    }
    public Task<GingerDesktopObservation> PrepareFiltersAsync(GingerDesktopObservation observed, CancellationToken token)
        => ChooseSearchWord is { } choose
            ? _reasonedFilters.PrepareAsync(observed, choose,
                (before, refreshToken) => ReadOnceAsync(refreshToken, before), ReadFilterOnceAsync, token, ReportFilter)
            : GingerDesktopFilterFlow.PrepareFiltersAsync(observed, PlanFiltersAsync, ReadFilterOnceAsync, token, ReportFilter, FilterCandidateTarget);

    private void ReportFilter(string message)
    {
        if (FilterProgress is not null) FilterProgress(message);
        else Record(message);
    }

    private async Task<string> PlanFiltersAsync(GingerDesktopObservation observed, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var python = Environment.GetEnvironmentVariable("BABYAI_PYTHON");
        if (string.IsNullOrWhiteSpace(python)) python = "python";
        var info = new ProcessStartInfo { FileName = python, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
        foreach (var arg in new[] { "-X", "utf8", Path.Combine(AppContext.BaseDirectory, "Ginger", "ginger-desktop-plan.py"),
            Path.Combine(observed.Directory, observed.NeedsFilters ? "filter-context.json" : "search-context.json") }) info.ArgumentList.Add(arg);
        if (observed.NeedsFilters) info.ArgumentList.Add("--filter-only");
        return await GingerDiagnostics.RunAsync(info, TimeSpan.FromSeconds(20), token, DirectoryPath);
    }
    public async Task<GingerDesktopObservation> MatchAsync(GingerDesktopObservation before, string candidate, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        before.RequireComplete();
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
