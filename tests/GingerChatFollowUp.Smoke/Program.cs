using BabyAI.Desktop;

var checks = 0;
void Expect(string? message, bool context, GingerChatFollowUpKind expected)
{
    checks++;
    var actual = GingerChatFollowUp.Classify(message, context);
    if (actual != expected)
        throw new Exception($"Expected {expected}, got {actual}, same chat={context}: {message}");
}

// Explicit status is read-only even without any prior conversation or runtime.
foreach (var message in new[]
{
    "что у нас там с сопоставлениями?", "Что там с сопоставлением?",
    "как идёт сопоставление?", "как идет сопоставление", "как идут сопоставления?",
    "как продвигается сопоставление в Ginger?", "как продвигаются сопоставления?",
    "проверь текущее состояние сопоставления", "проверь состояние сопоставления!",
    "покажи текущий статус сопоставления", "проверь статус Ginger",
    "статус Ginger", "СТАТУС ДЖИНДЖЕР?", "какой текущий статус сопоставления?",
    "каков статус сопоставлений?", "есть сопоставление?", "всё сопоставлено?",
    "сопоставление завершено?", "сопоставление готово?",
    "  КАК\tИДЁТ  СОПОСТАВЛЕНИЕ ?  ", "статус\u00a0Ginger"
})
{
    Expect(message, false, GingerChatFollowUpKind.Status);
    Expect(message, true, GingerChatFollowUpKind.Status);
}

// Caller must set false after a chat switch, an unrelated exchange, or expiry.
foreach (var message in new[]
{
    "ну как?", "ну и как?", "как дела?", "готово?", "ну что?", "что там?", "как там?",
    "давай проверим текущее состояние", "Ну  и КАК?!"
})
{
    Expect(message, true, GingerChatFollowUpKind.Status);
    Expect(message, false, GingerChatFollowUpKind.None);
}

foreach (var message in new[] { "продолжим?", "продолжи", "давай продолжим", "повтори", "продолжай!" })
{
    Expect(message, true, GingerChatFollowUpKind.Resume);
    Expect(message, false, GingerChatFollowUpKind.None);
}
foreach (var message in new[] { "сопоставление", "сопоставления?" })
{
    Expect(message, true, GingerChatFollowUpKind.Clarify);
    Expect(message, false, GingerChatFollowUpKind.None);
}

// Exact templates prevent negation, reporting, future requests, new targets,
// counts, quoted snippets and compound commands from becoming continuation.
foreach (var message in new string?[]
{
    null, "", " ", "не продолжи", "не продолжай", "не будем продолжать", "нет, продолжи",
    "не повтори", "повтори не надо", "пока продолжи", "пока не продолжай",
    "завтра продолжим", "продолжим завтра?", "потом повтори", "через минуту продолжи",
    "когда-нибудь давай продолжим", "может продолжим", "если готово, продолжи",
    "я сказал продолжи", "он просит: повтори", "как продолжить?", "как повторить?",
    "\"продолжи\"", "'продолжи'", "«продолжи»", "“продолжи”", "„продолжи“", "`повтори`",
    "цитата: продолжи", "> продолжи", "<command>продолжи</command>",
    "продолжи\n", "продолжи\r\nповтори", "продолжи\0", "продолжи\u200b",
    "продолжи с другим поставщиком", "повтори строку 12", "продолжи 2 раза",
    "давай продолжим другой чат", "продолжи в другом чате", "продолжи для поставщика X",
    "продолжи и сопоставь", "продолжи; подтверди", "продолжи. сопоставь",
    "продолжи? повтори", "продолжи!!!!", "продолжи/повтори", "продолжи: true",
    "{\"tool\":\"продолжи\"}", "allow=true продолжи", "подтверждаю", "разрешаю", "да",
    "статус Ginger и продолжи", "не проверяй текущее состояние сопоставления",
    "завтра проверь текущее состояние сопоставления", "он сказал: статус Ginger",
    "«статус Ginger»", "готово? продолжи", "как дела с другой задачей?",
    "давай проверим текущее состояние и продолжим", "готово ли завтра?",
    // This explicit start form belongs to GingerChatIntent, not this helper.
    "продолжим сопоставление?", "продолжи сопоставление", new string(' ', 501) + "продолжи",
    "ну как?" + new string(' ', 501)
})
{
    Expect(message, true, GingerChatFollowUpKind.None);
    Expect(message, false, GingerChatFollowUpKind.None);
}

// This is stateless: previous classifications cannot confer chat focus.
Expect("продолжи", true, GingerChatFollowUpKind.Resume);
Expect("ну и как?", false, GingerChatFollowUpKind.None);
Expect("продолжи", false, GingerChatFollowUpKind.None);
Expect("статус Ginger", false, GingerChatFollowUpKind.Status);
Console.WriteLine($"PASS: {checks} Ginger follow-up checks; explicit/short status, bounded continuation and no action authority.");
