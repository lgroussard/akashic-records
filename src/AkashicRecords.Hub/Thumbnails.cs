using System.Collections.Concurrent;
using System.Diagnostics;

namespace AkashicRecords.Hub;

/// <summary>
/// Small previews of the pictures the hub holds, so a device can show a whole gallery without fetching every
/// original (3-5 MB each): GET /media/{hash}/thumb returns a JPEG of at most <see cref="Side"/> pixels, about 30 KB.
///
/// Made on first demand and kept in DataDir/thumbs (same hash, same picture: a preview never goes stale).
/// vipsthumbnail (libvips-tools) is preferred: it reads iPhone HEIC pictures and turns them upright from their
/// EXIF orientation. ffmpeg, already there for the downloads, is the fallback for plain JPEG/PNG.
/// A blob that is not a picture (or that neither tool reads) is remembered until the hub restarts.
/// </summary>
public sealed class Thumbnails(HubOptions options, BlobStore blobs)
{
    public const int Side = 480;
    private readonly string _dir = Path.Combine(options.DataDir, "thumbs");
    private readonly SemaphoreSlim _gate = new(2);
    private readonly ConcurrentDictionary<string, bool> _failed = new();
    private readonly ConcurrentDictionary<string, Lazy<Task<string?>>> _making = new();

    public string PathOf(string hash) => Path.Combine(_dir, hash[..2], hash + ".jpg");

    /// <summary>The preview's file, made now if needed; null when the blob is unknown or not a readable picture.</summary>
    public async Task<string?> GetAsync(string hash, CancellationToken ct)
    {
        var target = PathOf(hash);
        if (File.Exists(target)) return target;
        if (!blobs.Exists(hash) || _failed.ContainsKey(hash)) return null;
        // Two screens asking for the same preview at once share one making.
        var making = _making.GetOrAdd(hash, h => new Lazy<Task<string?>>(() => MakeAsync(h, target)));
        try { return await making.Value.WaitAsync(ct); }
        finally { if (making.Value.IsCompleted) _making.TryRemove(hash, out _); }
    }

    private async Task<string?> MakeAsync(string hash, string target)
    {
        await _gate.WaitAsync();
        try
        {
            if (File.Exists(target)) return target;
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            var tmp = target + "." + Guid.NewGuid().ToString("N")[..8] + ".part.jpg";
            try
            {
                var source = blobs.PathOf(hash);
                var ok = await RunAsync("vipsthumbnail", [source, "--size", $"{Side}x{Side}", "-o", tmp + "[Q=78,strip]"], tmp)
                         || await RunAsync(FfmpegExe(), ["-v", "error", "-y", "-i", source, "-frames:v", "1",
                             "-vf", $"scale='min({Side},iw)':'min({Side},ih)':force_original_aspect_ratio=decrease", "-q:v", "5", tmp], tmp);
                if (!ok) { _failed[hash] = true; return null; }
                File.Move(tmp, target, overwrite: true);
                return target;
            }
            finally
            {
                try { File.Delete(tmp); } catch (IOException) { }
            }
        }
        finally { _gate.Release(); }
    }

    private string FfmpegExe()
    {
        var f = options.Ffmpeg;
        var exe = OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg";
        if (f.Length == 0) return exe;
        return Directory.Exists(f) ? Path.Combine(f, exe) : f;
    }

    /// <summary>Runs a tool; true when it ended well and wrote a non-empty file. A missing tool is just false.</summary>
    private static async Task<bool> RunAsync(string exe, string[] args, string output)
    {
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true, CreateNoWindow = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        Process? p;
        try { p = Process.Start(psi); }
        catch (Exception) { return false; } // not installed
        if (p is null) return false;
        using (p)
        {
            _ = p.StandardOutput.ReadToEndAsync();
            _ = p.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            try { await p.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException)
            {
                try { p.Kill(entireProcessTree: true); } catch (Exception) { }
                return false;
            }
            return p.ExitCode == 0 && File.Exists(output) && new FileInfo(output).Length > 0;
        }
    }
}
