using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace AkashicRecords.Sync;

// Wire contract of the hub (see src/AkashicRecords.Hub/Contracts.cs), client side.
public sealed record HubMutation(string MutationId, string EntityType, string EntityId, long BaseRev, JsonElement? Payload, bool Deleted);
public sealed record HubEntityState(long Rev, JsonElement? Payload, bool Deleted, string LastDevice);
public sealed record HubMutationResult(string MutationId, string Status, long Rev, long? Seq, HubEntityState? Current);
public sealed record HubPushResponse(List<HubMutationResult> Results, long HeadSeq);
public sealed record HubLogRow(long Seq, string EntityType, string EntityId, long Rev, string DeviceId, JsonElement? Payload, bool Deleted);
public sealed record HubPullResponse(List<HubLogRow> Rows, long NextCursor, bool HasMore, long HeadSeq);
public sealed record HubPairResponse(string DeviceId, string Token);
public sealed record HubHealth(string Status, string? Version, long HeadSeq);
public sealed record HubDownloadZone(string Id, string Name, string Folder);
public sealed record HubDownloadJob(string Id, string Query, string Label, string? ZoneId, string Status, string? Error,
    string AddedBy, string? Folder, string? TrackPath, string? FinishedAt);
public sealed record HubDownloadTool(string? Version, string? Problem, string? Current);
public sealed record HubDownloadBoard(bool Enabled, HubDownloadTool Tool, List<HubDownloadZone> Zones, List<HubDownloadJob> Jobs, List<string> Folders);

// The "Serveur" screen (the hub's ServerStatus.cs): room, last backup, devices, the hub's own work.
public sealed record HubServerDisk(string Path, long Total, long Free);
public sealed record HubStorageKind(string Kind, long Files, long Bytes);
public sealed record HubBackupStatus(string State, string? At, string? LastOkAt, string? Message, long? Bytes, long? Files, long? DiskTotal, long? DiskFree, long? Seconds);
public sealed record HubServerDevice(string Name, string? LastSeenAt, string? LastChangeAt, long Changes, bool Revoked, bool Hub);
public sealed record HubWork(int Queued, int Running, int Failed, int Done);
public sealed record HubDiskFolder(string Name, long Size, long Files, bool Denied, bool Mount, bool HasChildren);
public sealed record HubDiskFile(string Name, long Size, string Kind = "other", string? Label = null, string? Hash = null, string? Note = null);
public sealed record HubDiskView(string State, string? At, long? Seconds, bool Refreshing, string Root, string Path, long Size, long Files,
    List<HubDiskFolder> Folders, List<HubDiskFile> Largest, long OtherFilesBytes, long SmallFoldersBytes, long SmallFolders, bool Catalogue = false);
public sealed record HubCleanup(string RanAt, int GraceDays, int PendingFiles, long PendingBytes, string? NextAt, int DeletedFiles, int DeletedBlobs, long DeletedBytes, string? Brake);
public sealed record HubServerStatus(string Version, string Host, string StartedAt, HubServerDisk? Disk, List<HubStorageKind> Storage,
    long BlobBytes, long BlobFiles, HubBackupStatus Backup, List<HubServerDevice> Devices, Dictionary<string, long> Entities, HubWork Downloads, HubWork Compute,
    HubCleanup? Cleanup = null);

// Shared computing (the hub's Compute.cs): the hub keeps the model, devices run it when they can.
public sealed record HubComputeModel(string Name, string Label, string Description, bool Hub, List<string>? Runners);
public sealed record HubComputeWorker(string Device, string Name, string Rid, int Models, bool Busy, int SeenSecondsAgo);
public sealed record HubComputeJob(string Id, string Model, string InputHash, string InputName, long InputSize, string Status,
    string AddedAt, string AddedBy, string? Worker, string? WorkerName, string? StartedAt, string? FinishedAt,
    double? Seconds, string? ResultHash, long? ResultSize, string? ResultName, string? Error,
    string? AttachType = null, string? AttachId = null, string? AttachedPath = null, string? AttachError = null,
    string? Kind = null, string? Params = null, string? Results = null);
public sealed record HubComputeBoard(bool Enabled, List<HubComputeModel> Models, List<HubComputeWorker> Workers, List<HubComputeJob> Jobs);
public sealed record HubComputeFile(string Path, string Hash, long Size);
public sealed record HubComputeModelFiles(string Name, string Label, bool Hub, List<HubComputeFile> Files);
public sealed record HubComputeManifest(List<HubComputeModelFiles> Models);

public sealed class HubException(string message, HttpStatusCode? status = null) : Exception(message)
{
    public HttpStatusCode? Status { get; } = status;
}

/// <summary>Talks to the hub over HTTP. One instance per device; the token goes in every call.</summary>
public sealed class HubClient : IDisposable
{
    private static readonly JsonSerializerOptions Wire = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
    };

    private readonly HttpClient _http;
    private readonly HttpClient _long; // file transfers: no overall time limit here, each call sets its own

    public HubClient(string hubUrl, string? token = null, HttpMessageHandler? handler = null)
    {
        _http = handler is null ? new HttpClient() : new HttpClient(handler);
        _http.BaseAddress = new Uri(hubUrl.TrimEnd('/') + "/");
        _http.Timeout = TimeSpan.FromSeconds(20);
        _long = new HttpClient { BaseAddress = _http.BaseAddress, Timeout = Timeout.InfiniteTimeSpan };
        if (!string.IsNullOrEmpty(token))
        {
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            _long.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }
    }

    public async Task<HubHealth> HealthAsync(CancellationToken ct = default) =>
        await Read<HubHealth>(await Send(() => _http.GetAsync("health", ct)), ct);

    /// <summary>Exchanges a one-time code (shown on the hub) for this device's token.</summary>
    public async Task<HubPairResponse> PairAsync(string code, string deviceId, string deviceName, CancellationToken ct = default)
    {
        var digits = new string(code.Where(char.IsDigit).ToArray());
        var resp = await Send(() => _http.PostAsJsonAsync("auth/pair", new { code = digits, device_id = deviceId, device_name = deviceName }, Wire, ct));
        if (resp.StatusCode == HttpStatusCode.Forbidden)
            throw new HubException("Code d'appairage refuse : faux, expire ou deja utilise.", resp.StatusCode);
        return await Read<HubPairResponse>(resp, ct);
    }

    public async Task<HubPushResponse> PushAsync(IReadOnlyList<HubMutation> mutations, CancellationToken ct = default) =>
        await Read<HubPushResponse>(await Send(() => _http.PostAsJsonAsync("sync/push", new { mutations }, Wire, ct)), ct);

    public async Task<HubPullResponse> PullAsync(long since, int limit, CancellationToken ct = default) =>
        await Read<HubPullResponse>(await Send(() => _http.GetAsync($"sync/pull?since={since}&limit={limit}", ct)), ct);

    // ---- The download board shared by every device; the hub itself downloads (see the hub's Downloads.cs).

    public async Task<HubDownloadBoard> DownloadsAsync(CancellationToken ct = default) =>
        await Read<HubDownloadBoard>(await Send(() => _http.GetAsync("downloads", ct)), ct);

    public async Task AddDownloadAsync(string query, string label, string? zoneId, CancellationToken ct = default) =>
        await Ensure(await Send(() => _http.PostAsJsonAsync("downloads/jobs", new { query, label, zone_id = zoneId }, Wire, ct)), ct);

    public async Task MoveDownloadAsync(string id, string? zoneId, CancellationToken ct = default) =>
        await Ensure(await Send(() => _http.PostAsJsonAsync($"downloads/jobs/{id}/zone", new { zone_id = zoneId }, Wire, ct)), ct);

    public async Task RetryDownloadAsync(string id, CancellationToken ct = default) =>
        await Ensure(await Send(() => _http.PostAsJsonAsync($"downloads/jobs/{id}/retry", new { }, Wire, ct)), ct);

    public async Task RemoveDownloadAsync(string id, CancellationToken ct = default) =>
        await Ensure(await Send(() => _http.DeleteAsync($"downloads/jobs/{id}", ct)), ct);

    public async Task ClearDoneDownloadsAsync(CancellationToken ct = default) =>
        await Ensure(await Send(() => _http.PostAsJsonAsync("downloads/clear-done", new { }, Wire, ct)), ct);

    /// <summary>The zone for a folder (existing music folder or new one); the hub reuses the zone it may already have.</summary>
    public async Task<HubDownloadZone> AddZoneAsync(string? name, string? folder, CancellationToken ct = default) =>
        await Read<HubDownloadZone>(await Send(() => _http.PostAsJsonAsync("downloads/zones", new { name, folder }, Wire, ct)), ct);

    public async Task RenameZoneAsync(string id, string name, CancellationToken ct = default) =>
        await Ensure(await Send(() => _http.PostAsJsonAsync($"downloads/zones/{id}", new { name }, Wire, ct)), ct);

    public async Task RemoveZoneAsync(string id, CancellationToken ct = default) =>
        await Ensure(await Send(() => _http.DeleteAsync($"downloads/zones/{id}", ct)), ct);

    /// <summary>One folder of the server's disk (measured apart, see the hub's DiskIndex.cs); null path = the root.</summary>
    public async Task<HubDiskView> ServerDiskAsync(string? path, CancellationToken ct = default) =>
        await Read<HubDiskView>(await Send(() => _http.GetAsync("server/disk?path=" + Uri.EscapeDataString(path ?? ""), ct)), ct);

    public async Task<HubDiskView> RefreshServerDiskAsync(CancellationToken ct = default) =>
        await Read<HubDiskView>(await Send(() => _http.PostAsJsonAsync("server/disk/refresh", new { }, Wire, ct)), ct);

    public async Task<HubServerStatus> ServerAsync(CancellationToken ct = default) =>
        await Read<HubServerStatus>(await Send(() => _http.GetAsync("server", ct)), ct);

    // ---- Shared computing.

    public async Task<HubComputeBoard> ComputeAsync(CancellationToken ct = default) =>
        await Read<HubComputeBoard>(await Send(() => _http.GetAsync("compute", ct)), ct);

    public async Task<HubComputeManifest> ComputeManifestAsync(CancellationToken ct = default) =>
        await Read<HubComputeManifest>(await Send(() => _http.GetAsync("compute/manifest", ct)), ct);

    /// <summary>Asks for an image (already sent with UploadFileAsync) to be upscaled with a model.</summary>
    public async Task<HubComputeJob> AddComputeJobAsync(string model, string inputHash, string inputName, CancellationToken ct = default) =>
        await Read<HubComputeJob>(await Send(() => _http.PostAsJsonAsync("compute/jobs", new { model, input_hash = inputHash, input_name = inputName }, Wire, ct)), ct);

    /// <summary>Asks the hub for a job among the models this device can run; null when there is none for it.</summary>
    public async Task<HubComputeJob?> ClaimComputeAsync(string name, string rid, IReadOnlyCollection<string> models, CancellationToken ct = default)
    {
        var resp = await Send(() => _http.PostAsJsonAsync("compute/claim", new { name, rid, models }, Wire, ct));
        if (resp.StatusCode == HttpStatusCode.NoContent) { resp.Dispose(); return null; }
        return await Read<HubComputeJob>(resp, ct);
    }

    /// <summary>Asks for an image already in the app to be upscaled (entity type and its sync id); the hub reads the
    /// original itself and puts the result next to it. A refusal comes back as a HubException with the hub's message.</summary>
    public async Task<HubComputeJob> AddComputeJobFromAsync(string entityType, string entityId, CancellationToken ct = default) =>
        await AddComputeJobFromAsync(new { entity_type = entityType, entity_id = entityId }, ct);

    /// <summary>Asks for an AI edit of an image already in the app: kind "edit" (a sentence, optionally a mask blob, a
    /// text to mask, whether to empty the zone first, optionally a frame [x, y, w, h] in fractions: the only part shown to
    /// the AI, pasted back) or "restore". Once done the hub makes the result a revertible edit.</summary>
    public async Task<HubComputeJob> AddComputeEditAsync(string entityType, string entityId, string kind, string? prompt,
        string? maskHash, string? maskText, bool erase, int tries = 1, string? engine = null, double[]? frame = null, CancellationToken ct = default) =>
        await AddComputeJobFromAsync(new { entity_type = entityType, entity_id = entityId, kind, prompt, mask_hash = maskHash, mask_text = maskText, erase, tries, engine, frame }, ct);

    private async Task<HubComputeJob> AddComputeJobFromAsync(object body, CancellationToken ct)
    {
        var resp = await Send(() => _http.PostAsJsonAsync("compute/jobs/from", body, Wire, ct));
        if (resp.StatusCode == HttpStatusCode.UnprocessableEntity)
        {
            using (resp)
            {
                var text = await resp.Content.ReadAsStringAsync(ct);
                string? message = null;
                try { message = JsonDocument.Parse(text).RootElement.GetProperty("error").GetString(); } catch (Exception) { }
                throw new HubException(message ?? text, resp.StatusCode);
            }
        }
        return await Read<HubComputeJob>(resp, ct);
    }

    /// <summary>`resultHashes`: every version of a job asked with several tries (they then wait for a choice).</summary>
    public async Task CompleteComputeAsync(string id, string resultHash, double seconds, IReadOnlyList<string>? resultHashes = null, CancellationToken ct = default) =>
        await Ensure(await Send(() => _http.PostAsJsonAsync($"compute/jobs/{id}/complete", new { result_hash = resultHash, seconds, result_hashes = resultHashes }, Wire, ct)), ct);

    /// <summary>Keeps one of the versions of a job asked with several tries: the hub poses it as an edit of the picture.</summary>
    public async Task ChooseComputeAsync(string id, string resultHash, CancellationToken ct = default)
    {
        var resp = await Send(() => _http.PostAsJsonAsync($"compute/jobs/{id}/choose", new { result_hash = resultHash }, Wire, ct));
        if (resp.StatusCode == HttpStatusCode.UnprocessableEntity)
        {
            using (resp)
            {
                var text = await resp.Content.ReadAsStringAsync(ct);
                string? message = null;
                try { message = JsonDocument.Parse(text).RootElement.GetProperty("error").GetString(); } catch (Exception) { }
                throw new HubException(message ?? text, resp.StatusCode);
            }
        }
        await Ensure(resp, ct);
    }

    public async Task FailComputeAsync(string id, string error, CancellationToken ct = default) =>
        await Ensure(await Send(() => _http.PostAsJsonAsync($"compute/jobs/{id}/fail", new { error }, Wire, ct)), ct);

    public async Task RetryComputeAsync(string id, CancellationToken ct = default) =>
        await Ensure(await Send(() => _http.PostAsJsonAsync($"compute/jobs/{id}/retry", new { }, Wire, ct)), ct);

    public async Task RemoveComputeAsync(string id, CancellationToken ct = default) =>
        await Ensure(await Send(() => _http.DeleteAsync($"compute/jobs/{id}", ct)), ct);

    /// <summary>Downloads a model file of the hub's catalogue (verified against its hash).</summary>
    public Task<bool> DownloadComputeFileAsync(string hash, string target, CancellationToken ct = default) =>
        DownloadAsync($"compute/files/{hash}", hash, target, ct);

    // ---- Files (blobs addressed by the SHA-256 of their content). A file can be large and slow to move:
    // these calls have their own, much longer, time limit.

    private static readonly TimeSpan FileTimeout = TimeSpan.FromMinutes(15);

    /// <summary>Does the hub already hold this content?</summary>
    public async Task<bool> HasFileAsync(string hash, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Head, $"media/{hash}");
        using var resp = await Send(() => _http.SendAsync(req, ct));
        if (resp.StatusCode == HttpStatusCode.NotFound) return false;
        if (resp.StatusCode == HttpStatusCode.Unauthorized) throw new HubException("Le hub refuse ce jeton : appareil non appaire ou revoque.", resp.StatusCode);
        return resp.IsSuccessStatusCode;
    }

    public async Task UploadFileAsync(string path, string hash, CancellationToken ct = default)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(FileTimeout);
        await using var file = File.OpenRead(path);
        using var content = new StreamContent(file);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        using var req = new HttpRequestMessage(HttpMethod.Post, $"media?hash={hash}") { Content = content };
        using var resp = await Send(() => _long.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, limit.Token));
        if (!resp.IsSuccessStatusCode)
            throw new HubException($"Envoi d'un fichier refuse par le hub ({(int)resp.StatusCode}).", resp.StatusCode);
    }

    /// <summary>Downloads a content to `target` (written beside it first, moved once complete and
    /// verified). False when the hub does not have it (yet).</summary>
    public Task<bool> DownloadFileAsync(string hash, string target, CancellationToken ct = default) =>
        DownloadAsync($"media/{hash}", hash, target, ct);

    /// <summary>The hub's small JPEG preview of a picture (~30 KB); null when it is not a picture or unknown.</summary>
    public async Task<byte[]?> ThumbAsync(string hash, CancellationToken ct = default)
    {
        using var resp = await _http.GetAsync($"media/{hash}/thumb", ct);
        return resp.IsSuccessStatusCode ? await resp.Content.ReadAsByteArrayAsync(ct) : null;
    }

    private async Task<bool> DownloadAsync(string url, string hash, string target, CancellationToken ct)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(FileTimeout);
        using var resp = await Send(() => _long.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, limit.Token));
        if (resp.StatusCode == HttpStatusCode.NotFound) return false;
        if (!resp.IsSuccessStatusCode)
            throw new HubException($"Telechargement d'un fichier refuse par le hub ({(int)resp.StatusCode}).", resp.StatusCode);

        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var temp = target + ".part";
        try
        {
            string actual;
            await using (var file = File.Create(temp))
            using (var sha = System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256))
            {
                await using var body = await resp.Content.ReadAsStreamAsync(limit.Token);
                var buffer = new byte[81920];
                int n;
                while ((n = await body.ReadAsync(buffer, limit.Token)) > 0)
                {
                    sha.AppendData(buffer, 0, n);
                    await file.WriteAsync(buffer.AsMemory(0, n), limit.Token);
                }
                actual = Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant();
            }
            if (actual != hash) throw new HubException("Fichier recu different de son empreinte : ignore.");
            File.Move(temp, target, overwrite: true);
            return true;
        }
        finally
        {
            try { File.Delete(temp); } catch (IOException) { }
        }
    }

    private static async Task<HttpResponseMessage> Send(Func<Task<HttpResponseMessage>> call)
    {
        try { return await call(); }
        catch (HttpRequestException ex) { throw new HubException("Hub injoignable : " + ex.Message); }
        catch (TaskCanceledException) { throw new HubException("Hub injoignable : delai depasse."); }
    }

    private static async Task Ensure(HttpResponseMessage resp, CancellationToken ct)
    {
        using (resp)
        {
            if (resp.StatusCode == HttpStatusCode.Unauthorized)
                throw new HubException("Le hub refuse ce jeton : appareil non appaire ou revoque.", resp.StatusCode);
            if (!resp.IsSuccessStatusCode)
                throw new HubException($"Le hub a repondu {(int)resp.StatusCode} : {await resp.Content.ReadAsStringAsync(ct)}", resp.StatusCode);
        }
    }

    private static async Task<T> Read<T>(HttpResponseMessage resp, CancellationToken ct)
    {
        if (resp.StatusCode == HttpStatusCode.Unauthorized)
            throw new HubException("Le hub refuse ce jeton : appareil non appaire ou revoque.", resp.StatusCode);
        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync(ct);
            throw new HubException($"Le hub a repondu {(int)resp.StatusCode} : {body}", resp.StatusCode);
        }
        return (await resp.Content.ReadFromJsonAsync<T>(Wire, ct)) ?? throw new HubException("Reponse vide du hub.");
    }

    public void Dispose()
    {
        _http.Dispose();
        _long.Dispose();
    }
}
