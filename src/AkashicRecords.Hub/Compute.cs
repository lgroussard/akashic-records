using System.Collections.Concurrent;
using System.Diagnostics;

namespace AkashicRecords.Hub;

/// <summary>
/// Shared computing: "everything is on the hub, each device takes what it can chew". The hub keeps the upscale
/// model (ComputeCatalog: one .onnx, NomosWebPhoto) and the list of requested jobs (ComputeStore). A paired PC asks
/// the hub for work (POST /compute/claim, saying which models it can run), downloads the model the first time,
/// runs it on its graphics card and sends the result back. The hub runs the model itself, on its processor and
/// much more slowly, only when no device able to do the job is around. The engine (OnnxUpscaler) is built into
/// the hub and the desktop app: nothing to install apart from the model.
/// </summary>
public sealed class ComputeWorkers
{
    /// <summary>A device that asked for work this recently is "around".</summary>
    public static readonly TimeSpan Alive = TimeSpan.FromSeconds(45);

    public sealed record Seen(string Device, string Name, string Rid, HashSet<string> Models, DateTime LastSeen);

    private readonly ConcurrentDictionary<string, Seen> _seen = new();

    public void Saw(string device, string name, string rid, IEnumerable<string> models) =>
        _seen[device] = new Seen(device, name, rid, models.ToHashSet(StringComparer.Ordinal), DateTime.UtcNow);

    public void Touch(string device)
    {
        if (_seen.TryGetValue(device, out var s)) _seen[device] = s with { LastSeen = DateTime.UtcNow };
    }

    private IEnumerable<Seen> Live() => _seen.Values.Where(s => DateTime.UtcNow - s.LastSeen < Alive);

    /// <summary>Is a device other than `except` around and able to run this model?</summary>
    public bool OtherCanRun(string model, string except) => Live().Any(s => s.Device != except && s.Models.Contains(model));

    /// <summary>Names of the devices around that can run a model.</summary>
    public List<string> Runners(string model) => Live().Where(s => s.Models.Contains(model)).Select(s => s.Name).OrderBy(n => n).ToList();

    public List<object> Describe(HashSet<string> busy) =>
        Live().OrderBy(s => s.Name).Select(s => (object)new
        {
            device = s.Device,
            name = s.Name,
            rid = s.Rid,
            models = s.Models.Count,
            busy = busy.Contains(s.Device),
            seen_seconds_ago = (int)(DateTime.UtcNow - s.LastSeen).TotalSeconds,
        }).ToList();
}

/// <summary>The hub's own worker: takes the jobs of the models it runs, when no device able to do them is around.</summary>
public sealed class HubComputeWorker(HubOptions options, ComputeStore jobs, ComputeCatalog catalog, ComputeWorkers workers, BlobStore blobs, ComputeAttach attach, RequestLog log) : BackgroundService
{
    /// <summary>How long the hub leaves jobs to the devices after it starts (they ask every 4 s).</summary>
    public static readonly TimeSpan StartupGrace = TimeSpan.FromSeconds(60);

    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        var hubModels = options.HubModels.ToHashSet(StringComparer.Ordinal);
        // Right after a start the hub does not know yet which devices are around (they ask every few seconds): it
        // waits a little before taking jobs itself, so that a PC that is on gets them instead.
        var since = DateTime.UtcNow;
        // The tool's results from before the "Agrandies" album: placed in it once.
        try { attach.PlacePastResults(jobs); } catch (Exception ex) { log.Info($"placing past upscale results failed: {ex.Message}"); }
        while (!stop.IsCancellationRequested)
        {
            ComputeJob? job = null;
            if (hubModels.Count > 0 && DateTime.UtcNow - since >= StartupGrace)
            {
                var models = catalog.Models().Where(m => m.Hub).Select(m => m.Name).ToList();
                if (models.Count > 0)
                {
                    workers.Saw(HubStore.HubDevice, "hub", "hub", models);
                    job = jobs.Claim(HubStore.HubDevice, "hub", j => models.Contains(j.Model) && !workers.OtherCanRun(j.Model, HubStore.HubDevice));
                }
            }
            if (job is null)
            {
                try { await Task.Delay(TimeSpan.FromSeconds(3), stop); } catch (OperationCanceledException) { return; }
                continue;
            }

            var work = Path.Combine(options.ComputeWorkDir, job.Id);
            try
            {
                if (Directory.Exists(work)) Directory.Delete(work, recursive: true);
                Directory.CreateDirectory(work);
                var output = Path.Combine(work, "result.png");
                var clock = Stopwatch.StartNew();
                // Off the request threads: minutes of processor work on the hub.
                await Task.Run(() =>
                {
                    using var upscaler = OnnxUpscaler.Load(catalog.ModelPath(job.Model), preferGpu: false);
                    upscaler.Run(blobs.PathOf(job.InputHash), output, stop);
                }, stop);
                clock.Stop();
                MediaResponse saved;
                await using (var s = File.OpenRead(output)) saved = await blobs.SaveAsync(s, null, long.MaxValue, stop);
                jobs.Complete(job.Id, HubStore.HubDevice, saved.Hash, saved.Size, OnnxUpscaler.ResultName(job.InputName), clock.Elapsed.TotalSeconds);
                attach.Attach(jobs, jobs.Job(job.Id)!);
                log.Info($"upscale done by the hub: {job.InputName} ({job.Model}) in {clock.Elapsed.TotalSeconds:F1} s");
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
                return; // left running: queued again at the next start
            }
            catch (Exception ex)
            {
                jobs.Fail(job.Id, HubStore.HubDevice, ex.Message);
                log.Info($"upscale failed on the hub: {job.InputName}: {ex.Message}");
            }
            finally
            {
                try { Directory.Delete(work, recursive: true); } catch { }
            }
        }
    }
}

/// <summary>The routes of shared computing. Every paired device sees the same list.</summary>
public static class ComputeRoutes
{
    public sealed record JobRequest(string? Model, string? InputHash, string? InputName);
    public sealed record ClaimRequest(string? Name, string? Rid, List<string>? Models);
    /// <summary>ResultHashes: every version of a job asked with several tries (ResultHash is then the first).</summary>
    public sealed record CompleteRequest(string? ResultHash, double? Seconds, List<string>? ResultHashes = null);
    public sealed record ChooseRequest(string? ResultHash);
    public sealed record FailRequest(string? Error);
    /// <summary>Kind: null/"upscale", "edit" (Prompt required; MaskHash = a PNG blob, white where the change goes;
    /// MaskText = what to find and mask too, in English; Erase = empty the zone first) or "restore".</summary>
    /// <summary>Tries: 1 to 4 versions (more than one: they wait for a choice, see /choose). Engine: "kontext" (default)
    /// or "sdxl" (RealVisXL, with a zone only). Frame: [x, y, width, height] as fractions of the upright picture — the AI
    /// is only shown that part, which is pasted back into the whole picture.</summary>
    public sealed record FromRequest(string? EntityType, string? EntityId, string? Kind = null, string? Prompt = null,
        string? MaskHash = null, string? MaskText = null, bool? Erase = null, int? Tries = null, string? Engine = null, double[]? Frame = null);

    public const string KindEdit = "edit";
    public const string KindRestore = "restore";

    public static void Map(WebApplication app, ComputeStore jobs, ComputeCatalog catalog, ComputeWorkers workers, BlobStore blobs, ComputeAttach attach, HubOptions options)
    {
        static string Device(HttpContext ctx) => (string)ctx.Items[HubApp.DeviceItem]!;
        IResult Disabled() => Results.Json(new { error = "compute_disabled" }, statusCode: StatusCodes.Status403Forbidden);

        object Board()
        {
            var busy = jobs.BusyWorkers();
            return new
            {
                enabled = options.Compute,
                models = catalog.Models().Select(m => new { m.Name, m.Label, m.Description, m.Hub, runners = workers.Runners(m.Name) })
                    .Append(new { Name = ComputeCatalog.Kontext, Label = "FLUX Kontext", Description = "Modifie une image en suivant une phrase, ou la restaure. Sur un PC avec ComfyUI.", Hub = false, runners = workers.Runners(ComputeCatalog.Kontext) }),
                workers = workers.Describe(busy),
                jobs = jobs.Jobs(),
            };
        }

        app.MapGet("/compute", () => Results.Ok(Board()));

        app.MapGet("/compute/manifest", () => Results.Ok(catalog.Manifest()));

        app.MapGet("/compute/files/{hash}", (string hash) =>
        {
            hash = hash.ToLowerInvariant();
            if (!BlobStore.IsHash(hash)) return Results.BadRequest(new { error = "hash must be 64 hex characters (SHA-256)" });
            var path = catalog.PathOfHash(hash);
            return path is null ? Results.NotFound(new { error = "unknown file" }) : Results.File(path, "application/octet-stream");
        });

        app.MapPost("/compute/jobs", (JobRequest? req, HttpContext ctx) =>
        {
            if (!options.Compute) return Disabled();
            // The model may be left out: there is one per feature, the first (only) one is meant.
            var model = req?.Model is { Length: > 0 } m ? catalog.Model(m) : catalog.Models().FirstOrDefault();
            if (model is null) return Results.BadRequest(new { error = "unknown model" });
            var hash = req?.InputHash?.ToLowerInvariant();
            if (!BlobStore.IsHash(hash) || !blobs.Exists(hash!)) return Results.BadRequest(new { error = "input_hash: send the image to /media first" });
            var name = Path.GetFileName((req!.InputName ?? "").Replace('\\', '/').Trim());
            if (name.Length is 0 or > 200 || name.Any(char.IsControl)) return Results.BadRequest(new { error = "input_name invalid" });
            if (!OnnxUpscaler.InputExtensions.Contains(Path.GetExtension(name).ToLowerInvariant()))
                return Results.BadRequest(new { error = "input_name: jpg, jpeg, png or webp" });
            var job = jobs.AddJob(model.Name, hash!, name, new FileInfo(blobs.PathOf(hash!)).Length, Device(ctx));
            return Results.Json(job, statusCode: StatusCodes.Status201Created);
        });

        // Upscale an image already in the app: the hub reads the original itself, and puts the result next to it
        // when the job is done (ComputeAttach).
        // An edit or a restore (FLUX Kontext, run by a PC) replaces the picture by a revertible edit once done.
        app.MapPost("/compute/jobs/from", async (FromRequest? req, HttpContext ctx) =>
        {
            if (!options.Compute) return Disabled();
            if (req?.EntityType is null || req.EntityId is null || !ComputeAttach.Kinds.ContainsKey(req.EntityType))
                return Results.BadRequest(new { error = "entity_type: Photo, JournalPhoto, RefImage or CanvasElement; entity_id required" });
            var kind = req.Kind is null or "" or "upscale" ? null : req.Kind;
            string modelName;
            string? parameters = null;
            if (kind is null)
            {
                var model = catalog.Models().FirstOrDefault();
                if (model is null) return Results.BadRequest(new { error = "no model" });
                modelName = model.Name;
            }
            else if (kind is KindEdit or KindRestore)
            {
                modelName = ComputeCatalog.Kontext;
                if (kind == KindEdit)
                {
                    var prompt = req.Prompt?.Trim() ?? "";
                    if (prompt.Length is 0 or > 1000) return Results.BadRequest(new { error = "prompt: say what to change (1 to 1000 characters)" });
                    var mask = req.MaskHash?.ToLowerInvariant();
                    if (mask is not null && (!BlobStore.IsHash(mask) || !blobs.Exists(mask))) return Results.BadRequest(new { error = "mask_hash: send the mask to /media first" });
                    var maskText = string.IsNullOrWhiteSpace(req.MaskText) ? null : req.MaskText.Trim();
                    if (maskText is { Length: > 200 }) return Results.BadRequest(new { error = "mask_text: 200 characters at most" });
                    var engine = req.Engine is null or "" ? "kontext" : req.Engine;
                    if (engine is not ("kontext" or "sdxl")) return Results.BadRequest(new { error = "engine: kontext or sdxl" });
                    if (engine == "sdxl" && mask is null && maskText is null)
                        return Results.BadRequest(new { error = "RealVisXL a besoin d'une zone : peins-la ou nomme-la." });
                    double[]? frame = null;
                    if (req.Frame is { } fr)
                    {
                        if (fr.Length != 4 || fr.Any(v => double.IsNaN(v) || v < -0.001 || v > 1.001) || fr[2] < 0.01 || fr[3] < 0.01
                            || fr[0] + fr[2] > 1.001 || fr[1] + fr[3] > 1.001)
                            return Results.BadRequest(new { error = "frame: [x, y, width, height], fractions of the picture" });
                        if (fr[2] < 0.999 || fr[3] < 0.999) frame = fr.Select(v => Math.Round(Math.Clamp(v, 0, 1), 5)).ToArray();
                    }
                    parameters = System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object?>
                    {
                        ["prompt"] = prompt, ["mask_hash"] = mask, ["mask_text"] = maskText, ["erase"] = req.Erase == true,
                        ["tries"] = Math.Clamp(req.Tries ?? 1, 1, 4), ["engine"] = engine, ["frame"] = frame,
                    });
                }
                else if (req.Tries is > 1)
                    parameters = System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object?> { ["tries"] = Math.Clamp(req.Tries.Value, 1, 4) });
            }
            else return Results.BadRequest(new { error = "kind: upscale, edit or restore" });
            try
            {
                var (hash, name, size) = await attach.InputAsync(req.EntityType, req.EntityId, ctx.RequestAborted, refuseUpscaled: kind is null);
                var job = jobs.AddJob(modelName, hash, name, size, Device(ctx), req.EntityType, req.EntityId, kind, parameters);
                return Results.Json(job, statusCode: StatusCodes.Status201Created);
            }
            catch (InvalidOperationException ex)
            {
                return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status422UnprocessableEntity);
            }
        });

        // A device asks for work, saying what it can run. 204 = nothing for it now.
        app.MapPost("/compute/claim", (ClaimRequest? req, HttpContext ctx) =>
        {
            if (!options.Compute) return Disabled();
            var device = Device(ctx);
            var models = (req?.Models ?? new()).Where(ComputeCatalog.IsName).ToHashSet(StringComparer.Ordinal);
            var name = string.IsNullOrWhiteSpace(req?.Name) ? device : req.Name.Trim()[..Math.Min(req.Name.Trim().Length, 100)];
            workers.Saw(device, name, ComputeCatalog.IsName(req?.Rid) ? req!.Rid! : "?", models);
            var job = jobs.Claim(device, name, j => models.Contains(j.Model));
            return job is null ? Results.NoContent() : Results.Ok(job);
        });

        app.MapPost("/compute/jobs/{id}/complete", (string id, CompleteRequest? req, HttpContext ctx) =>
        {
            var device = Device(ctx);
            workers.Touch(device);
            var job = jobs.Job(id);
            if (job is null) return Results.NotFound();
            var hash = req?.ResultHash?.ToLowerInvariant();
            if (!BlobStore.IsHash(hash) || !blobs.Exists(hash!)) return Results.BadRequest(new { error = "result_hash: send the result to /media first" });
            var size = new FileInfo(blobs.PathOf(hash!)).Length;
            var resultName = job.Kind is null ? OnnxUpscaler.ResultName(job.InputName) : Path.GetFileNameWithoutExtension(job.InputName) + "-ia.png";
            var versions = (req!.ResultHashes ?? new()).Select(h => h.ToLowerInvariant()).Distinct().ToList();
            if (versions.Any(h => !BlobStore.IsHash(h) || !blobs.Exists(h))) return Results.BadRequest(new { error = "result_hashes: send every version to /media first" });
            if (!jobs.Complete(id, device, hash!, size, resultName, Math.Max(0, req.Seconds ?? 0)))
                return Results.Conflict(new { error = "not your job (or not running)" });
            // Several versions of a picture of the app: they wait for the user's choice (/choose) instead of being posed.
            if (versions.Count > 1 && job.AttachType is not null)
                jobs.SetResults(id, System.Text.Json.JsonSerializer.Serialize(versions));
            else
                attach.Attach(jobs, jobs.Job(id)!);
            return Results.Ok(jobs.Job(id));
        });

        // The version kept among several: posed like a single result; the other ones stay only in the job's list.
        app.MapPost("/compute/jobs/{id}/choose", (string id, ChooseRequest? req) =>
        {
            var job = jobs.Job(id);
            if (job is null) return Results.NotFound();
            var hash = req?.ResultHash?.ToLowerInvariant();
            var versions = job.Results is null ? new List<string>() : System.Text.Json.JsonSerializer.Deserialize<List<string>>(job.Results) ?? new();
            if (hash is null || !versions.Contains(hash)) return Results.BadRequest(new { error = "result_hash: one of the job's versions" });
            if (job.AttachedPath is not null) return Results.Conflict(new { error = "already chosen" });
            jobs.Chose(id, hash, new FileInfo(blobs.PathOf(hash)).Length);
            attach.Attach(jobs, jobs.Job(id)!);
            var done = jobs.Job(id)!;
            return done.AttachError is null ? Results.Ok(done) : Results.Json(new { error = done.AttachError }, statusCode: StatusCodes.Status422UnprocessableEntity);
        });

        app.MapPost("/compute/jobs/{id}/fail", (string id, FailRequest? req, HttpContext ctx) =>
        {
            var device = Device(ctx);
            workers.Touch(device);
            var error = string.IsNullOrWhiteSpace(req?.Error) ? "Échec sans message." : req.Error.Trim();
            return jobs.Fail(id, device, error) ? Results.Ok(jobs.Job(id)) : Results.Conflict(new { error = "not your job (or not running)" });
        });

        app.MapPost("/compute/jobs/{id}/retry", (string id) => jobs.Retry(id) ? Results.Ok(Board()) : Results.Conflict(new { error = "not failed" }));
        app.MapDelete("/compute/jobs/{id}", (string id) => jobs.Delete(id) ? Results.Ok(Board()) : Results.Conflict(new { error = "running or unknown" }));
    }
}
