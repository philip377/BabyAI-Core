using System.Text.Json;
using System.Text.RegularExpressions;
namespace BabyAI.Desktop;

public sealed record GingerLearnedPair(string Supplier, string Source, string Candidate, string Reason,
    bool AllowAutomatic, DateTimeOffset RecordedAt);

public sealed class GingerLearning
{
    private readonly string _directory;
    public GingerLearning(string? directory = null) => _directory = directory ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BabyAI", "ginger-learning");
    public IReadOnlyList<GingerLearnedPair> Read()
    {
        if (!Directory.Exists(_directory)) return [];
        return Directory.GetFiles(_directory, "*.json").Order(StringComparer.Ordinal)
            .Select(path => JsonSerializer.Deserialize<GingerLearnedPair>(File.ReadAllText(path))
                ?? throw new InvalidDataException("Повреждена запись обучения.")) .ToArray();
    }
    public void Teach(GingerLearnedPair pair)
    {
        if (string.IsNullOrWhiteSpace(pair.Supplier) || string.IsNullOrWhiteSpace(pair.Source)
            || string.IsNullOrWhiteSpace(pair.Candidate) || pair.Reason.Length > 4000)
            throw new InvalidDataException("Для обучения нужна полная пара товаров и поставщик.");
        Directory.CreateDirectory(_directory);
        using var stream = new FileStream(Path.Combine(_directory, DateTime.UtcNow.ToString("yyyyMMddHHmmssfffffff")
            + "-" + Guid.NewGuid().ToString("N") + ".json"), FileMode.CreateNew, FileAccess.Write, FileShare.None);
        JsonSerializer.Serialize(stream, pair); stream.Flush(true);
    }
    public string? AutomaticCandidate(string supplier, string source, IEnumerable<string> candidates)
    {
        var list = candidates.ToArray();
        var learned = Read().Where(p => p.Supplier == supplier && p.Source == source).ToArray();
        if (learned.Length > 0)
        {
            var latest = learned[^1];
            return latest.AllowAutomatic && list.Count(c => c == latest.Candidate) == 1 ? latest.Candidate : null;
        }
        // Exact token identity only. Similarity scores, variant research and guessed aliases never authorize input.
        var key = Canonical(source);
        if (!Regex.IsMatch(key, @"\b\d+(?:[.,]\d+)? ml\b") || !Regex.IsMatch(key, @"\b(edp|edt|edc|extrait)\b")) return null;
        var equal = list.Where(c => Canonical(c) == key).ToArray();
        return equal.Length == 1 ? equal[0] : null;
    }
    private static string Canonical(string title)
    {
        var text = title.Normalize(System.Text.NormalizationForm.FormKC).ToLowerInvariant();
        text = Regex.Replace(text, @"\bтестер\b", "tester");
        text = Regex.Replace(text, @"\bтв\b", "edt");
        text = Regex.Replace(text, @"\bпв\b", "edp");
        text = Regex.Replace(text, @"(\d)\s*(?:ml|мл)\b", "$1 ml");
        // Keep every lexical token: vintage, lux, gender, concentration and flankers are not noise.
        return string.Join(" ", Regex.Matches(text, @"[\p{L}\p{N}]+(?:[.,]\d+)?").Select(m => m.Value));
    }
}
