using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AkashicRecords.Infrastructure.Web;

public sealed record OnlineArtist(long Id, string Name, string? Genre);
public sealed record OnlineAlbum(long Id, string Title, string Artist, string? Artwork, string? Year);

// Artists and albums of a query; TopArtist is set when the query names an artist outright (then the best match is
// that artist, not one of its songs).
public sealed record OnlineExtras(IReadOnlyList<OnlineArtist> Artists, IReadOnlyList<OnlineAlbum> Albums, OnlineArtist? TopArtist)
{
    public static readonly OnlineExtras Empty = new(Array.Empty<OnlineArtist>(), Array.Empty<OnlineAlbum>(), null);
}

// Player "RECHERCHER EN LIGNE", against the free iTunes catalogue (no key, no account), searched as you type.
// iTunes allows ~20 requests a minute, so the search is split: songs (1 request) follow the typing, artists + albums
// (2 requests, sent together) come once the typing pauses. Every answer is cached for the app's life, so erasing
// letters costs nothing. iTunes returns its own relevance order; SearchRanking re-sorts the songs on top of it.
// Port of the phone's OnlineSearch.kt — same requests, same rules. Best-effort: empty lists on any failure.
public sealed class OnlineMusicSearch
{
    private const int Songs = 40;
    private const int Artists = 6;
    private const int Albums = 10;
    private const int MaxDurationMillis = 15 * 60 * 1000; // longer = compilation/set, not a song
    private const int CacheSize = 40;

    private readonly Dictionary<string, List<SimilarTrack>> _songCache = new();
    private readonly Dictionary<string, (List<OnlineArtist>, List<OnlineAlbum>)> _extrasCache = new();

    private static void Remember<T>(Dictionary<string, T> cache, string key, T value)
    {
        lock (cache)
        {
            if (cache.Count >= CacheSize) cache.Remove(cache.Keys.First());
            cache[key] = value;
        }
    }

    private static bool Recall<T>(Dictionary<string, T> cache, string key, out T value)
    {
        lock (cache) return cache.TryGetValue(key, out value!);
    }

    // Songs for the query, re-ranked. Only non-empty answers are cached (an empty one may be the rate limit).
    public async Task<IReadOnlyList<SimilarTrack>> TracksAsync(string query, ISet<string> libraryArtists, CancellationToken ct = default)
    {
        var q = query.Trim();
        if (q.Length == 0) return Array.Empty<SimilarTrack>();
        var key = SearchRanking.Norm(q);
        if (!Recall(_songCache, key, out var raw))
        {
            raw = await SongsAsync($"term={Uri.EscapeDataString(q)}&entity=song&limit={Songs}", null, ct);
            if (raw.Count > 0) Remember(_songCache, key, raw);
        }
        return SearchRanking.RankTracks(q, raw, libraryArtists);
    }

    // Artists and albums for the query (two requests at once).
    public async Task<OnlineExtras> ExtrasAsync(string query, CancellationToken ct = default)
    {
        var q = query.Trim();
        if (q.Length == 0) return OnlineExtras.Empty;
        var key = SearchRanking.Norm(q);
        if (!Recall(_extrasCache, key, out var found))
        {
            var term = Uri.EscapeDataString(q);
            var artists = ArtistsAsync(term, ct);
            var albums = AlbumsAsync(term, ct);
            found = (await artists, await albums);
            if (found.Item1.Count > 0 || found.Item2.Count > 0) Remember(_extrasCache, key, found);
        }
        var ranked = SearchRanking.RankArtists(q, found.Item1);
        var top = ranked.Count > 0 && SearchRanking.NamesArtist(q, ranked[0].Name) ? ranked[0] : null;
        return new OnlineExtras(ranked, found.Item2, top);
    }

    // An artist's songs, most popular first: iTunes' own order for a search on the artist name, kept to that artist.
    public async Task<IReadOnlyList<SimilarTrack>> ArtistTracksAsync(OnlineArtist artist, CancellationToken ct = default) =>
        SearchRanking.Dedup(await SongsAsync($"term={Uri.EscapeDataString(artist.Name)}&entity=song&attribute=artistTerm&limit=60", artist.Id, ct));

    // An album's songs, in album order.
    public async Task<IReadOnlyList<SimilarTrack>> AlbumTracksAsync(OnlineAlbum album, CancellationToken ct = default)
    {
        var json = await SearchGetAsync($"https://itunes.apple.com/lookup?id={album.Id}&entity=song&country=FR", ct)
                   ?? await SearchGetAsync($"https://itunes.apple.com/lookup?id={album.Id}&entity=song", ct);
        return json is null ? Array.Empty<SimilarTrack>() : ParseSongs(json, null);
    }

    // A search request, within the search's share of the iTunes budget (see ItunesGate). Held back or refused by
    // iTunes: ItunesRateLimitedException, so the player can say so and retry, instead of showing "no result".
    private static async Task<string?> SearchGetAsync(string url, CancellationToken ct)
    {
        if (!ItunesGate.TryAcquire(search: true)) throw new ItunesRateLimitedException(ItunesGate.SearchWait);
        var json = await MusicRecommendationService.GetStringAsync(url, ct, gated: false);
        if (json is null && ItunesGate.SearchWait > TimeSpan.Zero) throw new ItunesRateLimitedException(ItunesGate.SearchWait);
        return json;
    }

    // French storefront first, the default one if it has nothing.
    private static async Task<string?> FetchAsync(string parameters, CancellationToken ct)
    {
        foreach (var country in new[] { "&country=FR", "" })
        {
            var json = await SearchGetAsync("https://itunes.apple.com/search?" + parameters + country, ct);
            if (json is null) continue;
            try
            {
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("resultCount", out var n) && n.GetInt32() > 0) return json;
            }
            catch { /* malformed: next storefront */ }
        }
        return null;
    }

    private static async Task<List<SimilarTrack>> SongsAsync(string parameters, long? artistId, CancellationToken ct)
    {
        var json = await FetchAsync(parameters, ct);
        return json is null ? new List<SimilarTrack>() : ParseSongs(json, artistId);
    }

    private static List<SimilarTrack> ParseSongs(string json, long? artistId)
    {
        var list = new List<SimilarTrack>();
        try
        {
            foreach (var r in Results(json))
            {
                if (Str(r, "wrapperType") != "track") continue; // a lookup's first row is the album itself
                var name = Str(r, "trackName");
                if (string.IsNullOrWhiteSpace(name)) continue;
                if (artistId is { } id && Long(r, "artistId") != id) continue;
                if (Long(r, "trackTimeMillis") > MaxDurationMillis) continue;
                list.Add(new SimilarTrack(name!, Str(r, "artistName") ?? string.Empty, Str(r, "trackViewUrl"),
                    Str(r, "previewUrl"), CleanAlbum(Str(r, "collectionName")), Str(r, "artworkUrl100")));
            }
        }
        catch { /* keep what was parsed */ }
        return list;
    }

    private static async Task<List<OnlineArtist>> ArtistsAsync(string term, CancellationToken ct)
    {
        var list = new List<OnlineArtist>();
        var json = await FetchAsync($"term={term}&entity=musicArtist&limit={Artists}", ct);
        if (json is null) return list;
        try
        {
            foreach (var r in Results(json))
            {
                if (Long(r, "artistId") is not > 0 || Str(r, "artistName") is not { } name) continue;
                list.Add(new OnlineArtist(Long(r, "artistId")!.Value, name, Str(r, "primaryGenreName")));
            }
        }
        catch { /* keep what was parsed */ }
        return list;
    }

    private static async Task<List<OnlineAlbum>> AlbumsAsync(string term, CancellationToken ct)
    {
        var list = new List<OnlineAlbum>();
        var json = await FetchAsync($"term={term}&entity=album&limit={Albums}", ct);
        if (json is null) return list;
        try
        {
            foreach (var r in Results(json))
            {
                if (Long(r, "collectionId") is not > 0 || CleanAlbum(Str(r, "collectionName")) is not { } title) continue;
                var year = Str(r, "releaseDate") is { Length: >= 4 } d ? d[..4] : null;
                list.Add(new OnlineAlbum(Long(r, "collectionId")!.Value, title, Str(r, "artistName") ?? string.Empty,
                    Str(r, "artworkUrl100"), year));
            }
        }
        catch { /* keep what was parsed */ }
        return list;
    }

    private static List<JsonElement> Results(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.TryGetProperty("results", out var results) && results.ValueKind == JsonValueKind.Array
            ? results.EnumerateArray().Select(e => e.Clone()).ToList()
            : new List<JsonElement>();
    }

    // iTunes names a single "X - Single" and an EP "X - EP": the suffix would end up in the folder name.
    private static string? CleanAlbum(string? album) =>
        album is null ? null : Regex.Replace(album, @"\s+-\s+(Single|EP)$", string.Empty).Trim();

    private static string? Str(JsonElement el, string prop) =>
        el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static long? Long(JsonElement el, string prop) =>
        el.TryGetProperty(prop, out var v) && v.TryGetInt64(out var n) ? n : null;
}

// Re-sorting of the iTunes song hits, cheap and local (a few dozen strings). Score, highest first:
//   * how much of the query the title + artist cover (main signal), with bonuses when the whole title was typed
//     (more when the title is just that, without feat./version suffix) and when the artist was named;
//   * artists already in the library;
//   * iTunes' own rank as a tie-breaker;
//   * minus variants nobody means by default (karaoke, tribute, remix, live…), unless the query asks for them.
// Kept identical to the phone's SearchRanking (OnlineSearch.kt).
public static class SearchRanking
{
    private static readonly string[] Heavy = { "karaoke", "tribute", "lullaby", "berceuse", "nightcore", "instrumental", "sped", "slowed", "8bit", "made famous" };
    private static readonly string[] Medium = { "remix", "remixed", "live", "extended", "acoustic", "acoustique", "cover", "reprise", "rework", "bootleg", "mix" };
    private static readonly string[] Light = { "edit", "version", "remaster", "remastered", "radio" };

    public static string Norm(string s)
    {
        var decomposed = s.ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark) sb.Append(c);
        var t = sb.ToString().Replace("8-bit", "8bit").Replace("8 bit", "8bit");
        return Regex.Replace(t, "[^a-z0-9]+", " ").Trim();
    }

    private static HashSet<string> Words(string s) => Norm(s).Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet();

    // The title without what comes after "(", "[" or " - " (feat., remix, version…).
    private static string Core(string title)
    {
        var t = title;
        foreach (var cut in new[] { " (", " [", " - " })
        {
            var i = t.IndexOf(cut, StringComparison.Ordinal);
            if (i > 0) t = t[..i];
        }
        return t;
    }

    public static double Score(string query, SimilarTrack t, int index, int count, ISet<string> libraryArtists)
    {
        var q = Words(query);
        if (q.Count == 0) return 0;
        var core = Core(t.Title);
        var coreWords = Words(core);
        var artistWords = Words(t.Artist);
        var allWords = Words(t.Title);
        allWords.UnionWith(artistWords);

        var s = 50.0 * q.Count(allWords.Contains) / q.Count;
        if (coreWords.Count > 0 && coreWords.IsSubsetOf(q))
        {
            s += 20;
            // The plain title, with nothing after it (no feat., version, remix…): the one people mean.
            if (Norm(t.Title) == Norm(core)) s += 6;
        }
        if (artistWords.Count > 0 && artistWords.IsSubsetOf(q)) s += 10;
        if (libraryArtists.Contains(Norm(t.Artist))) s += 8;
        if (count > 1) s += 10.0 * (1 - (double)index / (count - 1));

        var variantWords = Words(t.Title[core.Length..]);
        variantWords.UnionWith(Words(t.Album ?? string.Empty));
        variantWords.UnionWith(artistWords);
        var variantPhrase = " " + Norm(t.Title + " " + (t.Album ?? string.Empty) + " " + t.Artist) + " ";
        bool Has(string w) => w.Contains(' ') ? variantPhrase.Contains(" " + w + " ") : variantWords.Contains(w);
        foreach (var w in Heavy) if (Has(w) && !q.Contains(w)) s -= 40;
        foreach (var w in Medium) if (Has(w) && !q.Contains(w)) s -= 15;
        foreach (var w in Light) if (Has(w) && !q.Contains(w)) s -= 3;
        return s;
    }

    public static IReadOnlyList<SimilarTrack> RankTracks(string query, IReadOnlyList<SimilarTrack> tracks, ISet<string> libraryArtists) =>
        Dedup(tracks.Select((t, i) => (t, score: Score(query, t, i, tracks.Count, libraryArtists)))
            .OrderByDescending(x => x.score).Select(x => x.t).ToList());

    // The same song on a single and on its album shows once (the better-ranked copy stays).
    public static IReadOnlyList<SimilarTrack> Dedup(IReadOnlyList<SimilarTrack> tracks)
    {
        var seen = new HashSet<string>();
        return tracks.Where(t => seen.Add(Norm(t.Title) + "|" + Norm(t.Artist))).ToList();
    }

    // Artists whose whole name was typed come first, then iTunes' order.
    public static IReadOnlyList<OnlineArtist> RankArtists(string query, IReadOnlyList<OnlineArtist> artists)
    {
        var q = Words(query);
        return artists.Select((a, i) => (a, i)).OrderByDescending(x => Words(x.a.Name).IsSubsetOf(q)).ThenBy(x => x.i)
            .Select(x => x.a).ToList();
    }

    // The query IS the artist's name (accents/case aside): the best match is the artist, not one of its songs.
    public static bool NamesArtist(string query, string name) => Norm(query) == Norm(name);
}
