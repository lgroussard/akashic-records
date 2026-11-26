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
    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        var hubModels = options.HubModels.ToHashSet(StringComparer.Ordinal);
        // The tool's results from before the "Agrandies" album: placed in it once.
        try { attach.PlacePastResults(jobs); } catch (Exception ex) { log.Info($"placing past upscale results failed: {ex.Message}"); }
        while (!stop.IsCancellationRequested)
        {
            ComputeJob? job = null;
            if (hubModels.Count > 0)
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
    public sealed record CompleteRequest(string? ResultHash, double? Seconds);
    public sealed record FailRequest(string? Error);
    public sealed record FromRequest(string? EntityType, string? EntityId);

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
                models = catalog.Models().Select(m => new { m.Name, m.Label, m.Description, m.Hub, runners = workers.Runners(m.Name) }),
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
        app.MapPost("/compute/jobs/from", async (FromRequest? req, HttpContext ctx) =>
        {
            if (!options.Compute) return Disabled();
            var model = catalog.Models().FirstOrDefault();
            if (model is null) return Results.BadRequest(new { error = "no model" });
            if (req?.EntityType is null || req.EntityId is null || !ComputeAttach.Kinds.ContainsKey(req.EntityType))
                return Results.BadRequest(new { error = "entity_type: Photo, JournalPhoto, RefImage or CanvasElement; entity_id required" });
            try
            {
                var (hash, name, size) = await attach.InputAsync(req.EntityType, req.EntityId, ctx.RequestAborted);
                var job = jobs.AddJob(model.Name, hash, name, size, Device(ctx), req.EntityType, req.EntityId);
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
            if (!jobs.Complete(id, device, hash!, size, OnnxUpscaler.ResultName(job.InputName), Math.Max(0, req!.Seconds ?? 0)))
                return Results.Conflict(new { error = "not your job (or not running)" });
            attach.Attach(jobs, jobs.Job(id)!);
            return Results.Ok(jobs.Job(id));
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
