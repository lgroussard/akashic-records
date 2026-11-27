using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using SkiaSharp;

namespace AkashicRecords.Hub;

/// <summary>
/// Putting upscaled images in the app. An image upscaled from outside the app (dropped in the tool) goes into the
/// Archives album "Agrandies". An image that is already in the app (an Archives photo, a journal photo, an Art picture) gets
/// the result next to it: same album, same journal entry, same board or canvas, as a new picture beside the
/// original (which is never touched). Done by the hub, once for every device: the original is read from the
/// hub's own copy (no upload), and the result goes into the catalogue like any synchronised row, so every device
/// gets it at its next sync.
/// </summary>
public sealed class ComputeAttach(HubStore store, BlobStore blobs, HubOptions options, RequestLog log)
{
    /// <summary>The kinds of rows that can be upscaled, and the column holding their picture.</summary>
    public static readonly IReadOnlyDictionary<string, string> Kinds = new Dictionary<string, string>
    {
        ["Photo"] = "ImagePath",          // Archives
        ["JournalPhoto"] = "ImagePath",   // journal entries
        ["RefImage"] = "ImagePath",       // Art › Références
        ["CanvasElement"] = "ImagePath",  // Art › Archive artistique (a picture on a project's canvas)
    };

    /// <summary>A file name up to the apps' edit marker (".edit-"): the picture's first name ("abc.heic.edit-1a2b3c4d.jpg" → "abc.heic").</summary>
    public static string RootName(string fileName)
    {
        var i = fileName.IndexOf(".edit-", StringComparison.Ordinal);
        return i >= 0 ? fileName[..i] : fileName;
    }

    /// <summary>The name of a copy made by upscaling: "…-x4.jpg" or "…-x4-2.jpg".</summary>
    private static readonly System.Text.RegularExpressions.Regex AlreadyUpscaled =
        new(@"-x4(-\d+)?\.jpg$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    /// <summary>A photo of the "Agrandies" album: already the result of an upscale.</summary>
    private bool InAgrandiesAlbum(JsonElement photo)
    {
        var album = store.FindLive("PhotoAlbum", "Name", AlbumName);
        return album is not null && photo.TryGetProperty("AlbumId", out var a) && a.ValueKind == JsonValueKind.String && a.GetString() == album;
    }

    /// <summary>The picture of a row, ready to be upscaled: its blob (made readable first if the upscaler cannot
    /// read it, e.g. an iPhone HEIC), its name and size. Throws InvalidOperationException with a message for the user.</summary>
    public async Task<(string Hash, string Name, long Size)> InputAsync(string kind, string entityId, CancellationToken ct, bool refuseUpscaled = true)
    {
        if (!Kinds.TryGetValue(kind, out var field)) throw new InvalidOperationException("Ce type d'image ne peut pas être agrandi.");
        var payload = store.Current(kind, entityId) ?? throw new InvalidOperationException("Cette image n'est pas (encore) sur le serveur : synchronise d'abord.");
        var path = payload.TryGetProperty(field, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
        if (string.IsNullOrWhiteSpace(path)) throw new InvalidOperationException("Cet élément n'a pas d'image.");
        var hash = store.FileHash(path);
        if (hash is null || !blobs.Exists(hash)) throw new InvalidOperationException("Le fichier de cette image n'est pas (encore) sur le serveur : synchronise d'abord.");

        var name = Path.GetFileName(path.Replace('\\', '/'));
        // Upscaling an upscaled picture again adds next to nothing (the model works from detail it made up): refused.
        if (refuseUpscaled && (AlreadyUpscaled.IsMatch(RootName(name)) || (kind == "Photo" && InAgrandiesAlbum(payload))))
            throw new InvalidOperationException("Cette image est déjà agrandie : l'agrandir encore n'ajouterait presque rien.");
        if (OnnxUpscaler.InputExtensions.Contains(Path.GetExtension(name).ToLowerInvariant()))
            return (hash, name, new FileInfo(blobs.PathOf(hash)).Length);

        // HEIC and the like: turned into a JPEG the upscaler reads (upright, from the EXIF orientation).
        var tmp = Path.Combine(options.ComputeWorkDir, Guid.NewGuid().ToString("N") + ".jpg");
        Directory.CreateDirectory(options.ComputeWorkDir);
        try
        {
            var ok = await RunAsync("vips", ["autorot", blobs.PathOf(hash), tmp + "[Q=95]"], tmp, ct)
                     || await RunAsync("ffmpeg", ["-v", "error", "-y", "-i", blobs.PathOf(hash), "-frames:v", "1", "-q:v", "2", tmp], tmp, ct);
            if (!ok) throw new InvalidOperationException("Format d'image que le serveur ne sait pas lire.");
            MediaResponse saved;
            await using (var s = File.OpenRead(tmp)) saved = await blobs.SaveAsync(s, null, long.MaxValue, ct);
            return (saved.Hash, Path.ChangeExtension(name, ".jpg"), saved.Size);
        }
        finally
        {
            try { File.Delete(tmp); } catch (IOException) { }
        }
    }

    /// <summary>Puts a finished job's result next to its original. Never throws: what happened is recorded on the job.</summary>
    /// <summary>The Archives album where the images upscaled from outside the app (dropped in the tool) go.</summary>
    public const string AlbumName = "Agrandies";

    private readonly object _albumLock = new();

    /// <summary>Puts a finished job's result in the app: next to its original when it came from the app, else in the
    /// "Agrandies" album. Never throws: what happened is recorded on the job.</summary>
    public void Attach(ComputeStore jobs, ComputeJob job)
    {
        if (job.ResultHash is null) return;
        try
        {
            var path = job.AttachType is null || job.AttachId is null ? AddToAlbum(job)
                : job.Kind is null ? Add(job.AttachType, job.AttachId, job.ResultHash)
                : ReplaceAsEdit(job.AttachType, job.AttachId, job.ResultHash);
            jobs.Attached(job.Id, path, null);
        }
        catch (Exception ex)
        {
            jobs.Attached(job.Id, null, ex.Message);
            log.Info($"upscale result of {job.InputName} not put in the app: {ex.Message}");
        }
    }

    /// <summary>
    /// Once, at start: the results of the tool made before the album existed go into it too. One per image (the
    /// newest result when the same image was upscaled several times); the other ones are only marked as handled.
    /// </summary>
    public void PlacePastResults(ComputeStore jobs)
    {
        var waiting = jobs.Jobs(1000).Where(j => j.Status == "done" && j.ResultHash is not null && j.AttachType is null
                                                  && j.AttachedPath is null && j.AttachError is null).ToList();
        foreach (var group in waiting.GroupBy(j => j.InputHash))
        {
            var ordered = group.OrderByDescending(j => j.FinishedAt, StringComparer.Ordinal).ToList();
            Attach(jobs, ordered[0]);
            foreach (var older in ordered.Skip(1)) jobs.Attached(older.Id, "(même image, version plus récente rangée)", null);
        }
        if (waiting.Count > 0) log.Info($"upscale results placed in the album {AlbumName}: {waiting.GroupBy(j => j.InputHash).Count()}");
    }

    private string AddToAlbum(ComputeJob job)
    {
        var (hash, size) = Jpeg(job.ResultHash!);
        var now = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss.fffffff");
        lock (_albumLock)
        {
            var album = store.FindLive("PhotoAlbum", "Name", AlbumName);
            var mutations = new List<Mutation>();
            if (album is null)
            {
                album = Guid.NewGuid().ToString();
                mutations.Add(new Mutation(Guid.NewGuid().ToString(), "PhotoAlbum", album, 0,
                    JsonSerializer.SerializeToElement(new Dictionary<string, object?> { ["Name"] = AlbumName, ["CreatedAt"] = now }), false));
            }
            // Where the Archives keep their photos, under a name of its own.
            var path = $"media\\archive\\{Guid.NewGuid():N}.jpg";
            mutations.Add(new Mutation(Guid.NewGuid().ToString(), "MediaFile", Guid.NewGuid().ToString(), 0,
                JsonSerializer.SerializeToElement(new Dictionary<string, object?> { ["Path"] = path.Replace('\\', '/'), ["Hash"] = hash, ["Size"] = size }), false));
            mutations.Add(new Mutation(Guid.NewGuid().ToString(), "Photo", Guid.NewGuid().ToString(), 0,
                JsonSerializer.SerializeToElement(new Dictionary<string, object?>
                {
                    ["ImagePath"] = path,
                    ["Title"] = Path.GetFileNameWithoutExtension(job.InputName),
                    ["Description"] = "",
                    ["TakenDate"] = null,
                    ["Tags"] = "",
                    ["IsFavorite"] = 0,
                    ["AlbumId"] = album,
                    ["ImportedAt"] = now,
                }), false));
            var result = store.Push(HubStore.HubDevice, mutations);
            if (result.Results.Any(r => r.Status == MutationStatus.Conflict)) throw new InvalidOperationException("Le catalogue a refusé l'image.");
            return path;
        }
    }

    /// <summary>In the app a picture is a JPEG (a tenth of the PNG kept in the history, no visible difference).</summary>
    private (string Hash, long Size) Jpeg(string resultHash)
    {
        using var bitmap = SKBitmap.Decode(blobs.PathOf(resultHash)) ?? throw new InvalidOperationException("Résultat illisible.");
        using var image = SKImage.FromBitmap(bitmap);
        using var jpeg = image.Encode(SKEncodedImageFormat.Jpeg, 92);
        using var s = jpeg.AsStream();
        var saved = blobs.SaveAsync(s, null, long.MaxValue, CancellationToken.None).GetAwaiter().GetResult();
        return (saved.Hash, saved.Size);
    }

    private string Add(string kind, string entityId, string resultHash)
    {
        var field = Kinds[kind];
        var original = store.Current(kind, entityId) ?? throw new InvalidOperationException("L'original a été supprimé entre-temps.");
        var path = original.GetProperty(field).GetString()!;

        var (hash, size) = Jpeg(resultHash);

        // Same folder and same slash style as the original, its name with "-x4".
        var sep = path.Contains('\\') ? '\\' : '/';
        var cut = path.LastIndexOfAny(['/', '\\']);
        var folder = cut >= 0 ? path[..(cut + 1)] : "";
        // Named after the picture's first name: an edit's marker (".edit-…", see the apps' image editing) is left out, so
        // the copy is a picture of its own, never taken for an edit that could be "reverted".
        var baseName = Path.GetFileNameWithoutExtension(RootName(path[(cut + 1)..]));
        var newPath = folder + baseName + "-x4.jpg";
        for (var i = 2; store.FileHash(newPath) is not null; i++)
            newPath = folder + baseName + $"-x4-{i}.jpg";

        var row = JsonNode.Parse(original.GetRawText())!.AsObject();
        row[field] = newPath;
        var now = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss.fffffff");
        switch (kind)
        {
            case "Photo":
                row["ImportedAt"] = now;
                break;
            case "RefImage":
                row["CreatedAt"] = now;
                if (row["Width"] is JsonValue w && w.TryGetValue<int>(out var wi)) row["Width"] = wi * OnnxUpscaler.Scale;
                if (row["Height"] is JsonValue h && h.TryGetValue<int>(out var hi)) row["Height"] = hi * OnnxUpscaler.Scale;
                if (row["Title"] is JsonValue t && t.TryGetValue<string>(out var title) && title.Length > 0) row["Title"] = title + " (×4)";
                break;
            case "CanvasElement":
                // Beside the original on the canvas, same size on screen.
                var x = row["X"] is JsonValue xv && xv.TryGetValue<double>(out var xd) ? xd : 0;
                var width = row["Width"] is JsonValue wv && wv.TryGetValue<double>(out var wd) ? wd : 200;
                row["X"] = x + width + 24;
                break;
        }

        var mediaFile = JsonSerializer.SerializeToElement(new Dictionary<string, object?> { ["Path"] = newPath.Replace('\\', '/'), ["Hash"] = hash, ["Size"] = size });
        var entity = JsonSerializer.SerializeToElement(row);
        // The file first, the row after: a device that sees the row can fetch the file at once.
        var result = store.Push(HubStore.HubDevice, [
            new Mutation(Guid.NewGuid().ToString(), "MediaFile", Guid.NewGuid().ToString(), 0, mediaFile, false),
            new Mutation(Guid.NewGuid().ToString(), kind, Guid.NewGuid().ToString(), 0, entity, false),
        ]);
        if (result.Results.Any(r => r.Status == MutationStatus.Conflict)) throw new InvalidOperationException("Le catalogue a refusé l'image.");
        return newPath;
    }

    /// <summary>
    /// An AI edit or restore of a picture of the app: the same row now shows the result, exactly as an edit made in the
    /// apps' editor (ImageEditing on the PC, ImageEdits on the phone): a new JPEG next to the current file, named
    /// &lt;root&gt;.edit-&lt;8 hex&gt;.jpg, the original left untouched, so « Revenir à l'original » and « Rendre définitif »
    /// work on it as on any edit.
    /// </summary>
    private string ReplaceAsEdit(string kind, string entityId, string resultHash)
    {
        var field = Kinds[kind];
        var current = store.Current(kind, entityId) ?? throw new InvalidOperationException("L'image a été supprimée entre-temps.");
        var path = current.GetProperty(field).GetString()!;
        var (hash, size) = Jpeg(resultHash);
        int width, height;
        using (var codec = SKCodec.Create(blobs.PathOf(hash))) (width, height) = (codec.Info.Width, codec.Info.Height);

        var cut = path.LastIndexOfAny(['/', '\\']);
        var folder = cut >= 0 ? path[..(cut + 1)] : "";
        var root = RootName(path[(cut + 1)..]);
        string newPath;
        do newPath = folder + root + ".edit-" + Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(4)).ToLowerInvariant() + ".jpg";
        while (store.FileHash(newPath) is not null);

        var row = JsonNode.Parse(current.GetRawText())!.AsObject();
        row[field] = newPath;
        switch (kind)
        {
            case "RefImage":
                row["Width"] = width;
                row["Height"] = height;
                break;
            case "CanvasElement":
                // The box on the canvas keeps its width and takes the new picture's shape.
                if (row["Width"] is JsonValue wv && wv.TryGetValue<double>(out var boxWidth) && boxWidth > 0)
                    row["Height"] = boxWidth * height / width;
                break;
        }

        var mediaFile = JsonSerializer.SerializeToElement(new Dictionary<string, object?> { ["Path"] = newPath.Replace('\\', '/'), ["Hash"] = hash, ["Size"] = size });
        var result = store.Push(HubStore.HubDevice, [
            new Mutation(Guid.NewGuid().ToString(), "MediaFile", Guid.NewGuid().ToString(), 0, mediaFile, false),
            new Mutation(Guid.NewGuid().ToString(), kind, entityId, store.Rev(kind, entityId), JsonSerializer.SerializeToElement(row), false),
        ]);
        if (result.Results.Any(r => r.Status == MutationStatus.Conflict))
            throw new InvalidOperationException("L'image a changé pendant le travail : la version modifiée n'a pas été posée (elle reste dans la liste).");
        return newPath;
    }

    /// <summary>Runs a tool; true when it ended well and wrote a non-empty file. A missing tool is just false.</summary>
    private static async Task<bool> RunAsync(string exe, string[] args, string output, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true, CreateNoWindow = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        Process? p;
        try { p = Process.Start(psi); }
        catch (Exception) { return false; }
        if (p is null) return false;
        using (p)
        {
            var err = p.StandardError.ReadToEndAsync(ct);
            var outp = p.StandardOutput.ReadToEndAsync(ct);
            await p.WaitForExitAsync(ct);
            await err; await outp;
            return p.ExitCode == 0 && File.Exists(output) && new FileInfo(output).Length > 0;
        }
    }
}
