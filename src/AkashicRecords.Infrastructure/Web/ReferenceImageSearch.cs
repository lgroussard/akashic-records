using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AkashicRecords.Infrastructure.Web;

// An image found online for the Références boards, before it is saved.
public sealed record ReferenceHit(
    string Source, string ThumbUrl, string FullUrl, string Title, string Author, string? PageUrl, int Width, int Height);

// The image a dropped link points to: the link itself when it is an image, else the page's preview (og:image).
public sealed record LinkImage(string ImageUrl, string PageUrl, string Title);

// Same sources as the phone (references/ImageSearch.kt): free public APIs, no key, no bot wall.
//  - Wikimedia Commons: photos, art, scans of everything;
//  - Cleveland Museum of Art: its open-access (CC0) collection.
// Are.na (account token required) and the Art Institute of Chicago (image server refuses non-browsers) were tried.
public static class ReferenceImageSearch
{
    public const string Commons = "Wikimedia Commons";
    public const string Cleveland = "Cleveland Museum of Art";
    public static readonly IReadOnlyList<string> Sources = new[] { Commons, Cleveland };

    private const int PerPage = 30;
    // Generic client name: Wikimedia asks automated clients for a descriptive User-Agent.
    private const string UserAgent = "AkashicRecords/1.0 (personal reference boards; Windows)";
    private const long MaxBytes = 30L * 1024 * 1024;

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = true }) { Timeout = TimeSpan.FromSeconds(20) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        return client;
    }

    // One page (from 1). Throws on network errors so the view can say which source failed.
    public static Task<IReadOnlyList<ReferenceHit>> SearchAsync(string source, string query, int page, CancellationToken ct = default) =>
        source switch
        {
            Commons => CommonsAsync(query, page, ct),
            Cleveland => ClevelandAsync(query, page, ct),
            _ => Task.FromResult<IReadOnlyList<ReferenceHit>>(Array.Empty<ReferenceHit>()),
        };

    private static async Task<IReadOnlyList<ReferenceHit>> CommonsAsync(string query, int page, CancellationToken ct)
    {
        var search = Uri.EscapeDataString(query.Trim() + " filetype:bitmap"); // photos and paintings, no PDF / SVG
        var url = "https://commons.wikimedia.org/w/api.php?action=query&format=json&generator=search" +
                  $"&gsrsearch={search}&gsrnamespace=6&gsrlimit={PerPage}&gsroffset={(page - 1) * PerPage}" +
                  "&prop=imageinfo&iiprop=url%7Csize%7Cextmetadata&iiextmetadatafilter=Artist%7CObjectName&iiurlwidth=480";
        using var doc = JsonDocument.Parse(await Http.GetStringAsync(url, ct));
        if (!doc.RootElement.TryGetProperty("query", out var q) || !q.TryGetProperty("pages", out var pages)) return Array.Empty<ReferenceHit>();
        var hits = new List<(int Index, ReferenceHit Hit)>();
        foreach (var p in pages.EnumerateObject().Select(o => o.Value))
        {
            if (!p.TryGetProperty("imageinfo", out var infos) || infos.GetArrayLength() == 0) continue;
            var info = infos[0];
            string Meta(string key) =>
                info.TryGetProperty("extmetadata", out var m) && m.TryGetProperty(key, out var v) && v.TryGetProperty("value", out var s)
                    ? StripHtml(s.ToString()) : string.Empty;
            var fileTitle = Str(p, "title").Replace("File:", "");
            var dot = fileTitle.LastIndexOf('.');
            if (dot > 0) fileTitle = fileTitle[..dot];
            var full = Str(info, "url");
            hits.Add((Int(p, "index"), new ReferenceHit(
                Commons,
                Str(info, "thumburl") is { Length: > 0 } t ? t : full,
                full,
                Meta("ObjectName") is { Length: > 0 } name ? name : fileTitle,
                Meta("Artist"),
                Str(info, "descriptionurl") is { Length: > 0 } d ? d : null,
                Int(info, "width"), Int(info, "height"))));
        }
        return hits.OrderBy(h => h.Index).Select(h => h.Hit).ToList();
    }

    private static async Task<IReadOnlyList<ReferenceHit>> ClevelandAsync(string query, int page, CancellationToken ct)
    {
        var url = $"https://openaccess-api.clevelandart.org/api/artworks/?q={Uri.EscapeDataString(query.Trim())}" +
                  $"&has_image=1&cc0=1&limit={PerPage}&skip={(page - 1) * PerPage}&fields=id,title,creators,url,images";
        using var doc = JsonDocument.Parse(await Http.GetStringAsync(url, ct));
        if (!doc.RootElement.TryGetProperty("data", out var data)) return Array.Empty<ReferenceHit>();
        var hits = new List<ReferenceHit>();
        foreach (var a in data.EnumerateArray())
        {
            if (!a.TryGetProperty("images", out var images) || images.ValueKind != JsonValueKind.Object) continue;
            if (!images.TryGetProperty("web", out var web) || web.ValueKind != JsonValueKind.Object) continue;
            var webUrl = Str(web, "url");
            if (webUrl.Length == 0) continue;
            var print = images.TryGetProperty("print", out var pr) && pr.ValueKind == JsonValueKind.Object ? Str(pr, "url") : "";
            // "Utagawa Hiroshige (Japanese, 1797–1858)" → "Utagawa Hiroshige"
            var author = a.TryGetProperty("creators", out var cr) && cr.ValueKind == JsonValueKind.Array && cr.GetArrayLength() > 0
                ? Str(cr[0], "description") : "";
            var paren = author.IndexOf(" (", StringComparison.Ordinal);
            if (paren > 0) author = author[..paren];
            hits.Add(new ReferenceHit(
                Cleveland, webUrl, print.Length > 0 ? print : webUrl, Str(a, "title"), author.Trim(),
                Str(a, "url") is { Length: > 0 } page2 ? page2 : null,
                int.TryParse(Str(web, "width"), out var w) ? w : 0, int.TryParse(Str(web, "height"), out var h) ? h : 0));
        }
        return hits;
    }

    // Downloads an image (any size up to 30 MB). Returns the bytes and a file extension.
    public static async Task<(byte[] Bytes, string Extension)> DownloadAsync(string url, CancellationToken ct = default)
    {
        using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        var type = response.Content.Headers.ContentType?.MediaType ?? "";
        if (type.Length > 0 && !type.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Le lien ne mène pas à une image.");
        if (response.Content.Headers.ContentLength > MaxBytes) throw new InvalidOperationException("Image trop lourde.");
        var bytes = await response.Content.ReadAsByteArrayAsync(ct);
        var ext = type switch
        {
            "image/png" => ".png",
            "image/gif" => ".gif",
            "image/webp" => ".webp",
            "image/bmp" => ".bmp",
            _ => ".jpg",
        };
        return (bytes, ext);
    }

    // A dropped or pasted link → the image to save, or null when the page has no preview image.
    public static async Task<LinkImage?> ResolveLinkAsync(string link, CancellationToken ct = default)
    {
        using var response = await Http.GetAsync(link, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        var final = response.RequestMessage?.RequestUri ?? new Uri(link);
        if (response.Content.Headers.ContentType?.MediaType?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) == true)
            return new LinkImage(final.ToString(), link, "");
        var html = await response.Content.ReadAsStringAsync(ct);
        if (html.Length > 512 * 1024) html = html[..(512 * 1024)]; // the <head> is enough
        var image = Meta(html, "og:image:secure_url") ?? Meta(html, "og:image") ?? Meta(html, "twitter:image");
        if (image is null) return null;
        return new LinkImage(new Uri(final, image).ToString(), final.ToString(), Meta(html, "og:title") ?? "");
    }

    // <meta property|name="key" content="…"> in either attribute order.
    private static string? Meta(string html, string key)
    {
        var tag = Regex.Match(html, $"<meta[^>]+(?:property|name)\\s*=\\s*[\"']{Regex.Escape(key)}[\"'][^>]*>", RegexOptions.IgnoreCase);
        if (!tag.Success) return null;
        var content = Regex.Match(tag.Value, "content\\s*=\\s*[\"']([^\"']+)[\"']", RegexOptions.IgnoreCase);
        if (!content.Success) return null;
        var value = System.Net.WebUtility.HtmlDecode(content.Groups[1].Value).Trim();
        return value.Length > 0 ? value : null;
    }

    private static string Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind != JsonValueKind.Null ? v.ToString() : "";

    private static int Int(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : 0;

    private static string StripHtml(string s) =>
        Regex.Replace(System.Net.WebUtility.HtmlDecode(Regex.Replace(s, "<[^>]*>", "")), "\\s+", " ").Trim();
}
