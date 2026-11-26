using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace AkashicRecords.Hub;

/// <summary>One file a worker needs, addressed like the blobs: by the SHA-256 of its content.</summary>
public sealed record ComputeFile(string Path, string Hash, long Size);

/// <summary>An upscale model: one .onnx file, run by OnnxUpscaler on the hub and on the PCs.</summary>
public sealed record ComputeModel(string Name, string Label, string Description, bool Hub, List<ComputeFile> Files);

/// <summary>What a device downloads to work: the models (the engine is built into the apps).</summary>
public sealed record ComputeManifest(List<ComputeModel> Models);

/// <summary>
/// The models the hub keeps for every device ("everything is on the hub, each device takes what it can run"):
/// plain files under DataDir/compute/models/&lt;name&gt;.onnx. One model per feature: today NomosWebPhoto only.
/// Read from disk at each call, hashes remembered while a file is unchanged.
/// </summary>
public sealed class ComputeCatalog(HubOptions options)
{
    private readonly ConcurrentDictionary<string, (long Size, long Ticks, string Hash)> _hashes = new();

    // How the known models are presented. Any other model shows under its file name.
    private static readonly Dictionary<string, (string Label, string Description)> Known = new()
    {
        ["nomos-webphoto-4x"] = ("NomosWebPhoto", "Net et contrasté, pensé pour les photos du web compressées."),
    };

    public string ModelsDir => Path.Combine(options.ComputeDir, "models");

    public string ModelPath(string name) => Path.Combine(ModelsDir, name + ".onnx");

    public List<ComputeModel> Models()
    {
        if (!Directory.Exists(ModelsDir)) return new();
        var hubModels = options.HubModels.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return Directory.EnumerateFiles(ModelsDir, "*.onnx")
            .Select(f => (File: f, Name: Path.GetFileNameWithoutExtension(f)))
            .Where(m => IsName(m.Name))
            .Select(m =>
            {
                var (label, description) = Known.TryGetValue(m.Name, out var k) ? k : (m.Name, "");
                return new ComputeModel(m.Name, label, description, hubModels.Contains(m.Name), [Describe(m.File)]);
            })
            // The known model first: it is the one meant when a request names none.
            .OrderBy(m => Known.ContainsKey(m.Name) ? 0 : 1).ThenBy(m => m.Name, StringComparer.Ordinal).ToList();
    }

    public ComputeModel? Model(string name) => Models().FirstOrDefault(m => m.Name == name);

    public ComputeManifest Manifest() => new(Models());

    /// <summary>Where a listed file is on disk, from its hash; null for anything not in the catalogue.</summary>
    public string? PathOfHash(string hash)
    {
        Models(); // fills the cache with what is there now
        foreach (var (path, entry) in _hashes)
            if (entry.Hash == hash && File.Exists(path)) return path;
        return null;
    }

    private ComputeFile Describe(string fullPath)
    {
        var info = new FileInfo(fullPath);
        if (!_hashes.TryGetValue(fullPath, out var cached) || cached.Size != info.Length || cached.Ticks != info.LastWriteTimeUtc.Ticks)
        {
            using var s = File.OpenRead(fullPath);
            cached = (info.Length, info.LastWriteTimeUtc.Ticks, Convert.ToHexString(SHA256.HashData(s)).ToLowerInvariant());
            _hashes[fullPath] = cached;
        }
        return new ComputeFile(Path.GetFileName(fullPath), cached.Hash, cached.Size);
    }

    /// <summary>A model name: letters, digits, '-', '_', '.'; nothing that walks out of a folder.</summary>
    public static bool IsName(string? s) =>
        s is { Length: > 0 and <= 80 } && s.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_' or '.') && !s.StartsWith('.');
}
