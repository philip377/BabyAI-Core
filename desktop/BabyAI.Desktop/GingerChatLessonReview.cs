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
    public GingerLearning.LanguageLesson? Saved { get; private set; }
    public GingerLearning.LanguageLesson? Draft { get; private set; }
    public bool CanPreview => !_closed && _working == 0;
    public bool CanSave => CanPreview && Draft is not null;
    public bool CanRevoke => CanPreview && Saved is { Enabled: true };

    public GingerChatLessonReview(string chatId, GingerChatItem item, GingerLearning learning)
    {
        if (string.IsNullOrWhiteSpace(chatId) || string.IsNullOrWhiteSpace(item.Supplier)
            || string.IsNullOrWhiteSpace(item.Title) || item.Candidates.Length is < 1 or > 100
            || item.Candidates.Any(string.IsNullOrWhiteSpace))
            throw new InvalidDataException("Для урока нужны чат, поставщик, товар и ограниченный список кандидатов.");
        ChatId = chatId; Supplier = item.Supplier; Source = item.Title;
        _candidates = item.Candidates.ToArray();
        _learning = learning;
        Saved = learning.ReadLanguageLesson(Supplier);
    }

    public void Invalidate() { _closed = true; DiscardDraft(); }
    public void DiscardDraft() { Interlocked.Increment(ref _revision); Draft = null; }

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
            token.ThrowIfCancellationRequested();
            var result = (await preview(Supplier, Source, text, _candidates.ToArray(), token)).Clone();
            token.ThrowIfCancellationRequested();
            RequireContext(chatId);
            if (revision != _revision)
                throw new InvalidOperationException("Объяснение изменено во время проверки. Проверь его заново.");
            if (result.TryGetProperty("error", out _))
                throw new InvalidDataException("Модель не завершила проверку объяснения.");
            var understanding = result.GetProperty("understanding").GetString();
            var candidate = result.GetProperty("candidate").GetString();
            var explanation = result.GetProperty("explanation").GetString();
            if (string.IsNullOrWhiteSpace(understanding) || understanding.Length > 4000
                || string.IsNullOrWhiteSpace(explanation) || explanation.Length > 4000
                || (candidate is not null && _candidates.Count(c => c == candidate) != 1))
                throw new InvalidDataException("Модель вернула непроверяемое понимание урока или кандидата.");
            Draft = new(Supplier, text, understanding, true, DateTimeOffset.UtcNow);
            return JsonSerializer.SerializeToElement(new {
                request = new { supplier = Supplier, source = Source, lesson = text, candidates = _candidates }, result
            });
        }
        finally { Volatile.Write(ref _working, 0); }
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
            _learning.SaveLanguageLessonIfUnchanged(saved, Saved);
            Saved = saved; Draft = null;
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
            _learning.SaveLanguageLessonIfUnchanged(revoked, saved);
            Saved = revoked; Draft = null;
        }
        finally { Volatile.Write(ref _working, 0); }
    }
}
