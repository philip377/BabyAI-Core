using System.Text.Json;

namespace BabyAI.Desktop;

public sealed record AgentProfile(string Id, string Name, string Task, string Appearance = "violet",
    int? X = null, int? Y = null);
public sealed record AgentPreferences(bool Animate = true, bool Quiet = false, bool HideInFullscreen = true);
public sealed record AgentCatalog(int Version, List<AgentProfile> Agents, AgentPreferences Preferences);

// Profiles describe a specialization; they never grant additional tool permissions.
public sealed class AgentProfileStore
{
    private readonly string _path;
    public AgentProfileStore(string? path = null) => _path = path ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BabyAI", "agents.json");
    public static AgentProfile Ginger => new("ginger", "Ginger Agent", "Сопоставление товаров и проверка прайсов Ginger");
    public AgentCatalog Load()
    {
        if (!File.Exists(_path)) return new(1, [Ginger], new());
        var catalog = JsonSerializer.Deserialize<AgentCatalog>(File.ReadAllText(_path))
            ?? throw new InvalidDataException("Не удалось прочитать список агентов.");
        Validate(catalog);
        return catalog;
    }
    public void Save(AgentCatalog catalog)
    {
        Validate(catalog);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
        var temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, catalog, new JsonSerializerOptions { WriteIndented = true });
                stream.Flush(true);
            }
            File.Move(temporary, _path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    private static void Validate(AgentCatalog catalog)
    {
        if (catalog.Version != 1 || catalog.Agents is null || catalog.Preferences is null
            || catalog.Agents.Count is < 1 or > 50
            || catalog.Agents.Count(a => a.Id == "ginger") != 1
            || catalog.Agents.Select(a => a.Id).Distinct(StringComparer.Ordinal).Count() != catalog.Agents.Count)
            throw new InvalidDataException("Некорректный список агентов; исходный файл сохранён.");
        foreach (var a in catalog.Agents)
            if (string.IsNullOrWhiteSpace(a.Id) || string.IsNullOrWhiteSpace(a.Name)
                || a.Name.Length > 60 || string.IsNullOrWhiteSpace(a.Task) || a.Task.Length > 2000
                || a.Appearance is not ("violet" or "mint" or "amber") || a.X.HasValue != a.Y.HasValue)
                throw new InvalidDataException("Проверь имя, задачу и внешний вид агента.");
    }
}
