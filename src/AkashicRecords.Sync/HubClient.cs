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
    public async Task<bool> DownloadFileAsync(string hash, string target, CancellationToken ct = default)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(FileTimeout);
        using var resp = await Send(() => _long.GetAsync($"media/{hash}", HttpCompletionOption.ResponseHeadersRead, limit.Token));
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
