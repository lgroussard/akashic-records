using System.Globalization;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Text;
using System.Text.Json;

namespace AkashicRecords.Infrastructure.Web;

// Kind of work being looked up, so the search can be steered to the right article
// (e.g. the *film* "Akira", not the person, and not the concept).
public enum ImageSearchKind
{
    Generic,
    Film,
    AnimatedFilm,
    Anime,
    Book,
    VideoGame
}

// Best-effort "find a cover image for this title" lookup.
//
// For films, animated films and anime, TMDB (a free API key, no regional restriction) is the
// reliable source of real posters. For everything else (books, generic), a no-key Wikipedia
// fallback is used: search each Wikipedia edition with a media hint ("<title> film" etc.) so
// results land on the work's article rather than a same-named person/concept, then take the
// first result whose title actually matches the query and that has a lead image (the
// poster/cover). If nothing confidently matches, returns null so the caller just shows the
// title - far better than a confidently-wrong statue/portrait.
public sealed class ImageSearchService
{
    private static readonly HttpClient Http = CreateClient();

    // Logs why a cover lookup failed, so a swallowed 429/rate-limit doesn't look like
    // "the app ignores it". Written to its own log next to the app's crash.log.
    private static readonly object LogLock = new();

    private static void LogFailure(string title, string reason)
    {
        try
        {
            var folder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "AkashicRecords");
            Directory.CreateDirectory(folder);
            lock (LogLock)
            {
                File.AppendAllText(Path.Combine(folder, "cover-search.log"),
                    $"{DateTime.Now:O} [{title}] {reason}\n");
            }
        }
        catch
        {
            // Logging must never throw.
        }
    }

    // Wikipedias to consult, in order: the user's locale first, then the largest edition.
    private static readonly string[] Languages = { "fr", "en" };

    // Article titles that can match a query but are never the work itself.
    private static readonly string[] JunkMarkers =
    {
        "list of", "liste des", "music of", "musique de", "discography", "discographie",
        "soundtrack", "bande originale", "(disambiguation)", "(homonymie)", "filmographie"
    };

    // Optional TMDB API key (v3, free) for movie/anime poster lookup. TMDB has no regional
    // restriction like the Custom Search JSON API, so it's the reliable source for film covers.
    private readonly string? _tmdbApiKey;

    // Optional RAWG API key (free, from rawg.io) for video game cover lookup. Without it,
    // video games fall back to the no-key Wikipedia search like everything else.
    private readonly string? _rawgApiKey;

    // Pass a TMDB API key for movie/anime poster lookup and an optional RAWG key for video
    // game covers. Either may be null/empty, in which case the no-key Wikipedia fallback is
    // used for that media type.
    public ImageSearchService(string? tmdbApiKey = null, string? rawgApiKey = null)
    {
        _tmdbApiKey = tmdbApiKey;
        _rawgApiKey = rawgApiKey;
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = true })
        {
            Timeout = TimeSpan.FromSeconds(15)
        };
        // Wikimedia asks API clients to send a descriptive User-Agent.
        client.DefaultRequestHeaders.Add("User-Agent", "AkashicRecords/1.0 (personal desktop app)");
        return client;
    }

    // Returns the raw bytes + file extension of a cover image for the title,
    // or null when offline or nothing confidently matched.
    //    // A cover lookup is a chain of network calls (search -> summary -> image bytes).
    // Any single hop can fail transiently (429 rate-limit, dropped connection), so we retry
    // the whole lookup a few times before giving up, and log the reason it failed so a
    // swallowed error doesn't look like "the app ignores it".
    public async Task<(byte[] Data, string Extension)?> TryFindImageAsync(
        string title, ImageSearchKind kind = ImageSearchKind.Generic, CancellationToken ct = default)
    {
        if (!NetworkInterface.GetIsNetworkAvailable() || string.IsNullOrWhiteSpace(title)) return null;

        const int maxAttempts = 3;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                var result = await TryFetchOneAsync(title, kind, ct);
                if (result is not null) return result;
            }
            catch (OperationCanceledException)
            {
                return null;
            }
            catch (HttpRequestException ex)
            {
                // Transient (429, connection reset, timeout): retry. Log only the last try.
                if (attempt == maxAttempts)
                    LogFailure(title, $"network error: {ex.GetType().Name}: {ex.Message}");
            }
            catch (Exception ex)
            {
                LogFailure(title, $"{ex.GetType().Name}: {ex.Message}");
                return null;
            }

            if (attempt < maxAttempts)
            {
                // Exponential backoff so we don't hammer a rate-limited server.
                await Task.Delay(TimeSpan.FromMilliseconds(500 * attempt), ct);
            }
        }
        return null;
    }

    // Performs a single attempt: resolve the image URL, then download its bytes.
    // Returns null (rather than throwing) when the lookup simply found nothing, so the
    // caller can tell "no result" apart from a transient network error.
    private async Task<(byte[] Data, string Extension)?> TryFetchOneAsync(string title, ImageSearchKind kind, CancellationToken ct)
    {
        var imageUrl = await FindImageUrlAsync(title, kind, ct);
        if (imageUrl is null)
        {
            LogFailure(title, "no matching article/cover found");
            return null;
        }

        using var response = await Http.GetAsync(imageUrl, ct);
        if (!response.IsSuccessStatusCode)
        {
            LogFailure(title, $"download failed: HTTP {(int)response.StatusCode} {response.StatusCode}");
            return null;
        }

        var contentType = response.Content.Headers.ContentType?.MediaType;
        if (contentType is null || !contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
        {
            LogFailure(title, $"unexpected content type: {contentType}");
            return null;
        }

        var data = await response.Content.ReadAsByteArrayAsync(ct);
        if (data.Length == 0)
        {
            LogFailure(title, "downloaded 0 bytes");
            return null;
        }

        var extension = contentType.ToLowerInvariant() switch
        {
            "image/png" => ".png",
            "image/gif" => ".gif",
            "image/bmp" => ".bmp",
            _ => ".jpg"
        };
        return (data, extension);
    }

    // Verifies that a media key actually yields a downloadable cover, mirroring production: the
    // API-specific resolver (no silent Wikipedia fallback) finds the source, then the bytes are
    // really fetched. Unlike a reachability ping this only returns "ok" when real image bytes came
    // back, so it never reports a working key that still finds nothing inside the app.
    // Returns "ok" | "no-result" | "error: ...".
    public async Task<string> TestImageLookupAsync(string title, ImageSearchKind kind)
    {
        if (!NetworkInterface.GetIsNetworkAvailable()) return "error: réseau indisponible";

        if (kind is ImageSearchKind.Film or ImageSearchKind.AnimatedFilm or ImageSearchKind.Anime
            && string.IsNullOrWhiteSpace(_tmdbApiKey))
            return "error: aucune clé TMDB configurée";

        if (kind is ImageSearchKind.VideoGame && string.IsNullOrWhiteSpace(_rawgApiKey))
            return "error: aucune clé RAWG configurée";

        try
        {
            var ct = default(CancellationToken);
            // Call the exact per-API resolver the app uses, so a valid key that finds nothing cannot
            // be masked by the generic no-key Wikipedia fallback.
            var imageUrl = kind switch
            {
                ImageSearchKind.Film or ImageSearchKind.AnimatedFilm or ImageSearchKind.Anime
                    => await TryFindTmdbImageUrlAsync(title, kind, ct),
                ImageSearchKind.VideoGame => await RawgImageUrlAsync(title, ct),
                _ => await FindImageUrlAsync(title, kind, ct)
            };
            if (imageUrl is null) return "no-result";

            using var response = await Http.GetAsync(imageUrl, ct);
            if (!response.IsSuccessStatusCode)
                return $"error: téléchargement HTTP {(int)response.StatusCode}";

            var contentType = response.Content.Headers.ContentType?.MediaType;
            if (contentType is null || !contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
                return "no-result";

            var bytes = await response.Content.ReadAsByteArrayAsync(ct);
            return bytes.Length > 1000 ? "ok" : "no-result";
        }
        catch (Exception ex)
        {
            return $"error: {ex.GetType().Name}: {ex.Message}";
        }
    }

    private async Task<string?> FindImageUrlAsync(string title, ImageSearchKind kind, CancellationToken ct)
    {
        // Manga: when the title mentions "manga"/"manhwa"/"manhua", use MangaDex, a dedicated
        // manga database. This is important because the generic book fallback can match the
        // wrong thing (e.g. "Rainbow" -> "Rainbow Six"), while MangaDex returns the actual
        // manga and its real published cover. This works regardless of the selected category.
        if (ContainsMangaHint(title))
        {
            var viaMangaDex = await MangaDexImageUrlAsync(title, ct);
            if (viaMangaDex is not null) return viaMangaDex;
        }

        // TMDB is the reliable source for film/anime posters and has no regional restriction
        // (unlike the Custom Search JSON API). It needs a free API key.
        if (kind is ImageSearchKind.Film or ImageSearchKind.AnimatedFilm or ImageSearchKind.Anime)
        {
            var viaTmdb = await TryFindTmdbImageUrlAsync(title, kind, ct);
            if (viaTmdb is not null) return viaTmdb;
        }

        // Books: OpenLibrary is the reliable source of real covers (no key, no regional
        // restriction). It returns a cover id per edition; we download the medium cover and
        // skip the 43-byte "no cover" placeholder so we never show a blank book.
        if (kind is ImageSearchKind.Book)
        {
            var viaOpenLibrary = await OpenLibraryImageUrlAsync(title, ct);
            if (viaOpenLibrary is not null) return viaOpenLibrary;
        }

        // Video games: RAWG is the reliable source of real box art (free key, no regional
        // restriction). We search by title, take the first result whose title matches, then
        // download its background_image (the box/cover art). Without a key, this is skipped
        // and the no-key Wikipedia fallback below is used instead.
        if (kind is ImageSearchKind.VideoGame)
        {
            var viaRawg = await RawgImageUrlAsync(title, ct);
            if (viaRawg is not null) return viaRawg;
        }

        // Last resort: the no-key Wikipedia fallback.
        return await WikipediaImageUrlAsync(title, kind, ct);
    }

    // TMDB (The Movie Database) poster lookup. Free API key, no regional restriction like the
    // Custom Search JSON API, so it reliably returns real movie/anime posters.
    private async Task<string?> TryFindTmdbImageUrlAsync(string title, ImageSearchKind kind, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_tmdbApiKey)) return null;

        // Anime are TV series, not films: the movie search returns the movie (or nothing),
        // while the TV search returns the actual series. Films and animated films use movie.
        var searchType = kind is ImageSearchKind.Anime ? "tv" : "movie";

        // The search endpoint returns poster_path directly on each result, which is more
        // reliable than the details endpoint: some entries (notably anime films) have a
        // poster in search but an empty "images.posters" array in details.
        var hit = await FindTmdbMovieAsync(title, searchType, ct);
        if (hit is null) return null;

        if (hit.PosterPath is not null)
            return "https://image.tmdb.org/t/p/w500" + hit.PosterPath;

        // No poster on the English search: try the movie's original-language title, which
        // often surfaces the real poster (e.g. Japanese anime titles).
        if (!string.IsNullOrWhiteSpace(hit.OriginalTitle))
        {
            var hit2 = await FindTmdbMovieAsync(hit.OriginalTitle, searchType, ct);
            if (hit2?.PosterPath is not null)
                return "https://image.tmdb.org/t/p/w500" + hit2.PosterPath;
        }
        return null;
    }

    // OpenLibrary book-cover lookup. Free, no key, no regional restriction. We search by
    // title and take the first edition that carries a cover id (cover_i), then download the
    // medium cover. The 43-byte GIF OpenLibrary serves when a book has no cover is skipped.
    private async Task<string?> OpenLibraryImageUrlAsync(string title, CancellationToken ct)
    {
        var url = "https://openlibrary.org/search.json?" +
                  "title=" + Uri.EscapeDataString(title) +
                  "&limit=8" +
                  "&fields=isbn_t,cover_i,cover_edition_key,title,author_name";
        using var response = await Http.GetAsync(url, ct);
        if (!response.IsSuccessStatusCode) return null;

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

        // OpenLibrary's search response carries "docs" at the top level (not under "query").
        var docs = doc.RootElement;
        if (!docs.TryGetProperty("docs", out var results) || results.GetArrayLength() == 0)
            return null;

        foreach (var docElement in results.EnumerateArray())
        {
            if (!docElement.TryGetProperty("cover_i", out var coverId) || coverId.ValueKind != JsonValueKind.Number)
                continue;

            var coverIdValue = coverId.GetInt32();
            if (coverIdValue <= 0) continue;

            var coverUrl = $"https://covers.openlibrary.org/b/id/{coverIdValue}-M.jpg";
            if (await IsRealCoverAsync(coverUrl, ct))
                return coverUrl;
        }
        return null;
    }

    // Downloads the cover and returns true only if it's a real image, not OpenLibrary's
    // 43-byte "no cover" placeholder GIF. We also require a real image content type.
    private async Task<bool> IsRealCoverAsync(string coverUrl, CancellationToken ct)
    {
        using var response = await Http.GetAsync(coverUrl, ct);
        if (!response.IsSuccessStatusCode) return false;

        var contentType = response.Content.Headers.ContentType?.MediaType;
        if (contentType is null || !contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
            return false;

        var bytes = await response.Content.ReadAsByteArrayAsync(ct);
        // OpenLibrary's placeholder is exactly 43 bytes; real covers are several KB.
        return bytes.Length > 1000;
    }

    // RAWG video-game cover lookup. Free API key (from rawg.io), no regional restriction.
    // We search by title, take the first result whose title matches the query, then download
    // its background_image (the box/cover art). Without a configured key this is skipped so
    // the caller falls back to the no-key Wikipedia search.
    private async Task<string?> RawgImageUrlAsync(string title, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_rawgApiKey)) return null;

        var url = "https://api.rawg.io/api/games" +
                  "?key=" + Uri.EscapeDataString(_rawgApiKey) +
                  "&search=" + Uri.EscapeDataString(title) +
                  "&page_size=10&fields=name,background_image,slug";
        using var response = await Http.GetAsync(url, ct);
        if (!response.IsSuccessStatusCode) return null;

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

        var root = doc.RootElement;
        if (!root.TryGetProperty("results", out var results) || results.GetArrayLength() == 0)
            return null;

        foreach (var result in results.EnumerateArray())
        {
            if (!result.TryGetProperty("slug", out var slug) || slug.ValueKind != JsonValueKind.String)
                continue;

            var candidate = slug.GetString();
            if (string.IsNullOrWhiteSpace(candidate)) continue;

            // RAWG's "slug" is a lowercased, hyphenated form of the title ("final-fantasy-vii"),
            // so match on that rather than the display name.
            if (!RawgSlugMatches(title, candidate)) continue;

            if (!result.TryGetProperty("background_image", out var bg) ||
                bg.ValueKind != JsonValueKind.String)
            {
                // No box art on this hit: try the next result instead of giving up.
                continue;
            }

            var bgUrl = bg.GetString();
            if (!string.IsNullOrWhiteSpace(bgUrl) && await IsRealCoverAsync(bgUrl, ct))
                return bgUrl;
        }
        return null;
    }

    // Matches a title against a RAWG slug: case-insensitive, ignoring accents, and treating
    // hyphens/spaces as word boundaries so "final fantasy vii" matches "Final Fantasy VII".
    private static bool RawgSlugMatches(string query, string slug)
    {
        var q = Normalize(query);
        var s = Normalize(slug).Replace("-", " ").Replace("_", " ");
        if (q.Length == 0 || s.Length == 0) return false;

        // Whole-title match, or the slug's leading words cover the query (so "Portal" matches
        // "Portal: Half-Life Zero Point" without a false positive on a longer title).
        return s == q || s.StartsWith(q + " ", StringComparison.Ordinal) ||
               q.StartsWith(s + " ", StringComparison.Ordinal);
    }

    // A single MangaDex entry: its id plus the best available title (preferring English,
    // then any other language) and the cover_art relationship id used to fetch the cover.
    private sealed class MangaDexEntry
    {
        public required string Id { get; init; }
        public required string? Title { get; init; }
        public required string? CoverId { get; init; }
    }

    // Detects a manga-family hint in the title so we route to MangaDex. Covers manga/manhwa/
    // manhua (and their language-specific spellings) plus the French "manga".
    private static bool ContainsMangaHint(string title)
    {
        if (string.IsNullOrWhiteSpace(title)) return false;
        var lower = title.ToLowerInvariant();
        return MangaHints.Any(h => lower.Contains(h, StringComparison.Ordinal));
    }

    private static readonly string[] MangaHints =
    {
        "manga", "manhwa", "manhua", "만화", "漫画", "웹툰", "webtoon"
    };

    // Removes the manga-family hint word(s) from a title, wherever they appear. A real
    // manga title never contains the word "manga" itself - it's just a user hint - so
    // stripping it is safe and makes the search land on the actual entry.
    private static string StripMangaHint(string title)
    {
        var result = title;
        foreach (var hint in MangaHints)
        {
            result = string.Join(" ",
                result.Split(new[] { hint }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        }
        return result.Trim();
    }

    // MangaDex cover lookup. Free, no key. We search by title, pick the entry whose title
    // matches the query, then fetch its cover_art attachment and download the image.
    private async Task<string?> MangaDexImageUrlAsync(string title, CancellationToken ct)
    {
        // Drop the manga-family hint word(s) from the query: "Rainbow manga" returns zero
        // results on MangaDex, while "Rainbow" returns the real entry.
        var query = StripMangaHint(title);
        var url = "https://api.mangadex.org/manga?title=" + Uri.EscapeDataString(query) + "&limit=50";
        using var response = await Http.GetAsync(url, ct);
        if (!response.IsSuccessStatusCode) return null;

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

        var root = doc.RootElement;
        if (!root.TryGetProperty("data", out var data) || data.GetArrayLength() == 0)
            return null;

        var entries = new List<MangaDexEntry>();
        foreach (var element in data.EnumerateArray())
        {
            if (!element.TryGetProperty("id", out var idEl) || idEl.ValueKind != JsonValueKind.String)
                continue;

            var entry = ParseMangaDexEntry(element);
            if (entry is not null) entries.Add(entry);
        }

        var hit = entries.FirstOrDefault(e => !string.IsNullOrWhiteSpace(e.Title) &&
            MangaDexTitleMatches(title, e.Title));
        if (hit is null) hit = entries.FirstOrDefault();
        if (hit is null || hit.CoverId is null) return null;

        // The cover attachment id is not the image filename: we must ask the cover endpoint
        // for the actual file name, then build the uploads URL as
        // /covers/{mangaId}/{filename}.jpg.
        var filename = await MangaDexCoverFilenameAsync(hit.CoverId, ct);
        if (string.IsNullOrWhiteSpace(filename)) return null;

        var coverUrl = $"https://uploads.mangadex.org/covers/{hit.Id}/{filename}";
        if (await IsRealCoverAsync(coverUrl, ct))
            return coverUrl;
        return null;
    }

    // Resolves the cover attachment id to the actual image filename MangaDex serves.
    private async Task<string?> MangaDexCoverFilenameAsync(string coverId, CancellationToken ct)
    {
        var url = "https://api.mangadex.org/cover/" + coverId;
        using var response = await Http.GetAsync(url, ct);
        if (!response.IsSuccessStatusCode) return null;

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

        var root = doc.RootElement;
        if (!root.TryGetProperty("data", out var data) ||
            !data.TryGetProperty("attributes", out var attrs) ||
            !attrs.TryGetProperty("filename", out var filename) ||
            filename.ValueKind != JsonValueKind.String)
            return null;

        var name = filename.GetString();
        return name ?? "";
    }

    // Extracts the id, best title, and cover_art id from a MangaDex search element.
    private static MangaDexEntry? ParseMangaDexEntry(JsonElement element)
    {
        var id = element.GetProperty("id").GetString();

        // The title is a map keyed by language ("en", "ja-ro", ...). Prefer English, then any.
        string? title = null;
        if (element.TryGetProperty("attributes", out var attributes) &&
            attributes.TryGetProperty("title", out var titleMap) &&
            titleMap.ValueKind == JsonValueKind.Object)
        {
            if (titleMap.TryGetProperty("en", out var en) && en.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(en.GetString()))
            {
                title = en.GetString();
            }
            else
            {
                foreach (var prop in titleMap.EnumerateObject())
                {
                    if (prop.Value.ValueKind == JsonValueKind.String &&
                        !string.IsNullOrWhiteSpace(prop.Value.GetString()))
                    {
                        title = prop.Value.GetString();
                        break;
                    }
                }
            }
        }

        // The cover is referenced by a "cover_art" relationship; its id is the attachment id.
        string? coverId = null;
        if (element.TryGetProperty("relationships", out var rels) && rels.ValueKind == JsonValueKind.Array)
        {
            foreach (var rel in rels.EnumerateArray())
            {
                if (rel.TryGetProperty("type", out var relType) &&
                    relType.GetString() == "cover_art" &&
                    rel.TryGetProperty("id", out var relId) &&
                    relId.ValueKind == JsonValueKind.String)
                {
                    coverId = relId.GetString();
                    break;
                }
            }
        }

        return new MangaDexEntry { Id = id ?? "", Title = title, CoverId = coverId };
    }

    // Matches a MangaDex title against the query: case-insensitive, ignoring accents, and
    // accepting a prefix match so "Tokyo Ghoul" matches "Tokyo Ghoul:re".
    private static bool MangaDexTitleMatches(string query, string candidate)
    {
        var q = Normalize(query);
        var c = Normalize(candidate);
        if (q.Length == 0 || c.Length == 0) return false;
        return c == q || c.StartsWith(q, StringComparison.Ordinal) || q.StartsWith(c, StringComparison.Ordinal);
    }

    // A single TMDB search hit: the movie id plus the poster path (may be null) and the
    // original-language title (may be null), both taken straight from the search result.
    private sealed class TmdbHit
    {
        public required int Id { get; init; }
        public required string? PosterPath { get; init; }
        public required string? OriginalTitle { get; init; }
    }

    // Runs a TMDB search (movie or tv) and returns the first result whose title matches the
    // query (ignoring year/disambiguation), carrying its poster_path and original title.
    private async Task<TmdbHit?> FindTmdbMovieAsync(string query, string searchType, CancellationToken ct)
    {
        var url = $"https://api.themoviedb.org/3/search/{searchType}" +
                  $"?api_key={Uri.EscapeDataString(_tmdbApiKey!)}" +
                  $"&query={Uri.EscapeDataString(query)}" +
                  "&page=1";
        using var response = await Http.GetAsync(url, ct);
        if (!response.IsSuccessStatusCode) return null;

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

        var root = doc.RootElement;
        if (!root.TryGetProperty("results", out var results) || results.GetArrayLength() == 0)
            return null;

        // TV results use "name"; movie results use "title".
        var titleProp = searchType == "tv" ? "name" : "title";

        foreach (var result in results.EnumerateArray())
        {
            if (!result.TryGetProperty("id", out var id) || !result.TryGetProperty(titleProp, out var t))
                continue;

            var candidate = t.GetString();
            if (string.IsNullOrWhiteSpace(candidate)) continue;

            // Match on the normalized title (strip year, disambiguation, and trailing "Part X").
            if (!TmdbTitleMatches(query, candidate)) continue;

            string? poster = null;
            if (result.TryGetProperty("poster_path", out var pp))
            {
                var ppv = pp.GetString();
                if (!string.IsNullOrWhiteSpace(ppv)) poster = ppv;
            }

            // TV results use "original_name"; movie results use "original_title".
            string? original = null;
            var titleKey = searchType == "tv" ? "original_name" : "original_title";
            if (result.TryGetProperty(titleKey, out var ot) && !string.IsNullOrWhiteSpace(ot.GetString()))
                original = ot.GetString();

            return new TmdbHit { Id = id.GetInt32(), PosterPath = poster, OriginalTitle = original };
        }
        return null;
    }

    // Normalizes a title for fuzzy matching: strips trailing "(year)", disambiguation, and
    // "Part X" / "Deuxième partie" style qualifiers, and ignores case/accents.
    private static bool TmdbTitleMatches(string query, string candidate)
    {
        var q = Normalize(query);
        var c = Normalize(candidate);

        // Strip trailing disambiguation: "Your Name (2016)", "Kaguya (film)".
        var paren = c.IndexOf('(');
        if (paren >= 0) c = c[..paren];

        // Strip trailing "Part 2", "Deuxième partie", "Chapter X", etc.
        var spaceIdx = c.LastIndexOf(' ');
        if (spaceIdx > 0 && spaceIdx < c.Length - 1)
        {
            var tail = c[(spaceIdx + 1)..];
            if (tail.Length <= 3 && int.TryParse(tail, out _))
                c = c[..spaceIdx];
        }

        if (c.Length == 0) return false;
        return c == q || c.StartsWith(q, StringComparison.Ordinal) || q.StartsWith(c, StringComparison.Ordinal);
    }

    // Best-effort original-language title for a work, so TMDB matches the native name.
    // Falls back to the raw title when unknown.
    private static async Task<string?> WikipediaImageUrlAsync(string title, ImageSearchKind kind, CancellationToken ct)
    {
        foreach (var lang in Languages)
        {
            var hint = WikipediaHintFor(kind, lang);
            var query = hint is null ? title : $"{title} {hint}";

            foreach (var candidate in await SearchTitlesAsync(lang, query, ct))
            {
                if (IsJunk(candidate) || !TitleMatches(title, candidate)) continue;

                var image = await SummaryImageAsync(lang, candidate, ct);
                if (image is not null) return image;
            }
        }
        return null;
    }

    // Localized media hint so the Wikipedia search resolves to the work's article.
    private static string? WikipediaHintFor(ImageSearchKind kind, string lang) => (kind, lang) switch
    {
        (ImageSearchKind.Film, _) => "film",
        (ImageSearchKind.AnimatedFilm, "fr") => "film d'animation",
        (ImageSearchKind.AnimatedFilm, _) => "animated film",
        (ImageSearchKind.Anime, _) => "anime",
        (ImageSearchKind.Book, "fr") => "roman",
        (ImageSearchKind.Book, _) => "novel",
        _ => null
    };

    private static async Task<IReadOnlyList<string>> SearchTitlesAsync(string lang, string query, CancellationToken ct)
    {
        var url = $"https://{lang}.wikipedia.org/w/api.php?action=query&format=json&list=search&srlimit=5&srnamespace=0&srsearch={Uri.EscapeDataString(query)}";
        using var response = await Http.GetAsync(url, ct);
        if (!response.IsSuccessStatusCode) return Array.Empty<string>();

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

        var hits = doc.RootElement.GetProperty("query").GetProperty("search");
        var titles = new List<string>(hits.GetArrayLength());
        foreach (var hit in hits.EnumerateArray())
        {
            if (hit.TryGetProperty("title", out var t) && t.GetString() is { } s) titles.Add(s);
        }
        return titles;
    }

    private static async Task<string?> SummaryImageAsync(string lang, string title, CancellationToken ct)
    {
        var url = $"https://{lang}.wikipedia.org/api/rest_v1/page/summary/{Uri.EscapeDataString(title.Replace(' ', '_'))}?redirect=true";
        using var response = await Http.GetAsync(url, ct);
        if (!response.IsSuccessStatusCode) return null;

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

        if (doc.RootElement.TryGetProperty("originalimage", out var original) &&
            original.TryGetProperty("source", out var src))
        {
            return src.GetString();
        }
        if (doc.RootElement.TryGetProperty("thumbnail", out var thumb) &&
            thumb.TryGetProperty("source", out var thumbSrc))
        {
            return thumbSrc.GetString();
        }
        return null;
    }

    private static bool IsJunk(string articleTitle)
    {
        var lower = articleTitle.ToLowerInvariant();
        return JunkMarkers.Any(marker => lower.Contains(marker, StringComparison.Ordinal));
    }

    // Accepts an article only when its name (minus any "(...)" qualifier) matches the query,
    // so "Akira (film)" is kept for "Akira" but "Françoise Bertin" / "Atalante" are rejected.
    // Leading definite articles on the *article* are ignored, so the query "Garden of Words"
    // still matches the article "The Garden of Words" (and "Le Fabuleux Destin" matches
    // "La Fabuleux Destin"). Anime titles rarely carry an article, which is why the fallback
    // worked for anime but silently failed for films/animation.
    private static bool TitleMatches(string query, string articleTitle)
    {
        var q = Normalize(query);
        if (q.Length == 0) return false;

        // The article name minus its disambiguation qualifier, e.g. "The Garden of Words (film)".
        var a = Normalize(StripQualifier(articleTitle));
        if (a.Length == 0) return false;

        // Try the article as-is, then with any leading definite article removed.
        var candidates = new[] { a, StripLeadingArticle(a) };
        return candidates.Any(c => c.Length > 0 && (c == q || c.StartsWith(q, StringComparison.Ordinal) || q.StartsWith(c, StringComparison.Ordinal)));
    }

    // Removes a leading leading-article word (the/le/la/die/ein/...) so "The Garden of Words"
    // becomes "Garden of Words". Only strips when the remainder is non-empty.
    private static string StripLeadingArticle(string title)
    {
        // Space-delimited articles, ordered longest-first so "de la" / "de les" beat "de".
        var articles = new[]
        {
            "de la", "de les", "de los", "del", "della", "delle", "dello",
            "the", "les", "der", "die", "das", "ein", "eine",
            "le", "la", "un", "une", "des", "du", "aux",
            "el", "los", "las", "una", "unos", "unas",
            "il", "gli", "lo", "i", "le", "uno", "a", "an"
        };
        foreach (var art in articles)
        {
            if (title.Length > art.Length &&
                string.Equals(title[..art.Length], art, StringComparison.OrdinalIgnoreCase) &&
                char.IsWhiteSpace(title[art.Length]))
            {
                return title[art.Length..].TrimStart();
            }
        }

        // Elided forms: "l'Étranger", "d'Artagnan", "qu'il".
        foreach (var art in new[] { "l'", "d'", "j'", "m'", "t'", "c'", "qu'", "s'", "n'", "b'", "f'", "p'", "v'", "z'", "x'" })
        {
            if (title.Length > art.Length &&
                string.Equals(title[..art.Length], art, StringComparison.OrdinalIgnoreCase))
            {
                return title[art.Length..].TrimStart();
            }
        }
        return title;
    }

    private static string StripQualifier(string title)
    {
        var paren = title.IndexOf(" (", StringComparison.Ordinal);
        return paren >= 0 ? title[..paren] : title;
    }

    private static string Normalize(string value)
    {
        var decomposed = value.Trim().ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);
        foreach (var ch in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark) sb.Append(ch);
        }
        return sb.ToString().Normalize(NormalizationForm.FormC);
    }
}

