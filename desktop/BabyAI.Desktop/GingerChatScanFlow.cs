namespace BabyAI.Desktop;

internal sealed record GingerChatScanRow(GingerChatItem Item, int Count, long ProcessId,
    long TableHandle, string ObservationDirectory);
internal sealed record GingerChatScanResult(GingerChatScanRow Row, string? Proposal,
    int DeferredCount, int AlreadyLinkedCount, bool ReachedEnd);

// Navigation only. Matching remains behind the separate one-use confirmation gate.
internal sealed class GingerChatScanFlow(
    Func<CancellationToken, Task<GingerChatScanRow>> read,
    Func<GingerChatScanRow, CancellationToken, Task<GingerChatScanRow>> filter,
    Func<GingerChatScanRow, CancellationToken, Task<GingerChatScanRow>> next,
    Func<GingerChatScanRow, CancellationToken, Task> defer,
    Func<GingerChatItem, CancellationToken, Task<string>> compare,
    Action<GingerChatStage, string> report)
{
    public async Task<GingerChatScanResult> RunAsync(CancellationToken token)
    {
        try
        {
            token.ThrowIfCancellationRequested();
            report(GingerChatStage.Started, "Активирован агент Ginger. Ищу следующую позицию для проверки пары в открытом прайсе.");
            report(GingerChatStage.Reading, "Использую компьютер: открываю панель сопоставления и читаю текущий прайс.");
            var row = await read(token);
            token.ThrowIfCancellationRequested();
            Validate(row);
            var start = row;
            var seen = new HashSet<int>();
            var deferred = 0;
            var linked = 0;
            while (true)
            {
                token.ThrowIfCancellationRequested();
                Validate(row);
                if (!SameDocument(start, row) || !seen.Add(row.Item.Position))
                    throw new InvalidDataException("Прайс или порядок строк изменился. Автоматический переход остановлен.");
                if (row.Item.Linked)
                {
                    linked++;
                    report(GingerChatStage.Skipping, $"Строка {row.Item.Position}: уже сопоставлена, повтор не выполняется.");
                }
                else
                {
                    if (row.Item.Candidates.Length == 0)
                    {
                        report(GingerChatStage.Filtering, $"Строка {row.Item.Position}: проверяю звёздочки слов слева и перечитываю кандидатов.");
                        var filtered = await filter(row, token);
                        token.ThrowIfCancellationRequested();
                        Validate(filtered);
                        if (!SameDocument(row, filtered) || !SameItem(row.Item, filtered.Item))
                            throw new InvalidDataException("Товар изменился во время подготовки поиска. Нужно новое чтение.");
                        row = filtered;
                    }
                    if (row.Item.Candidates.Length != 0)
                    {
                        report(GingerChatStage.Comparing, $"Строка {row.Item.Position}: сравниваю кандидатов ({row.Item.Candidates.Length}). Компьютером сейчас не управляю.");
                        var proposal = await compare(row.Item, token);
                        token.ThrowIfCancellationRequested();
                        report(GingerChatStage.Review, "Ответ готов к проверке. Привязка товара ещё не выполнена.");
                        return new(row, proposal, deferred, linked, false);
                    }
                    // The durable inbox must accept this exact observation before any next-row input.
                    await defer(row, token);
                    deferred++;
                    report(GingerChatStage.Skipping, $"Строка {row.Item.Position}: кандидатов после фильтров нет. Сохранил для разбора: {row.Item.Title}");
                    token.ThrowIfCancellationRequested();
                }
                if (row.Item.Position == row.Count)
                {
                    report(GingerChatStage.Completed, $"Достигнут конец открытого прайса. Отложено без кандидатов: {deferred}; уже сопоставлено: {linked}.");
                    return new(row, null, deferred, linked, true);
                }
                report(GingerChatStage.Reading, $"Использую компьютер: проверяю текущую строку и перехожу к строке {row.Item.Position + 1} из {row.Count}.");
                token.ThrowIfCancellationRequested();
                var after = await next(row, token);
                token.ThrowIfCancellationRequested();
                Validate(after);
                if (!SameDocument(row, after) || after.Item.Position != row.Item.Position + 1)
                    throw new InvalidDataException("Следующая строка не подтверждена. Переход не повторяется.");
                row = after;
            }
        }
        catch (OperationCanceledException)
        {
            report(GingerChatStage.Cancelled, "Остановлено. Отложенные позиции сохранены; отправленные действия не повторяются.");
            throw;
        }
        catch (Exception ex)
        {
            report(GingerChatStage.Failed, "Процесс остановлен: " + ex.Message);
            throw;
        }
    }

    private static void Validate(GingerChatScanRow row)
    {
        if (row.Count is < 1 or > 500 || row.Item.Position < 1 || row.Item.Position > row.Count ||
            row.ProcessId <= 0 || row.TableHandle <= 0 || string.IsNullOrWhiteSpace(row.Item.Supplier) ||
            string.IsNullOrWhiteSpace(row.Item.Document) || string.IsNullOrWhiteSpace(row.Item.Title) ||
            string.IsNullOrWhiteSpace(row.ObservationDirectory) || row.Item.Candidates.Length > 256)
            throw new InvalidDataException("Неполное наблюдение строки Ginger; продолжение запрещено.");
    }

    private static bool SameDocument(GingerChatScanRow first, GingerChatScanRow second) =>
        first.ProcessId == second.ProcessId && first.TableHandle == second.TableHandle &&
        first.Count == second.Count && first.Item.Supplier == second.Item.Supplier && first.Item.Document == second.Item.Document;
    private static bool SameItem(GingerChatItem first, GingerChatItem second) =>
        first.Position == second.Position && first.Title == second.Title && first.Linked == second.Linked;
}
