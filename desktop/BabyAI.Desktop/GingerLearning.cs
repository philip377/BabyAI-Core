using System.Text.Json;
using System.Text.RegularExpressions;
namespace BabyAI.Desktop;

public sealed record GingerLearnedPair(string Supplier, string Source, string Candidate, string Reason,
    bool AllowAutomatic, DateTimeOffset RecordedAt);

public sealed class GingerLearning
{
    public sealed record AutomaticDecision(string Candidate, string Basis, string Explanation);
    public sealed record AutomaticAssessment(AutomaticDecision? Decision, string Reason);
    public sealed record LanguageLesson(string Supplier, string Text, string Understanding, bool Enabled,
        DateTimeOffset RecordedAt, string LessonId = "legacy");
    public LanguageLesson? LessonForReview(string supplier, string source)
    {
        // Never replace an explicit pair decision (including revocation) with a model guess.
        if (Read().Any(pair => pair.Supplier == supplier && pair.Source == source)) return null;
        var active = ReadLanguageLessons(supplier).Where(lesson => lesson.Enabled).ToArray();
        if (active.Length == 0) return null;
        var text = ComposeLanguageLessons(active);
        return active.Length == 1 ? active[0] : new(supplier, text,
            "Действующие подтверждённые уроки поставщика.", true,
            active.Max(lesson => lesson.RecordedAt), "aggregate");
    }

    // Compatibility reader for callers that still expect the last published record.
    // Decisions and editors must use the complete bundle, not this single record.
    public LanguageLesson? ReadLanguageLesson(string supplier)
        => ReadLanguageLessonHistory().LastOrDefault(item => item.Supplier == supplier);

    public IReadOnlyList<LanguageLesson> ReadLanguageLessons(string supplier)
    {
        var current = new Dictionary<string, LanguageLesson>(StringComparer.Ordinal);
        var order = new List<string>();
        foreach (var lesson in ReadLanguageLessonHistory().Where(item => item.Supplier == supplier))
        {
            if (!current.ContainsKey(lesson.LessonId)) order.Add(lesson.LessonId);
            current[lesson.LessonId] = lesson;
        }
        // First publication fixes order; editing or revoking a lesson cannot move it.
        return order.Select(id => current[id]).ToArray();
    }

    private IReadOnlyList<LanguageLesson> ReadLanguageLessonHistory()
    {
        var folder = Path.Combine(_directory, "language-lessons");
        if (!Directory.Exists(folder)) return [];
        return Directory.GetFiles(folder, "*.json").Order(StringComparer.Ordinal)
            .Select(path =>
            {
                var lesson = JsonSerializer.Deserialize<LanguageLesson>(File.ReadAllText(path))
                    ?? throw new InvalidDataException("Повреждён урок поставщика.");
                ValidateLanguageLesson(lesson);
                return lesson;
            }).ToArray();
    }

    public static string ComposeLanguageLessons(IEnumerable<LanguageLesson> lessons)
    {
        var bundle = lessons.ToArray();
        foreach (var lesson in bundle) ValidateLanguageLesson(lesson);
        if (bundle.Select(lesson => lesson.Supplier).Distinct(StringComparer.Ordinal).Skip(1).Any())
            throw new InvalidDataException("Нельзя объединить уроки разных поставщиков.");
        if (bundle.Select(lesson => lesson.LessonId).Distinct(StringComparer.Ordinal).Count() != bundle.Length)
            throw new InvalidDataException("В наборе несколько версий одного урока. Заново прочитай уроки поставщика.");
        var active = bundle.Where(lesson => lesson.Enabled).ToArray();
        if (active.Select(lesson => lesson.Text.Trim()).Distinct(StringComparer.Ordinal).Count() != active.Length)
            throw new InvalidDataException("Такое объяснение уже есть среди действующих уроков поставщика.");
        var text = active.Length == 1 ? active[0].Text
            : string.Join("\n\n", active.Select((lesson, index) => $"{index + 1}. {lesson.Text}"));
        if (text.Length > 4000)
            throw new InvalidDataException("Общий текст действующих уроков превышает 4000 символов. Сократи или отключи ненужные уроки; сохранённые объяснения не изменены.");
        return text;
    }

    public void SaveLanguageLesson(LanguageLesson lesson)
        => WriteLanguageLesson(lesson, null, null, compare: false);

    public void SaveLanguageLessonIfUnchanged(LanguageLesson lesson, LanguageLesson? expected)
        => WriteLanguageLesson(lesson, expected, null, compare: true);

    public void SaveLanguageLessonIfUnchanged(LanguageLesson lesson, IReadOnlyList<LanguageLesson> expected)
    {
        ArgumentNullException.ThrowIfNull(expected);
        // Snapshot the caller's list before acquiring the inter-window writer lock.
        WriteLanguageLesson(lesson, null, expected.ToArray(), compare: true);
    }

    private static void ValidateLanguageLesson(LanguageLesson lesson)
    {
        ArgumentNullException.ThrowIfNull(lesson);
        if (string.IsNullOrWhiteSpace(lesson.Supplier) || string.IsNullOrWhiteSpace(lesson.Text)
            || lesson.Text.Length > 4000 || string.IsNullOrWhiteSpace(lesson.Understanding) || lesson.Understanding.Length > 4000)
            throw new InvalidDataException("Нужны объяснение и проверенное понимание урока.");
        if (lesson.LessonId != "legacy" && !Guid.TryParseExact(lesson.LessonId, "N", out _)
            && !Guid.TryParseExact(lesson.LessonId, "D", out _))
            throw new InvalidDataException("Повреждён идентификатор урока поставщика.");
    }

    private void WriteLanguageLesson(LanguageLesson lesson, LanguageLesson? expected,
        IReadOnlyList<LanguageLesson>? expectedBundle, bool compare)
    {
        ValidateLanguageLesson(lesson);
        var folder = Path.Combine(_directory, "language-lessons");
        Directory.CreateDirectory(folder);
        // All writers, including the agent dialog, share this lock. Readers see
        // only complete immutable JSON files; an interrupted staging file is ignored.
        using var writeLock = new FileStream(Path.Combine(folder, ".write.lock"),
            FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var current = ReadLanguageLessons(lesson.Supplier);
        if (compare && (expectedBundle is not null ? !current.SequenceEqual(expectedBundle)
            : ReadLanguageLesson(lesson.Supplier) != expected))
            throw new InvalidOperationException("Уроки уже изменены в другом окне. Заново прочитай прайс и проверь сохранённые уроки.");
        // Validate the whole future context before publishing anything. Disabled
        // records stay in the bundle so stale editors cannot revive revoked lessons.
        ComposeLanguageLessons(current.Where(item => item.LessonId != lesson.LessonId).Append(lesson));
        var path = Path.Combine(folder, DateTime.UtcNow.ToString("yyyyMMddHHmmssfffffff")
            + "-" + Guid.NewGuid().ToString("N") + ".json");
        var pending = path + ".pending";
        try
        {
            using (var stream = new FileStream(pending, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { JsonSerializer.Serialize(stream, lesson); stream.Flush(true); }
            File.Move(pending, path);
        }
        finally { if (File.Exists(pending)) File.Delete(pending); }
    }
    public sealed record ConditionRule(string Supplier, bool Enabled, DateTimeOffset RecordedAt);
    public void TeachDentedTesterRule(string supplier, bool enabled)
    {
        if (string.IsNullOrWhiteSpace(supplier)) throw new InvalidDataException("Нужен поставщик правила.");
        var folder = Path.Combine(_directory, "condition-rules");
        Directory.CreateDirectory(folder);
        using var stream = new FileStream(Path.Combine(folder, DateTime.UtcNow.ToString("yyyyMMddHHmmssfffffff") + "-" + Guid.NewGuid().ToString("N") + ".json"), FileMode.CreateNew, FileAccess.Write, FileShare.None);
        JsonSerializer.Serialize(stream, new ConditionRule(supplier, enabled, DateTimeOffset.UtcNow));
        stream.Flush(true);
    }
    public bool HasDentedTesterRule(string supplier)
    {
        var folder = Path.Combine(_directory, "condition-rules");
        if (!Directory.Exists(folder)) return false;
        return Directory.GetFiles(folder, "*.json").Order(StringComparer.Ordinal)
            .Select(path => JsonSerializer.Deserialize<ConditionRule>(File.ReadAllText(path))
                ?? throw new InvalidDataException("Повреждено правило обучения."))
            .LastOrDefault(rule => rule.Supplier == supplier)?.Enabled == true;
    }
    private readonly string _directory;
    public GingerLearning(string? directory = null) => _directory = directory ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BabyAI", "ginger-learning");
    public IReadOnlyList<GingerLearnedPair> Read()
    {
        if (!Directory.Exists(_directory)) return [];
        return Directory.GetFiles(_directory, "*.json").Order(StringComparer.Ordinal)
            .Select(path => JsonSerializer.Deserialize<GingerLearnedPair>(File.ReadAllText(path))
                ?? throw new InvalidDataException("Повреждена запись обучения.")) .ToArray();
    }
    public void Teach(GingerLearnedPair pair)
    {
        if (string.IsNullOrWhiteSpace(pair.Supplier) || string.IsNullOrWhiteSpace(pair.Source)
            || string.IsNullOrWhiteSpace(pair.Candidate) || pair.Reason.Length > 4000)
            throw new InvalidDataException("Для обучения нужна полная пара товаров и поставщик.");
        Directory.CreateDirectory(_directory);
        using var stream = new FileStream(Path.Combine(_directory, DateTime.UtcNow.ToString("yyyyMMddHHmmssfffffff")
            + "-" + Guid.NewGuid().ToString("N") + ".json"), FileMode.CreateNew, FileAccess.Write, FileShare.None);
        JsonSerializer.Serialize(stream, pair); stream.Flush(true);
    }
    public string? AutomaticCandidate(string supplier, string source, IEnumerable<string> candidates)
        => DecideAutomatic(supplier, source, candidates)?.Candidate;
    public AutomaticDecision? DecideAutomatic(string supplier, string source, IEnumerable<string> candidates)
        => AssessAutomatic(supplier, source, candidates).Decision;
    public AutomaticAssessment AssessAutomatic(string supplier, string source, IEnumerable<string> candidates)
    {
        var list = candidates.ToArray();
        var learned = Read().Where(p => p.Supplier == supplier && p.Source == source).ToArray();
        if (learned.Length > 0)
        {
            var latest = learned[^1];
            if (!latest.AllowAutomatic) return new(null, "Последнее решение пользователя запрещает автоматическое применение этой пары.");
            var count = list.Count(c => c == latest.Candidate);
            if (count != 1) return new(null, count == 0 ? "Обученный кандидат отсутствует в текущем списке." : "Обученный кандидат повторяется в текущем списке.");
            return new(new(latest.Candidate, "learned_pair", "Подтверждённая точная пара этого поставщика. Объяснение пользователя: " + latest.Reason), "Применена обученная пара.");
        }
        // Exact token identity only. Similarity scores, variant research and guessed aliases never authorize input.
        var key = Canonical(source);
        var conditionApplied = false;
        if (HasDentedTesterRule(supplier) && Regex.IsMatch(key, @"\bнемного подмят\b"))
        {
            conditionApplied = true;
            key = Regex.Replace(key, @"\bнемного подмят\b", "");
            key = Regex.Replace(key, @"\s+", " ").Trim();
            if (!Regex.IsMatch(key, @"\btester\b")) key += " tester";
        }
        if (!Regex.IsMatch(key, @"\b\d+(?:[.,]\d+)? ml\b") || !Regex.IsMatch(key, @"\b(edp|edt|edc|extrait)\b"))
            return new(null, "В исходном названии не удалось подтвердить объём и концентрацию.");
        var equal = list.Where(c => Canonical(c) == key).ToArray();
        if (equal.Length == 0) return new(null, list.Length == 0 ? "Список кандидатов пуст." :
            "Нет единственного строгого совпадения. Название для сравнения: «" + key + "». Кандидаты: " +
            string.Join("; ", list.Select(c => "«" + Canonical(c) + "»")) + ". Оставшиеся различия обозначений и порядка слов требуют проверки.");
        if (equal.Length > 1) return new(null, "В списке несколько кандидатов с одинаковыми признаками.");
        return new(new(equal[0], conditionApplied ? "dented_tester_rule" : "exact_identity",
            conditionApplied ? "Включённое правило этого поставщика: немного подмят → тестер. Остальные признаки совпали; кандидат единственный."
                : "Все признаки названия совпали с учётом обозначений и соседнего порядка концентрации/объёма и написания линейки Moschino Cheap And Chic; кандидат единственный."), "Найдено единственное строгое совпадение.");
    }
    private static string Canonical(string title)
    {
        var text = title.Normalize(System.Text.NormalizationForm.FormKC).ToLowerInvariant();
        // A spelling alias scoped to the exact brand, not general word joining.
        text = Regex.Replace(text, @"\bmoschino\s+cheapandchic\b", "moschino cheap and chic");
        text = Regex.Replace(text, @"\bтестер\b", "tester");
        text = Regex.Replace(text, @"\bтв\b", "edt");
        text = Regex.Replace(text, @"\bпв\b", "edp");
        text = Regex.Replace(text, @"(\d)\s*(?:ml|мл)\b", "$1 ml");
        // Reorder only a single adjacent volume/concentration pair. Do not sort
        // name tokens or move attributes across words, punctuation or set components.
        if (Regex.Matches(text, @"\b\d+(?:[.,]\d+)?\s+ml\b").Count == 1 &&
            Regex.Matches(text, @"\b(?:edp|edt|edc|extrait)\b").Count == 1)
            text = Regex.Replace(text, @"\b(\d+(?:[.,]\d+)?\s+ml)\s+(edp|edt|edc|extrait)\b", "$2 $1");
        // Keep every lexical token: vintage, lux, gender, concentration and flankers are not noise.
        return string.Join(" ", Regex.Matches(text, @"[\p{L}\p{N}]+(?:[.,]\d+)?").Select(m => m.Value));
    }
}
