using System.IO;
using System.Text.Json.Nodes;
using AkashicRecords.Sync;

namespace AkashicRecords.App;

/// <summary>
/// This PC's own disks, folder by folder, for the browser of the Serveur window (« Ce PC »): a TreeSize of the PC.
/// Same rules as the mini-PC's index (the hub's DiskIndex): every folder of 1 MB or more is kept, smaller ones are
/// summed up per parent, each folder keeps its 20 largest files. Junctions and symbolic links are not followed, and
/// files that only live in the cloud (OneDrive « disponible en ligne ») take no room here, so they count 0.
/// Measured on demand (a few minutes for a full C:), the last measure is kept in cache/disk-index.json.
/// Read-only: nothing is ever changed.
/// </summary>
public static class LocalDisk
{
    private const long KeepFolder = 1L << 20;
    private const int KeepFiles = 20;
    /// <summary>The part of a disk this measure could not see (Windows' own folders, other accounts…).</summary>
    public const string Unseen = "Système et dossiers non lisibles";
    private const FileAttributes CloudOnly = FileAttributes.Offline | (FileAttributes)0x00040000 | (FileAttributes)0x00400000;

    private static readonly string CacheFile = Path.Combine(AppContext.BaseDirectory, "cache", "disk-index.json");
    private static readonly object Gate = new();
    private static JsonObject? _doc;
    private static Task? _running;
    private static long _seen;

    /// <summary>True while a measure runs; <see cref="FilesSeen"/> says how far it is.</summary>
    public static bool Measuring => _running is { IsCompleted: false };
    public static long FilesSeen => Interlocked.Read(ref _seen);

    /// <summary>Starts measuring every fixed disk in the background (nothing when one is already running).</summary>
    public static void StartMeasure()
    {
        lock (Gate)
        {
            if (Measuring) return;
            Interlocked.Exchange(ref _seen, 0);
            _running = Task.Run(Measure);
        }
    }

    private static void Measure()
    {
        var started = DateTime.UtcNow;
        var drives = new JsonArray();
        long total = 0, files = 0;
        foreach (var drive in Disks())
        {
            // Google Drive and the like: a disk letter for files that live online, not room taken here.
            if (IsCloud(drive)) { drives.Add(new JsonObject { ["n"] = drive.Name.TrimEnd('\\'), ["label"] = drive.VolumeLabel, ["s"] = 0L, ["m"] = true }); continue; }
            var node = MeasureFolder(new DirectoryInfo(drive.RootDirectory.FullName));
            node["n"] = drive.Name.TrimEnd('\\');
            node["label"] = drive.VolumeLabel;
            node["total"] = drive.TotalSize;
            node["free"] = drive.AvailableFreeSpace;
            // What the disk holds that this measure could not read: shown as one line, so the sizes add up.
            var unseen = drive.TotalSize - drive.TotalFreeSpace - (long)node["s"]!;
            if (unseen > KeepFolder)
            {
                var items = (node["d"] as JsonArray)?.OfType<JsonObject>().ToList() ?? new();
                (node["d"] as JsonArray)?.Clear(); // detached, to be put in the new list
                items.Add(new JsonObject { ["n"] = Unseen, ["s"] = unseen, ["u"] = true });
                node["d"] = new JsonArray(items.OrderByDescending(c => (long)c["s"]!).Select(c => (JsonNode)c).ToArray());
                node["s"] = (long)node["s"]! + unseen;
            }
            total += (long)node["s"]!;
            files += node["f"]?.GetValue<long>() ?? 0;
            drives.Add(node);
        }
        var doc = new JsonObject
        {
            ["at"] = DateTime.UtcNow.ToString("O"),
            ["seconds"] = (long)(DateTime.UtcNow - started).TotalSeconds,
            ["tree"] = new JsonObject { ["n"] = "Ce PC", ["s"] = total, ["f"] = files, ["d"] = drives },
        };
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(CacheFile)!);
            File.WriteAllText(CacheFile + ".part", doc.ToJsonString());
            File.Move(CacheFile + ".part", CacheFile, overwrite: true);
        }
        catch (IOException) { }
        lock (Gate) _doc = doc;
    }

    private static bool IsCloud(DriveInfo d) => d.VolumeLabel.Contains("Google Drive", StringComparison.OrdinalIgnoreCase) || d.VolumeLabel.Contains("OneDrive", StringComparison.OrdinalIgnoreCase);

    private static IEnumerable<DriveInfo> Disks() =>
        DriveInfo.GetDrives().Where(d => d.DriveType is DriveType.Fixed or DriveType.Removable && d.IsReady);

    private static JsonObject MeasureFolder(DirectoryInfo dir)
    {
        var node = new JsonObject { ["n"] = dir.Name };
        long size = 0, files = 0, smallBytes = 0, smallCount = 0;
        var children = new List<JsonObject>();
        var top = new List<(string Name, long Size)>();
        List<FileSystemInfo> entries;
        try
        {
            entries = dir.EnumerateFileSystemInfos("*", new EnumerationOptions { IgnoreInaccessible = false, AttributesToSkip = 0, RecurseSubdirectories = false }).ToList();
        }
        catch (Exception)
        {
            node["s"] = 0L;
            node["x"] = true;
            return node;
        }

        foreach (var e in entries)
        {
            try
            {
                if ((e.Attributes & FileAttributes.ReparsePoint) != 0 && e is DirectoryInfo) continue; // junction, link: not followed
                if (e is DirectoryInfo d)
                {
                    var child = MeasureFolder(d);
                    var s = (long)child["s"]!;
                    size += s;
                    files += child["f"]?.GetValue<long>() ?? 0;
                    if (s >= KeepFolder || child["x"] is not null) children.Add(child);
                    else { smallBytes += s; smallCount++; }
                }
                else if (e is FileInfo f)
                {
                    files++;
                    Interlocked.Increment(ref _seen);
                    if ((f.Attributes & CloudOnly) != 0) continue; // in the cloud only: no room taken here
                    var len = f.Length;
                    size += len;
                    top.Add((f.Name, len));
                }
            }
            catch (Exception) { }
        }

        node["s"] = size;
        node["f"] = files;
        if (children.Count > 0) node["d"] = new JsonArray(children.OrderByDescending(c => (long)c["s"]!).Select(c => (JsonNode)c).ToArray());
        var kept = top.OrderByDescending(t => t.Size).Take(KeepFiles).ToList();
        if (kept.Count > 0) node["t"] = new JsonArray(kept.Select(t => (JsonNode)new JsonArray(t.Name, t.Size)).ToArray());
        if (smallCount > 0) { node["o"] = smallBytes; node["oc"] = smallCount; }
        return node;
    }

    private static JsonObject? Load()
    {
        lock (Gate)
        {
            if (_doc is not null) return _doc;
            try { if (File.Exists(CacheFile)) _doc = JsonNode.Parse(File.ReadAllText(CacheFile))!.AsObject(); }
            catch (Exception) { }
            return _doc;
        }
    }

    /// <summary>The disks' free space as Windows reports it now: (name, label, total, free).</summary>
    public static List<(string Name, string Label, long Total, long Free)> DiskSpace() =>
        Disks().Select(d => (d.Name.TrimEnd('\\'), d.VolumeLabel, d.TotalSize, d.AvailableFreeSpace)).ToList();

    /// <summary>The full Windows path of a browser path ("/C:/Users" → "C:\Users").</summary>
    public static string FullPath(string browserPath)
    {
        var parts = browserPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 0 ? "" : parts[0] + "\\" + string.Join('\\', parts.Skip(1));
    }

    /// <summary>One folder of the last measure, in the same shape as the mini-PC's (so one browser shows both);
    /// path "/" = the PC (its disks), "/C:/Users" = a folder. Files carry their full path in Label.</summary>
    public static HubDiskView Browse(string? path)
    {
        var doc = Load();
        if (doc is null) return new HubDiskView("none", null, null, Measuring, "Ce PC", "/", 0, 0, new(), new(), 0, 0, 0);
        var node = doc["tree"]!.AsObject();
        var walked = new List<string>();
        foreach (var part in (path ?? "").Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            var next = (node["d"] as JsonArray)?.OfType<JsonObject>().FirstOrDefault(c => string.Equals(c["n"]?.GetValue<string>(), part, StringComparison.OrdinalIgnoreCase));
            if (next is null || next["u"] is not null) break;
            node = next;
            walked.Add((string)next["n"]!);
        }
        var here = "/" + string.Join('/', walked);
        var folders = (node["d"] as JsonArray)?.OfType<JsonObject>().Select(c => new HubDiskFolder(
            c["n"]!.GetValue<string>(), c["s"]!.GetValue<long>(), c["f"]?.GetValue<long>() ?? 0,
            c["x"] is not null || c["u"] is not null, c["m"] is not null, c["d"] is JsonArray { Count: > 0 } || c["t"] is not null)).ToList() ?? new();
        var dir = FullPath(here);
        var largest = (node["t"] as JsonArray)?.OfType<JsonArray>().Select(t =>
        {
            var name = t[0]!.GetValue<string>();
            return new HubDiskFile(name, t[1]!.GetValue<long>(), KindOf(name), Path.Combine(dir, name));
        }).ToList() ?? new();
        var size = node["s"]!.GetValue<long>();
        var small = node["o"]?.GetValue<long>() ?? 0;
        var otherFiles = Math.Max(0, size - folders.Sum(f => f.Size) - small - largest.Sum(f => f.Size));
        return new HubDiskView("ready", doc["at"]?.GetValue<string>(), doc["seconds"]?.GetValue<long>(), Measuring, "Ce PC", here,
            size, node["f"]?.GetValue<long>() ?? 0, folders, largest, otherFiles, small, node["oc"]?.GetValue<long>() ?? 0);
    }

    private static readonly Dictionary<string, string> Kinds = new(StringComparer.OrdinalIgnoreCase)
    {
        [".jpg"] = "image", [".jpeg"] = "image", [".png"] = "image", [".webp"] = "image", [".heic"] = "image",
        [".gif"] = "image", [".bmp"] = "image", [".tif"] = "image", [".tiff"] = "image", [".avif"] = "image",
        [".mp3"] = "music", [".flac"] = "music", [".m4a"] = "music", [".ogg"] = "music", [".opus"] = "music", [".wav"] = "music", [".aac"] = "music",
        [".epub"] = "book", [".pdf"] = "document", [".doc"] = "document", [".docx"] = "document", [".odt"] = "document", [".txt"] = "document",
        [".mp4"] = "video", [".mkv"] = "video", [".avi"] = "video", [".mov"] = "video", [".webm"] = "video",
    };

    private static string KindOf(string name) => Kinds.TryGetValue(Path.GetExtension(name), out var k) ? k : "other";
}
