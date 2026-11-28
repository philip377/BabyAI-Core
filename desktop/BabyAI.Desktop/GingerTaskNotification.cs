namespace BabyAI.Desktop;

internal enum GingerNotificationKind
{
    PairReady, ManualReview, AlreadyLinked, Matched, ReadReady, RunFinished,
    Cancelled, Failed, LessonReady
}

internal sealed record GingerTaskNotification(string Title, string Body)
{
    // A model proposal is not a ready confirmation card, and neither is a write.
    internal static GingerNotificationKind ForPreparation(bool alreadyLinked, bool confirmationReady) =>
        alreadyLinked ? GingerNotificationKind.AlreadyLinked : confirmationReady
            ? GingerNotificationKind.PairReady : GingerNotificationKind.ManualReview;

    // A late stop or history failure cannot undo a verified Ginger result.
    internal static GingerNotificationKind ForMatch(bool verified, bool cancelled) =>
        verified ? GingerNotificationKind.Matched : cancelled
            ? GingerNotificationKind.Cancelled : GingerNotificationKind.Failed;

    internal static GingerTaskNotification Create(GingerNotificationKind kind) => kind switch
    {
        GingerNotificationKind.PairReady => new("Ginger: пара готова к подтверждению",
            "Открой UNIX и проверь предложенную пару. Эта пара ещё не сопоставлена."),
        GingerNotificationKind.ManualReview => new("Ginger: нужна ручная проверка",
            "Однозначная пара не подтверждена. Результат и причины доступны в UNIX."),
        GingerNotificationKind.AlreadyLinked => new("Ginger: строка уже сопоставлена",
            "Новое сопоставление не выполнялось. Можно выбрать следующую строку."),
        GingerNotificationKind.Matched => new("Ginger: сопоставление проверено",
            "Результат подтверждён в Ginger. Документ остаётся открытым. Подробности — в UNIX."),
        GingerNotificationKind.ReadReady => new("Ginger: чтение завершено",
            "Данные текущей строки доступны в UNIX. Проверь результат чтения."),
        GingerNotificationKind.RunFinished => new("Ginger: проход завершён",
            "Открой UNIX: результаты и отложенные позиции доступны в журнале прохода."),
        GingerNotificationKind.Cancelled => new("Ginger: работа остановлена",
            "Открой UNIX и проверь итог операции. Уже отправленные действия не повторяются."),
        GingerNotificationKind.Failed => new("Ginger: требуется внимание",
            "Работа остановилась. Причина и результат — в UNIX. Выполненные действия не повторяются."),
        GingerNotificationKind.LessonReady => new("Ginger: объяснение разобрано",
            "Проверь в UNIX, как модель поняла объяснение. Новый урок ещё не сохранён."),
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };
}

internal enum GingerNotificationNavigationAction { Defer, Reveal, SwitchChat }

internal static class GingerNotificationNavigation
{
    internal static GingerNotificationNavigationAction Decide(bool busy, bool gingerRunning,
        bool hasDialogOrApproval, string? currentChatId, string? notificationChatId)
    {
        // Never bring an overlay over the row while the driver is working.
        if (busy || gingerRunning) return GingerNotificationNavigationAction.Defer;
        // Revealing the current chat must preserve its pending one-use confirmation.
        if (hasDialogOrApproval || string.IsNullOrEmpty(notificationChatId) || currentChatId == notificationChatId)
            return GingerNotificationNavigationAction.Reveal;
        return GingerNotificationNavigationAction.SwitchChat;
    }
}
