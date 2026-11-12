using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AkashicRecords.Infrastructure.Web;

// One recommended track for the "similar tracks" list.
// PageUrl   = the Apple Music page (what yt-dlp resolves to a full file).
// PreviewUrl= iTunes 30 s clip, for a quick listen of a non-local title.
// Album     = the iTunes album name, when known (search hits: it names the download folder).
// Artwork   = small iTunes cover URL (search results only).
public sealed record SimilarTrack(
    string Title, string Artist, string? PageUrl = null, string? PreviewUrl = null, string? Album = null,
    string? Artwork = null);

// "Similar tracks" lookup, no API key and no account needed (works on a virgin machine).
// Category-first, like the real Spotify algo:
//   * iTunes / Apple Music Search  -> the seed + its album neighbours (30 s preview + page URL).
//   * MusicBrainz                  -> the seed's CATEGORY (genre name), then recordings of that
//     category: first the same artist's other releases, then other artists of the same genre
//     (same sound, different acts). Each pair is enriched back through iTunes for page/preview.
//
// Everything is best-effort: any miss simply returns what was gathered so far (possibly empty),
// so the player never blocks on a flaky/slow source.
public sealed class MusicRecommendationService
{
    private const int MaxResults = 10;
    // Full-length cap for the list: beyond this the "song" is a compilation/set,
    // not the single the feature is meant to propose.
    private const int MaxDurationMillis = 15 * 60 * 1000;

    private static readonly HttpClient Http = CreateClient();
    private static readonly object LogLock = new();

    private static HttpClient CreateClient() =>
        new(new HttpClientHandler { AllowAutoRedirect = true })
        {
            Timeout = TimeSpan.FromSeconds(15)
        };

    // MusicBrainz answers 403 without an explicit User-Agent; iTunes tolerates one anyway.
    static MusicRecommendationService()
    {
        Http.DefaultRequestHeaders.UserAgent.ParseAdd("AkashicRecords/1.0 (similar-tracks lookup)");
    }

    private static void Log(string reason)
    {
        try
        {
            var folder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "AkashicRecords");
            Directory.CreateDirectory(folder);
            lock (LogLock)
            {
                File.AppendAllText(Path.Combine(folder, "music-recommend.log"),
                    $"{DateTime.Now:O} {reason}\n");
            }
        }
        catch
        {
            // Logging must never throw.
        }
    }

    // Combined list of neighbours for a title (artist helps disambiguate). Best-effort.
    // Lists already built, per seed: a title heard again (or the panel refreshed) costs no request. Shared by every
    // instance (the player creates its own service).
    private static readonly Dictionary<string, IReadOnlyList<SimilarTrack>> SimilarCache = new();

    public async Task<IReadOnlyList<SimilarTrack>> GetSimilarAsync(
        string title, string? artist, int limit = MaxResults, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(title)) return Array.Empty<SimilarTrack>();
        // A downloaded song is named after its YouTube video ("Daft Punk - Get Lucky (Official Audio) ft. Pharrell…", artist =
        // the channel): searched as is, iTunes and MusicBrainz matched other songs, so its suggestions drifted away from it.
        // The seed is brought back to the plain title + artist a suggestion carries (SeedOf).
        (title, artist) = SeedOf(title, artist);
        var key = $"{title.ToLowerInvariant()}|{artist?.ToLowerInvariant()}|{limit}";
        lock (SimilarCache)
            if (SimilarCache.TryGetValue(key, out var cached)) return cached;
        // A list cut short by the iTunes limit is not kept, so the next try can complete it.
        var refusalsBefore = ItunesGate.RefusalCount;
        var list = await GetSimilarUncachedAsync(title, artist, limit, ct);
        if (list.Count > 0 && ItunesGate.RefusalCount == refusalsBefore)
            lock (SimilarCache)
            {
                if (SimilarCache.Count >= 60) SimilarCache.Remove(SimilarCache.Keys.First());
                SimilarCache[key] = list;
            }
        return list;
    }

    private async Task<IReadOnlyList<SimilarTrack>> GetSimilarUncachedAsync(
        string title, string? artist, int limit, CancellationToken ct)
    {
        if (!System.Net.NetworkInformation.NetworkInterface.GetIsNetworkAvailable())
            return Array.Empty<SimilarTrack>();

        var clean = title;
        var query = string.IsNullOrWhiteSpace(artist) ? clean : $"{clean} {artist.Trim()}";
        var result = new List<SimilarTrack>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            // --- iTunes: seed + a couple of album neighbours (rich: preview + page) --------
            var hits = await ItunesSearchAsync(query, 3, ct);
            string? itunesGenre = null;
            if (hits.Count > 0)
            {
                var seed = hits[0];
                itunesGenre = seed.Genre;
                Add(result, seen, seed.Title, seed.Artist, seed.Page, seed.Preview, seed.DurationMillis);
                if (seed.CollectionId is { } cid)
                {
                    // Leave room for the category blocks below.
                    var albumCap = Math.Max(1, limit - 3);
                    foreach (var t in await ItunesLookupAlbumAsync(cid, albumCap, ct))
                        Add(result, seen, t.Item1, t.Item2, t.Item3, t.Item4, t.Item5);
                }
            }

            // --- Category-based neighbours: MusicBrainz genre first, iTunes as fallback ----
            // (MusicBrainz recordings often carry no genre at all; iTunes always answers.)
            // Fielded query when the artist is known: a loose one often landed on a namesake recording (another genre).
            var mbQuery = string.IsNullOrWhiteSpace(artist) ? clean : $"recording:\"{MbEscape(clean)}\" AND artist:\"{MbEscape(artist)}\"";
            var genre = await MusicBrainzGenreAsync(mbQuery, ct)
                ?? (mbQuery != query ? await MusicBrainzGenreAsync(query, ct) : null)
                ?? itunesGenre;
            if (!string.IsNullOrWhiteSpace(genre))
            {
                // Same artist, other releases sharing the category...
                if (!string.IsNullOrWhiteSpace(artist) && result.Count < limit)
                {
                    foreach (var t in await MusicBrainzSearchAsync(
                        $"genre:\"{genre}\" AND artist:\"{artist!.Trim()}\"", limit - result.Count, ct))
                        await AddEnrichedAsync(result, seen, t.Item1, t.Item2, ct);
                }
                // ...then other artists of the category: same sound, different acts.
                if (result.Count < limit)
                {
                    foreach (var t in await MusicBrainzSearchAsync(
                        $"genre:\"{genre}\"", limit - result.Count + 2, ct))
                    {
                        if (result.Count >= limit) break;
                        await AddEnrichedAsync(result, seen, t.Item1, t.Item2, ct);
                    }
                }
            }

            // --- Category fill: remaining slots keyed on the genre name (iTunes). ---------
            // Keeps lists near the limit instead of stopping at 2-4 when MusicBrainz is thin.
            if (!string.IsNullOrWhiteSpace(genre) && result.Count < limit)
            {
                foreach (var t in await ItunesSearchAsync(genre!.Trim(), limit - result.Count, ct))
                    Add(result, seen, t.Title, t.Artist, t.Page, t.Preview, t.DurationMillis);
            }

            return result;
        }
        catch (OperationCanceledException)
        {
            return result;
        }
        catch (HttpRequestException ex)
        {
            Log($"network: {ex.GetType().Name}: {ex.Message}");
            return result;
        }
        catch (Exception ex)
        {
            Log($"{ex.GetType().Name}: {ex.Message}");
            return result;
        }
    }

    // Adds a title once (dedup by normalized title), up to the limit. Rows without a 30 s
    // clip are dropped (nothing to hear on ▶), as are entries longer than 15 minutes
    // (compilation sets, not single tracks).
    private static void Add(
        List<SimilarTrack> list, HashSet<string> seen,
        string title, string artist, string? page, string? preview, int? durationMillis = null)
    {
        if (list.Count >= MaxResults) return;
        if (string.IsNullOrWhiteSpace(title)) return;
        if (string.IsNullOrWhiteSpace(preview)) return;
        if (durationMillis is > MaxDurationMillis) return;
        if (!seen.Add(Normalized(title))) return;
        list.Add(new SimilarTrack(title, artist ?? string.Empty, page, preview));
    }

    // Up to `cap` iTunes search hits (title/artist/page/preview + album id). Several hits with
    // the same title but different artists give the cross-artist diversity of the real algo.
    private static async Task<List<(string Title, string Artist, string? Page, string? Preview, int? CollectionId, string? Genre, int? DurationMillis)>>
        ItunesSearchAsync(string query, int cap, CancellationToken ct)
    {
        var list = new List<(string Title, string Artist, string? Page, string? Preview, int? CollectionId, string? Genre, int? DurationMillis)>();
        var url = "https://itunes.apple.com/search?term=" + Uri.EscapeDataString(query) +
                  "&entity=song&limit=" + cap;
        var json = await GetStringAsync(url, ct);
        if (json is null) return list;

        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("results", out var results) ||
            results.ValueKind != JsonValueKind.Array)
            return list;

        foreach (var r in results.EnumerateArray())
        {
            var name = Str(r, "trackName");
            if (string.IsNullOrWhiteSpace(name)) continue;
            list.Add((name!, Str(r, "artistName") ?? string.Empty, Str(r, "trackViewUrl"),
                      Str(r, "previewUrl"), Int(r, "collectionId"), Str(r, "primaryGenreName"),
                      Int(r, "trackTimeMillis")));
            if (list.Count >= cap) break;
        }
        return list;
    }

    // All songs of one album (collection), for the album-neighbour block.
    private static async Task<List<(string, string, string?, string?, int?)>> ItunesLookupAlbumAsync(
        int collectionId, int limit, CancellationToken ct)
    {
        var list = new List<(string, string, string?, string?, int?)>();
        var url = "https://itunes.apple.com/lookup?id=" + collectionId + "&entity=song&limit=" + limit;
        var json = await GetStringAsync(url, ct);
        if (json is null) return list;

        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("results", out var results) ||
            results.ValueKind != JsonValueKind.Array)
            return list;

        foreach (var r in results.EnumerateArray())
        {
            var name = Str(r, "trackName");
            if (string.IsNullOrWhiteSpace(name)) continue;
            list.Add((name!, Str(r, "artistName") ?? string.Empty, Str(r, "trackViewUrl"), Str(r, "previewUrl"),
                      Int(r, "trackTimeMillis")));
            if (list.Count >= limit) break;
        }
        return list;
    }

    // Seed recording -> its category name (MusicBrainz genre), from the release carrying it.
    private static async Task<string?> MusicBrainzGenreAsync(string query, CancellationToken ct)
    {
        var searchJson = await GetStringAsync(
            "https://musicbrainz.org/ws/2/recording?query=" + Uri.EscapeDataString(query) + "&fmt=json", ct);
        if (searchJson is null) return null;

        // A single recording may carry no genre; the top few hits cover the gaps cheaply.
        var ids = new List<string>();
        using (var doc = JsonDocument.Parse(searchJson))
        {
            if (doc.RootElement.TryGetProperty("recordings", out var recs) &&
                recs.ValueKind == JsonValueKind.Array)
            {
                foreach (var rec in recs.EnumerateArray())
                {
                    var id = Str(rec, "id");
                    if (!string.IsNullOrWhiteSpace(id)) ids.Add(id!);
                    if (ids.Count >= 3) break;
                }
            }
        }
        if (ids.Count == 0) return null;

        // The genre comes from the release: any of the top hits may be the one carrying it.
        foreach (var recordingId in ids)
        {
            var detail = await GetStringAsync(
                "https://musicbrainz.org/ws/2/recording/" + recordingId + "?inc=releases+genres&fmt=json", ct);
            if (detail is null) continue;

            using var full = JsonDocument.Parse(detail);
            if (TryFirstGenre(full.RootElement, out var name)) return name;
            if (full.RootElement.TryGetProperty("releases", out var rels) && rels.ValueKind == JsonValueKind.Array)
            {
                foreach (var rel in rels.EnumerateArray())
                    if (TryFirstGenre(rel, out name)) return name;
            }
        }
        return null;
    }

    // Up to `cap` recordings for a MusicBrainz field query ("genre:X AND artist:Y").
    private static async Task<List<(string, string)>> MusicBrainzSearchAsync(
        string query, int cap, CancellationToken ct)
    {
        var list = new List<(string, string)>();
        if (cap <= 0) return list;

        var json = await GetStringAsync(
            "https://musicbrainz.org/ws/2/recording?query=" + Uri.EscapeDataString(query) +
            "&limit=" + cap + "&fmt=json", ct);
        if (json is null) return list;

        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("recordings", out var recs) ||
            recs.ValueKind != JsonValueKind.Array)
            return list;

        foreach (var rec in recs.EnumerateArray())
        {
            var title = Str(rec, "title");
            if (string.IsNullOrWhiteSpace(title)) continue;
            string artist = string.Empty;
            if (rec.TryGetProperty("artist-credit", out var ac) && ac.ValueKind == JsonValueKind.Array)
            {
                foreach (var a in ac.EnumerateArray())
                {
                    if (a.TryGetProperty("name", out var nm) && !string.IsNullOrWhiteSpace(nm.GetString()))
                    {
                        artist = nm.GetString()!;
                        break;
                    }
                }
            }
            list.Add((title!, artist));
            if (list.Count >= cap) break;
        }
        return list;
    }

    // Adds a (title, artist) pair after resolving page/preview through iTunes.
    private static async Task AddEnrichedAsync(
        List<SimilarTrack> list, HashSet<string> seen, string title, string? artist, CancellationToken ct)
    {
        string? page = null, preview = null;
        int? durationMillis = null;
        // Past the background budget the title is kept without a clip (the full track still comes from yt-dlp): one
        // iTunes request per suggestion is what used to eat the whole minute and lock the search out.
        if (!ItunesGate.BackgroundAvailable)
        {
            Add(list, seen, title, artist ?? string.Empty, null, null, null);
            return;
        }
        var hit = await ItunesSearchAsync(
            string.IsNullOrWhiteSpace(artist) ? title : $"{title} {artist}", 1, ct);
        if (hit.Count > 0)
        {
            page = hit[0].Page;
            preview = hit[0].Preview;
            durationMillis = hit[0].DurationMillis;
        }
        Add(list, seen, title, artist ?? string.Empty, page, preview, durationMillis);
    }

    private static bool TryFirstGenre(JsonElement element, out string name)
    {
        name = string.Empty;
        if (!element.TryGetProperty("genres", out var genres) || genres.ValueKind != JsonValueKind.Array)
            return false;
        foreach (var g in genres.EnumerateArray())
        {
            var n = Str(g, "name");
            if (!string.IsNullOrWhiteSpace(n))
            {
                name = n!;
                return true;
            }
        }
        return false;
    }

    // One-shot connectivity test: exercises the real chain (iTunes album + MB extras).
    // Returns "ok" | "no-result" | "error: ...".
    public async Task<string> TestAsync(CancellationToken ct = default)
    {
        if (!System.Net.NetworkInformation.NetworkInterface.GetIsNetworkAvailable())
            return "error: réseau indisponible";
        try
        {
            var recos = await GetSimilarAsync("In Uppercase", null, 3, ct);
            return recos.Count > 0 ? "ok" : "no-result";
        }
        catch (Exception ex)
        {
            return $"error: {ex.GetType().Name}: {ex.Message}";
        }
    }

    // iTunes requests go through ItunesGate (gated = false when the caller already took its slot, see OnlineMusicSearch).
    internal static async Task<string?> GetStringAsync(string url, CancellationToken ct, bool gated = true)
    {
        var itunes = ItunesGate.IsItunes(url);
        if (itunes && gated && !ItunesGate.TryAcquire(search: false)) return null;
        try
        {
            using var response = await Http.GetAsync(url, ct);
            if (itunes && (int)response.StatusCode is 403 or 429) ItunesGate.Refused();
            if (!response.IsSuccessStatusCode)
            {
                Log($"HTTP {(int)response.StatusCode} for {url}");
                return null;
            }
            return await response.Content.ReadAsStringAsync(ct);
        }
        catch (HttpRequestException ex)
        {
            Log($"network: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    private static string? Str(JsonElement el, string prop) =>
        el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int? Int(JsonElement el, string prop) =>
        el.TryGetProperty(prop, out var v) && v.TryGetInt32(out var n) ? n : null;

    private static string Normalized(string s) => s.Trim().ToLowerInvariant();

    // yt-dlp filenames look like "Title [youtubeId]": drop the trailing bracket id so the
    // search term matches what the music APIs index. A leading track number ("01. ", "5.", "07 - ") is dropped too.
    private static string CleanTitle(string raw)
    {
        var t = raw.Trim();
        t = Regex.Replace(t, @"\s*\[[A-Za-z0-9_-]{11}\]\s*$", ""); // the YouTube id
        var bracket = t.IndexOf('[');
        if (bracket > 0 && !t[bracket..].Contains(']')) t = t[..bracket].Trim();
        t = Regex.Replace(t, @"^\d{1,2}\s*(\.|-|–)\s*", "").Trim();
        return t;
    }

    private static string MbEscape(string s) => s.Replace("\\", "").Replace("\"", "");

    // Words that only describe the video, not the song: "(Official Audio)", "[HD]", "| Lyrics"…
    private static readonly Regex Noise = new(
        @"\b(official|officiel|audio|video|vidéo|clip|lyrics?|paroles|hd|hq|4k|remaster(ed)?|visuali[sz]er|mv|explicit)\b",
        RegexOptions.IgnoreCase);

    // (title, artist) of a song as the music APIs know it, from a library file name / tags or a suggestion (already
    // clean: left as is). Steps: video noise in brackets dropped, "ft./feat." credits dropped, "Artist - Title" split
    // (the artist side found by the known artist, else YouTube's usual order), channel suffixes ("VEVO", " - Topic")
    // dropped from the artist. Same as the phone's Recommendations.seed.
    public static (string Title, string Artist) SeedOf(string rawTitle, string? rawArtist)
    {
        var t = CleanTitle(rawTitle);
        // A web slug ("mika-ring-ring", "vianney-je-men-vais-clip-officiel"): words back.
        if (!t.Contains(' ') && t.Count(c => c == '-' || c == '_') >= 2) t = t.Replace('-', ' ').Replace('_', ' ').TrimEnd('.');
        // Bracketed parts that are only video noise; other brackets ("(Remix)", "(Live)") stay.
        t = Regex.Replace(t, @"[\(\[]([^\)\]]*)[\)\]]", m => Noise.IsMatch(m.Groups[1].Value) ? " " : m.Value);
        // The same noise left bare at the end: "… Lyrics", "… Extreme HD", "… clip officiel".
        t = Regex.Replace(t, @"(\s+" + Noise + @")+\s*$", "", RegexOptions.IgnoreCase);
        var pipe = t.IndexOf(" | ", StringComparison.Ordinal);
        if (pipe > 0) t = t[..pipe];
        var slashes = t.IndexOf(" // ", StringComparison.Ordinal);
        if (slashes > 0) t = t[..slashes];
        t = Regex.Replace(t, @"\s+(ft\.?|feat\.?|featuring)\s+.*$", "", RegexOptions.IgnoreCase);
        t = Regex.Replace(t, @"\s+", " ").Trim().Trim('-', '–', '—', ' ');

        var artist = (rawArtist ?? "").Trim();
        artist = Regex.Replace(artist, @"(?i)\s*(vevo|official|- topic)$", "").Trim();
        // A channel name squeezed together ("DaftPunk" from "DaftPunkVEVO"): spaces back between the words.
        if (!artist.Contains(' ')) artist = Regex.Replace(artist, @"(?<=\p{Ll})(?=\p{Lu})", " ");
        // "Artist - Title" (also "Artist - Title - Album"): keep the title side.
        var parts = Regex.Split(t, @"\s+[-–—]\s+").Select(p => p.Trim()).Where(p => p.Length > 0).ToList();
        if (parts.Count >= 2)
        {
            static string Fold(string s) => new(s.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
            static bool Same(string a, string b)
            {
                var x = Fold(a);
                var y = Fold(b);
                return x.Length > 0 && y.Length > 0 && (x.Contains(y) || y.Contains(x));
            }
            if (artist.Length == 0 || Same(parts[0], artist)) { artist = parts[0]; t = parts[1]; }
            else if (Same(parts[1], artist)) t = parts[0];
            // A channel name that is not the artist: YouTube's usual order wins.
            else { artist = parts[0]; t = parts[1]; }
        }
        return (t.Length > 0 ? t : CleanTitle(rawTitle), artist);
    }
}
