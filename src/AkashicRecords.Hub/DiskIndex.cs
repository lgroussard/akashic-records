using System.Text.Json;
using System.Text.Json.Nodes;

namespace AkashicRecords.Hub;

/// <summary>
/// The mini-PC's whole disk, folder by folder, for the "Serveur" screen's browser (a TreeSize of the server).
///
/// The hub runs under a restricted account that cannot see the user's home folder nor the system's protected ones,
/// and it should stay that way. So the measuring is done apart, as root, by this same program:
///   AkashicRecords.Hub index-disk / /var/lib/akashic-hub/disk-index.json
/// run by akashic-disk-index.service every night and whenever an app asks for a refresh (the hub only drops a
/// request file, see the .path unit). The hub then only reads the result. Read-only: nothing is ever changed.
///
/// The index keeps every folder of 1 MB or more; smaller ones are summed up per parent ("petits dossiers"), and
/// each folder keeps its 20 largest files. Other mounted disks (the backup disk…) and the system's virtual
/// folders are listed but not entered. Symbolic links are not followed.
/// </summary>
public static class DiskIndex
{
    public const string FileName = "disk-index.json";
    public const string RequestName = "disk-index.request";
    private const long KeepFolder = 1L << 20;
    private const int KeepFiles = 20;
    private static readonly string[] Virtual = { "/proc", "/sys", "/dev", "/run", "/tmp" };

    // ------------------------------------------------------------------ measuring (run as root)

    public static int Run(string root, string output)
    {
        var started = DateTime.UtcNow;
        root = Path.GetFullPath(root);
        var mounts = OtherMounts(root);
        var tree = Measure(new DirectoryInfo(root), root, mounts, isRoot: true);
        tree["n"] = root;

        var doc = new JsonObject
        {
            ["at"] = DateTime.UtcNow.ToString("O"),
            ["root"] = root,
            ["seconds"] = (long)(DateTime.UtcNow - started).TotalSeconds,
            ["tree"] = tree,
        };
        var tmp = output + ".part";
        File.WriteAllText(tmp, doc.ToJsonString());
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(tmp, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        File.Move(tmp, output, overwrite: true);
        Console.WriteLine($"{root} : {Size((long)tree["s"]!)} en {(long)doc["seconds"]!} s -> {output}");
        return 0;
    }

    /// <summary>Linux file systems that hold the mini-PC's own data: entered like any folder. Every mount point
    /// is not another disk: systemd's protections (ProtectSystem, ReadWritePaths) re-mount /home, the hub's data…
    /// read-only in the service's view, on the same disk.</summary>
    private static readonly HashSet<string> LocalFileSystems = new(StringComparer.Ordinal) { "ext2", "ext3", "ext4", "xfs", "btrfs", "f2fs", "zfs", "jfs", "reiserfs" };

    /// <summary>The mount points not to enter: the system's virtual ones (proc, tmpfs, snap images…) and other
    /// disks, such as the backup disk (ntfs). They are listed, not measured.</summary>
    private static HashSet<string> OtherMounts(string root)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        if (OperatingSystem.IsWindows() || !File.Exists("/proc/self/mounts")) return set;
        foreach (var line in File.ReadLines("/proc/self/mounts"))
        {
            var parts = line.Split(' ');
            if (parts.Length < 3) continue;
            var point = parts[1].Replace("\\040", " ");
            if (point == root || !point.StartsWith(root.TrimEnd('/') + "/")) continue;
            if (!LocalFileSystems.Contains(parts[2])) set.Add(point);
        }
        return set;
    }

    private static JsonObject Measure(DirectoryInfo dir, string root, HashSet<string> mounts, bool isRoot = false)
    {
        var node = new JsonObject { ["n"] = dir.Name };
        long size = 0, files = 0, smallBytes = 0, smallCount = 0;
        var children = new List<JsonObject>();
        var top = new List<(string Name, long Size)>();
        IEnumerable<FileSystemInfo> entries;
        try
        {
            // Listed up front: a folder that cannot be read is said so, instead of silently counting 0.
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
                if (e.LinkTarget is not null) continue; // symbolic link: not followed, not counted
                if (e is DirectoryInfo d)
                {
                    var full = d.FullName;
                    if (mounts.Contains(full) || (isRoot && Virtual.Contains(full)))
                    {
                        children.Add(new JsonObject { ["n"] = d.Name, ["s"] = 0L, ["m"] = true });
                        continue;
                    }
                    var child = Measure(d, root, mounts);
                    var s = (long)child["s"]!;
                    size += s;
                    files += child["f"]?.GetValue<long>() ?? 0;
                    if (s >= KeepFolder || child["x"] is not null) children.Add(child);
                    else { smallBytes += s; smallCount++; }
                }
                else if (e is FileInfo f)
                {
                    var len = f.Length;
                    size += len;
                    files++;
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

    private static string Size(long b) => b >= 1L << 30 ? $"{b / (double)(1L << 30):0.0} Go" : $"{b / (double)(1L << 20):0} Mo";

    // ------------------------------------------------------------------ reading (the hub)

    public sealed record Folder(string Name, long Size, long Files, bool Denied, bool Mount, bool HasChildren);
    /// <summary>A file of the folder. A blob of the catalogue (named by its hash) also says what it is there: Label =
    /// its path in the catalogue, Hash = for the apps to ask for its preview (GET /media/{hash}/thumb). Kind: image,
    /// music, book, video, document, other; "unknown" for a blob the catalogue does not list (results of the hub's work).</summary>
    public sealed record FileEntry(string Name, long Size, string Kind = "other", string? Label = null, string? Hash = null);
    public sealed record View(
        string State, string? At, long? Seconds, bool Refreshing, string Root, string Path, long Size, long Files,
        List<Folder> Folders, List<FileEntry> Largest, long OtherFilesBytes, long SmallFoldersBytes, long SmallFolders);

    private static (DateTime Stamp, JsonObject Doc)? _cache;
    private static readonly object Gate = new();

    /// <summary>One folder of the index and what is directly in it; path "" or "/" = the root.</summary>
    public static View Browse(HubOptions options, string? path, HubStore? store = null)
    {
        var file = System.IO.Path.Combine(options.DataDir, FileName);
        var refreshing = File.Exists(System.IO.Path.Combine(options.DataDir, RequestName));
        var doc = Load(file);
        if (doc is null) return new View("none", null, null, refreshing, "/", "/", 0, 0, new(), new(), 0, 0, 0);

        var root = doc["root"]?.GetValue<string>() ?? "/";
        var node = doc["tree"]!.AsObject();
        var walked = new List<string>();
        foreach (var part in (path ?? "").Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            var next = (node["d"] as JsonArray)?.OfType<JsonObject>().FirstOrDefault(c => c["n"]?.GetValue<string>() == part);
            if (next is null) break; // gone since, or folded into "petits dossiers": stop at the deepest known
            node = next;
            walked.Add(part);
        }

        var folders = (node["d"] as JsonArray)?.OfType<JsonObject>().Select(c => new Folder(
            c["n"]!.GetValue<string>(), c["s"]!.GetValue<long>(), c["f"]?.GetValue<long>() ?? 0,
            c["x"] is not null, c["m"] is not null, c["d"] is JsonArray { Count: > 0 } || c["t"] is not null)).ToList() ?? new();
        var largest = Describe((node["t"] as JsonArray)?.OfType<JsonArray>().Select(t => new FileEntry(t[0]!.GetValue<string>(), t[1]!.GetValue<long>())).ToList() ?? new(), store);
        var size = node["s"]!.GetValue<long>();
        var small = node["o"]?.GetValue<long>() ?? 0;
        var otherFiles = Math.Max(0, size - folders.Sum(f => f.Size) - small - largest.Sum(f => f.Size));

        return new View("ready", doc["at"]?.GetValue<string>(), doc["seconds"]?.GetValue<long>(), refreshing, root,
            "/" + string.Join('/', walked), size, node["f"]?.GetValue<long>() ?? 0, folders, largest, otherFiles, small,
            node["oc"]?.GetValue<long>() ?? 0);
    }

    private static readonly Dictionary<string, string> Kinds = new(StringComparer.OrdinalIgnoreCase)
    {
        [".jpg"] = "image", [".jpeg"] = "image", [".png"] = "image", [".webp"] = "image", [".heic"] = "image", [".heif"] = "image",
        [".gif"] = "image", [".bmp"] = "image", [".tif"] = "image", [".tiff"] = "image", [".avif"] = "image",
        [".mp3"] = "music", [".flac"] = "music", [".m4a"] = "music", [".ogg"] = "music", [".opus"] = "music", [".wav"] = "music", [".aac"] = "music",
        [".epub"] = "book", [".pdf"] = "document", [".doc"] = "document", [".docx"] = "document", [".odt"] = "document", [".txt"] = "document",
        [".mp4"] = "video", [".mkv"] = "video", [".avi"] = "video", [".mov"] = "video", [".webm"] = "video",
    };

    private static string KindOf(string name) => Kinds.TryGetValue(System.IO.Path.GetExtension(name), out var k) ? k : "other";

    /// <summary>Each file's kind, and for the catalogue's blobs (hash names) what they are there.</summary>
    private static List<FileEntry> Describe(List<FileEntry> files, HubStore? store)
    {
        var hashes = files.Select(f => f.Name.ToLowerInvariant()).Where(BlobStore.IsHash).ToList();
        var known = hashes.Count > 0 && store is not null ? store.CataloguePaths(hashes) : new Dictionary<string, string>();
        return files.Select(f =>
        {
            var h = f.Name.ToLowerInvariant();
            if (!BlobStore.IsHash(h)) return f with { Kind = KindOf(f.Name) };
            return known.TryGetValue(h, out var path)
                ? f with { Kind = KindOf(path), Label = path, Hash = h }
                : f with { Kind = "unknown", Hash = h };
        }).ToList();
    }

    private static JsonObject? Load(string file)
    {
        if (!File.Exists(file)) return null;
        var stamp = File.GetLastWriteTimeUtc(file);
        lock (Gate)
        {
            if (_cache is { } c && c.Stamp == stamp) return c.Doc;
            try
            {
                var doc = JsonNode.Parse(File.ReadAllText(file))!.AsObject();
                _cache = (stamp, doc);
                return doc;
            }
            catch (Exception) { return null; }
        }
    }

    /// <summary>Asks for a new measure: the system service watches this file (akashic-disk-index.path).</summary>
    public static void RequestRefresh(HubOptions options) =>
        File.WriteAllText(System.IO.Path.Combine(options.DataDir, RequestName), DateTime.UtcNow.ToString("O"));
}
