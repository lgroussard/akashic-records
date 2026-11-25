using System.Text.RegularExpressions;

namespace AkashicRecords.Hub;

/// <summary>
/// Hub settings. Sources, later ones win:
///   1. built-in defaults
///   2. hub.json next to the executable (shipped, replaced by an upgrade)
///   3. hub.local.json next to the executable (written by the install script, kept by an upgrade)
///   4. environment: AKASHIC_HUB_PORT, AKASHIC_HUB_LISTEN, AKASHIC_HUB_DATA, AKASHIC_HUB_ENTITY_TYPES (comma list)
///   5. command line: --Hub:Port=5199 --Hub:Listen=0.0.0.0 --Hub:DataDir=...
/// The server and the `pair` / `devices` / `revoke` commands read the same sources, so they always
/// agree on where the data lives.
/// </summary>
public sealed partial class HubOptions
{
    public string Listen { get; set; } = "0.0.0.0"; // all interfaces: the phone must reach it; every sync call needs a token
    public int Port { get; set; } = 5199;
    public string DataDir { get; set; } = "hub-data"; // relative = next to the executable
    public int RetentionDays { get; set; } = 30;      // tombstone retention; no purge implemented yet
    public int MaxBlobMegabytes { get; set; } = 1024;
    public int PairCodeMinutes { get; set; } = 10;
    /// <summary>Accepted entity types. "*" accepts any well-formed name.</summary>
    public List<string> EntityTypes { get; set; } = new();

    /// <summary>Full listen URL; overrides Listen/Port. Used by the smoke test (port 0).</summary>
    public string? Url { get; set; }

    public string EffectiveUrl => Url ?? $"http://{(Listen.Contains(':') ? $"[{Listen}]" : Listen)}:{Port}";
    public string DatabasePath => Path.Combine(DataDir, "hub.db");
    public string BlobDir => Path.Combine(DataDir, "blobs");
    public string LogDir => Path.Combine(DataDir, "logs");
    public long MaxBlobBytes => MaxBlobMegabytes * 1024L * 1024L;

    public bool AcceptsEntityType(string? type) =>
        type is not null && TypeName().IsMatch(type) && (EntityTypes.Contains("*") || EntityTypes.Contains(type));

    [GeneratedRegex("^[A-Za-z][A-Za-z0-9_]{0,63}$")]
    private static partial Regex TypeName();

    public static HubOptions Load(string[] args)
    {
        var config = new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(AppContext.BaseDirectory, "hub.json"), optional: true)
            .AddJsonFile(Path.Combine(AppContext.BaseDirectory, "hub.local.json"), optional: true)
            .AddCommandLine(args)
            .Build();

        var o = new HubOptions();
        var section = config.GetSection("Hub");

        // Lists merge by index across JSON files, which would mix two lists: take the last file that defines one.
        foreach (var file in new[] { "hub.json", "hub.local.json" })
        {
            var types = new ConfigurationBuilder()
                .AddJsonFile(Path.Combine(AppContext.BaseDirectory, file), optional: true).Build()
                .GetSection("Hub:EntityTypes").GetChildren().Select(c => c.Value).OfType<string>().ToList();
            if (types.Count > 0) o.EntityTypes = types;
        }

        o.Listen = section["Listen"] ?? o.Listen;
        o.DataDir = section["DataDir"] ?? o.DataDir;
        o.Port = Int(section["Port"], o.Port, "Hub:Port");
        o.RetentionDays = Int(section["RetentionDays"], o.RetentionDays, "Hub:RetentionDays");
        o.MaxBlobMegabytes = Int(section["MaxBlobMegabytes"], o.MaxBlobMegabytes, "Hub:MaxBlobMegabytes");
        o.PairCodeMinutes = Int(section["PairCodeMinutes"], o.PairCodeMinutes, "Hub:PairCodeMinutes");

        Func<string, string?> env = Environment.GetEnvironmentVariable;
        if (env("AKASHIC_HUB_LISTEN") is { Length: > 0 } l) o.Listen = l;
        if (env("AKASHIC_HUB_DATA") is { Length: > 0 } d) o.DataDir = d;
        if (env("AKASHIC_HUB_PORT") is { Length: > 0 } p) o.Port = Int(p, o.Port, "AKASHIC_HUB_PORT");
        if (env("AKASHIC_HUB_ENTITY_TYPES") is { Length: > 0 } t)
            o.EntityTypes = t.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

        // Command line wins over the environment.
        var cli = new ConfigurationBuilder().AddCommandLine(args).Build().GetSection("Hub");
        o.Listen = cli["Listen"] ?? o.Listen;
        o.DataDir = cli["DataDir"] ?? o.DataDir;
        o.Port = Int(cli["Port"], o.Port, "--Hub:Port");

        return o.Normalize();
    }

    public HubOptions Normalize()
    {
        if (EntityTypes.Count == 0) EntityTypes = new List<string> { "Recipe" };
        if (Port is < 0 or > 65535) throw new InvalidOperationException($"Hub:Port invalide : {Port}");
        if (MaxBlobMegabytes < 1) MaxBlobMegabytes = 1;
        if (PairCodeMinutes < 1) PairCodeMinutes = 1;
        DataDir = Path.GetFullPath(DataDir, AppContext.BaseDirectory);
        return this;
    }

    private static int Int(string? raw, int fallback, string name)
    {
        if (string.IsNullOrWhiteSpace(raw)) return fallback;
        return int.TryParse(raw, out var v) ? v : throw new InvalidOperationException($"{name} : nombre attendu, recu '{raw}'");
    }
}
