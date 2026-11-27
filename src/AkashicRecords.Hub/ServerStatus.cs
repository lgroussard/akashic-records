using System.Text.Json;

namespace AkashicRecords.Hub;

/// <summary>
/// GET /server: everything the "Serveur" screen of the apps shows about the hub, in one answer.
///   - the disk the hub writes to (total, free) and what takes the room (catalogue by kind, previews, tools);
///   - the last copy to the external disk, as backup.sh reported it in DataDir/backup-status.json;
///   - the paired devices, when each was last seen and last sent a change;
///   - the hub's own work: music downloads and shared computing, by status.
/// Folder sizes are walked at most once a minute.
/// </summary>
public static class ServerStatus
{
    private static readonly DateTime Started = DateTime.UtcNow;
    private static (DateTime At, Dictionary<string, long> Sizes)? _sizes;
    private static readonly object Gate = new();

    public sealed record Disk(string Path, long Total, long Free);
    public sealed record StorageKind(string Kind, long Files, long Bytes);
    public sealed record Backup(string State, string? At, string? LastOkAt, string? Message, long? Bytes, long? Files, long? DiskTotal, long? DiskFree, long? Seconds);
    public sealed record Device(string Name, string? LastSeenAt, string? LastChangeAt, long Changes, bool Revoked, bool Hub);
    public sealed record Work(int Queued, int Running, int Failed, int Done);
    public sealed record Status(
        string Version, string Host, string StartedAt, Disk? Disk, List<StorageKind> Storage, long BlobBytes, long BlobFiles,
        Backup Backup, List<Device> Devices, Dictionary<string, long> Entities, Work Downloads, Work Compute,
        BlobCleaner.Report? Cleanup = null);

    public static void Map(WebApplication app, HubStore store, DownloadStore downloads, ComputeStore compute, HubOptions options, BlobStore blobs)
    {
        app.MapGet("/server", () => Results.Ok(Build(store, downloads, compute, options)));
        // The disk folder by folder (DiskIndex.cs): measured apart, as root; the hub only reads it and asks again.
        app.MapGet("/server/disk", (string? path) => Results.Ok(DiskIndex.Browse(options, path, store, compute)));
        app.MapPost("/server/disk/refresh", () => { DiskIndex.RequestRefresh(options); return Results.Ok(DiskIndex.Browse(options, null, store, compute)); });
        // « Effacer maintenant »: the files deleted in the apps go now instead of after DeleteAfterDays — those the daily
        // pass already found unused at least an hour ago (BlobCleaner.Run), brakes kept.
        app.MapPost("/server/cleanup/now", () => Results.Ok(BlobCleaner.Run(options, store, compute, blobs, DateTime.UtcNow, TimeSpan.FromHours(1))));
    }

    public static Status Build(HubStore store, DownloadStore downloads, ComputeStore compute, HubOptions options)
    {
        var sizes = Sizes(options);
        var kinds = store.FilesByKind().Select(k => new StorageKind(k.Kind, k.Files, k.Bytes)).ToList();
        // The hub's own extras: previews, tools and models, results of computations not in the catalogue.
        var catalogued = kinds.Sum(k => k.Bytes);
        var extra = Math.Max(0, sizes["blobs"] - catalogued);
        if (extra > 0) kinds.Add(new StorageKind("hub_files", 0, extra));
        if (sizes["thumbs"] > 0) kinds.Add(new StorageKind("previews", 0, sizes["thumbs"]));
        var tools = sizes["compute"] + sizes["bin"];
        if (tools > 0) kinds.Add(new StorageKind("tools", 0, tools));

        var devices = store.DeviceActivities()
            .Select(d => new Device(d.Name, d.LastSeenAt, d.LastChangeAt, d.Changes, d.Revoked, d.DeviceId == HubStore.HubDevice))
            .ToList();

        static Work Count(IEnumerable<string> statuses)
        {
            var l = statuses.ToList();
            return new Work(l.Count(s => s == "queued"), l.Count(s => s == "running"), l.Count(s => s == "failed"), l.Count(s => s == "done"));
        }

        return new Status(
            HubApp.Version, Environment.MachineName, Started.ToString("O"), DiskOf(options.DataDir), kinds,
            sizes["blobs"], sizes["blob_files"], ReadBackup(options), devices, store.EntityCounts(),
            Count(downloads.Jobs(200).Select(j => j.Status)), Count(compute.Jobs(200).Select(j => j.Status)), ReadCleanup(options));
    }

    /// <summary>The last clean-up pass (BlobCleaner.cs): what is waiting to be removed for good, and when.</summary>
    private static BlobCleaner.Report? ReadCleanup(HubOptions options)
    {
        var file = Path.Combine(options.DataDir, BlobCleaner.StatusFile);
        if (!File.Exists(file)) return null;
        try
        {
            return JsonSerializer.Deserialize<BlobCleaner.Report>(File.ReadAllText(file),
                new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower });
        }
        catch (Exception) { return null; }
    }

    private static Disk? DiskOf(string path)
    {
        try
        {
            var d = new DriveInfo(Path.GetFullPath(path));
            // On Linux DriveInfo takes the path as the mount point: pick the deepest mount that holds it.
            if (!OperatingSystem.IsWindows())
            {
                var full = Path.GetFullPath(path);
                d = DriveInfo.GetDrives()
                    .Where(x => x.IsReady && (full == x.Name || full.StartsWith(x.Name.TrimEnd('/') + "/") || x.Name == "/"))
                    .OrderByDescending(x => x.Name.Length).FirstOrDefault() ?? d;
            }
            return d.IsReady ? new Disk(d.Name, d.TotalSize, d.AvailableFreeSpace) : null;
        }
        catch (Exception) { return null; }
    }

    private static Dictionary<string, long> Sizes(HubOptions options)
    {
        lock (Gate)
        {
            if (_sizes is { } s && DateTime.UtcNow - s.At < TimeSpan.FromMinutes(1)) return s.Sizes;
            var (blobBytes, blobFiles) = Walk(options.BlobDir);
            var map = new Dictionary<string, long>
            {
                ["blobs"] = blobBytes,
                ["blob_files"] = blobFiles,
                ["thumbs"] = Walk(Path.Combine(options.DataDir, "thumbs")).Bytes,
                ["compute"] = Walk(options.ComputeDir).Bytes,
                ["bin"] = Walk(options.ToolDir).Bytes,
            };
            _sizes = (DateTime.UtcNow, map);
            return map;
        }
    }

    private static (long Bytes, long Files) Walk(string dir)
    {
        if (!Directory.Exists(dir)) return (0, 0);
        long bytes = 0, files = 0;
        try
        {
            foreach (var f in new DirectoryInfo(dir).EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true }))
            {
                bytes += f.Length;
                files++;
            }
        }
        catch (Exception) { }
        return (bytes, files);
    }

    /// <summary>backup.sh writes DataDir/backup-status.json at the end of every run (done, disk absent, failed).
    /// No file: the backup was never set up (or not run since it learnt to write it).</summary>
    private static Backup ReadBackup(HubOptions options)
    {
        var file = Path.Combine(options.DataDir, "backup-status.json");
        if (!File.Exists(file)) return new Backup("unknown", null, null, null, null, null, null, null, null);
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            var r = doc.RootElement;
            string? S(string n) => r.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            long? L(string n) => r.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt64() : null;
            var state = S("state") ?? "unknown";
            var at = S("at");
            var lastOk = S("last_ok_at") ?? (state == "ok" ? at : null);
            // A night missed is worth saying: the timer runs every night, a copy older than ~1.5 day means it did not.
            if (state == "ok" && DateTimeOffset.TryParse(at, out var when) && DateTimeOffset.UtcNow - when > TimeSpan.FromHours(36)) state = "late";
            return new Backup(state, at, lastOk, S("message"), L("bytes"), L("files"), L("disk_total"), L("disk_free"), L("seconds"));
        }
        catch (Exception e)
        {
            return new Backup("unknown", null, null, e.Message, null, null, null, null, null);
        }
    }
}
