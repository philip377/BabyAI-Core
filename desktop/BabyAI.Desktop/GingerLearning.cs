using System.Text.Json;
using System.Text.RegularExpressions;
namespace BabyAI.Desktop;

public sealed record GingerLearnedPair(string Supplier, string Source, string Candidate, string Reason,
    bool AllowAutomatic, DateTimeOffset RecordedAt);

public sealed class GingerLearning
{
    public sealed record AutomaticDecision(string Candidate, string Basis, string Explanation);
    public sealed record AutomaticAssessment(AutomaticDecision? Decision, string Reason);
    public sealed record LanguageLesson(string Supplier, string Text, string Understanding, bool Enabled, DateTimeOffset RecordedAt);
    public LanguageLesson? LessonForReview(string supplier, string source)
    {
        // Never replace an explicit pair decision (including revocation) with a model guess.
        if (Read().Any(pair => pair.Supplier == supplier && pair.Source == source)) return null;
        return ReadLanguageLesson(supplier) is { Enabled: true } lesson ? lesson : null;
    }
    public LanguageLesson? ReadLanguageLesson(string supplier)
    {
        var folder = Path.Combine(_directory, "language-lessons");
        if (!Directory.Exists(folder)) return null;
        return Directory.GetFiles(folder, "*.json").Order(StringComparer.Ordinal)
            .Select(path => JsonSerializer.Deserialize<LanguageLesson>(File.ReadAllText(path))
                ?? throw new InvalidDataException("Повреждён урок поставщика."))
            .LastOrDefault(item => item.Supplier == supplier);
    }
    public void SaveLanguageLesson(LanguageLesson lesson)
        => WriteLanguageLesson(lesson, null, compare: false);

    public void SaveLanguageLessonIfUnchanged(LanguageLesson lesson, LanguageLesson? expected)
        => WriteLanguageLesson(lesson, expected, compare: true);

    private void WriteLanguageLesson(LanguageLesson lesson, LanguageLesson? expected, bool compare)
    {
        if (string.IsNullOrWhiteSpace(lesson.Supplier) || string.IsNullOrWhiteSpace(lesson.Text)
            || lesson.Text.Length > 4000 || string.IsNullOrWhiteSpace(lesson.Understanding) || lesson.Understanding.Length > 4000)
            throw new InvalidDataException("Нужны объяснение и проверенное понимание урока.");
        var folder = Path.Combine(_directory, "language-lessons");
        Directory.CreateDirectory(folder);
        // All writers, including the agent dialog, share this lock. Readers see
        // only complete immutable JSON files; an interrupted staging file is ignored.
        using var writeLock = new FileStream(Path.Combine(folder, ".write.lock"),
            FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        if (compare && ReadLanguageLesson(lesson.Supplier) != expected)
            throw new InvalidOperationException("Урок уже изменён в другом окне. Заново прочитай прайс и проверь сохранённый урок.");
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
