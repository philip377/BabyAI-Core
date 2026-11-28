using System.Text.Json;

namespace BabyAI.Desktop;

// Immutable evidence of a completed comparison, not a lesson or authorization.
// The complete report is retained, including model and supplier-lesson evidence.
internal static class GingerChatComparisonEvidence
{
    internal const int MaxReportBytes = 2_000_000;

    internal static string Save(string observationDirectory, JsonElement report)
    {
        var directory = RequireDirectory(observationDirectory);
        if (report.ValueKind != JsonValueKind.Object)
            throw Invalid("Нужен полный отчёт сравнения.");
        var snapshot = report.Clone();
        Validate(snapshot);
        using var buffer = new ReportBuffer();
        using (var writer = new Utf8JsonWriter(buffer))
            JsonSerializer.Serialize(writer, snapshot);
        var bytes = buffer.ToArray();

        var name = "comparison-" + Guid.NewGuid().ToString("N");
        var pending = Path.Combine(directory, name + ".pending");
        var completed = Path.Combine(directory, name + ".json");
        var created = false;
        try
        {
            using (var stream = new FileStream(pending, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                created = true;
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            // Recheck the directory before publishing; never replace old evidence.
            RequireDirectory(directory);
            File.Move(pending, completed, overwrite: false);
            created = false;
            return completed;
        }
        finally
        {
            if (created)
            {
                // Only the uniquely named file created by this invocation is ours.
                try { File.Delete(pending); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }

    private static string RequireDirectory(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Path.IsPathFullyQualified(directory))
            throw Invalid("Нужен полный путь к папке свежего наблюдения.");
        var full = Path.GetFullPath(directory);
        for (DirectoryInfo? current = new(full); current is not null; current = current.Parent)
        {
            var attributes = File.GetAttributes(current.FullName);
            if ((attributes & FileAttributes.Directory) == 0 || (attributes & FileAttributes.ReparsePoint) != 0)
                throw Invalid("Папка наблюдения и её родители не должны быть файлами или ссылками.");
        }
        return full;
    }

    private static void Validate(JsonElement report)
    {
        if (report.ValueKind != JsonValueKind.Object)
            throw Invalid("Нужен полный отчёт сравнения.");
        RequireUniqueFields(report, 0);
        var request = Object(report, "request");
        var result = Object(report, "result");
        Text(request, "supplier");
        Text(request, "source");
        if (!request.TryGetProperty("lesson", out var lesson) || lesson.ValueKind != JsonValueKind.String)
            throw Invalid("В отчёте отсутствует исходный текст уроков.");
        if (request.TryGetProperty("mode", out _) || result.TryGetProperty("mode", out _))
            throw Invalid("Проверка исходной строки не является сравнением кандидатов.");
        if (!request.TryGetProperty("candidates", out var candidates) || candidates.ValueKind != JsonValueKind.Array ||
            candidates.GetArrayLength() is < 1 or > 256)
            throw Invalid("Нужен полный непустой список кандидатов, не более 256.");
        foreach (var value in candidates.EnumerateArray())
            if (value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()))
                throw Invalid("Список кандидатов содержит недопустимое название.");
        if (result.TryGetProperty("error", out _) ||
            (result.TryGetProperty("ok", out var ok) && ok.ValueKind != JsonValueKind.True))
            throw Invalid("Ошибка модели не является завершённым сравнением.");
        var expectedCommand = string.IsNullOrWhiteSpace(lesson.GetString()) ? "ginger.recognize.preview" : "ginger.teach.preview";
        if (result.TryGetProperty("command", out var command) &&
            (command.ValueKind != JsonValueKind.String || command.GetString() != expectedCommand))
            throw Invalid("Ответ получен не от ожидаемой проверки кандидатов.");
        if (result.TryGetProperty("id", out var id) &&
            (id.ValueKind != JsonValueKind.Number || !id.TryGetInt64(out var number) || number < 0))
            throw Invalid("Недопустимый идентификатор ответа модели.");
        Text(result, "explanation", 4000);
        Text(result, "understanding", 4000);
        if (!result.TryGetProperty("candidate", out var candidate) ||
            candidate.ValueKind is not (JsonValueKind.Null or JsonValueKind.String))
            throw Invalid("В отчёте отсутствует проверяемый выбор модели.");
        if (candidate.ValueKind == JsonValueKind.String &&
            candidates.EnumerateArray().Count(value => string.Equals(value.GetString(), candidate.GetString(), StringComparison.Ordinal)) != 1)
            throw Invalid("Выбор модели не совпадает с единственным исходным кандидатом.");
    }

    private static JsonElement Object(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Object
            ? value : throw Invalid("В отчёте отсутствует объект " + name + ".");

    private static void Text(JsonElement parent, string name, int maximum = int.MaxValue)
    {
        if (!parent.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(value.GetString()) || value.GetString()!.Length > maximum)
            throw Invalid("Недопустимое текстовое поле " + name + ".");
    }

    private static void RequireUniqueFields(JsonElement value, int depth)
    {
        if (depth > 64) throw Invalid("Слишком сложная структура отчёта.");
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var field in value.EnumerateObject())
            {
                if (!names.Add(field.Name)) throw Invalid("Повторяющееся поле в отчёте: " + field.Name + ".");
                RequireUniqueFields(field.Value, depth + 1);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray()) RequireUniqueFields(item, depth + 1);
    }

    private static InvalidDataException Invalid(string detail) => new("Не удалось сохранить сравнение Ginger. " + detail);

    private sealed class ReportBuffer : MemoryStream
    {
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            RequireSize(buffer.Length);
            base.Write(buffer);
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            RequireSize(count);
            base.Write(buffer, offset, count);
        }

        private void RequireSize(int count)
        {
            if (count > MaxReportBytes - Length)
                throw Invalid("Отчёт сравнения превышает допустимый размер.");
        }
    }
}
