using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

namespace AkashicRecords.Hub;

/// <summary>
/// Music downloads done by the hub: the devices queue a link or a "ytsearch1:title artist" search on the shared
/// board (DownloadStore); the hub runs yt-dlp, keeps the audio in its blob store and adds the track to the
/// catalogue itself (a MediaFile and a MusicTrack written under the device id "hub"), exactly as if a device had
/// synchronised it. Each device then lists it at its next sync, streams it, and keeps it if asked (PC: all).
/// What is downloaded, and respecting each source's terms of use, stays the user's responsibility, as with the
/// apps' own downloaders.
/// </summary>
public sealed class DownloadWorker(HubOptions options, HubStore store, BlobStore blobs, DownloadStore board, RequestLog log) : BackgroundService
{
    // What the devices see of the tool (shown on the board).
    public static string? ToolVersion { get; private set; }
    public static string? ToolProblem { get; private set; }
    public static string? Current { get; private set; }

    private DateTime _lastUpdate = DateTime.MinValue;

    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            var next = board.TakeNext();
            if (next is null)
            {
                try { await Task.Delay(TimeSpan.FromSeconds(2), stop); } catch (OperationCanceledException) { return; }
                continue;
            }
            var (job, folder) = next.Value;
            Current = job.Label;
            string? path = null, error = null;
            try
            {
                path = await RunAsync(job, folder, stop);
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
                return; // left "running": queued again at the next start
            }
            catch (Exception ex)
            {
                error = ex.Message.Length > 500 ? ex.Message[..500] : ex.Message;
            }
            Current = null;
            board.Finish(job.Id, path, error);
            log.Info(error is null ? $"download done: {path}" : $"download failed: {job.Label}: {error}");
        }
    }

    // ------------------------------------------------------------------ the tool

    private string ManagedTool => Path.Combine(options.ToolDir, RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "yt-dlp.exe" : "yt-dlp");

    /// <summary>The yt-dlp to run: the configured one, or the hub's own copy, fetched on first use and updated daily.</summary>
    private async Task<string> ToolAsync(CancellationToken ct)
    {
        if (options.YtDlp.Length > 0)
        {
            if (!File.Exists(options.YtDlp)) throw new InvalidOperationException($"yt-dlp introuvable : {options.YtDlp}");
            await VersionAsync(options.YtDlp, ct);
            return options.YtDlp;
        }
        var tool = ManagedTool;
        if (!File.Exists(tool))
        {
            Directory.CreateDirectory(options.ToolDir);
            var asset = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "yt-dlp.exe"
                : RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "yt-dlp_linux_aarch64" : "yt-dlp_linux";
            log.Info($"fetching yt-dlp ({asset}) from its GitHub releases");
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
            var bytes = await http.GetByteArrayAsync($"https://github.com/yt-dlp/yt-dlp/releases/latest/download/{asset}", ct);
            var temp = tool + ".part";
            await File.WriteAllBytesAsync(temp, bytes, ct);
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                File.SetUnixFileMode(temp, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute);
            File.Move(temp, tool, overwrite: true);
            _lastUpdate = DateTime.UtcNow;
        }
        else if (DateTime.UtcNow - _lastUpdate > TimeSpan.FromHours(24))
        {
            // YouTube changes often and refuses an old yt-dlp: refresh it once a day. A failed update keeps the old one.
            _lastUpdate = DateTime.UtcNow;
            try { await RunToolAsync(tool, ["-U"], TimeSpan.FromMinutes(3), ct); } catch (Exception ex) { log.Info($"yt-dlp update failed: {ex.Message}"); }
        }
        await VersionAsync(tool, ct);
        return tool;
    }

    private async Task VersionAsync(string tool, CancellationToken ct)
    {
        if (ToolVersion is not null) return;
        var (code, output, _) = await RunToolAsync(tool, ["--version"], TimeSpan.FromSeconds(30), ct);
        ToolVersion = code == 0 ? output.Trim() : null;
    }

    private static async Task<(int Code, string Out, string Err)> RunToolAsync(string tool, IEnumerable<string> args, TimeSpan timeout, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(tool) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in args) psi.ArgumentList.Add(a); // one argument each: nothing is ever parsed by a shell
        psi.Environment["PYTHONIOENCODING"] = "utf-8";
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("yt-dlp n'a pas démarré.");
        var stdout = p.StandardOutput.ReadToEndAsync(ct);
        var stderr = p.StandardError.ReadToEndAsync(ct);
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(timeout);
        try
        {
            await p.WaitForExitAsync(limit.Token);
        }
        catch (OperationCanceledException)
        {
            try { p.Kill(entireProcessTree: true); } catch { }
            if (ct.IsCancellationRequested) throw;
            throw new TimeoutException("yt-dlp a mis trop de temps.");
        }
        return (p.ExitCode, await stdout, await stderr);
    }

    // ------------------------------------------------------------------ one download

    private async Task<string> RunAsync(DownloadJob job, string folder, CancellationToken ct)
    {
        foreach (var type in new[] { "MusicTrack", "MediaFile" })
            if (!options.AcceptsEntityType(type))
                throw new InvalidOperationException($"Le hub n'accepte pas le type {type} (EntityTypes).");
        string tool;
        try
        {
            tool = await ToolAsync(ct);
            ToolProblem = null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ToolProblem = ex.Message;
            throw new InvalidOperationException($"yt-dlp indisponible : {ex.Message}");
        }

        var work = Path.Combine(options.DownloadWorkDir, job.Id);
        if (Directory.Exists(work)) Directory.Delete(work, recursive: true);
        Directory.CreateDirectory(work);
        try
        {
            var args = new List<string>();
            args.AddRange(options.DownloadArgs.Split(' ', StringSplitOptions.RemoveEmptyEntries));
            if (options.Ffmpeg.Length > 0) args.AddRange(["--ffmpeg-location", options.Ffmpeg]);
            args.AddRange([
                "--no-playlist", "--no-mtime", "--no-progress",
                // Same file name as the apps' downloaders: "Title [id].mp3".
                "-o", Path.Combine(work, "%(title)s [%(id)s].%(ext)s"),
                "--print", "after_move:%(filepath)s\t%(duration)s",
                "--", job.Query,
            ]);
            var (code, output, err) = await RunToolAsync(tool, args, TimeSpan.FromMinutes(20), ct);

            // A failing post-step (e.g. the thumbnail) makes yt-dlp exit non-zero although the audio is there: the
            // file decides.
            var printed = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(l => l.Split('\t')).Where(p => p.Length >= 1 && File.Exists(p[0])).LastOrDefault();
            var file = printed?[0] ?? Directory.EnumerateFiles(work).Where(f => !f.EndsWith(".part")).OrderByDescending(f => new FileInfo(f).Length).FirstOrDefault();
            if (file is null)
            {
                var why = err.Split('\n', StringSplitOptions.RemoveEmptyEntries).LastOrDefault(l => l.Contains("ERROR")) ?? err.Split('\n', StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
                throw new InvalidOperationException(why?.Trim() ?? $"yt-dlp a échoué (code {code}).");
            }
            double? duration = printed is { Length: >= 2 } && double.TryParse(printed[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : null;
            return await AddToCatalogueAsync(file, folder, duration, ct);
        }
        finally
        {
            try { Directory.Delete(work, recursive: true); } catch { }
        }
    }

    /// <summary>Keeps the audio and lists it in the catalogue (MediaFile + MusicTrack), as a device's sync would.</summary>
    private async Task<string> AddToCatalogueAsync(string file, string folder, double? duration, CancellationToken ct)
    {
        var name = Path.GetFileName(file);
        var rel = folder.Length > 0 ? $"{folder}/{name}" : name;
        var path = "music/" + rel;                                   // as sync_file lists it
        var filePath = "music\\" + rel.Replace('/', '\\');           // as the apps store MusicTrack.FilePath
        if (store.HasMusicPath(filePath)) return path;               // already in the catalogue: nothing to add

        string hash;
        await using (var s = File.OpenRead(file)) hash = Convert.ToHexString(await SHA256.HashDataAsync(s, ct)).ToLowerInvariant();
        await using (var s = File.OpenRead(file)) await blobs.SaveAsync(s, hash, options.MaxBlobBytes, ct);

        var size = new FileInfo(file).Length;
        var mediaFile = JsonSerializer.SerializeToElement(new Dictionary<string, object?> { ["Path"] = path, ["Hash"] = hash, ["Size"] = size });
        var track = JsonSerializer.SerializeToElement(new Dictionary<string, object?>
        {
            ["FilePath"] = filePath,
            ["Title"] = Path.GetFileNameWithoutExtension(name),     // what the apps' scanners would call it
            ["Artist"] = "",
            ["Album"] = folder,                                     // the scanners' rule: album = the folder
            ["AddedAt"] = DateTime.Now.ToString("O"),
            ["DurationSeconds"] = duration,
            ["CoverImagePath"] = null,
        });
        // The file first, the track after: a device that sees the track can fetch the file at once.
        var result = store.Push(HubStore.HubDevice, [
            new Mutation(Guid.NewGuid().ToString(), "MediaFile", Guid.NewGuid().ToString(), 0, mediaFile, false),
            new Mutation(Guid.NewGuid().ToString(), "MusicTrack", Guid.NewGuid().ToString(), 0, track, false),
        ]);
        if (result.Results.Any(r => r.Status == MutationStatus.Conflict))
            throw new InvalidOperationException("Le catalogue a refusé le titre.");
        return path;
    }
}

/// <summary>The board's routes: every paired device reads and changes the same board.</summary>
public static class DownloadRoutes
{
    public sealed record JobRequest(string? Query, string? Label, string? ZoneId);
    public sealed record ZoneRequest(string? Name, string? Folder);
    public sealed record MoveRequest(string? ZoneId);

    public static void Map(WebApplication app, HubStore store, DownloadStore board, HubOptions options)
    {
        object Board() => new
        {
            enabled = options.Downloads,
            tool = new { version = DownloadWorker.ToolVersion, problem = DownloadWorker.ToolProblem, current = DownloadWorker.Current },
            zones = board.Zones(),
            jobs = board.Jobs(),
            folders = store.MusicFolders(),
        };

        app.MapGet("/downloads", () => Results.Ok(Board()));

        app.MapPost("/downloads/jobs", (JobRequest? req, HttpContext ctx) =>
        {
            if (!options.Downloads) return Results.Json(new { error = "downloads_disabled" }, statusCode: StatusCodes.Status403Forbidden);
            var query = req?.Query?.Trim() ?? "";
            if (!ValidQuery(query)) return Results.BadRequest(new { error = "query: a http(s) link or a ytsearch search" });
            if (req!.ZoneId is not null && board.Zone(req.ZoneId) is null) return Results.BadRequest(new { error = "unknown zone" });
            var label = string.IsNullOrWhiteSpace(req.Label) ? query.Replace("ytsearch1:", "") : req.Label.Trim();
            if (label.Length > 300) label = label[..300];
            var job = board.AddJob(query, label, req.ZoneId, (string)ctx.Items[HubApp.DeviceItem]!);
            return Results.Json(job, statusCode: StatusCodes.Status201Created);
        });

        app.MapPost("/downloads/jobs/{id}/zone", (string id, MoveRequest? req) =>
        {
            if (req?.ZoneId is not null && board.Zone(req.ZoneId) is null) return Results.BadRequest(new { error = "unknown zone" });
            return board.MoveJob(id, req?.ZoneId) ? Results.Ok(Board()) : Results.Conflict(new { error = "not movable" });
        });
        app.MapPost("/downloads/jobs/{id}/retry", (string id) => board.Retry(id) ? Results.Ok(Board()) : Results.Conflict(new { error = "not failed" }));
        app.MapDelete("/downloads/jobs/{id}", (string id) => board.DeleteJob(id) ? Results.Ok(Board()) : Results.Conflict(new { error = "running or unknown" }));
        app.MapPost("/downloads/clear-done", () => { board.ClearDone(); return Results.Ok(Board()); });

        app.MapPost("/downloads/zones", (ZoneRequest? req) =>
        {
            if (string.IsNullOrWhiteSpace(req?.Name) && string.IsNullOrWhiteSpace(req?.Folder)) return Results.BadRequest(new { error = "name or folder required" });
            return Results.Json(board.AddZone(req!.Name, req.Folder), statusCode: StatusCodes.Status201Created);
        });
        app.MapPost("/downloads/zones/{id}", (string id, ZoneRequest? req) =>
            string.IsNullOrWhiteSpace(req?.Name) ? Results.BadRequest(new { error = "name required" })
            : board.RenameZone(id, req!.Name!) ? Results.Ok(Board()) : Results.NotFound());
        app.MapDelete("/downloads/zones/{id}", (string id) => board.DeleteZone(id) ? Results.Ok(Board()) : Results.NotFound());
    }

    /// <summary>A link, or a yt-dlp search ("ytsearch1:..."); never something yt-dlp would read as an option.</summary>
    public static bool ValidQuery(string q) =>
        q.Length is > 0 and <= 2000 && !q.StartsWith('-') && !q.Any(char.IsControl)
        && (q.StartsWith("https://", StringComparison.OrdinalIgnoreCase) || q.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || System.Text.RegularExpressions.Regex.IsMatch(q, "^ytsearch[0-9]*:.+"));
}
