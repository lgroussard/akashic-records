using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using AkashicRecords.Sync;

namespace AkashicRecords.App.Sync;

/// <summary>
/// This computer's part in the hub's shared computing (the hub's Compute.cs): while Akashic runs and is paired,
/// it asks the hub for upscale jobs, saying which models it can run. The model (one .onnx, NomosWebPhoto) comes from
/// the hub the first time a job needs it and is kept in &lt;app&gt;\compute; the engine (OnnxUpscaler) is part of the
/// app. The work runs on this computer's graphics card, the result goes back to the hub, where every device can
/// fetch it. The hub only does a job itself (slowly, on its processor) when no device able to do it is around.
/// </summary>
public sealed class ComputeService
{
    public static ComputeService Instance { get; } = new();

    private static readonly TimeSpan Idle = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan ManifestAge = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan PauseAfterFailure = TimeSpan.FromMinutes(5);

    private readonly string _root = Path.Combine(AppContext.BaseDirectory, "compute");
    private readonly string _settingsPath = Path.Combine(AppContext.BaseDirectory, "config", "compute.json");
    private readonly Dictionary<string, (long Size, long Ticks, string Hash)> _hashes = new();
    private HubComputeManifest? _manifest;
    private DateTime _manifestAt = DateTime.MinValue;
    private DateTime _pausedUntil = DateTime.MinValue;
    private bool _started;

    private ComputeService() => (Worker, ComfyUiDir) = LoadSettings();

    /// <summary>Does this computer take jobs? (The window's checkbox; on by default.)</summary>
    public bool Worker { get; private set; }

    /// <summary>The portable ComfyUI folder used for the AI edits (FLUX Kontext), or null: chosen in the window.</summary>
    public string? ComfyUiDir { get; private set; }

    /// <summary>What this computer is doing now, for the window; null when idle.</summary>
    public string? Current { get; private set; }

    /// <summary>Why this computer does not take jobs right now, for the window; null when all is fine.</summary>
    public string? Problem { get; private set; }

    public event Action? StateChanged;

    public static string Rid => RuntimeInformation.RuntimeIdentifier.StartsWith("win", StringComparison.OrdinalIgnoreCase) ? "win-x64" : RuntimeInformation.RuntimeIdentifier;

    private string ModelsDir => Path.Combine(_root, "models");
    private string ResultsDir => Path.Combine(_root, "results");
    private string WorkDir => Path.Combine(_root, "work");

    public void SetWorker(bool on)
    {
        Worker = on;
        SaveSettings();
        if (on) { _pausedUntil = DateTime.MinValue; Problem = null; }
        StateChanged?.Invoke();
    }

    public void SetComfyUiDir(string? dir)
    {
        ComfyUiDir = string.IsNullOrWhiteSpace(dir) ? null : dir;
        SaveSettings();
        _pausedUntil = DateTime.MinValue;
        StateChanged?.Invoke();
    }

    private void SaveSettings()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_settingsPath)!);
            File.WriteAllText(_settingsPath, JsonSerializer.Serialize(new { Worker, ComfyUi = ComfyUiDir }));
        }
        catch (IOException) { /* the choice still holds for this run */ }
    }

    private (bool Worker, string? ComfyUi) LoadSettings()
    {
        try
        {
            if (!File.Exists(_settingsPath)) return (true, null);
            using var doc = JsonDocument.Parse(File.ReadAllText(_settingsPath));
            var r = doc.RootElement;
            var worker = !r.TryGetProperty("Worker", out var w) || w.ValueKind != JsonValueKind.False;
            var comfy = r.TryGetProperty("ComfyUi", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : null;
            return (worker, comfy);
        }
        catch (Exception ex) when (ex is IOException or JsonException) { return (true, null); }
    }

    /// <summary>Starts asking the hub for work, in the background, for the life of the app.</summary>
    public void Start()
    {
        if (_started) return;
        _started = true;
        // What the older engine left here (its program and .param/.bin models): no longer used.
        try
        {
            if (Directory.Exists(Path.Combine(_root, "tools"))) Directory.Delete(Path.Combine(_root, "tools"), recursive: true);
            if (Directory.Exists(ModelsDir))
                foreach (var old in Directory.EnumerateFiles(ModelsDir).Where(f => f.EndsWith(".param") || f.EndsWith(".bin"))) File.Delete(old);
        }
        catch (IOException) { }
        _ = Task.Run(LoopAsync);
    }

    private async Task LoopAsync()
    {
        while (true)
        {
            var settings = SyncService.Instance.Settings;
            if (!settings.IsPaired || !Worker || DateTime.UtcNow < _pausedUntil)
            {
                await Task.Delay(TimeSpan.FromSeconds(10));
                continue;
            }
            try
            {
                var outcome = await OnceAsync(settings);
                if (outcome == Outcome.NoModel) await Task.Delay(TimeSpan.FromMinutes(1));
                else if (outcome == Outcome.Nothing) await Task.Delay(Idle);
            }
            catch (HubException)
            {
                await Task.Delay(TimeSpan.FromSeconds(30)); // hub away: try again later, quietly
            }
            catch (Exception ex)
            {
                SetProblem(ex.Message);
                await Task.Delay(TimeSpan.FromSeconds(30));
            }
        }
    }

    private enum Outcome { Nothing, NoModel, Worked }

    /// <summary>One request for work, and the job if one came.</summary>
    private async Task<Outcome> OnceAsync(SyncSettings settings)
    {
        using var hub = new HubClient(settings.HubUrl, settings.Token);
        if (_manifest is null || DateTime.UtcNow - _manifestAt > ManifestAge)
        {
            _manifest = await hub.ComputeManifestAsync();
            _manifestAt = DateTime.UtcNow;
        }
        if (_manifest.Models.Count == 0)
        {
            SetProblem("Le serveur n'a pas encore de modèle d'agrandissement.");
            return Outcome.NoModel;
        }
        var models = _manifest.Models.Select(m => m.Name).ToList();
        // The AI edits, when this computer's ComfyUI has what they need.
        if (ComfyUi.Instance.Available) models.Add(ComfyUi.Model);
        var job = await hub.ClaimComputeAsync(string.IsNullOrEmpty(settings.DeviceName) ? Environment.MachineName : settings.DeviceName, Rid, models);
        if (job is null)
        {
            SetProblem(null);
            return Outcome.Nothing;
        }
        await RunAsync(hub, _manifest, job);
        return Outcome.Worked;
    }

    /// <summary>For `--compute-once`: asks once, does the job if one comes, says what happened.</summary>
    public async Task<string> RunOnceAsync()
    {
        var settings = SyncService.Instance.Settings;
        if (!settings.IsPaired) return "non appaire";
        try
        {
            var outcome = await OnceAsync(settings);
            return Problem is not null ? "ERREUR: " + Problem : outcome == Outcome.Worked ? "OK: un travail fait" : "OK: rien a faire";
        }
        catch (Exception ex)
        {
            return "ERREUR: " + ex.Message;
        }
    }

    private async Task RunAsync(HubClient hub, HubComputeManifest manifest, HubComputeJob job)
    {
        var work = Path.Combine(WorkDir, job.Id);
        try
        {
            Current = job.Kind is null ? job.InputName : $"{job.InputName} (IA)";
            StateChanged?.Invoke();
            Directory.CreateDirectory(work);
            string output;
            var versions = new List<string>(); // several tries: every version (the first is `output`)
            var onGpu = true;
            var clock = Stopwatch.StartNew();
            if (job.Kind is null)
            {
                // The model, fetched from the hub when missing or changed. The engine is part of the app.
                var model = manifest.Models.FirstOrDefault(m => m.Name == job.Model) ?? throw new InvalidOperationException($"Modèle {job.Model} inconnu du serveur.");
                await EnsureAsync(hub, model.Files, ModelsDir);
                var modelPath = Path.Combine(ModelsDir, model.Files[0].Path);

                var input = Path.Combine(work, "input" + Path.GetExtension(job.InputName).ToLowerInvariant());
                if (!await hub.DownloadFileAsync(job.InputHash, input)) throw new InvalidOperationException("L'image n'est plus sur le serveur.");
                output = Path.Combine(work, "result.png");

                Current = job.InputName;
                StateChanged?.Invoke();
                clock.Restart();
                onGpu = await Task.Run(() =>
                {
                    // Loaded for the job only: the graphics card's memory is given back between jobs.
                    using var upscaler = AkashicRecords.Hub.OnnxUpscaler.Load(modelPath, preferGpu: true);
                    upscaler.Run(input, output);
                    return upscaler.OnGpu;
                });
            }
            else
            {
                // An AI edit or restore: FLUX Kontext through this computer's ComfyUI.
                versions = await ComfyUi.Instance.RunAsync(hub, job, work);
                output = versions[0];
            }
            clock.Stop();

            var hash = await HashAsync(output);
            var hashes = new List<string>();
            foreach (var version in versions.Count > 0 ? versions : [output])
            {
                var h = await HashAsync(version);
                if (!await hub.HasFileAsync(h)) await hub.UploadFileAsync(version, h);
                hashes.Add(h);
            }
            await hub.CompleteComputeAsync(job.Id, hash, clock.Elapsed.TotalSeconds, hashes.Count > 1 ? hashes : null);
            // A picture of the app: the hub has just put the result next to it; fetch it now rather than at the next pass.
            if (job.AttachType is not null)
                _ = System.Windows.Application.Current?.Dispatcher.InvokeAsync(() => SyncService.Instance.RunAsync());
            // Keep it: opening it here then needs no download.
            Directory.CreateDirectory(ResultsDir);
            File.Copy(output, ResultPath(hash), overwrite: true);
            SetProblem(onGpu ? null : "Fait sur le processeur : la carte graphique n'a pas pu être utilisée (lent).");
        }
        catch (HubException)
        {
            throw; // the hub went away: it gives the job back to the queue when this computer's hold runs out
        }
        catch (Exception ex)
        {
            // This computer could not do it (no graphics driver, broken file...): say so, and step back for a while
            // so that the hub or another device takes the next ones.
            try { await hub.FailComputeAsync(job.Id, $"{Environment.MachineName} : {ex.Message}"); } catch (HubException) { }
            _pausedUntil = DateTime.UtcNow + PauseAfterFailure;
            SetProblem($"Échec ici : {ex.Message} (pause de 5 minutes)");
        }
        finally
        {
            Current = null;
            StateChanged?.Invoke();
            try { Directory.Delete(work, recursive: true); } catch (IOException) { }
        }
    }

    private void SetProblem(string? problem)
    {
        if (Problem == problem) return;
        Problem = problem;
        StateChanged?.Invoke();
    }

    /// <summary>Makes `dir` hold these files (by hash), downloading the missing or different ones.</summary>
    private async Task<int> EnsureAsync(HubClient hub, IEnumerable<HubComputeFile> files, string dir)
    {
        var fetched = 0;
        foreach (var f in files)
        {
            if (f.Path.Split('/').Any(p => p is "" or "." or "..")) throw new InvalidOperationException($"Chemin refusé : {f.Path}");
            var target = Path.Combine(dir, f.Path.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(target) && new FileInfo(target).Length == f.Size && await HashAsync(target) == f.Hash) continue;
            Current = $"téléchargement de {f.Path} depuis le serveur";
            StateChanged?.Invoke();
            if (!await hub.DownloadComputeFileAsync(f.Hash, target)) throw new InvalidOperationException($"{f.Path} manque sur le serveur.");
            fetched++;
        }
        return fetched;
    }

    private async Task<string> HashAsync(string path)
    {
        var info = new FileInfo(path);
        lock (_hashes)
            if (_hashes.TryGetValue(path, out var c) && c.Size == info.Length && c.Ticks == info.LastWriteTimeUtc.Ticks) return c.Hash;
        await using var s = File.OpenRead(path);
        var hash = Convert.ToHexString(await SHA256.HashDataAsync(s)).ToLowerInvariant();
        lock (_hashes) _hashes[path] = (info.Length, info.LastWriteTimeUtc.Ticks, hash);
        return hash;
    }

    // ------------------------------------------------------------------ for the window

    public string ResultPath(string hash) => Path.Combine(ResultsDir, hash + ".png");

    /// <summary>A result (or an input) kept on the hub, as a local file: fetched once, then kept here.</summary>
    public async Task<string> LocalCopyAsync(string hash, string extension)
    {
        var target = Path.Combine(ResultsDir, hash + extension);
        if (File.Exists(target)) return target;
        var s = SyncService.Instance.Settings;
        using var hub = new HubClient(s.HubUrl, s.Token);
        if (!await hub.DownloadFileAsync(hash, target)) throw new HubException("Ce fichier n'est plus sur le serveur.");
        return target;
    }

    /// <summary>The kinds of rows whose picture can be upscaled in place (same names as the tables and the hub's).</summary>
    public static readonly string[] InPlaceKinds = ["Photo", "JournalPhoto", "RefImage", "CanvasElement"];

    /// <summary>
    /// Upscales a picture already in the app (a row of Photo, JournalPhoto, RefImage or CanvasElement): the hub reads
    /// the original itself and, once done, adds the result next to it (same album, entry, board or canvas); it
    /// reaches this computer at a sync. A row not yet on the hub is synchronised first. Throws HubException with a
    /// message fit for the user.
    /// </summary>
    public async Task<HubComputeJob> RequestFromAsync(string table, long localId)
    {
        if (!InPlaceKinds.Contains(table)) throw new HubException("Ce type d'image ne peut pas être agrandi.");
        var s = SyncService.Instance.Settings;
        if (!s.IsPaired) throw new HubException("Cet ordinateur n'est pas appairé au serveur (Réglages → Synchronisation).");
        var syncId = SyncIdOf(table, localId);
        if (syncId is null)
        {
            // Just added here: it goes to the hub first, with its file.
            await System.Windows.Application.Current.Dispatcher.InvokeAsync(() => SyncService.Instance.RunAsync()).Task.Unwrap();
            syncId = SyncIdOf(table, localId) ?? throw new HubException("Cette image n'a pas encore pu être envoyée au serveur : réessaie dans un moment.");
        }
        using var hub = new HubClient(s.HubUrl, s.Token);
        return await hub.AddComputeJobFromAsync(table, syncId);
    }

    /// <summary>
    /// Asks for an AI edit ("edit": a sentence, optionally a mask PNG — white where the change goes — and words naming
    /// the zone, and whether to empty it first; hidePng = white where the picture is hidden from the AI) or a restore ("restore") of a picture already in the app. Done by a PC
    /// with ComfyUI; once done, the hub makes the result an edit of the same row. Throws HubException for the user.
    /// </summary>
    public async Task<HubComputeJob> RequestEditAsync(string table, long localId, string kind, string? prompt, byte[]? maskPng, string? maskText, bool erase,
        int tries = 1, string engine = "kontext", byte[]? hidePng = null)
    {
        if (!InPlaceKinds.Contains(table)) throw new HubException("Ce type d'image ne peut pas être modifié par l'IA.");
        var s = SyncService.Instance.Settings;
        if (!s.IsPaired) throw new HubException("Cet ordinateur n'est pas appairé au serveur (Réglages → Synchronisation).");
        var syncId = SyncIdOf(table, localId);
        if (syncId is null)
        {
            await System.Windows.Application.Current.Dispatcher.InvokeAsync(() => SyncService.Instance.RunAsync()).Task.Unwrap();
            syncId = SyncIdOf(table, localId) ?? throw new HubException("Cette image n'a pas encore pu être envoyée au serveur : réessaie dans un moment.");
        }
        using var hub = new HubClient(s.HubUrl, s.Token);
        var maskHash = await SendMaskAsync(hub, maskPng);
        var hideHash = await SendMaskAsync(hub, hidePng);
        return await hub.AddComputeEditAsync(table, syncId, kind, prompt, maskHash, maskText, erase, tries, engine, hideHash);
    }

    // A mask PNG sent to the hub as a blob (once); its hash, or null without a mask.
    private async Task<string?> SendMaskAsync(HubClient hub, byte[]? png)
    {
        if (png is null) return null;
        Directory.CreateDirectory(WorkDir);
        var file = Path.Combine(WorkDir, $"mask-{Guid.NewGuid():N}.png");
        await File.WriteAllBytesAsync(file, png);
        try
        {
            var hash = await HashAsync(file);
            if (!await hub.HasFileAsync(hash)) await hub.UploadFileAsync(file, hash);
            return hash;
        }
        finally
        {
            try { File.Delete(file); } catch (IOException) { }
        }
    }

    /// <summary>The id a row has on the hub (sync_map), or null when it has not been synchronised yet.</summary>
    private static string? SyncIdOf(string table, long localId)
    {
        var db = Path.Combine(AppContext.BaseDirectory, "data", "akashic.db");
        if (!File.Exists(db)) return null;
        using var c = new Microsoft.Data.Sqlite.SqliteConnection(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = db }.ToString());
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT sync_id FROM sync_map WHERE entity_type = $t AND local_id = $id AND deleted = 0 LIMIT 1";
        cmd.Parameters.AddWithValue("$t", table);
        cmd.Parameters.AddWithValue("$id", localId);
        try { return cmd.ExecuteScalar() as string; }
        catch (Microsoft.Data.Sqlite.SqliteException) { return null; } // never synchronised: no sync_map yet
    }

    /// <summary>Sends an image to the hub and asks for it to be upscaled.</summary>
    public async Task<HubComputeJob> RequestAsync(string path, string model)
    {
        var s = SyncService.Instance.Settings;
        using var hub = new HubClient(s.HubUrl, s.Token);
        var hash = await HashAsync(path);
        if (!await hub.HasFileAsync(hash)) await hub.UploadFileAsync(path, hash);
        // The original is kept next to the results: "before / after" needs no download.
        Directory.CreateDirectory(ResultsDir);
        var copy = Path.Combine(ResultsDir, hash + Path.GetExtension(path).ToLowerInvariant());
        if (!File.Exists(copy)) File.Copy(path, copy);
        return await hub.AddComputeJobAsync(model, hash, Path.GetFileName(path));
    }
}
