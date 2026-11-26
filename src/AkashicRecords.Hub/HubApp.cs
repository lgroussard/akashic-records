using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Features;

namespace AkashicRecords.Hub;

/// <summary>Builds the hub. Kept apart from Program.cs so the smoke test can host it in-process
/// on a real Kestrel port with a throw-away data folder.</summary>
public static class HubApp
{
    public const string DeviceItem = "hub.device";
    public const int DefaultPullLimit = 500;
    public const int MaxPullLimit = 1000;
    public const int MaxBatch = 1000;

    public static string Version =>
        typeof(HubApp).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "?";

    public static WebApplication Build(string[] args, HubOptions? options = null)
    {
        options = (options ?? HubOptions.Load(args)).Normalize();

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = args,
            ContentRootPath = AppContext.BaseDirectory, // a service starts in System32: never rely on the working directory
        });
        builder.Services.AddWindowsService(o => o.ServiceName = "AkashicHub"); // no effect outside a Windows service
        builder.WebHost.UseUrls(options.EffectiveUrl);

        builder.Services.ConfigureHttpJsonOptions(o =>
        {
            o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower;
            o.SerializerOptions.PropertyNameCaseInsensitive = true;
        });

        Directory.CreateDirectory(options.DataDir);
        var store = new HubStore(options.DatabasePath);
        var blobs = new BlobStore(options.BlobDir);
        var log = new RequestLog(options.LogDir);
        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton(store);
        builder.Services.AddSingleton(blobs);
        builder.Services.AddSingleton(log);
        var board = new DownloadStore(options.DownloadsDbPath);
        builder.Services.AddSingleton(board);
        if (options.Downloads) builder.Services.AddHostedService<DownloadWorker>();

        var app = builder.Build();
        var lastTouch = new ConcurrentDictionary<string, long>();

        // Request log + authentication. Only /health and /auth/pair are open; everything else
        // needs the token of a paired, non-revoked device.
        app.Use(async (ctx, next) =>
        {
            var sw = Stopwatch.StartNew();
            try
            {
                var path = ctx.Request.Path.Value ?? "";
                var open = path.Equals("/health", StringComparison.OrdinalIgnoreCase)
                           || path.Equals("/auth/pair", StringComparison.OrdinalIgnoreCase);
                if (!open)
                {
                    var header = ctx.Request.Headers.Authorization.ToString();
                    var device = header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
                        ? store.Authenticate(header["Bearer ".Length..].Trim())
                        : null;
                    if (device is null)
                    {
                        ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                        ctx.Response.Headers.WWWAuthenticate = "Bearer";
                        await ctx.Response.WriteAsJsonAsync(new { error = "unauthorized" });
                        return;
                    }
                    ctx.Items[DeviceItem] = device;
                    var minute = Environment.TickCount64 / 60_000;
                    if (lastTouch.TryGetValue(device, out var seen) ? seen != minute : true)
                    {
                        lastTouch[device] = minute;
                        store.Touch(device);
                    }
                }
                await next(ctx);
            }
            finally
            {
                log.Write(ctx, sw.ElapsedMilliseconds);
            }
        });

        app.MapGet("/health", () => Results.Ok(new
        {
            status = "ok",
            version = Version,
            head_seq = store.HeadSeq(),
            retention_days = options.RetentionDays,
        }));

        app.MapPost("/auth/pair", (PairRequest? req) =>
        {
            if (req is null || string.IsNullOrWhiteSpace(req.Code)) return Results.BadRequest(new { error = "code required" });
            var deviceId = string.IsNullOrWhiteSpace(req.DeviceId) ? Guid.NewGuid().ToString() : req.DeviceId.Trim();
            if (deviceId.Length > 64 || deviceId.Any(char.IsControl)) return Results.BadRequest(new { error = "device_id invalid" });
            var name = string.IsNullOrWhiteSpace(req.DeviceName) ? deviceId : req.DeviceName.Trim();
            if (name.Length > 100) name = name[..100];

            var token = store.Pair(req.Code.Trim(), deviceId, name);
            if (token is null)
            {
                log.Info($"pair refused for device '{deviceId}'");
                return Results.Json(new { error = "invalid_code" }, statusCode: StatusCodes.Status403Forbidden);
            }
            log.Info($"paired device '{deviceId}' ({name})");
            return Results.Ok(new PairResponse(deviceId, token));
        });

        app.MapPost("/sync/push", (PushRequest? req, HttpContext ctx) =>
        {
            var device = (string)ctx.Items[DeviceItem]!;
            if (req is not null && !string.IsNullOrEmpty(req.DeviceId) && req.DeviceId != device)
                return Results.Json(new { error = "device_id does not match the token" }, statusCode: StatusCodes.Status403Forbidden);
            var error = Validate(req, options);
            if (error is not null) return Results.BadRequest(new { error });
            return Results.Ok(store.Push(device, req!.Mutations));
        });

        app.MapGet("/sync/pull", (long? since, int? limit) =>
        {
            var s = since ?? 0;
            if (s < 0) return Results.BadRequest(new { error = "since must be >= 0" });
            var n = Math.Clamp(limit ?? DefaultPullLimit, 1, MaxPullLimit);
            return Results.Ok(store.Pull(s, n));
        });

        app.MapGet("/snapshot", (string? entity_type, string? after_type, string? after_id, int? limit) =>
        {
            if (entity_type is not null && !options.AcceptsEntityType(entity_type))
                return Results.BadRequest(new { error = $"unknown entity_type '{entity_type}'" });
            if ((after_type is null) != (after_id is null))
                return Results.BadRequest(new { error = "after_type and after_id go together" });
            var n = Math.Clamp(limit ?? DefaultPullLimit, 1, MaxPullLimit);
            return Results.Ok(store.Snapshot(entity_type, after_type, after_id, n));
        });

        app.MapPost("/media", async (HttpContext ctx) =>
        {
            var max = options.MaxBlobBytes;
            if (ctx.Request.ContentLength > max) return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
            var limit = ctx.Features.Get<IHttpMaxRequestBodySizeFeature>();
            if (limit is { IsReadOnly: false }) limit.MaxRequestBodySize = max;

            string? expected = ctx.Request.Query["hash"];
            if (string.IsNullOrEmpty(expected)) expected = null;
            else if (!BlobStore.IsHash(expected = expected.ToLowerInvariant()))
                return Results.BadRequest(new { error = "hash must be 64 hex characters (SHA-256)" });

            try
            {
                var saved = await blobs.SaveAsync(ctx.Request.Body, expected, max, ctx.RequestAborted);
                return saved.Created ? Results.Json(saved, statusCode: StatusCodes.Status201Created) : Results.Ok(saved);
            }
            catch (BlobStore.TooLargeException)
            {
                return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
            }
            catch (BlobStore.HashMismatchException ex)
            {
                return Results.BadRequest(new { error = "hash_mismatch", actual = ex.Actual });
            }
        });

        app.MapMethods("/media/{hash}", new[] { "GET", "HEAD" }, (string hash, HttpContext ctx) =>
        {
            hash = hash.ToLowerInvariant();
            if (!BlobStore.IsHash(hash)) return Results.BadRequest(new { error = "hash must be 64 hex characters (SHA-256)" });
            if (!blobs.Exists(hash)) return Results.NotFound(new { error = "unknown blob" });
            // Same hash = same bytes, forever: the client may cache it for good.
            ctx.Response.Headers.CacheControl = "private, max-age=31536000, immutable";
            return Results.File(blobs.PathOf(hash), "application/octet-stream", enableRangeProcessing: true,
                entityTag: new Microsoft.Net.Http.Headers.EntityTagHeaderValue($"\"{hash}\""));
        });

        DownloadRoutes.Map(app, store, board, options);

        log.Info($"hub {Version} starting on {options.EffectiveUrl}, data in {options.DataDir}, {options.EntityTypes.Count} entity types");
        return app;
    }

    private static string? Validate(PushRequest? req, HubOptions options)
    {
        if (req is null) return "body required";
        if (req.Mutations is null) return "mutations required";
        if (req.Mutations.Count > MaxBatch) return $"at most {MaxBatch} mutations per batch";
        for (var i = 0; i < req.Mutations.Count; i++)
        {
            var m = req.Mutations[i];
            if (m is null) return $"mutations[{i}]: null";
            if (string.IsNullOrWhiteSpace(m.MutationId)) return $"mutations[{i}]: mutation_id required";
            if (string.IsNullOrWhiteSpace(m.EntityId)) return $"mutations[{i}]: entity_id required";
            if (!options.AcceptsEntityType(m.EntityType))
                return $"mutations[{i}]: unknown entity_type '{m.EntityType}'";
            if (m.BaseRev < 0) return $"mutations[{i}]: base_rev must be >= 0";
            if (!m.Deleted && m.Payload?.ValueKind != JsonValueKind.Object)
                return $"mutations[{i}]: payload must be a JSON object unless deleted";
        }
        return null;
    }
}
