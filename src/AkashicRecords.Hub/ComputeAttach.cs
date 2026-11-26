using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using SkiaSharp;

namespace AkashicRecords.Hub;

/// <summary>
/// Upscaling an image that is already in the app (an Archives photo, a journal photo, an Art picture), and putting
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

    /// <summary>The picture of a row, ready to be upscaled: its blob (made readable first if the upscaler cannot
    /// read it, e.g. an iPhone HEIC), its name and size. Throws InvalidOperationException with a message for the user.</summary>
    public async Task<(string Hash, string Name, long Size)> InputAsync(string kind, string entityId, CancellationToken ct)
    {
        if (!Kinds.TryGetValue(kind, out var field)) throw new InvalidOperationException("Ce type d'image ne peut pas être agrandi.");
        var payload = store.Current(kind, entityId) ?? throw new InvalidOperationException("Cette image n'est pas (encore) sur le serveur : synchronise d'abord.");
        var path = payload.TryGetProperty(field, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
        if (string.IsNullOrWhiteSpace(path)) throw new InvalidOperationException("Cet élément n'a pas d'image.");
        var hash = store.FileHash(path);
        if (hash is null || !blobs.Exists(hash)) throw new InvalidOperationException("Le fichier de cette image n'est pas (encore) sur le serveur : synchronise d'abord.");

        var name = Path.GetFileName(path.Replace('\\', '/'));
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
    public void Attach(ComputeStore jobs, ComputeJob job)
    {
        if (job.AttachType is null || job.AttachId is null || job.ResultHash is null) return;
        try
        {
            jobs.Attached(job.Id, Add(job.AttachType, job.AttachId, job.ResultHash), null);
        }
        catch (Exception ex)
        {
            jobs.Attached(job.Id, null, ex.Message);
            log.Info($"upscale result not put next to {job.AttachType} {job.AttachId}: {ex.Message}");
        }
    }

    private string Add(string kind, string entityId, string resultHash)
    {
        var field = Kinds[kind];
        var original = store.Current(kind, entityId) ?? throw new InvalidOperationException("L'original a été supprimé entre-temps.");
        var path = original.GetProperty(field).GetString()!;

        // In the app the picture is a JPEG (a tenth of the PNG kept in the history, no visible difference).
        string hash;
        long size;
        using (var bitmap = SKBitmap.Decode(blobs.PathOf(resultHash)) ?? throw new InvalidOperationException("Résultat illisible."))
        using (var image = SKImage.FromBitmap(bitmap))
        using (var jpeg = image.Encode(SKEncodedImageFormat.Jpeg, 92))
        using (var s = jpeg.AsStream())
        {
            var saved = blobs.SaveAsync(s, null, long.MaxValue, CancellationToken.None).GetAwaiter().GetResult();
            (hash, size) = (saved.Hash, saved.Size);
        }

        // Same folder and same slash style as the original, its name with "-x4".
        var sep = path.Contains('\\') ? '\\' : '/';
        var cut = path.LastIndexOfAny(['/', '\\']);
        var folder = cut >= 0 ? path[..(cut + 1)] : "";
        var newPath = folder + Path.GetFileNameWithoutExtension(path[(cut + 1)..]) + "-x4.jpg";
        for (var i = 2; store.FileHash(newPath) is not null; i++)
            newPath = folder + Path.GetFileNameWithoutExtension(path[(cut + 1)..]) + $"-x4-{i}.jpg";

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
