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

    private ComputeService() => Worker = LoadWorkerSetting();

    /// <summary>Does this computer take jobs? (The window's checkbox; on by default.)</summary>
    public bool Worker { get; private set; }

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
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_settingsPath)!);
            File.WriteAllText(_settingsPath, JsonSerializer.Serialize(new { Worker = on }));
        }
        catch (IOException) { /* the choice still holds for this run */ }
        if (on) { _pausedUntil = DateTime.MinValue; Problem = null; }
        StateChanged?.Invoke();
    }

    private bool LoadWorkerSetting()
    {
        try
        {
            if (!File.Exists(_settingsPath)) return true;
            using var doc = JsonDocument.Parse(File.ReadAllText(_settingsPath));
            return !doc.RootElement.TryGetProperty("Worker", out var w) || w.ValueKind != JsonValueKind.False;
        }
        catch (Exception ex) when (ex is IOException or JsonException) { return true; }
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
            Current = job.InputName;
            StateChanged?.Invoke();

            // The model, fetched from the hub when missing or changed. The engine is part of the app.
            var model = manifest.Models.FirstOrDefault(m => m.Name == job.Model) ?? throw new InvalidOperationException($"Modèle {job.Model} inconnu du serveur.");
            await EnsureAsync(hub, model.Files, ModelsDir);
            var modelPath = Path.Combine(ModelsDir, model.Files[0].Path);

            Directory.CreateDirectory(work);
            var input = Path.Combine(work, "input" + Path.GetExtension(job.InputName).ToLowerInvariant());
            if (!await hub.DownloadFileAsync(job.InputHash, input)) throw new InvalidOperationException("L'image n'est plus sur le serveur.");
            var output = Path.Combine(work, "result.png");

            Current = job.InputName;
            StateChanged?.Invoke();
            var clock = Stopwatch.StartNew();
            var onGpu = await Task.Run(() =>
            {
                // Loaded for the job only: the graphics card's memory is given back between jobs.
                using var upscaler = AkashicRecords.Hub.OnnxUpscaler.Load(modelPath, preferGpu: true);
                upscaler.Run(input, output);
                return upscaler.OnGpu;
            });
            clock.Stop();

            var hash = await HashAsync(output);
            await hub.UploadFileAsync(output, hash);
            await hub.CompleteComputeAsync(job.Id, hash, clock.Elapsed.TotalSeconds);
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
