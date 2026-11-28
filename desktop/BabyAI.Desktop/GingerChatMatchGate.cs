namespace BabyAI.Desktop;

internal enum GingerChatMatchStage { Checking, Matching, Verified }
internal sealed record GingerChatMatchSnapshot(long ProcessId, long TableHandle, int RowCount,
    string Price, string LinkedProduct, GingerChatItem Item);

// An in-memory, one-use confirmation of the exact pair displayed in one chat.
// Model output only proposes a candidate; only the UI confirmation invokes this gate.
internal sealed class GingerChatMatchGate
{
    private readonly GingerChatMatchSnapshot _source;
    private readonly string _chatId;
    private readonly Func<DateTimeOffset> _clock;
    private readonly DateTimeOffset _expires;
    private int _consumed;
    public string Candidate { get; }

    public GingerChatMatchGate(GingerChatMatchSnapshot source, string chatId, string candidate,
        Func<DateTimeOffset>? clock = null)
    {
        if (string.IsNullOrWhiteSpace(chatId) || string.IsNullOrWhiteSpace(candidate) ||
            source.Item.Linked || source.Item.Candidates.Count(c => c == candidate) != 1)
            throw new InvalidDataException("Для подтверждения нужна одна точная пара несопоставленной строки.");
        _source = source with { Item = source.Item with { Candidates = source.Item.Candidates.ToArray() } };
        _chatId = chatId;
        Candidate = candidate;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _expires = _clock().AddMinutes(15);
    }

    private bool SameSource(GingerChatMatchSnapshot value) =>
        value.ProcessId == _source.ProcessId && value.TableHandle == _source.TableHandle &&
        value.RowCount == _source.RowCount && value.Price == _source.Price &&
        value.Item.Supplier == _source.Item.Supplier && value.Item.Document == _source.Item.Document &&
        value.Item.Position == _source.Item.Position && value.Item.Title == _source.Item.Title;

    public async Task RunAsync(string chatId, Func<CancellationToken, Task<GingerChatMatchSnapshot>> read,
        Func<bool> hasPriorAttempt, Func<CancellationToken, Task<GingerChatMatchSnapshot>> match,
        Action<GingerChatMatchStage, string> report, CancellationToken token)
    {
        if (Interlocked.Exchange(ref _consumed, 1) != 0)
            throw new InvalidOperationException("Это подтверждение уже использовано. Повтор запрещён.");
        token.ThrowIfCancellationRequested();
        if (chatId != _chatId || _clock() >= _expires)
            throw new InvalidOperationException("Подтверждение устарело или относится к другому чату. Нужно новое чтение.");
        report(GingerChatMatchStage.Checking, "Использую компьютер: заново проверяю выбранную строку и кандидатов.");
        var fresh = await read(token);
        token.ThrowIfCancellationRequested();
        if (_clock() >= _expires || !SameSource(fresh) || fresh.Item.Linked ||
            fresh.LinkedProduct != _source.LinkedProduct ||
            !fresh.Item.Candidates.SequenceEqual(_source.Item.Candidates, StringComparer.Ordinal))
            throw new InvalidDataException("Строка или кандидаты изменились. Сопоставление не отправлено; нужно новое чтение.");
        if (hasPriorAttempt())
            throw new InvalidOperationException("Есть защитная запись прежней попытки. Повтор запрещён; нужна проверка результата.");
        token.ThrowIfCancellationRequested();
        report(GingerChatMatchStage.Matching, "Использую компьютер: проверяю синее выделение, сопоставляю одну пару и читаю результат.");
        // The existing writer reconciles a dispatched action even after Stop.
        // Never turn an already verified match into a cancellation or retry it.
        var after = await match(token);
        if (!SameSource(after) || !after.Item.Linked || after.LinkedProduct != Candidate)
            throw new InvalidDataException("Результат сопоставления не подтверждён. Повтор запрещён; требуется ручная проверка.");
        report(GingerChatMatchStage.Verified, "Сопоставление проверено в Ginger. Документ остаётся открытым.");
    }
}
