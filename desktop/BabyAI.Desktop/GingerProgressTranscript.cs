using System.Text.Encodings.Web;
using System.Text.Json;

namespace BabyAI.Desktop;

internal enum GingerProgressState { Running, Completed, Failed }
internal sealed record GingerProgressStep(string Label, GingerProgressState State);
internal sealed record GingerProgressSnapshot(string Summary, string Details, IReadOnlyList<GingerProgressStep> Steps);

// A display-only envelope in the existing chat content field. It carries no
// commands, candidate selection or confirmation state.
internal static class GingerProgressTranscript
{
    internal const string Prefix = "[[UNIX_GINGER_PROGRESS:1]]";
    internal const int MaxSteps = 8;
    internal const int MaxDetailsLength = 20_000;
    internal const int MaxSerializedLength = 59_999;
    internal const string TruncationNotice = "\n\n[Часть подробностей сокращена. Полный журнал — в папке сеанса Ginger: journal-ru.txt.]\n\n";
    private const int MaxLength = 600_000;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    internal static string Format(GingerProgressSnapshot snapshot)
    {
        // Keep the outcome and every stage intact. Only the display journal may
        // be shortened; the desktop session retains the complete event journal.
        if (snapshot.Summary.Length > 5_000 || snapshot.Steps.Count > MaxSteps ||
            snapshot.Steps.Any(step => string.IsNullOrWhiteSpace(step.Label) || step.Label.Length > 160 ||
                !Enum.IsDefined(step.State)))
            throw new InvalidDataException("Недопустимый итог или этап Ginger.");
        var details = LimitDetails(snapshot.Details);
        var result = Serialize(snapshot, details);
        if (result.Length <= MaxSerializedLength) return result;

        // Escaping controls and surrogate pairs can multiply JSON length. Find
        // a fitting display excerpt from its actual serialized representation.
        var low = TruncationNotice.Length;
        var high = details.Length - 1;
        var best = Serialize(snapshot, TruncationNotice);
        if (best.Length > MaxSerializedLength)
            throw new InvalidDataException("Итог Ginger превышает размер записи истории.");
        while (low <= high)
        {
            var length = low + (high - low) / 2;
            var candidate = Serialize(snapshot, ShortenDetails(details, length));
            if (candidate.Length <= MaxSerializedLength)
            {
                best = candidate;
                low = length + 1;
            }
            else high = length - 1;
        }
        return best;
    }

    internal static string LimitDetails(string details) => ShortenDetails(details, MaxDetailsLength);

    private static string ShortenDetails(string details, int maximum)
    {
        if (details.Length <= maximum) return details;
        var available = maximum - TruncationNotice.Length;
        var tailLength = Math.Min(5_000, available / 4);
        var headLength = available - tailLength;
        // Neither end of the excerpt may split an astral Unicode character.
        if (headLength > 0 && char.IsHighSurrogate(details[headLength - 1])) headLength--;
        var tailStart = details.Length - tailLength;
        if (tailStart < details.Length && char.IsLowSurrogate(details[tailStart])) tailStart++;
        return details[..headLength] + TruncationNotice + details[tailStart..];
    }

    private static string Serialize(GingerProgressSnapshot snapshot, string details) => Prefix + JsonSerializer.Serialize(new
    {
        summary = snapshot.Summary,
        details,
        steps = snapshot.Steps.Select(step => new
        {
            label = step.Label,
            state = step.State.ToString().ToLowerInvariant()
        })
    }, JsonOptions);

    internal static bool TryParse(string text, out GingerProgressSnapshot? snapshot)
    {
        snapshot = null;
        if (text.Length > MaxLength || !text.StartsWith(Prefix, StringComparison.Ordinal)) return false;
        try
        {
            using var document = JsonDocument.Parse(text[Prefix.Length..], new JsonDocumentOptions { MaxDepth = 6 });
            var root = document.RootElement;
            if (!HasExactFields(root, "summary", "details", "steps") ||
                !ReadString(root.GetProperty("summary"), 5_000, out var summary) ||
                !ReadString(root.GetProperty("details"), 500_000, out var details)) return false;
            var items = root.GetProperty("steps");
            if (items.ValueKind != JsonValueKind.Array || items.GetArrayLength() > MaxSteps) return false;
            var steps = new List<GingerProgressStep>();
            foreach (var item in items.EnumerateArray())
            {
                if (!HasExactFields(item, "label", "state") ||
                    !ReadString(item.GetProperty("label"), 160, out var label) || string.IsNullOrWhiteSpace(label) ||
                    !ReadString(item.GetProperty("state"), 16, out var state)) return false;
                var value = state switch
                {
                    "running" => GingerProgressState.Running,
                    "completed" => GingerProgressState.Completed,
                    "failed" => GingerProgressState.Failed,
                    _ => (GingerProgressState?)null
                };
                if (value is null) return false;
                steps.Add(new(label, value.Value));
            }
            snapshot = new(summary, details, steps);
            return true;
        }
        catch (JsonException) { return false; }
    }

    private static bool ReadString(JsonElement element, int limit, out string value)
    {
        value = element.ValueKind == JsonValueKind.String ? element.GetString()! : "";
        return element.ValueKind == JsonValueKind.String && value.Length <= limit;
    }

    private static bool HasExactFields(JsonElement element, params string[] fields)
    {
        if (element.ValueKind != JsonValueKind.Object) return false;
        var actual = element.EnumerateObject().Select(property => property.Name).ToArray();
        return actual.Length == fields.Length && actual.Distinct(StringComparer.Ordinal).Count() == fields.Length &&
            fields.All(field => actual.Contains(field, StringComparer.Ordinal));
    }
}
