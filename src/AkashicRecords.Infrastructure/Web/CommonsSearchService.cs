using System.Net.Http;
using System.Text.Json;

namespace AkashicRecords.Infrastructure.Web;

// One Commons hit: display image + its short description.
public sealed record CommonsHit(string Title, string? ImageUrl);

// Wikimedia Commons search client (no key required). Used to enrich the Atelier's study
// references with a real thumbnail when the network is up; every miss simply returns what
// was gathered, so an offline run still shows the text-only reference.
public sealed class CommonsSearchService
{
    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = true })
        {
            Timeout = TimeSpan.FromSeconds(10)
        };
        // Commons asks API clients to send a descriptive User-Agent.
        client.DefaultRequestHeaders.Add("User-Agent", "AkashicRecords/1.0 (personal desktop app)");
        return client;
    }

    // First `limit` image URLs (+ titles) for a term, via the generator=search endpoint.
    public async Task<IReadOnlyList<CommonsHit>> SearchAsync(
        string term, int limit = 3, CancellationToken ct = default)
    {
        var list = new List<CommonsHit>();
        if (string.IsNullOrWhiteSpace(term)) return list;
        if (!System.Net.NetworkInformation.NetworkInterface.GetIsNetworkAvailable()) return list;

        try
        {
            var url = "https://commons.wikimedia.org/w/api.php?action=query&format=json" +
                      $"&generator=search&gsrlimit={Math.Clamp(limit, 1, 10)}" +
                      "&gsrnamespace=6&prop=imageinfo&iiprop=url|extmetadata" +
                      $"&gsrsearch={Uri.EscapeDataString(term)}";
            using var doc = await GetJsonAsync(url, ct);
            if (doc is null) return list;

            if (!doc.RootElement.TryGetProperty("query", out var query)
                || !query.TryGetProperty("pages", out var pages)) return list;

            foreach (var page in pages.EnumerateObject())
            {
                if (list.Count >= limit) break;
                var title = page.Value.TryGetProperty("title", out var t) ? t.GetString() : null;
                var image = FirstImageUrl(page.Value);
                if (image is not null) list.Add(new CommonsHit(title ?? term, image));
            }
            return list;
        }
        catch (OperationCanceledException) { return list; }
        catch (HttpRequestException) { return list; }
        catch (JsonException) { return list; }
    }

    private static async Task<JsonDocument?> GetJsonAsync(string url, CancellationToken ct)
    {
        var json = await Http.GetStringAsync(url, ct);
        return JsonDocument.Parse(json);
    }

    // imageinfo[0].url, when present.
    private static string? FirstImageUrl(JsonElement page)
    {
        if (!page.TryGetProperty("imageinfo", out var info)) return null;
        foreach (var entry in info.EnumerateArray())
        {
            if (entry.TryGetProperty("url", out var u) && u.ValueKind == JsonValueKind.String)
                return u.GetString();
        }
        return null;
    }
}
