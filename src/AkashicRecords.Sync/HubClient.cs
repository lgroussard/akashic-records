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

    public HubClient(string hubUrl, string? token = null, HttpMessageHandler? handler = null)
    {
        _http = handler is null ? new HttpClient() : new HttpClient(handler);
        _http.BaseAddress = new Uri(hubUrl.TrimEnd('/') + "/");
        _http.Timeout = TimeSpan.FromSeconds(20);
        if (!string.IsNullOrEmpty(token))
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
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

    private static async Task<HttpResponseMessage> Send(Func<Task<HttpResponseMessage>> call)
    {
        try { return await call(); }
        catch (HttpRequestException ex) { throw new HubException("Hub injoignable : " + ex.Message); }
        catch (TaskCanceledException) { throw new HubException("Hub injoignable : delai depasse."); }
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

    public void Dispose() => _http.Dispose();
}
