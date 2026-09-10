using System.IO;
using System.Text.Json;

namespace AkashicRecords.Infrastructure.Configuration;

// Loads/saves app settings as JSON under <exe folder>\config\config.json — kept relative to the
// executable (not %AppData%) so the whole app stays portable: copy the folder, run it anywhere.
public sealed class ConfigService
{
    private readonly string _filePath;

    public ConfigService()
    {
        var folder = Path.Combine(AppContext.BaseDirectory, "config");
        Directory.CreateDirectory(folder);
        _filePath = Path.Combine(folder, "config.json");
    }

    public AppConfig Load()
    {
        if (!File.Exists(_filePath)) return new AppConfig();

        try
        {
            var json = File.ReadAllText(_filePath);
            return JsonSerializer.Deserialize<AppConfig>(json) ?? new AppConfig();
        }
        catch (JsonException)
        {
            // Corrupted config file - fall back to defaults rather than crash the app.
            return new AppConfig();
        }
    }

    public void Save(AppConfig config)
    {
        var json = JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(_filePath, json);
    }
}
