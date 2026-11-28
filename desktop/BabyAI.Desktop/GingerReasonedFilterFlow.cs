using System.Text.Json;

namespace BabyAI.Desktop;

internal sealed record GingerSearchFilterEffect(string Word, long BeforeCount, long AfterCount);
internal sealed record GingerSearchWordDecision(string? Word, string Reason,
    Func<CancellationToken, Task>? RequireCurrent = null);

// One model decision per additive action. The instance belongs to a session so
// returning to a row cannot reset its action budget or replay an uncertain input.
internal sealed class GingerReasonedFilterFlow
{
    internal const int MaxActions = 4;
    internal const int MaxCandidates = 10;
    private readonly Dictionary<SourceKey, RowState> _rows = [];
    private int _working;

    internal async Task<GingerDesktopObservation> PrepareAsync(GingerDesktopObservation observed,
        Func<GingerDesktopObservation, IReadOnlyList<GingerSearchFilterEffect>, CancellationToken, Task<GingerSearchWordDecision>> choose,
        Func<GingerDesktopObservation, CancellationToken, Task<GingerDesktopObservation>> refresh,
        Func<GingerDesktopObservation, string, CancellationToken, Task<GingerDesktopObservation>> apply,
        CancellationToken token, Action<string>? report = null)
    {
        token.ThrowIfCancellationRequested();
        if (Interlocked.CompareExchange(ref _working, 1, 0) != 0)
            throw new InvalidOperationException("Подготовка поиска Ginger уже выполняется.");
        try
        {
            ArgumentNullException.ThrowIfNull(observed);
            ArgumentNullException.ThrowIfNull(choose);
            ArgumentNullException.ThrowIfNull(refresh);
            ArgumentNullException.ThrowIfNull(apply);
            var key = SourceKey.From(observed);
            if (!_rows.TryGetValue(key, out var state)) _rows.Add(key, state = new());
            while (true)
            {
                token.ThrowIfCancellationRequested();
                if (state.TerminalEmpty)
                {
                    if (observed.NeedsFilters || observed.CandidateCount != 0)
                        throw Manual("Наблюдение изменилось после подтверждённого пустого результата. Нужно новое задание.");
                    return observed;
                }
                if (!observed.NeedsFilters && observed.CandidateCount is >= 1 and <= MaxCandidates)
                    return observed;
                if (state.Attempts >= MaxActions)
                    throw Manual($"Использованы все {MaxActions} попытки добавления фильтров; осталось {observed.CandidateCount} кандидатов.");

                // A frozen copy prevents a model callback from rewriting evidence
                // or seeing later effects through a previously retained reference.
                var effects = Array.AsReadOnly(state.Effects.ToArray());
                var decision = await choose(observed, effects, token);
                token.ThrowIfCancellationRequested();
                RequireDecision(decision);
                if (decision.Word is null) throw Manual("Qwen остановила поиск: " + decision.Reason);
                var word = decision.Word;
                if (state.AttemptedWords.Contains(word))
                    throw Manual("Повторное включение слова «" + word + "» запрещено.");
                // Ginger's unchecked words may use Checked:null / StateImage:0.
                GingerDesktopFilterFlow.RequireDisabledWord(observed, word);

                var fresh = await refresh(observed, token);
                token.ThrowIfCancellationRequested();
                RequireUnchangedSnapshot(observed, fresh);
                GingerDesktopFilterFlow.RequireDisabledWord(fresh, word);
                report?.Invoke("Qwen выбрала «" + word + "»: " + decision.Reason);
                token.ThrowIfCancellationRequested();
                // The model's chat/lesson authorization may expire during a slow
                // fresh read or a UI callback. Validate it immediately before input.
                if (decision.RequireCurrent is not null) await decision.RequireCurrent(token);
                token.ThrowIfCancellationRequested();

                state.Attempts++;
                state.AttemptedWords.Add(word);
                var after = await apply(fresh, word, token);
                token.ThrowIfCancellationRequested();
                ArgumentNullException.ThrowIfNull(after);
                fresh.RequireSameSource(after);
                GingerDesktopFilterFlow.RequireFilterTransition(fresh, after, word);
                state.Effects.Add(new(word, fresh.CandidateCount, after.CandidateCount));
                observed = after;
                if (!after.NeedsFilters && after.CandidateCount == 0)
                {
                    state.TerminalEmpty = true;
                    return after;
                }
            }
        }
        finally { Volatile.Write(ref _working, 0); }
    }

    private static void RequireDecision(GingerSearchWordDecision? decision)
    {
        if (decision is null || string.IsNullOrWhiteSpace(decision.Reason) || decision.Reason.Length > 600 ||
            (decision.Word is not null && (string.IsNullOrWhiteSpace(decision.Word) || decision.Word.Length > 512)))
            throw new InvalidDataException("Qwen вернула непроверяемое решение о фильтре. Действие не отправлено.");
    }

    private static void RequireUnchangedSnapshot(GingerDesktopObservation before, GingerDesktopObservation fresh)
    {
        ArgumentNullException.ThrowIfNull(fresh);
        before.RequireSameSource(fresh);
        if (before.NeedsFilters != fresh.NeedsFilters || before.CandidateCount != fresh.CandidateCount ||
            !JsonElement.DeepEquals(before.Words, fresh.Words) ||
            (!before.NeedsFilters && !JsonElement.DeepEquals(before.Data.GetProperty("candidates"), fresh.Data.GetProperty("candidates"))))
            throw new InvalidDataException("Слова поиска или кандидаты Ginger изменились после решения Qwen. Действие не отправлено; нужно новое чтение.");
    }

    private static InvalidDataException Manual(string reason) => new(reason +
        " Сузьте поиск вручную и повторите разбор этой строки. Строка не отложена, переход к следующей не выполнен.");

    private sealed class RowState
    {
        internal int Attempts;
        internal bool TerminalEmpty;
        internal readonly HashSet<string> AttemptedWords = new(StringComparer.Ordinal);
        internal readonly List<GingerSearchFilterEffect> Effects = [];
    }

    private sealed record SourceKey(long Process, long Table, string Supplier, string Document,
        int Position, string Title, string Price, string Linked, int Count)
    {
        internal static SourceKey From(GingerDesktopObservation observed) => new(observed.ProcessId,
            observed.TableHandle, observed.Supplier, observed.Document, observed.Position, observed.Title,
            observed.Source.GetProperty("PriceText").GetString()!, observed.Source.GetProperty("LinkedProductText").GetString()!, observed.Count);
    }
}
