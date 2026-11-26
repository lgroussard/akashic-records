using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace AkashicRecords.Hub;

/// <summary>One file a worker needs, addressed like the blobs: by the SHA-256 of its content.</summary>
public sealed record ComputeFile(string Path, string Hash, long Size);

/// <summary>An upscale model: a .param + .bin pair for realesrgan-ncnn-vulkan.</summary>
public sealed record ComputeModel(string Name, string Label, string Description, int Order, bool Hub, List<ComputeFile> Files);

/// <summary>What a device of one platform downloads to work: the program for its platform and every model.</summary>
public sealed record ComputeManifest(string Tool, string Rid, List<ComputeFile> ToolFiles, List<ComputeModel> Models);

/// <summary>
/// The tools and models the hub keeps for every device ("everything is on the hub, each device takes what it can
/// run"). Plain files under DataDir/compute, dropped there by hand or by the install script:
///   compute/tools/realesrgan/&lt;rid&gt;/...   the program for one platform (linux-x64, win-x64...)
///   compute/models/&lt;name&gt;.param + .bin     one model; adding a model = adding its two files
/// Read from disk at each call (a handful of files), hashes remembered while a file is unchanged.
/// </summary>
public sealed class ComputeCatalog(HubOptions options)
{
    public const string ToolName = "realesrgan";

    private readonly ConcurrentDictionary<string, (long Size, long Ticks, string Hash)> _hashes = new();

    // How the known models are presented. Any other model shows under its file name.
    private static readonly Dictionary<string, (string Label, string Description, int Order)> Known = new()
    {
        ["high-fidelity-4x"] = ("High Fidelity", "Fidèle à l'original, garde le grain de la peau et des tissus.", 1),
        ["ultrasharp-4x"] = ("UltraSharp", "Le plus net : contours et petits détails très marqués.", 2),
        ["ultramix-balanced-4x"] = ("UltraMix", "Équilibré entre netteté et douceur.", 3),
        ["remacri-4x"] = ("Remacri", "Doux et naturel, ajoute peu de détails.", 4),
        ["realesrgan-x4plus"] = ("Real-ESRGAN x4plus", "Lisse beaucoup : peau cireuse, mais propre.", 5),
        ["realesrgan-x4plus-anime"] = ("Real-ESRGAN anime", "Pour les dessins et les animés.", 6),
    };

    public string ModelsDir => Path.Combine(options.ComputeDir, "models");
    public string ToolDir(string rid) => Path.Combine(options.ComputeDir, "tools", ToolName, rid);

    public List<ComputeModel> Models()
    {
        if (!Directory.Exists(ModelsDir)) return new();
        var hubModels = options.HubModels.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var list = new List<ComputeModel>();
        foreach (var param in Directory.EnumerateFiles(ModelsDir, "*.param"))
        {
            var name = Path.GetFileNameWithoutExtension(param);
            var bin = Path.ChangeExtension(param, ".bin");
            if (!File.Exists(bin) || !IsName(name)) continue;
            var (label, description, order) = Known.TryGetValue(name, out var k) ? k : (name, "", 100);
            list.Add(new ComputeModel(name, label, description, order, hubModels.Contains(name),
                [Describe(param, Path.GetFileName(param)), Describe(bin, Path.GetFileName(bin))]));
        }
        return list.OrderBy(m => m.Order).ThenBy(m => m.Name, StringComparer.Ordinal).ToList();
    }

    public ComputeModel? Model(string name) => Models().FirstOrDefault(m => m.Name == name);

    /// <summary>The program files for a platform; empty when the hub has no build of the tool for it.</summary>
    public List<ComputeFile> ToolFiles(string rid)
    {
        if (!IsName(rid)) return new();
        var dir = ToolDir(rid);
        if (!Directory.Exists(dir)) return new();
        return Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
            .Select(f => Describe(f, Path.GetRelativePath(dir, f).Replace('\\', '/')))
            .OrderBy(f => f.Path, StringComparer.Ordinal).ToList();
    }

    public ComputeManifest Manifest(string rid) => new(ToolName, rid, ToolFiles(rid), Models());

    /// <summary>Where a listed file is on disk, from its hash; null for anything not in the catalogue.</summary>
    public string? PathOfHash(string hash)
    {
        foreach (var (path, entry) in _hashes)
            if (entry.Hash == hash && File.Exists(path)) return path;
        // Not seen yet in this run: list everything once (which fills the cache), then look again.
        Models();
        if (Directory.Exists(Path.Combine(options.ComputeDir, "tools", ToolName)))
            foreach (var rid in Directory.EnumerateDirectories(Path.Combine(options.ComputeDir, "tools", ToolName)))
                ToolFiles(Path.GetFileName(rid));
        foreach (var (path, entry) in _hashes)
            if (entry.Hash == hash && File.Exists(path)) return path;
        return null;
    }

    /// <summary>The hub's own executable for its platform, or null when it has none.</summary>
    public string? HubTool()
    {
        var rid = System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier;
        var exe = OperatingSystem.IsWindows() ? "realesrgan-ncnn-vulkan.exe" : "realesrgan-ncnn-vulkan";
        foreach (var candidate in new[] { rid, OperatingSystem.IsWindows() ? "win-x64" : "linux-x64" })
        {
            var path = Path.Combine(ToolDir(candidate), exe);
            if (File.Exists(path)) return path;
        }
        return null;
    }

    private ComputeFile Describe(string fullPath, string relative)
    {
        var info = new FileInfo(fullPath);
        if (!_hashes.TryGetValue(fullPath, out var cached) || cached.Size != info.Length || cached.Ticks != info.LastWriteTimeUtc.Ticks)
        {
            using var s = File.OpenRead(fullPath);
            cached = (info.Length, info.LastWriteTimeUtc.Ticks, Convert.ToHexString(SHA256.HashData(s)).ToLowerInvariant());
            _hashes[fullPath] = cached;
        }
        return new ComputeFile(relative, cached.Hash, cached.Size);
    }

    /// <summary>A model or platform name: letters, digits, '-', '_', '.'; nothing that walks out of a folder.</summary>
    public static bool IsName(string? s) =>
        s is { Length: > 0 and <= 80 } && s.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_' or '.') && !s.StartsWith('.');
}
