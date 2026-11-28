using System.Globalization;

namespace BabyAI.Desktop;

// The caller supplies the actual cancellation cause and the number of durable
// reminders from this run. Elapsed time and display text are not evidence.
internal static class GingerChatOutcomeText
{
    internal static string Deferred(int durableCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(durableCount);
        return durableCount == 0 ? "Новых отложенных позиций нет."
            : $"В этом проходе сохранено для разбора: {durableCount.ToString(CultureInfo.InvariantCulture)}.";
    }

    internal static string Cancelled(bool userRequested, int durableCount) =>
        (userRequested ? "Работа остановлена пользователем. " : "Работа прервана до завершения. ") + Deferred(durableCount);

    internal static string TimedOut(int durableCount) =>
        "Время ожидания текущего шага истекло. " + Deferred(durableCount) + " Автоматического повтора нет.";

    internal static string Failed(int durableCount) =>
        "Работа остановлена из-за ошибки. " + Deferred(durableCount) + " Подробности — в журнале.";
}
