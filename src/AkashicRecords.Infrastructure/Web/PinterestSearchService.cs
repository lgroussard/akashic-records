using System.Net.Http;
using System.Text.Json;

namespace AkashicRecords.Infrastructure.Web;

// One image hit from Pinterest (search result or pin on a board).
public sealed record PinterestHit(string Title, string? ImageUrl);

// A board of the authenticated account.
public sealed record PinterestBoardInfo(string Id, string Name, int PinCount);

// Pinterest REST API v5 client (requires a free OAuth token from the developer portal,
// tier "trial" = 1000 req/day). Used as the artistic-works image bank: search by term,
// or list the account's own boards and the pins inside one board.
// Everything is best-effort: any miss returns what was gathered so far.
public sealed class PinterestSearchService
{
    private const string BaseUrl = "https://api.pinterest.com/v5";

    private static readonly HttpClient Http = CreateClient();
    private readonly string? _token;

    private static HttpClient CreateClient() =>
        new(new HttpClientHandler { AllowAutoRedirect = true })
        {
            Timeout = TimeSpan.FromSeconds(15)
        };

    public PinterestSearchService(string? token = null)
    {
        _token = token;
    }

    // Up to `limit` image URLs (+ titles) for a term. First non-empty image wins per pin.
    public async Task<IReadOnlyList<PinterestHit>> SearchAsync(
        string term, int limit = 5, CancellationToken ct = default)
    {
        var list = new List<PinterestHit>();
        if (string.IsNullOrWhiteSpace(_token) || string.IsNullOrWhiteSpace(term)) return list;
        if (!System.Net.NetworkInformation.NetworkInterface.GetIsNetworkAvailable()) return list;

        try
        {
            var url = $"{BaseUrl}/search?term={Uri.EscapeDataString(term)}" +
                      $"&limit={Math.Clamp(limit, 1, 50)}";
            var json = await GetStringAsync(url, ct);
            if (json is null) return list;

            using var doc = JsonDocument.Parse(json);
            CollectHits(doc.RootElement, list, limit);
            return list;
        }
        catch (OperationCanceledException) { return list; }
        catch (HttpRequestException) { return list; }
        catch (JsonException) { return list; }
    }

    // The account's own boards (id, name, pin count).
    public async Task<IReadOnlyList<PinterestBoardInfo>> GetBoardsAsync(CancellationToken ct = default)
    {
        var list = new List<PinterestBoardInfo>();
        if (string.IsNullOrWhiteSpace(_token)) return list;
        if (!System.Net.NetworkInformation.NetworkInterface.GetIsNetworkAvailable()) return list;

        try
        {
            var json = await GetStringAsync($"{BaseUrl}/boards?page_size=50", ct);
            if (json is null) return list;

            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("items", out var items) ||
                items.ValueKind != JsonValueKind.Array)
                return list;

            foreach (var b in items.EnumerateArray())
            {
                var id = Str(b, "id");
                if (string.IsNullOrWhiteSpace(id)) continue;
                list.Add(new PinterestBoardInfo(id!, Str(b, "name") ?? id!, Int(b, "pin_count") ?? 0));
            }
            return list;
        }
        catch (OperationCanceledException) { return list; }
        catch (HttpRequestException) { return list; }
        catch (JsonException) { return list; }
    }

    // Pins of one board, as image hits.
    public async Task<IReadOnlyList<PinterestHit>> GetBoardPinsAsync(
        string boardId, int limit = 50, CancellationToken ct = default)
    {
        var list = new List<PinterestHit>();
        if (string.IsNullOrWhiteSpace(_token) || string.IsNullOrWhiteSpace(boardId)) return list;
        if (!System.Net.NetworkInformation.NetworkInterface.GetIsNetworkAvailable()) return list;

        try
        {
            var url = $"{BaseUrl}/boards/{Uri.EscapeDataString(boardId)}/pins" +
                      $"?page_size={Math.Clamp(limit, 1, 100)}";
            var json = await GetStringAsync(url, ct);
            if (json is null) return list;

            using var doc = JsonDocument.Parse(json);
            CollectHits(doc.RootElement, list, limit);
            return list;
        }
        catch (OperationCanceledException) { return list; }
        catch (HttpRequestException) { return list; }
        catch (JsonException) { return list; }
    }

    // Connectivity test for the settings panel. "ok" | "no-result" | "error: ..." —
    // "ok" only when real image URLs came back.
    public async Task<string> TestAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_token)) return "error: aucune clé Pinterest configurée";
        if (!System.Net.NetworkInformation.NetworkInterface.GetIsNetworkAvailable())
            return "error: réseau indisponible";
        try
        {
            var hits = await SearchAsync("inception", 2, ct);
            if (hits.Count == 0) return "no-result";
            return hits.Any(h => !string.IsNullOrWhiteSpace(h.ImageUrl)) ? "ok" : "no-result";
        }
        catch (Exception ex)
        {
            return $"error: {ex.GetType().Name}: {ex.Message}";
        }
    }

    // Pulls (title, best image url) out of a "items" array (search or board-pins shape).
    private static void CollectHits(JsonElement root, List<PinterestHit> list, int limit)
    {
        if (!root.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
            return;

        foreach (var pin in items.EnumerateArray())
        {
            var title = Str(pin, "title") ?? string.Empty;
            string? image = null;
            if (pin.TryGetProperty("images", out var imgs) && imgs.ValueKind == JsonValueKind.Object)
            {
                // Prefer "orig"/"original", fall back to any sized variant.
                image = FirstImageUrl(imgs, "orig") ?? FirstImageUrl(imgs, "original")
                        ?? FirstImageUrl(imgs, "large") ?? FirstImageUrl(imgs, "medium")
                        ?? FirstImageUrl(imgs, "small");
            }
            if (string.IsNullOrWhiteSpace(image)) continue;
            list.Add(new PinterestHit(title, image));
            if (list.Count >= limit) break;
        }
    }

    private static string? FirstImageUrl(JsonElement images, string key)
    {
        if (!images.TryGetProperty(key, out var variant)) return null;
        if (variant.ValueKind != JsonValueKind.Object) return null;
        var u = Str(variant, "url");
        return string.IsNullOrWhiteSpace(u) ? null : u;
    }

    private async Task<string?> GetStringAsync(string url, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + _token);
        using var response = await Http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode) return null;
        var body = await response.Content.ReadAsStringAsync(ct);
        return string.IsNullOrWhiteSpace(body) ? null : body;
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    private static int? Int(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i)
            ? i
            : null;
}
