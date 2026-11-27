using System.Text.Json;

namespace BabyAI.Desktop;

internal sealed record GingerAnswerRatingReceipt(string AnswerId, bool Active);

// Review is bound to the complete answer shown, never the current Ginger
// selection. It has no driver, model, lesson or permission dependencies.
internal sealed class GingerChatAnswerReview
{
    private readonly JsonElement _report;
    private int _writing;
    private bool _closed;
    public string ChatId { get; }
    public string Supplier => _report.GetProperty("request").GetProperty("supplier").GetString()!;
    public string Source => _report.GetProperty("request").GetProperty("source").GetString()!;
    public GingerAnswerRatingReceipt? Rating { get; private set; }
    public bool CanRate => !_closed && _writing == 0 && Rating is null;
    public bool CanRevoke => !_closed && _writing == 0 && Rating is { Active: true };

    public GingerChatAnswerReview(string chatId, JsonElement report)
    {
        if (string.IsNullOrWhiteSpace(chatId)) throw new InvalidDataException("Нужен чат показанного ответа.");
        var request = report.GetProperty("request");
        var result = report.GetProperty("result");
        if (string.IsNullOrWhiteSpace(request.GetProperty("supplier").GetString()) ||
            string.IsNullOrWhiteSpace(request.GetProperty("source").GetString()) ||
            request.GetProperty("candidates").GetArrayLength() == 0 ||
            !result.TryGetProperty("candidate", out _) || result.TryGetProperty("error", out _))
            throw new InvalidDataException("Для оценки нужен завершённый ответ и исходный список кандидатов.");
        ChatId = chatId;
        _report = report.Clone();
    }

    public void Invalidate() => _closed = true;

    private void RequireContext(string chatId, string feedback)
    {
        if (_closed || chatId != ChatId)
            throw new InvalidOperationException("Ответ относится к другому или уже закрытому просмотру.");
        if (string.IsNullOrWhiteSpace(feedback) || feedback.Length > 4000)
            throw new InvalidDataException("Напиши пояснение к оценке или причину её отзыва, не более 4000 символов.");
    }

    public async Task<GingerAnswerRatingReceipt> RateAsync(string chatId, int? points, string feedback,
        Func<JsonElement, int, string, Task<GingerAnswerRatingReceipt>> save)
    {
        if (Interlocked.CompareExchange(ref _writing, 1, 0) != 0)
            throw new InvalidOperationException("Сохранение оценки уже выполняется.");
        try
        {
            RequireContext(chatId, feedback);
            if (points is null or < 0 or > 10) throw new InvalidDataException("Выбери оценку от 0 до 10.");
            if (Rating is not null) throw new InvalidOperationException("Этот ответ уже оценён.");
            // Atomic content-addressed storage makes a user retry after an I/O
            // failure safe; it cannot duplicate evidence or revive a revocation.
            Rating = await save(_report, points.Value, feedback.Trim());
            return Rating;
        }
        finally { Volatile.Write(ref _writing, 0); }
    }

    public async Task RevokeAsync(string chatId, string reason, Func<string, string, Task> revoke)
    {
        if (Interlocked.CompareExchange(ref _writing, 1, 0) != 0)
            throw new InvalidOperationException("Сохранение оценки уже выполняется.");
        try
        {
            RequireContext(chatId, reason);
            if (Rating is not { Active: true } rating)
                throw new InvalidOperationException("У этого ответа нет действующей оценки для отзыва.");
            await revoke(rating.AnswerId, reason.Trim());
            Rating = rating with { Active = false };
        }
        finally { Volatile.Write(ref _writing, 0); }
    }
}
