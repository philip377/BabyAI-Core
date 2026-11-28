using System.Text.Json;

namespace BabyAI.Desktop;

// A model recommendation is bound to one observation, chat run and complete
// lesson revision set. It grants neither navigation nor matching permission.
internal sealed class GingerSearchWordPlanner(
    Func<JsonElement, CancellationToken, Task<JsonElement>> consult,
    Func<string, IReadOnlyList<GingerLearning.LanguageLesson>> readLessons,
    Func<CancellationToken, Task> requireCurrentRun,
    string chatId, string runId)
{
    internal async Task<GingerSearchWordDecision> ChooseAsync(GingerDesktopObservation observed,
        IReadOnlyList<GingerSearchFilterEffect> history, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        await requireCurrentRun(token);
        token.ThrowIfCancellationRequested();
        var lessons = readLessons(observed.Supplier).ToArray();
        var requestId = Guid.NewGuid().ToString("N");
        var words = observed.Words.EnumerateArray().Select(row => new {
            word = row.GetProperty("Title").GetString(),
            search = row.GetProperty("DescriptionText").GetString(),
            @checked = row.GetProperty("Checked").ValueKind == JsonValueKind.Null
                ? (bool?)null : row.GetProperty("Checked").GetBoolean()
        }).ToArray();
        if (words.Length is < 1 or > 64 || history.Count > 4)
            throw new InvalidDataException("Слишком много слов или шагов для проверяемого выбора Qwen. Нужна ручная проверка.");
        var request = JsonSerializer.SerializeToElement(new {
            request_id = requestId, supplier = observed.Supplier, source = observed.Title,
            lesson = GingerLearning.ComposeLanguageLessons(lessons), words,
            candidate_count = observed.CandidateCount,
            history = history.Select(effect => new {
                word = effect.Word, before_count = effect.BeforeCount, after_count = effect.AfterCount
            }).ToArray()
        });
        // The worker independently checks the complete input size before inference.
        var result = await consult(request, token);
        token.ThrowIfCancellationRequested();
        async Task RequireCurrent(CancellationToken currentToken)
        {
            currentToken.ThrowIfCancellationRequested();
            await requireCurrentRun(currentToken);
            currentToken.ThrowIfCancellationRequested();
            if (!readLessons(observed.Supplier).SequenceEqual(lessons))
                throw new InvalidDataException("Уроки поставщика изменились во время выбора звёздочки. Ответ Qwen не применяется.");
        }
        await RequireCurrent(token);
        var decision = ReadDecision(result, requestId, observed, history);
        SaveEvidence(observed.Directory, JsonSerializer.SerializeToElement(new {
            chat_id = chatId, run_id = runId, observation_directory = observed.Directory,
            recorded_at = DateTimeOffset.UtcNow, lessons, request, result
        }), requestId);
        await RequireCurrent(token);
        return decision with { RequireCurrent = RequireCurrent };
    }

    private static GingerSearchWordDecision ReadDecision(JsonElement result, string requestId,
        GingerDesktopObservation observed, IReadOnlyList<GingerSearchFilterEffect> history)
    {
        GingerDesktopObservation.Object(result);
        var required = new HashSet<string>(StringComparer.Ordinal) {
            "ok", "command", "mode", "request_id", "word", "reason", "can_match", "search_evidence"
        };
        foreach (var field in result.EnumerateObject())
        {
            if (field.Name == "id")
            {
                if (!field.Value.TryGetInt64(out var id) || id < 0) throw Invalid();
            }
            else if (!required.Remove(field.Name)) throw Invalid();
        }
        if (required.Count != 0 || result.GetProperty("ok").ValueKind != JsonValueKind.True ||
            result.GetProperty("can_match").ValueKind != JsonValueKind.False ||
            GingerDesktopObservation.Text(result, "command", 64) != "ginger.search.word.preview" ||
            GingerDesktopObservation.Text(result, "mode", 32) != "search_word" ||
            GingerDesktopObservation.Text(result, "request_id", 32) != requestId) throw Invalid();
        var reason = GingerDesktopObservation.Text(result, "reason", 600);
        var word = NullableText(result, "word", 512);
        RequireEvidence(result.GetProperty("search_evidence"), requestId, word, reason, observed.Title);
        if (word is null) return new(null, reason);
        GingerDesktopFilterFlow.RequireDisabledWord(observed, word);
        if (history.Any(effect => effect.Word == word)) throw Invalid();
        return new(word, reason);
    }

    // The host's literal citation is separate from the model's role estimate and
    // raw explanation. Neither a citation nor a veto grants action authority.
    private static void RequireEvidence(JsonElement evidence, string requestId, string? word, string reason, string source)
    {
        RequireFields(evidence, "version", "definitions", "proposal", "source_evidence", "blocked_reason");
        var version = GingerDesktopObservation.Field(evidence, "version", JsonValueKind.Number);
        if (!version.TryGetInt32(out var number) || number != 2) throw Invalid();
        var definitions = GingerDesktopObservation.Field(evidence, "definitions", JsonValueKind.Array);
        if (definitions.GetArrayLength() > 24) throw Invalid();
        foreach (var definition in definitions.EnumerateArray())
        {
            RequireFields(definition, "term", "role", "quote");
            GingerDesktopObservation.Text(definition, "term", 200);
            Role(definition);
            GingerDesktopObservation.Text(definition, "quote", 1000);
        }

        var blockedReason = NullableText(evidence, "blocked_reason", 600);
        var proposal = evidence.GetProperty("proposal");
        var sourceEvidence = evidence.GetProperty("source_evidence");
        if (proposal.ValueKind == JsonValueKind.Null)
        {
            if (word is not null || blockedReason is null || reason != blockedReason ||
                sourceEvidence.ValueKind != JsonValueKind.Null) throw Invalid();
            return;
        }
        RequireFields(proposal, "request_id", "word", "role", "reason");
        if (GingerDesktopObservation.Text(proposal, "request_id", 32) != requestId) throw Invalid();
        var proposedWord = NullableText(proposal, "word", 512);
        var proposedRole = Role(proposal);
        GingerDesktopObservation.Text(proposal, "reason", 600);
        if (proposedWord is null)
        {
            if (proposedRole != "unknown" || sourceEvidence.ValueKind != JsonValueKind.Null) throw Invalid();
        }
        else
        {
            RequireFields(sourceEvidence, "origin", "derived_by", "quote");
            var quote = GingerDesktopObservation.Text(sourceEvidence, "quote", 512);
            if (GingerDesktopObservation.Text(sourceEvidence, "origin", 32) != "observed_source" ||
                GingerDesktopObservation.Text(sourceEvidence, "derived_by", 32) != "host" ||
                !source.Contains(quote, StringComparison.Ordinal) ||
                !quote.Equals(proposedWord, StringComparison.OrdinalIgnoreCase)) throw Invalid();
        }
        if (blockedReason is not null)
        {
            if (word is not null || reason != blockedReason) throw Invalid();
            return;
        }
        // A model abstention must use the host's blocked reason. For a successful
        // choice the host reason intentionally differs from the raw model reason.
        if (word is null || word != proposedWord || proposedRole is not ("brand" or "name" or "variant")) throw Invalid();
    }

    private static void RequireFields(JsonElement value, params string[] names)
    {
        GingerDesktopObservation.Object(value);
        var required = new HashSet<string>(names, StringComparer.Ordinal);
        foreach (var field in value.EnumerateObject())
            if (!required.Remove(field.Name)) throw Invalid();
        if (required.Count != 0) throw Invalid();
    }

    private static string? NullableText(JsonElement parent, string name, int limit)
        => parent.GetProperty(name).ValueKind == JsonValueKind.Null
            ? null : GingerDesktopObservation.Text(parent, name, limit);

    private static string Role(JsonElement parent)
    {
        var role = GingerDesktopObservation.Text(parent, "role", 32);
        if (role is not ("brand" or "name" or "variant" or "packaging" or "service" or
            "concentration" or "volume" or "tester" or "gender" or "unknown")) throw Invalid();
        return role;
    }

    private static InvalidDataException Invalid() => new("Ответ Qwen о звёздочке не подтверждён. Поиск остановлен без подстановки другого слова.");

    private static void SaveEvidence(string directory, JsonElement report, string requestId)
    {
        if (!Path.IsPathFullyQualified(directory)) throw new InvalidDataException("Нет папки наблюдения поиска.");
        var full = Path.GetFullPath(directory);
        for (DirectoryInfo? current = new(full); current is not null; current = current.Parent)
            if ((File.GetAttributes(current.FullName) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Папка отчёта поиска не должна быть ссылкой.");
        var bytes = JsonSerializer.SerializeToUtf8Bytes(report);
        if (bytes.Length > 500_000) throw new InvalidDataException("Отчёт выбора слова превышает допустимый размер.");
        var pending = Path.Combine(full, "search-decision-" + requestId + ".pending");
        var completed = Path.ChangeExtension(pending, ".json");
        // A failed write leaves only a diagnostic .pending file and prevents input.
        using (var stream = new FileStream(pending, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            stream.Write(bytes);
            stream.Flush(flushToDisk: true);
        }
        File.Move(pending, completed, overwrite: false);
    }
}
