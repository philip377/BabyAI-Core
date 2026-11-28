using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BabyAI.Desktop;

internal sealed record GingerDeferredRow(string Id, string ChatId, string Supplier, string Document,
    int Position, string Title, string Reason, string ObservationDirectory, DateTimeOffset CreatedAt);

/// <summary>Local reminders only: these records grant no permission and contain no learned decisions.</summary>
internal sealed class GingerDeferredRows
{
    internal const int MaxRows = 10_000;
    internal const int MaxEntryBytes = 65_536;
    internal const string NoCandidatesReason = "Кандидатов после подготовки фильтров нет. Нужна ручная проверка.";
    internal const string ManualReviewReason = "Однозначный кандидат не подтверждён. Нужна ручная проверка.";
    internal const string SearchReviewPrefix = "Поиск требует объяснения: ";
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly string[] Fields = ["Id", "ChatId", "Supplier", "Document", "Position", "Title",
        "Reason", "ObservationDirectory", "CreatedAt"];
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true,
        MaxDepth = 4,
    };
    private readonly string _root;

    internal GingerDeferredRows(string? root = null) => _root = Path.GetFullPath(root ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BabyAI", "ginger-deferred-rows"));

    internal GingerDeferredRow Add(GingerChatItem item, string chatId, string observationDirectory)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (item.Linked || item.Candidates is null || item.Candidates.Length != 0)
            throw new InvalidDataException("Отложить можно только несопоставленную строку без кандидатов.");
        return Append(item, chatId, observationDirectory, NoCandidatesReason);
    }

    internal GingerDeferredRow AddManualReview(GingerChatItem item, string chatId, string observationDirectory)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (item.Linked || item.Candidates is null || item.Candidates.Length is < 1 or > 256 ||
            item.Candidates.Any(string.IsNullOrWhiteSpace))
            throw new InvalidDataException("Для ручного разбора нужна несопоставленная строка с непустым списком до 256 кандидатов.");
        return Append(item, chatId, observationDirectory, ManualReviewReason);
    }

    internal GingerDeferredRow AddSearchReview(GingerChatItem item, string chatId, string evidenceDirectory,
        string reason, long candidateCount)
    {
        ArgumentNullException.ThrowIfNull(item);
        ValidateText(reason, 600, "причина поиска");
        if (item.Linked || item.Candidates is null || candidateCount < 0 ||
            (candidateCount <= 256 ? item.Candidates.Length != candidateCount : item.Candidates.Length != 0) ||
            item.Candidates.Any(string.IsNullOrWhiteSpace))
            throw new InvalidDataException("Для разбора поиска нужна проверенная несопоставленная строка и явное число кандидатов.");
        // This directory contains the original request, result and lesson versions.
        // Overflow is a search problem, not an assertion that the catalog is empty.
        return Append(item, chatId, evidenceDirectory, SearchReviewPrefix + reason);
    }

    private GingerDeferredRow Append(GingerChatItem item, string chatId, string observationDirectory, string reason)
    {
        ValidateIdentity(item.Supplier, item.Document, item.Position, item.Title);
        ValidateChatId(chatId);
        ValidateObservationDirectory(observationDirectory);
        var row = new GingerDeferredRow(CreateId(item.Supplier, item.Document, item.Position, item.Title),
            chatId, item.Supplier, item.Document, item.Position, item.Title, reason,
            observationDirectory, DateTimeOffset.UtcNow);

        Directory.CreateDirectory(_root);
        EnsureDirectory();
        using var writeLock = Lock();
        // Never let a damaged queue disappear behind a successful append or a chat filter.
        var current = ReadAll();
        var previous = current.FirstOrDefault(entry => entry.Id == row.Id);
        if (previous is not null)
        {
            if (previous.Supplier != row.Supplier || previous.Document != row.Document ||
                previous.Position != row.Position || previous.Title != row.Title)
                throw new InvalidDataException("Коллизия идентификатора отложенной строки. Список не изменён.");
            return previous;
        }
        if (current.Count >= MaxRows)
            throw new InvalidDataException("Список отложенных строк заполнен. Строка не сохранена.");

        var bytes = JsonSerializer.SerializeToUtf8Bytes(row, JsonOptions);
        if (bytes.Length > MaxEntryBytes) throw new InvalidDataException("Запись отложенной строки слишком велика.");
        var path = Path.Combine(_root, row.Id + ".json");
        var pending = Path.Combine(_root, row.Id + "." + Guid.NewGuid().ToString("N") + ".pending");
        try
        {
            using (var stream = new FileStream(pending, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            // The same-directory rename is atomic and must never overwrite an existing entry.
            File.Move(pending, path, overwrite: false);
        }
        finally
        {
            try { File.Delete(pending); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return row;
    }

    internal IReadOnlyList<GingerDeferredRow> Read(string? chatId = null)
    {
        if (chatId is not null) ValidateChatId(chatId);
        if (!EnsureDirectory()) return [];
        using var readLock = Lock();
        var rows = ReadAll();
        return rows.Where(row => chatId is null || row.ChatId == chatId)
            .OrderBy(row => row.CreatedAt).ThenBy(row => row.Id, StringComparer.Ordinal).ToArray();
    }

    private FileStream Lock() => new(Path.Combine(_root, ".write.lock"),
        FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

    private bool EnsureDirectory()
    {
        FileAttributes attributes;
        try { attributes = File.GetAttributes(_root); }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
        if ((attributes & FileAttributes.Directory) == 0 || (attributes & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Путь списка отложенных строк не является обычной папкой.");
        return true;
    }

    private List<GingerDeferredRow> ReadAll()
    {
        var paths = new List<string>();
        var entryCount = 0;
        foreach (var path in Directory.EnumerateFileSystemEntries(_root))
        {
            if (++entryCount > MaxRows * 2 + 1)
                throw new InvalidDataException("Слишком много файлов в списке отложенных строк.");
            var name = Path.GetFileName(path);
            if (name == ".write.lock") continue;
            // A crash before rename leaves an uncommitted staging file, never a saved row.
            if (IsPendingName(name)) continue;
            if (name.Length != 69 || !name.EndsWith(".json", StringComparison.Ordinal) || !IsId(name[..64]))
                throw new InvalidDataException("Неизвестный файл в списке отложенных строк.");
            paths.Add(path);
            if (paths.Count > MaxRows)
                throw new InvalidDataException("Список отложенных строк превышает допустимое количество записей.");
        }

        var result = new List<GingerDeferredRow>(paths.Count);
        foreach (var path in paths)
        {
            if ((File.GetAttributes(path) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
                throw new InvalidDataException("Вместо записи отложенной строки обнаружена ссылка или папка.");
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length <= 0 || stream.Length > MaxEntryBytes)
                throw new InvalidDataException("Недопустимый размер записи отложенной строки.");
            try
            {
                using var document = JsonDocument.Parse(stream, new JsonDocumentOptions { MaxDepth = 4 });
                if (document.RootElement.ValueKind != JsonValueKind.Object)
                    throw new InvalidDataException("Повреждена запись отложенной строки.");
                var names = document.RootElement.EnumerateObject().Select(property => property.Name).ToArray();
                if (names.Length != Fields.Length || names.Distinct(StringComparer.Ordinal).Count() != Fields.Length ||
                    Fields.Any(field => !names.Contains(field, StringComparer.Ordinal)))
                    throw new InvalidDataException("Повреждены поля записи отложенной строки.");
                var row = document.RootElement.Deserialize<GingerDeferredRow>(JsonOptions)
                    ?? throw new InvalidDataException("Пустая запись отложенной строки.");
                ValidateIdentity(row.Supplier, row.Document, row.Position, row.Title);
                ValidateChatId(row.ChatId);
                ValidateObservationDirectory(row.ObservationDirectory);
                if (!IsId(row.Id) || row.Id != Path.GetFileNameWithoutExtension(path) ||
                    row.Id != CreateId(row.Supplier, row.Document, row.Position, row.Title) ||
                    !ValidReason(row.Reason) ||
                    row.CreatedAt == default || row.CreatedAt.Offset != TimeSpan.Zero)
                    throw new InvalidDataException("Не совпадает содержимое записи отложенной строки.");
                result.Add(row);
            }
            catch (JsonException ex) { throw new InvalidDataException("Не удалось прочитать запись отложенной строки.", ex); }
        }
        return result;
    }

    private static bool IsPendingName(string name) => name.Length == 105 && name[64] == '.' &&
        name.EndsWith(".pending", StringComparison.Ordinal) && IsId(name[..64]) &&
        Guid.TryParseExact(name.Substring(65, 32), "N", out _);

    private static bool ValidReason(string? reason)
    {
        if (reason is NoCandidatesReason or ManualReviewReason) return true;
        if (reason is null || !reason.StartsWith(SearchReviewPrefix, StringComparison.Ordinal)) return false;
        var detail = reason[SearchReviewPrefix.Length..];
        ValidateText(detail, 600, "причина поиска");
        return true;
    }

    private static bool IsId(string? value) => value is { Length: 64 } &&
        value.All(character => character is >= '0' and <= '9' or >= 'A' and <= 'F');

    private static string CreateId(string supplier, string document, int position, string title)
    {
        using var bytes = new MemoryStream();
        using (var writer = new BinaryWriter(bytes, StrictUtf8, leaveOpen: true))
        {
            // Length-prefixed strings keep field boundaries unambiguous; preserve the full title.
            writer.Write(supplier);
            writer.Write(document);
            writer.Write(position);
            writer.Write(title);
        }
        return Convert.ToHexString(SHA256.HashData(bytes.ToArray()));
    }

    private static void ValidateIdentity(string supplier, string document, int position, string title)
    {
        ValidateText(supplier, 512, "поставщик");
        ValidateText(document, 512, "документ");
        ValidateText(title, 4096, "название");
        if (position is < 1 or > 1_000_000)
            throw new InvalidDataException("Недопустимый номер отложенной строки.");
    }

    private static void ValidateChatId(string value)
    {
        ValidateText(value, 128, "чат");
        if (value.Any(character => !(char.IsAsciiLetterOrDigit(character) || character is '-' or '_')))
            throw new InvalidDataException("Недопустимый идентификатор чата отложенной строки.");
    }

    private static void ValidateObservationDirectory(string value)
    {
        ValidateText(value, 2048, "папка наблюдения");
        if (!Path.IsPathFullyQualified(value) || value.Any(char.IsControl))
            throw new InvalidDataException("Нужен полный путь папки наблюдения отложенной строки.");
    }

    private static void ValidateText(string value, int maximum, string field)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximum || value.Contains('\0'))
            throw new InvalidDataException("Недопустимое поле отложенной строки: " + field + ".");
        try { _ = StrictUtf8.GetByteCount(value); }
        catch (EncoderFallbackException ex) { throw new InvalidDataException("Повреждён текст отложенной строки: " + field + ".", ex); }
    }
}
