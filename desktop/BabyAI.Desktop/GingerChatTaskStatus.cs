using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace BabyAI.Desktop;

// Completed observations only. A persisted result is never evidence of a live
// process, a current screen, or permission to repeat or confirm an action.
internal enum GingerChatTaskState { Failed, Cancelled, TimedOut, Review, PairReady, Matched, End, AlreadyLinked, Rejected }

internal sealed record GingerChatTaskResult(string ChatId, string RunId,
    DateTimeOffset StartedAt, DateTimeOffset FinishedAt, GingerChatTaskState State,
    int DeferredCount, string Summary);

internal sealed class GingerChatTaskStatusStore
{
    internal const int MaxSummaryLength = 5_000;
    internal const int MaxRecordBytes = 32_768;
    internal const int MaxStoredChats = 1_000;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly string[] Fields = ["version", "chat_id", "run_id", "started_at",
        "finished_at", "state", "deferred_count", "summary"];
    private readonly string _root;

    internal GingerChatTaskStatusStore(string? root = null) => _root = Path.GetFullPath(root ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BabyAI", "ginger-chat-task-status"));

    // Read is independent of chat-history settings and creates no files.
    internal GingerChatTaskResult? Read(string chatId)
    {
        ValidateChatId(chatId);
        if (!EnsureDirectory()) return null;
        var lockPath = Path.Combine(_root, ".write.lock");
        if (!EnsureRegularFile(lockPath))
        {
            if (EnsureRegularFile(ResultPath(chatId))) throw Invalid("Отсутствует файл блокировки результата.");
            return null;
        }
        using var readLock = OpenLock(lockPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        return ReadCore(chatId);
    }

    private GingerChatTaskResult? ReadCore(string chatId)
    {
        var path = ResultPath(chatId);
        if (!EnsureRegularFile(path)) return null;
        try
        {
            // The shared read lock excludes the short replacement interval on
            // Windows, including transient missing-path or sharing responses.
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length is <= 0 or > MaxRecordBytes)
                throw Invalid("Недопустимый размер записи.");
            var bytes = new byte[checked((int)stream.Length)];
            stream.ReadExactly(bytes);
            _ = StrictUtf8.GetCharCount(bytes);
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 2 });
            var value = document.RootElement;
            if (value.ValueKind != JsonValueKind.Object) throw Invalid("Нужен объект результата.");
            var names = value.EnumerateObject().Select(field => field.Name).ToArray();
            if (names.Length != Fields.Length || names.Distinct(StringComparer.Ordinal).Count() != Fields.Length ||
                Fields.Any(field => !names.Contains(field, StringComparer.Ordinal)))
                throw Invalid("Неизвестные, повторённые или отсутствующие поля.");
            if (!value.GetProperty("version").TryGetInt32(out var version) || version != 1 ||
                !value.GetProperty("deferred_count").TryGetInt32(out var count))
                throw Invalid("Недопустимая версия или количество.");
            var result = new GingerChatTaskResult(Text(value, "chat_id"), Text(value, "run_id"),
                Timestamp(value, "started_at"), Timestamp(value, "finished_at"),
                ParseState(Text(value, "state")), count, Text(value, "summary"));
            Validate(result);
            if (!string.Equals(result.ChatId, chatId, StringComparison.Ordinal))
                throw Invalid("Результат относится к другому чату.");
            return result;
        }
        catch (JsonException ex) { throw Invalid("Не удалось прочитать JSON.", ex); }
        catch (DecoderFallbackException ex) { throw Invalid("Повреждён текст записи.", ex); }
        catch (InvalidOperationException ex) { throw Invalid("Недопустимый тип поля.", ex); }
    }

    // A failed save throws; callers must retain the new in-memory outcome or
    // report unavailable storage, never substitute an older successful result.
    internal void Save(GingerChatTaskResult result)
    {
        Validate(result);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new
        {
            version = 1, chat_id = result.ChatId, run_id = result.RunId,
            started_at = result.StartedAt.ToString("O", CultureInfo.InvariantCulture),
            finished_at = result.FinishedAt.ToString("O", CultureInfo.InvariantCulture),
            state = StateName(result.State), deferred_count = result.DeferredCount, summary = result.Summary
        });
        if (bytes.Length > MaxRecordBytes) throw Invalid("Результат слишком велик.");
        if (!EnsureDirectory()) Directory.CreateDirectory(_root);
        EnsureDirectory();
        var lockPath = Path.Combine(_root, ".write.lock");
        EnsureRegularFile(lockPath);
        using var writeLock = OpenLock(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var previous = ReadCore(result.ChatId);
        if (previous is not null)
        {
            if (previous == result) return; // An exact duplicate is harmless.
            if (previous.RunId == result.RunId || previous.StartedAt >= result.StartedAt ||
                previous.FinishedAt > result.StartedAt)
                throw Invalid("Старый, повторённый или пересекающийся запуск не заменяет последний результат.");
        }
        // Bound enumeration as well as storage; leftover staging files also
        // consume this limit instead of accumulating without a bound.
        var entries = 0;
        var maximumEntries = MaxStoredChats + (previous is null ? 0 : 1);
        foreach (var _ in Directory.EnumerateFileSystemEntries(_root))
            if (++entries > maximumEntries) throw Invalid("Хранилище результатов заполнено.");
        var path = ResultPath(result.ChatId);
        var pending = Path.Combine(_root, Guid.NewGuid().ToString("N") + ".pending");
        try
        {
            using (var stream = new FileStream(pending, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            if (EnsureRegularFile(path)) File.Replace(pending, path, destinationBackupFileName: null);
            else File.Move(pending, path, overwrite: false);
        }
        finally
        {
            try { File.Delete(pending); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    internal static void Validate(GingerChatTaskResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        ValidateChatId(result.ChatId);
        if (result.RunId is null || !Guid.TryParseExact(result.RunId, "N", out var run) ||
            run == Guid.Empty || run.ToString("N") != result.RunId)
            throw Invalid("Недопустимый идентификатор запуска.");
        if (result.StartedAt == default || result.FinishedAt == default ||
            result.StartedAt.Offset != TimeSpan.Zero || result.FinishedAt.Offset != TimeSpan.Zero ||
            result.FinishedAt < result.StartedAt)
            throw Invalid("Недопустимое время запуска.");
        if (!Enum.IsDefined(result.State) || result.DeferredCount is < 0 or > 1_000_000)
            throw Invalid("Недопустимое состояние или количество.");
        if (string.IsNullOrWhiteSpace(result.Summary) || result.Summary.Length > MaxSummaryLength ||
            result.Summary.Any(c => char.IsControl(c) && c is not '\r' and not '\n' and not '\t'))
            throw Invalid("Недопустимый итог запуска.");
        try { _ = StrictUtf8.GetByteCount(result.Summary); }
        catch (EncoderFallbackException ex) { throw Invalid("Повреждён текст итога.", ex); }
    }

    private static void ValidateChatId(string chatId)
    {
        if (string.IsNullOrWhiteSpace(chatId) || chatId.Length > 128 ||
            chatId.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '_')))
            throw Invalid("Недопустимый идентификатор чата.");
    }

    private string ResultPath(string chatId) => Path.Combine(_root,
        Convert.ToHexString(SHA256.HashData(StrictUtf8.GetBytes(chatId))) + ".json");

    private bool EnsureDirectory()
    {
        FileAttributes attributes;
        try { attributes = File.GetAttributes(_root); }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
        if ((attributes & FileAttributes.Directory) == 0 || (attributes & FileAttributes.ReparsePoint) != 0)
            throw Invalid("Хранилище не является обычной папкой.");
        return true;
    }

    private static bool EnsureRegularFile(string path)
    {
        FileAttributes attributes;
        try { attributes = File.GetAttributes(path); }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
        if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
            throw Invalid("Вместо записи обнаружена папка или ссылка.");
        return true;
    }

    private static FileStream OpenLock(string path, FileMode mode, FileAccess access, FileShare share)
    {
        // Coordinate only local result I/O, for at most 100 ms. Persistent file
        // contention remains an explicit error; no driver action is retried.
        for (var attempt = 0; ; attempt++)
        {
            try { return new FileStream(path, mode, access, share); }
            catch (IOException ex) when (attempt < 20 && (ex.HResult & 0xffff) is 32 or 33)
            {
                Thread.Sleep(5);
            }
        }
    }

    private static string Text(JsonElement value, string field) => value.GetProperty(field).ValueKind == JsonValueKind.String
        ? value.GetProperty(field).GetString()! : throw Invalid("Недопустимый текст поля " + field + ".");

    private static DateTimeOffset Timestamp(JsonElement value, string field) =>
        DateTimeOffset.TryParseExact(Text(value, field), "O", CultureInfo.InvariantCulture, DateTimeStyles.None, out var time)
            ? time : throw Invalid("Недопустимое время поля " + field + ".");

    private static string StateName(GingerChatTaskState state) => state switch
    {
        GingerChatTaskState.Failed => "failed", GingerChatTaskState.Cancelled => "cancelled",
        GingerChatTaskState.TimedOut => "timedout", GingerChatTaskState.Review => "review",
        GingerChatTaskState.PairReady => "pairready", GingerChatTaskState.Matched => "matched",
        GingerChatTaskState.End => "end", GingerChatTaskState.AlreadyLinked => "alreadylinked",
        GingerChatTaskState.Rejected => "rejected", _ => throw Invalid("Неизвестное состояние.")
    };

    private static GingerChatTaskState ParseState(string state) => state switch
    {
        "failed" => GingerChatTaskState.Failed, "cancelled" => GingerChatTaskState.Cancelled,
        "timedout" => GingerChatTaskState.TimedOut, "review" => GingerChatTaskState.Review,
        "pairready" => GingerChatTaskState.PairReady, "matched" => GingerChatTaskState.Matched,
        "end" => GingerChatTaskState.End, "alreadylinked" => GingerChatTaskState.AlreadyLinked,
        "rejected" => GingerChatTaskState.Rejected, _ => throw Invalid("Неизвестное состояние.")
    };

    private static InvalidDataException Invalid(string detail, Exception? inner = null) =>
        new("Не удалось прочитать или сохранить результат Ginger. " + detail, inner);
}

internal static class GingerChatTaskStatusFormatter
{
    internal const string Missing = "Нет сохранённого результата Ginger в этом чате. Новая проверка не запускалась.";

    internal static string Format(GingerChatTaskResult? result)
    {
        if (result is null) return Missing;
        GingerChatTaskStatusStore.Validate(result);
        var outcome = result.State switch
        {
            GingerChatTaskState.Failed => "Последняя попытка остановилась из-за ошибки. Результат уже выполненных действий нужно проверить в журнале.",
            GingerChatTaskState.Cancelled => "Последняя попытка была остановлена.",
            GingerChatTaskState.TimedOut => "В последней попытке истекло время ожидания шага. Автоматического повтора нет.",
            GingerChatTaskState.Review => "Результат последней попытки требует ручной проверки.",
            GingerChatTaskState.PairReady => "В последней попытке была подготовлена пара. Это ещё не подтверждённое сопоставление. Для привязки нужна действующая карточка подтверждения.",
            GingerChatTaskState.Matched => "В последней попытке подтверждено сопоставление одной пары.",
            GingerChatTaskState.End => "Последний проход дошёл до конца прайса. Это не означает, что все позиции сопоставлены.",
            GingerChatTaskState.AlreadyLinked => "При последней проверке строка уже имела привязку. Новое сопоставление не выполнялось.",
            GingerChatTaskState.Rejected => "Предложенная пара была отклонена. Её сопоставление не выполнялось.",
            _ => throw new InvalidDataException("Неизвестный результат Ginger.")
        };
        var count = result.DeferredCount == 0 ? "Новых отложенных позиций в этом результате нет."
            : $"Зафиксировано отложенных позиций: {result.DeferredCount.ToString(CultureInfo.InvariantCulture)}.";
        // Summary is retained for diagnostics, never interpreted or echoed as
        // status: it can contain model prose and cannot override the typed state.
        return $"Последний известный результат Ginger ({result.FinishedAt.ToString("dd.MM.yyyy HH:mm:ss 'UTC'", CultureInfo.InvariantCulture)}).\n" +
            outcome + " " + count + " Новая проверка Ginger не запускалась.";
    }
}
