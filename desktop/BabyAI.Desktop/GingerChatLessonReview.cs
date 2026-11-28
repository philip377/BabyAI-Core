using System.Text.Json;

namespace BabyAI.Desktop;

// A lesson is reviewed against a frozen example. This class has no desktop
// driver or matching permissions; publication requires a separate user action.
internal sealed class GingerChatLessonReview
{
    private readonly GingerLearning _learning;
    private readonly string[] _candidates;
    private bool _closed;
    private int _working;
    private int _revision;
    public string ChatId { get; }
    public string Supplier { get; }
    public string Source { get; }
    public bool SourceOnly { get; }
    public IReadOnlyList<GingerLearning.LanguageLesson> Lessons { get; private set; }
    public string? SelectedLessonId { get; private set; }
    public GingerLearning.LanguageLesson? Saved => Lessons.SingleOrDefault(lesson => lesson.LessonId == SelectedLessonId);
    public GingerLearning.LanguageLesson? Draft { get; private set; }
    public bool CanPreview => !_closed && _working == 0;
    public bool CanSave => CanPreview && Draft is not null;
    public bool CanRevoke => CanPreview && Saved is { Enabled: true };

    public GingerChatLessonReview(string chatId, GingerChatItem item, GingerLearning learning)
        : this(chatId, item, learning, sourceOnly: false) { }

    // A persisted reminder supplies a frozen teaching example, not a current
    // desktop observation or permission to compare, navigate or bind a product.
    public static GingerChatLessonReview ForDeferredSource(string chatId, GingerDeferredRow row, GingerLearning learning)
    {
        ArgumentNullException.ThrowIfNull(row);
        return new(chatId, new GingerChatItem(row.Supplier, row.Document, row.Position,
            row.Title, false, []), learning, sourceOnly: true);
    }

    private GingerChatLessonReview(string chatId, GingerChatItem item, GingerLearning learning, bool sourceOnly)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(learning);
        if (string.IsNullOrWhiteSpace(chatId) || string.IsNullOrWhiteSpace(item.Supplier)
            || string.IsNullOrWhiteSpace(item.Title) || item.Candidates is null
            || (sourceOnly ? item.Candidates.Length != 0 : item.Candidates.Length is < 1 or > 100)
            || item.Candidates.Any(string.IsNullOrWhiteSpace))
            throw new InvalidDataException("Для урока нужны чат, поставщик, товар и ограниченный список кандидатов.");
        ChatId = chatId; Supplier = item.Supplier; Source = item.Title;
        SourceOnly = sourceOnly;
        _candidates = item.Candidates.ToArray();
        _learning = learning;
        Lessons = learning.ReadLanguageLessons(Supplier);
    }

    public void Invalidate() { _closed = true; DiscardDraft(); }
    public void DiscardDraft() { Interlocked.Increment(ref _revision); Draft = null; }

    public void SelectLesson(string? lessonId)
    {
        Enter();
        try
        {
            RequireContext(ChatId);
            if (lessonId is not null && !Lessons.Any(lesson => lesson.LessonId == lessonId))
                throw new InvalidOperationException("Выбранный урок отсутствует в этом просмотре.");
            SelectedLessonId = lessonId;
            DiscardDraft();
        }
        finally { Volatile.Write(ref _working, 0); }
    }

    private GingerLearning.LanguageLesson[] WithLesson(GingerLearning.LanguageLesson lesson)
        => Lessons.Any(item => item.LessonId == lesson.LessonId)
            ? Lessons.Select(item => item.LessonId == lesson.LessonId ? lesson : item).ToArray()
            : Lessons.Append(lesson).ToArray();

    private void RequireUnchangedLessons()
    {
        if (!Lessons.SequenceEqual(_learning.ReadLanguageLessons(Supplier)))
            throw new InvalidOperationException("Уроки поставщика изменены в другом окне. Заново прочитай прайс и проверь объяснение.");
    }

    private void RequireContext(string chatId)
    {
        if (_closed || chatId != ChatId)
            throw new InvalidOperationException("Урок относится к другому или уже закрытому просмотру.");
    }

    private void Enter()
    {
        if (Interlocked.CompareExchange(ref _working, 1, 0) != 0)
            throw new InvalidOperationException("Работа с уроком уже выполняется.");
    }

    public async Task<JsonElement> PreviewAsync(string chatId, string text,
        Func<string, string, string, IEnumerable<string>, CancellationToken, Task<JsonElement>> preview,
        CancellationToken token)
    {
        Enter();
        try
        {
            RequireContext(chatId);
            Draft = null;
            var revision = _revision;
            text = text.Trim();
            if (text.Length is < 1 or > 4000)
                throw new InvalidDataException("Напиши объяснение до 4000 символов.");
            RequireUnchangedLessons();
            var lessonId = SelectedLessonId ?? Guid.NewGuid().ToString("N");
            if (Lessons.Any(item => item.Enabled && item.LessonId != lessonId && item.Text.Trim() == text))
                throw new InvalidDataException("Такое объяснение уже действует. Выбери его в списке для изменения.");
            var draft = new GingerLearning.LanguageLesson(Supplier, text, "Проверка понимания", true, DateTimeOffset.UtcNow, lessonId);
            var combined = GingerLearning.ComposeLanguageLessons(WithLesson(draft));
            token.ThrowIfCancellationRequested();
            var result = (await preview(Supplier, Source, combined, _candidates.ToArray(), token)).Clone();
            token.ThrowIfCancellationRequested();
            RequireContext(chatId);
            if (revision != _revision)
                throw new InvalidOperationException("Объяснение изменено во время проверки. Проверь его заново.");
            RequireUnchangedLessons();
            if (SourceOnly) RequireSourceOnlyResult(result);
            else if (result.ValueKind == JsonValueKind.Object && result.TryGetProperty("mode", out var mode)
                && mode.ValueKind == JsonValueKind.String && mode.GetString() == "source_only")
                throw new InvalidDataException("Проверка исходной строки не является проверкой пары кандидатов.");
            if (result.TryGetProperty("error", out _))
                throw new InvalidDataException("Модель не завершила проверку объяснения.");
            var understanding = result.GetProperty("understanding").GetString();
            var candidate = result.GetProperty("candidate").GetString();
            var explanation = result.GetProperty("explanation").GetString();
            if (string.IsNullOrWhiteSpace(understanding) || understanding.Length > 4000
                || string.IsNullOrWhiteSpace(explanation) || explanation.Length > 4000
                || (candidate is not null && _candidates.Count(c => c == candidate) != 1))
                throw new InvalidDataException("Модель вернула непроверяемое понимание урока или кандидата.");
            Draft = draft with { Understanding = understanding };
            if (SourceOnly)
                return JsonSerializer.SerializeToElement(new {
                    request = new { mode = "source_only", supplier = Supplier, source = Source,
                        lesson = combined, candidates = _candidates }, result
                });
            return JsonSerializer.SerializeToElement(new {
                request = new { supplier = Supplier, source = Source, lesson = combined, candidates = _candidates }, result
            });
        }
        finally { Volatile.Write(ref _working, 0); }
    }

    private static void RequireSourceOnlyResult(JsonElement result)
    {
        const string failure = "Модель не завершила отдельную проверку объяснения исходной строки.";
        if (result.ValueKind != JsonValueKind.Object) throw new InvalidDataException(failure);
        var fields = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in result.EnumerateObject())
            if (!fields.Add(property.Name) || property.Name is not
                ("mode" or "understanding" or "candidate" or "explanation" or "ok" or "command" or "id"))
                throw new InvalidDataException(failure);
        if (!result.TryGetProperty("mode", out var mode) || mode.ValueKind != JsonValueKind.String
            || mode.GetString() != "source_only" || !result.TryGetProperty("candidate", out var candidate)
            || candidate.ValueKind != JsonValueKind.Null)
            throw new InvalidDataException(failure);
        foreach (var name in new[] { "understanding", "explanation" })
            if (!result.TryGetProperty(name, out var text) || text.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(text.GetString()) || text.GetString()!.Length > 4000)
                throw new InvalidDataException(failure);
        // Direct command tests return the four payload fields. The real bridge
        // retains ok/command and its already-validated optional worker request id.
        if (fields.Overlaps(["ok", "command", "id"]))
        {
            if (!result.TryGetProperty("ok", out var ok) || ok.ValueKind != JsonValueKind.True
                || !result.TryGetProperty("command", out var command) || command.ValueKind != JsonValueKind.String
                || command.GetString() != "ginger.teach.source.preview")
                throw new InvalidDataException(failure);
            if (result.TryGetProperty("id", out var id) && (id.ValueKind != JsonValueKind.Number
                || !id.TryGetInt64(out var number) || number < 0))
                throw new InvalidDataException(failure);
        }
    }

    public void Save(string chatId, string currentText)
    {
        Enter();
        try
        {
            RequireContext(chatId);
            if (Draft is not { } draft || draft.Text != currentText.Trim())
                throw new InvalidOperationException("Объяснение изменено. Сначала снова проверь понимание модели.");
            var saved = draft with { RecordedAt = DateTimeOffset.UtcNow };
            _learning.SaveLanguageLessonIfUnchanged(saved, Lessons);
            Lessons = Array.AsReadOnly(WithLesson(saved));
            SelectedLessonId = saved.LessonId;
            Draft = null;
        }
        finally { Volatile.Write(ref _working, 0); }
    }

    public void Revoke(string chatId)
    {
        Enter();
        try
        {
            RequireContext(chatId);
            if (Saved is not { Enabled: true } saved)
                throw new InvalidOperationException("У поставщика нет действующего урока для отзыва.");
            var revoked = saved with { Enabled = false, RecordedAt = DateTimeOffset.UtcNow };
            _learning.SaveLanguageLessonIfUnchanged(revoked, Lessons);
            Lessons = Array.AsReadOnly(WithLesson(revoked));
            Draft = null;
        }
        finally { Volatile.Write(ref _working, 0); }
    }
}
