using System.Text.Json;

namespace AkashicRecords.Sync;

/// <summary>
/// This device's link to the hub, kept in its own small file (not in the database: the database is
/// copied to the phone and to backups, and the token must not travel with it).
/// </summary>
public sealed class SyncSettings
{
    public string HubUrl { get; set; } = "";
    public string DeviceId { get; set; } = "";
    public string DeviceName { get; set; } = "";
    public string Token { get; set; } = "";

    public bool IsPaired => HubUrl.Length > 0 && Token.Length > 0;

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static SyncSettings Load(string path)
    {
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<SyncSettings>(File.ReadAllText(path), Json) ?? new() : new();
        }
        catch (JsonException)
        {
            return new(); // unreadable file = not paired; pairing again rewrites it
        }
    }

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, JsonSerializer.Serialize(this, Json));
    }

    /// <summary>
    /// Pairs this device with a hub: checks the address, exchanges the one-time code for a token
    /// and returns the settings to save. The device id is created once and kept for life.
    /// </summary>
    public static async Task<SyncSettings> PairAsync(SyncSettings current, string hubUrl, string code, string deviceName, CancellationToken ct = default)
    {
        hubUrl = hubUrl.Trim().TrimEnd('/');
        if (!hubUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && !hubUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            hubUrl = "http://" + hubUrl;

        using var hub = new HubClient(hubUrl);
        await hub.HealthAsync(ct); // a clear "unreachable" before spending the code
        var deviceId = current.DeviceId.Length > 0 ? current.DeviceId : Guid.NewGuid().ToString();
        var paired = await hub.PairAsync(code, deviceId, deviceName, ct);
        return new SyncSettings { HubUrl = hubUrl, DeviceId = paired.DeviceId, DeviceName = deviceName, Token = paired.Token };
    }
}
