using System.IO;
using System.Text.Json;
using System.Threading;

namespace AkashicRecords.Infrastructure.Configuration;

// Loads/saves app settings as JSON under <exe folder>\config\config.json — kept relative to the
// executable (not %AppData%) so the whole app stays portable: copy the folder, run it anywhere.
//
// The file is saved many times a session (section changes, widget moves). A save cut short must not
// cost the settings, so it is written beside the file and swapped in, the previous version staying as
// config.json.bak; a file that cannot be read is set aside and the .bak is used instead.
public sealed class ConfigService
{
    private static readonly object WriteLock = new();

    private readonly string _filePath;
    private readonly string _backupPath;

    public ConfigService()
    {
        var folder = Path.Combine(AppContext.BaseDirectory, "config");
        Directory.CreateDirectory(folder);
        _filePath = Path.Combine(folder, "config.json");
        _backupPath = _filePath + ".bak";
    }

    public AppConfig Load()
    {
        if (TryRead(_filePath, out var unreadable) is { } config) return config;
        if (!unreadable) return new AppConfig(); // first run: no file yet

        // Kept for a look, and so that the next save does not turn it into the .bak.
        try { File.Copy(_filePath, _filePath + ".illisible", overwrite: true); } catch (IOException) { }

        return TryRead(_backupPath, out _) ?? new AppConfig();
    }

    public void Save(AppConfig config)
    {
        var json = JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true });
        var temp = _filePath + ".tmp";

        lock (WriteLock)
        {
            File.WriteAllText(temp, json);
            // Only a file that reads back becomes the .bak: an unreadable one must not replace a good backup.
            if (File.Exists(_filePath) && TryRead(_filePath, out _) is not null) File.Replace(temp, _filePath, _backupPath);
            else File.Move(temp, _filePath, overwrite: true);
        }
    }

    // The settings in [path]; null when there is no such file, or (unreadable) when it holds no valid JSON.
    private static AppConfig? TryRead(string path, out bool unreadable)
    {
        unreadable = false;
        if (!File.Exists(path)) return null;

        // A reader on another thread can meet the file while a save swaps it: one short second try.
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                var config = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(path));
                unreadable = config is null;
                return config;
            }
            catch (JsonException)
            {
                unreadable = true;
                return null;
            }
            // A file that stays locked is not a damaged one: the second failure goes to the caller, as before,
            // rather than sending it to the backup's older settings.
            catch (IOException) when (attempt == 0)
            {
                Thread.Sleep(40);
            }
        }
    }
}
