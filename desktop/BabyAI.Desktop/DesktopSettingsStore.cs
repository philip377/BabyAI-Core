using System.Text.Json;

namespace BabyAI.Desktop;

public sealed record DesktopSettings(int X, int Y);

public sealed record DesktopUiSettings(
    bool AlwaysOnTop = true,
    bool CheckForUpdatesOnStartup = true);

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

public sealed class DesktopUiSettingsStore
{
    private readonly string _path;

    public DesktopUiSettingsStore()
    {
        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "BabyAI");
        Directory.CreateDirectory(directory);
        _path = Path.Combine(directory, "ui.json");
    }

    public DesktopUiSettings Load()
    {
        if (!File.Exists(_path))
            return new DesktopUiSettings();

        try
        {
            var json = File.ReadAllText(_path);
            var settings = JsonSerializer.Deserialize<DesktopUiSettings>(json)
                ?? new DesktopUiSettings();
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty(
                    nameof(DesktopUiSettings.CheckForUpdatesOnStartup),
                    out _))
            {
                settings = settings with { CheckForUpdatesOnStartup = true };
            }
            return settings;
        }
        catch
        {
            return new DesktopUiSettings();
        }
    }

    public void Save(DesktopUiSettings settings)
    {
        File.WriteAllText(
            _path,
            JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
    }
}
