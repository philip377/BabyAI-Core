namespace BabyAI.Desktop;

internal enum GingerChatStage { Started, Reading, Filtering, Comparing, Review, Empty, Cancelled, Failed, Skipping, Completed }
internal sealed record GingerChatItem(string Supplier, string Document, int Position, string Title, bool Linked, string[] Candidates);

// One preparation budget for chat comparison, including supplier lessons and revisits.
// The reader's larger bound is not permission to truncate or compare a broad list.
internal static class GingerChatPreparation
{
    internal const int MaxCandidates = 10;

    internal static bool NeedsFilters(int count) => count == 0 || count > MaxCandidates;

    internal static void RequireCandidateLimit(int count)
    {
        if (count > MaxCandidates)
            throw new InvalidDataException($"После подготовки поиска в Ginger осталось {count} кандидатов. " +
                $"Для сравнения в чате нужно не более {MaxCandidates}. Сузьте поиск вручную и повторите разбор этой строки. " +
                "Строка не отложена, переход к следующей не выполнен.");
    }
}

internal sealed class GingerChatFlow(
    Func<CancellationToken, Task<GingerChatItem>> read,
    Func<CancellationToken, Task<GingerChatItem>> filter,
    Func<GingerChatItem, CancellationToken, Task<string>> compare,
    Action<GingerChatStage, string> report, bool reportFailures = true)
{
    public async Task<string> RunAsync(CancellationToken token)
    {
        try
        {
            token.ThrowIfCancellationRequested();
            report(GingerChatStage.Started, "Активирован агент Ginger. Задание: подготовить сопоставление текущей строки.");
            report(GingerChatStage.Reading, "Использую компьютер: открываю панель сопоставления и читаю текущий прайс.");
            var item = await read(token);
            token.ThrowIfCancellationRequested();
            if (item.Linked)
            {
                report(GingerChatStage.Review, "Строка уже сопоставлена. Повторная привязка не выполняется.");
                return $"{item.Supplier} · строка {item.Position}\n{item.Title}\nУже сопоставлено. Выбери в Ginger следующую нужную строку и дай новое задание.";
            }
            if (GingerChatPreparation.NeedsFilters(item.Candidates.Length))
            {
                report(GingerChatStage.Filtering, "Использую компьютер: проверяю звёздочки слов слева и перечитываю кандидатов.");
                var filtered = await filter(token);
                token.ThrowIfCancellationRequested();
                if (filtered.Supplier != item.Supplier || filtered.Document != item.Document ||
                    filtered.Position != item.Position || filtered.Title != item.Title || filtered.Linked != item.Linked)
                    throw new InvalidDataException("Товар изменился во время подготовки поиска. Нужно новое чтение.");
                item = filtered;
            }
            GingerChatPreparation.RequireCandidateLimit(item.Candidates.Length);
            if (item.Candidates.Length == 0)
            {
                report(GingerChatStage.Empty, "Подготовка завершена: кандидатов нет. Нужна ручная проверка поиска.");
                return $"{item.Title}\nКандидатов после подготовки фильтров нет. Модель не запрашивалась, сопоставление не выполнялось.";
            }
            report(GingerChatStage.Comparing, $"Сравниваю кандидатов: {item.Candidates.Length}. Компьютером сейчас не управляю.");
            var proposal = await compare(item, token);
            token.ThrowIfCancellationRequested();
            report(GingerChatStage.Review, "Предложение получено. Ожидается проверка пары; сопоставление не выполнялось.");
            return $"{item.Supplier} · документ {item.Document} · строка {item.Position}\n{item.Title}\n{proposal}\nЭто предложение. Привязка товара ещё не выполнена.";
        }
        catch (OperationCanceledException)
        {
            report(GingerChatStage.Cancelled, "Остановлено. Отправленные действия не повторяются; следующее задание начнётся со свежего чтения.");
            throw;
        }
        catch (Exception ex)
        {
            // An owning chat handler may distinguish manual review from errors.
            // Let that handler publish the outcome once, after classification.
            if (reportFailures) report(GingerChatStage.Failed, "Процесс остановлен: " + ex.Message);
            throw;
        }
    }
}
