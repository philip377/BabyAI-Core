using System.Text.Json;

namespace BabyAI.Desktop;

// A filter-only snapshot is useful for additive filters, never as a candidate list.
// Keep its status explicit all the way to the boundary that requires a full read.
internal sealed record GingerDesktopObservation
{
    public string Directory { get; }
    public JsonElement Data { get; }
    public JsonElement Source => Data.GetProperty("source");
    public string Title => Source.GetProperty("Title").GetString()!;
    public string Supplier => Source.GetProperty("Supplier").GetString()!;
    public string Document => Source.GetProperty("DocumentNumber").GetString()!;
    public int Position => Source.GetProperty("ObservedRowPosition").GetInt32();
    public int Count => Data.GetProperty("row_count").GetInt32();
    public long ProcessId => Data.GetProperty("process_id").GetInt64();
    public long TableHandle => Source.GetProperty("TableHandle").GetInt64();
    public bool NeedsFilters => Data.GetProperty("status").GetString() == "needs_filters";
    public long CandidateCount => NeedsFilters ? Data.GetProperty("candidate_count").GetInt64() : Data.GetProperty("candidates").GetArrayLength();
    public bool Linked => Source.GetProperty("LinkedProductText").GetString() is not ("<не указано" or "<не указано>");
    internal JsonElement Words => Data.GetProperty("words");
    public string[] Candidates
    {
        get
        {
            RequireComplete();
            return Data.GetProperty("candidates").EnumerateArray().Select(c => c.GetProperty("Title").GetString()!).ToArray();
        }
    }

    public GingerDesktopObservation(string directory, JsonElement data)
    {
        Object(data);
        var status = Text(data, "status", 32);
        if (status is not ("observed" or "needs_filters")) throw Invalid("статус наблюдения");
        if (!string.Equals(Text(data, "directory", 32768), directory, StringComparison.OrdinalIgnoreCase))
            throw Invalid("папка наблюдения");
        Field(data, "search_completion_verified", JsonValueKind.False);
        if (Number(data, "process_id") <= 0) throw Invalid("процесс");
        var count = Number(data, "row_count");
        var source = Field(data, "source", JsonValueKind.Object);
        Object(source);
        var position = Number(source, "ObservedRowPosition");
        if (count is < 1 or > 500 || position < 1 || position > count || Number(source, "TableHandle") <= 0)
            throw Invalid("размер таблицы или выделенная строка");
        foreach (var name in new[] { "Title", "Supplier", "DocumentNumber", "PriceText", "LinkedProductText" }) Text(source, name, 512);
        Field(source, "IdentityVerified", JsonValueKind.False);
        Field(source, "CanMatch", JsonValueKind.False);

        var words = Field(data, "words", JsonValueKind.Array);
        if (words.GetArrayLength() > 256) throw Invalid("число слов поиска");
        int index = 0;
        foreach (var word in words.EnumerateArray())
        {
            Object(word);
            Text(word, "Title", 512);
            Text(word, "DescriptionText", 2048, allowEmpty: true);
            if (Number(word, "ObservedPosition") != ++index) throw Invalid("порядок слов поиска");
            var state = Number(word, "StateImage");
            if (!word.TryGetProperty("Checked", out var enabled) ||
                enabled.ValueKind is not (JsonValueKind.True or JsonValueKind.False or JsonValueKind.Null) ||
                state is < 0 or > 2 || (state == 2) != (enabled.ValueKind == JsonValueKind.True))
                throw Invalid("состояние звёздочки поиска");
            Field(word, "IdentityVerified", JsonValueKind.False);
        }
        if (status == "needs_filters")
        {
            if (Number(data, "candidate_count") <= 256 || data.TryGetProperty("candidates", out _))
                throw Invalid("неполный список кандидатов");
        }
        else
        {
            if (data.TryGetProperty("candidate_count", out _)) throw Invalid("неоднозначный список кандидатов");
            var candidates = Field(data, "candidates", JsonValueKind.Array);
            if (candidates.GetArrayLength() > 256) throw Invalid("число кандидатов");
            index = 0;
            foreach (var candidate in candidates.EnumerateArray())
            {
                Object(candidate);
                Text(candidate, "Title", 512);
                if (Number(candidate, "ObservedPosition") != ++index) throw Invalid("порядок кандидатов");
                Field(candidate, "IdentityVerified", JsonValueKind.False);
            }
        }
        Directory = directory;
        Data = data.Clone();
    }

    public void RequireComplete()
    {
        if (NeedsFilters)
            throw new InvalidDataException($"Список Ginger слишком широкий: {CandidateCount} кандидатов, предел полного чтения — 256. Сузьте поиск вручную и повторите разбор этой строки.");
    }

    internal void RequireSameSource(GingerDesktopObservation after, int? selectedPosition = null)
    {
        if (ProcessId != after.ProcessId || TableHandle != after.TableHandle || Count != after.Count ||
            Supplier != after.Supplier || Document != after.Document)
            throw new InvalidDataException("Процесс, таблица или документ Ginger изменились. Продолжение остановлено.");
        if (selectedPosition.HasValue)
        {
            if (after.Position != selectedPosition.Value) throw Invalid("результат выбора строки");
        }
        else if (Position != after.Position || Title != after.Title ||
            Source.GetProperty("PriceText").GetString() != after.Source.GetProperty("PriceText").GetString() ||
            Source.GetProperty("LinkedProductText").GetString() != after.Source.GetProperty("LinkedProductText").GetString())
            throw new InvalidDataException("Выделенная позиция Ginger изменилась. Продолжение остановлено.");
    }

    internal static void Object(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object) throw Invalid("объект");
        var fields = new HashSet<string>(StringComparer.Ordinal);
        foreach (var field in value.EnumerateObject())
            if (!fields.Add(field.Name)) throw Invalid("повторяющееся поле");
    }
    internal static JsonElement Field(JsonElement parent, string name, JsonValueKind kind)
    {
        if (parent.ValueKind != JsonValueKind.Object || !parent.TryGetProperty(name, out var value) || value.ValueKind != kind)
            throw Invalid(name);
        return value;
    }
    internal static string Text(JsonElement parent, string name, int limit, bool allowEmpty = false)
    {
        var value = Field(parent, name, JsonValueKind.String).GetString()!;
        if (value.Length > limit || (!allowEmpty && (string.IsNullOrWhiteSpace(value) || value == "(null)"))) throw Invalid(name);
        return value;
    }
    private static long Number(JsonElement parent, string name)
    {
        if (!Field(parent, name, JsonValueKind.Number).TryGetInt64(out var value)) throw Invalid(name);
        return value;
    }
    internal static InvalidDataException Invalid(string field) => new("Неверные данные Ginger: " + field + ".");
}

// Delegates perform one fresh read or one additive filter. There is no navigation,
// retry or recursive planning in this flow, including when an intermediate list grows.
internal static class GingerDesktopFilterFlow
{
    internal static async Task<GingerDesktopObservation> ObserveAsync(
        Func<CancellationToken, Task<GingerDesktopObservation>> readOnce,
        Func<GingerDesktopObservation, CancellationToken, Task<string>> planOnce,
        Func<GingerDesktopObservation, string, CancellationToken, Task<GingerDesktopObservation>> filterOnce,
        CancellationToken token, bool prepareFiltersOnOverflow = false, Action<string>? report = null,
        int? candidateTarget = null)
    {
        token.ThrowIfCancellationRequested();
        RequireCandidateTarget(candidateTarget);
        var observed = await readOnce(token);
        token.ThrowIfCancellationRequested();
        if (!prepareFiltersOnOverflow) observed.RequireComplete();
        return observed.NeedsFilters
            ? await PrepareFiltersAsync(observed, planOnce, filterOnce, token, report, candidateTarget)
            : observed;
    }

    internal static async Task<GingerDesktopObservation> PrepareFiltersAsync(GingerDesktopObservation observed,
        Func<GingerDesktopObservation, CancellationToken, Task<string>> planOnce,
        Func<GingerDesktopObservation, string, CancellationToken, Task<GingerDesktopObservation>> filterOnce,
        CancellationToken token, Action<string>? report = null, int? candidateTarget = null)
    {
        token.ThrowIfCancellationRequested();
        RequireCandidateTarget(candidateTarget);
        if (ReachedCandidateTarget(observed, candidateTarget)) return observed;
        if (observed.NeedsFilters)
            report?.Invoke($"В панели Ginger {observed.CandidateCount} кандидатов. Сужаю список звёздочками слева, сохраняя уже включённые фильтры.");
        var json = await planOnce(observed, token);
        token.ThrowIfCancellationRequested();
        // Validate the entire plan before dispatching its first step.
        var words = ReadPlan(json, observed);
        foreach (var word in words)
        {
            token.ThrowIfCancellationRequested();
            RequireDisabledWord(observed, word);
            report?.Invoke("Проверяю и включаю звёздочку слева: " + word);
            token.ThrowIfCancellationRequested();
            var after = await filterOnce(observed, word, token);
            token.ThrowIfCancellationRequested();
            observed.RequireSameSource(after);
            RequireFilterTransition(observed, after, word);
            observed = after;
            // A plan is a bounded list of possible filters, not an obligation to
            // apply every word after a complete comparison-sized list is found.
            if (ReachedCandidateTarget(observed, candidateTarget))
            {
                report?.Invoke($"Найдено {observed.CandidateCount} кандидатов. Дальше звёздочки не включаю; список готов к сравнению.");
                token.ThrowIfCancellationRequested();
                return observed;
            }
        }
        // A broad list is never an empty result and never permits next-row/compare/match.
        observed.RequireComplete();
        return observed;
    }

    private static bool ReachedCandidateTarget(GingerDesktopObservation observed, int? target)
        => target is { } limit && !observed.NeedsFilters && observed.CandidateCount > 0 && observed.CandidateCount <= limit;

    private static void RequireCandidateTarget(int? target)
    {
        if (target is < 1 or > 256)
            throw new ArgumentOutOfRangeException(nameof(target), "Предел кандидатов должен быть от 1 до 256.");
    }

    private static string[] ReadPlan(string json, GingerDesktopObservation observed)
    {
        using var plan = JsonDocument.Parse(json);
        GingerDesktopObservation.Object(plan.RootElement);
        var steps = GingerDesktopObservation.Field(plan.RootElement, "steps", JsonValueKind.Array);
        if (steps.GetArrayLength() > 32) throw GingerDesktopObservation.Invalid("больше 32 шагов фильтрации");
        var words = new List<string>();
        var unique = new HashSet<string>(StringComparer.Ordinal);
        foreach (var step in steps.EnumerateArray())
        {
            GingerDesktopObservation.Object(step);
            if (step.EnumerateObject().Count() != 2) throw GingerDesktopObservation.Invalid("поля шага фильтрации");
            GingerDesktopObservation.Field(step, "enabled", JsonValueKind.True);
            var word = GingerDesktopObservation.Text(step, "word", 512);
            if (!unique.Add(word)) throw GingerDesktopObservation.Invalid("повторный шаг фильтрации");
            RequireDisabledWord(observed, word);
            words.Add(word);
        }
        return words.ToArray();
    }

    internal static void RequireDisabledWord(GingerDesktopObservation observed, string word)
    {
        var matches = observed.Words.EnumerateArray().Where(row => row.GetProperty("Title").GetString() == word).ToArray();
        if (matches.Length != 1 || matches[0].GetProperty("Checked").ValueKind == JsonValueKind.True)
            throw GingerDesktopObservation.Invalid("фильтр не является уникальным невключённым словом текущей строки");
    }

    internal static void RequireFilterTransition(GingerDesktopObservation before, GingerDesktopObservation after, string word)
    {
        if (before.Words.GetArrayLength() != after.Words.GetArrayLength()) throw GingerDesktopObservation.Invalid("слова поиска изменились");
        for (int i = 0; i < before.Words.GetArrayLength(); i++)
        {
            var left = before.Words[i];
            var right = after.Words[i];
            var title = left.GetProperty("Title").GetString();
            if (title != right.GetProperty("Title").GetString() ||
                left.GetProperty("DescriptionText").GetString() != right.GetProperty("DescriptionText").GetString())
                throw GingerDesktopObservation.Invalid("слова поиска изменились");
            if (title == word)
            {
                if (right.GetProperty("Checked").ValueKind != JsonValueKind.True)
                    throw GingerDesktopObservation.Invalid("включение звёздочки не подтверждено; повтор запрещён");
            }
            else if (left.GetProperty("Checked").ValueKind != right.GetProperty("Checked").ValueKind ||
                left.GetProperty("StateImage").GetInt32() != right.GetProperty("StateImage").GetInt32())
                throw GingerDesktopObservation.Invalid("изменился другой фильтр");
        }
    }
}
