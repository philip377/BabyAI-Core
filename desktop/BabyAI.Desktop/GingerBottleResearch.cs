using System.Text.Json;
using System.Text.RegularExpressions;

namespace BabyAI.Desktop;

internal sealed record BottleSource(string Id, Uri Url, string Title, Uri? ImageUrl, byte[]? Image);
internal sealed record BottleExcerpt(string SourceId, string Field, string Quote);
internal sealed record BottleResearchResult(string Query, string CheckedAt, string Status,
    IReadOnlyList<BottleSource> Sources, IReadOnlyList<BottleExcerpt> Excerpts, IReadOnlyList<string> Warnings);

internal static class GingerBottleResearch
{
    // A narrow public-search request is not a desktop action. Quoted/reported
    // instructions and general perfume conversation do not start a search.
    public static bool TryReadCommand(string message, out string query)
    {
        query = "";
        if (message.Length > 320 || message.IndexOfAny(['\n', '\r', '\0', '«', '»', '"', '`']) >= 0) return false;
        if (Regex.IsMatch(message, @"\b(?:не|нельзя|если|когда|потом|завтра|позже|отмени|сопоставь|проведи|сохрани|удали)\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)) return false;
        var match = Regex.Match(message.Trim(),
            @"\A(?:(?:unix|юникс)[,\s]+)?(?:пожалуйста[,\s]+)?(?:найди|поищи|покажи|проверь)\s+(?:фото\s+)?флакон(?:а|ы|ов)?(?:\s+в\s+интернете)?(?:\s+(?<query>.+?))?[.!?]*\z",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!match.Success) return false;
        query = match.Groups["query"].Value.Trim().TrimEnd('.', '!', '?');
        query = Regex.Replace(query, @"\s+в\s+интернете\z", "", RegexOptions.IgnoreCase).Trim();
        if (Regex.IsMatch(query, @"\A(?:этого товара|текущего товара|текущей позиции|для проверки)\z", RegexOptions.IgnoreCase))
            query = "";
        return true;
    }

    public static Uri PublicLink(string value)
    {
        if (value.Length > 2048 || !Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || uri.Scheme != "https" || uri.Port != 443 || uri.UserInfo.Length != 0
            || uri.IsLoopback || uri.HostNameType != UriHostNameType.Dns || !uri.Host.Contains('.') || uri.Host.EndsWith(".local", StringComparison.OrdinalIgnoreCase)
            || uri.Host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase)
            || uri.Host.EndsWith(".internal", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Источник не содержит допустимую публичную HTTPS-ссылку.");
        return uri;
    }

    private static string Text(JsonElement value, string name, int maximum)
    {
        var field = value.GetProperty(name);
        if (field.ValueKind != JsonValueKind.String || field.GetString() is not { } text || text.Length > maximum)
            throw new InvalidDataException("Некорректное поле интернет-проверки.");
        return text;
    }

    public static BottleResearchResult Parse(JsonElement result, string query)
    {
        if (result.GetProperty("ok").ValueKind != JsonValueKind.True ||
            Text(result, "command", 80) != "ginger.bottle.research" ||
            Text(result, "query", 240) != query ||
            result.GetProperty("can_match").ValueKind != JsonValueKind.False ||
            result.GetProperty("visual_comparison").ValueKind != JsonValueKind.False)
            throw new InvalidDataException("Ответ поиска не относится к этому товару.");
        var status = Text(result, "status", 40);
        if (status is not ("sources" or "no_results" or "unavailable"))
            throw new InvalidDataException("Неизвестное состояние поиска.");
        var checkedAt = Text(result, "checked_at", 80);
        if (!DateTimeOffset.TryParse(checkedAt, out _)) throw new InvalidDataException("Нет времени проверки источников.");
        var sources = new List<BottleSource>();
        foreach (var source in result.GetProperty("sources").EnumerateArray())
        {
            if (sources.Count >= 3) throw new InvalidDataException("Слишком много источников.");
            var id = Text(source, "id", 8);
            if (id != $"s{sources.Count + 1}") throw new InvalidDataException("Источники не пронумерованы однозначно.");
            var imageData = source.GetProperty("image_base64");
            byte[]? bytes = null;
            Uri? imageUrl = null;
            if (imageData.ValueKind != JsonValueKind.Null)
            {
                var encoded = Text(source, "image_base64", 1_333_336);
                bytes = Convert.FromBase64String(encoded);
                if (bytes.Length > 1_000_000) throw new InvalidDataException("Фотография слишком большая.");
                imageUrl = PublicLink(Text(source, "image_url", 2048));
            }
            sources.Add(new(id, PublicLink(Text(source, "url", 2048)), Text(source, "title", 600), imageUrl, bytes));
        }
        var excerpts = new List<BottleExcerpt>();
        foreach (var excerpt in result.GetProperty("excerpts").EnumerateArray())
        {
            if (excerpts.Count >= 5) throw new InvalidDataException("Слишком много выдержек.");
            var id = Text(excerpt, "page_id", 8);
            var field = Text(excerpt, "field", 30);
            if (!sources.Any(x => x.Id == id) || field is not ("design" or "volume" or "concentration" or "tester" or "packaging"))
                throw new InvalidDataException("Выдержка не связана с источником.");
            excerpts.Add(new(id, field, Text(excerpt, "quote", 220)));
        }
        var warnings = result.GetProperty("warnings").EnumerateArray().Select(x => x.GetString() ?? "").ToArray();
        if (warnings.Length > 8 || warnings.Any(x => x.Length > 1000)) throw new InvalidDataException("Слишком длинный ответ поиска.");
        return new(query, checkedAt, status, sources, excerpts, warnings);
    }
}
