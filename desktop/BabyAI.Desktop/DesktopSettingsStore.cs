using System.Text.Json;

namespace BabyAI.Desktop;

public sealed record DesktopSettings(int X, int Y);

public sealed class DesktopSettingsStore
{
    private readonly string _path;

    public DesktopSettingsStore()
    {
        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "BabyAI");
        Directory.CreateDirectory(directory);
        _path = Path.Combine(directory, "desktop.json");
    }

    public DesktopSettings? Load()
    {
        if (!File.Exists(_path))
            return null;

        try
        {
            return JsonSerializer.Deserialize<DesktopSettings>(File.ReadAllText(_path));
        }
        catch
        {
            return null;
        }
    }

    public void Save(int x, int y)
    {
        File.WriteAllText(
            _path,
            JsonSerializer.Serialize(new DesktopSettings(x, y), new JsonSerializerOptions { WriteIndented = true }));
    }
}
