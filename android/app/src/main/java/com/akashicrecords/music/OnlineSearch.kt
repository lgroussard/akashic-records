package com.akashicrecords.music

import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.async
import kotlinx.coroutines.coroutineScope
import kotlinx.coroutines.withContext
import org.json.JSONArray
import org.json.JSONObject
import java.text.Normalizer

data class OnlineArtist(val id: Long, val name: String, val genre: String?)
data class OnlineAlbum(val id: Long, val title: String, val artist: String, val artwork: String?, val year: String?)

// Artists and albums of a query; topArtist is set when the query names an artist outright (then the best match
// is that artist, not one of its songs).
data class OnlineExtras(val artists: List<OnlineArtist>, val albums: List<OnlineAlbum>, val topArtist: OnlineArtist?)

// "Rechercher" tab engine, against the free iTunes catalogue (no key, no account), searched as you type.
// iTunes allows ~20 requests a minute, so the search is split: songs (1 request) follow the typing, artists +
// albums (2 requests, sent together) come once the typing pauses. Every answer is cached for the app's life, so
// erasing letters or coming back to the tab costs nothing. iTunes returns its own relevance order; SearchRanking
// re-sorts the songs on top of it. Same rules on the PC (OnlineMusicSearch.cs). Best-effort: empty on failure.
object OnlineSearch {
    private const val SONGS = 40
    private const val ARTISTS = 6
    private const val ALBUMS = 10
    private const val MAX_DURATION_MS = 15 * 60 * 1000 // longer = compilation/set, not a song

    private val songCache = Lru<List<SimilarTrack>>()
    private val extrasCache = Lru<Pair<List<OnlineArtist>, List<OnlineAlbum>>>()

    private class Lru<T> {
        private val map = object : LinkedHashMap<String, T>(16, 0.75f, true) {
            override fun removeEldestEntry(eldest: MutableMap.MutableEntry<String, T>?) = size > 40
        }
        @Synchronized fun get(k: String): T? = map[k]
        @Synchronized fun put(k: String, v: T) { map[k] = v }
    }

    private fun key(q: String) = SearchRanking.norm(q)

    // Songs for the query, re-ranked. Only successful, non-empty answers are cached (a failure may be the rate limit).
    suspend fun tracks(query: String, libraryArtists: Set<String>): List<SimilarTrack> = withContext(Dispatchers.IO) {
        val q = query.trim()
        if (q.isEmpty()) return@withContext emptyList()
        val raw = songCache.get(key(q)) ?: songs("term=${Recommendations.enc(q)}&entity=song&limit=$SONGS")
            .also { if (it.isNotEmpty()) songCache.put(key(q), it) }
        SearchRanking.rankTracks(q, raw, libraryArtists)
    }

    // Artists and albums for the query (two requests at once).
    suspend fun extras(query: String): OnlineExtras = withContext(Dispatchers.IO) {
        val q = query.trim()
        if (q.isEmpty()) return@withContext OnlineExtras(emptyList(), emptyList(), null)
        val (artists, albums) = extrasCache.get(key(q)) ?: coroutineScope {
            val a = async { artists(q) }
            val b = async { albums(q) }
            a.await() to b.await()
        }.also { if (it.first.isNotEmpty() || it.second.isNotEmpty()) extrasCache.put(key(q), it) }
        val ranked = SearchRanking.rankArtists(q, artists)
        OnlineExtras(ranked, albums, ranked.firstOrNull()?.takeIf { SearchRanking.namesArtist(q, it.name) })
    }

    // An artist's songs, most popular first: iTunes' own order for a search on the artist name, kept to that artist.
    suspend fun artistTracks(artist: OnlineArtist): List<SimilarTrack> = withContext(Dispatchers.IO) {
        songs("term=${Recommendations.enc(artist.name)}&entity=song&attribute=artistTerm&limit=60", artistId = artist.id)
            .let { SearchRanking.dedup(it) }
    }

    // An album's songs, in album order.
    suspend fun albumTracks(album: OnlineAlbum): List<SimilarTrack> = withContext(Dispatchers.IO) {
        val json = searchGet("https://itunes.apple.com/lookup?id=${album.id}&entity=song&country=FR")
            ?: searchGet("https://itunes.apple.com/lookup?id=${album.id}&entity=song")
        json?.let { parseSongs(JSONObject(it).optJSONArray("results"), null) }.orEmpty()
    }

    // A search request, within the search's share of the iTunes budget (see ItunesGate). Held back or refused by
    // iTunes: ItunesRateLimited, so the screen can say so and retry, instead of showing "no result".
    private fun searchGet(url: String): String? {
        if (!ItunesGate.tryAcquire(search = true)) throw ItunesRateLimited(ItunesGate.searchWaitMs())
        val json = Recommendations.get(url, gated = false)
        if (json == null && ItunesGate.searchWaitMs() > 0) throw ItunesRateLimited(ItunesGate.searchWaitMs())
        return json
    }

    // French storefront first, the default one if it has nothing.
    private fun fetch(params: String): JSONArray? {
        for (country in listOf("&country=FR", "")) {
            val json = searchGet("https://itunes.apple.com/search?$params$country") ?: continue
            val results = runCatching { JSONObject(json).optJSONArray("results") }.getOrNull()
            if (results != null && results.length() > 0) return results
        }
        return null
    }

    private fun songs(params: String, artistId: Long? = null) = parseSongs(fetch(params), artistId)

    private fun parseSongs(results: JSONArray?, artistId: Long?): List<SimilarTrack> {
        val out = ArrayList<SimilarTrack>()
        if (results == null) return out
        for (i in 0 until results.length()) {
            val r = results.optJSONObject(i) ?: continue
            if (r.optString("wrapperType") != "track") continue // a lookup's first row is the album itself
            val name = r.str("trackName") ?: continue
            if (artistId != null && r.optLong("artistId") != artistId) continue
            if (r.optLong("trackTimeMillis") > MAX_DURATION_MS) continue
            out += SimilarTrack(
                name, r.str("artistName") ?: "", r.str("trackViewUrl"), r.str("previewUrl"),
                cleanAlbum(r.str("collectionName")), r.str("artworkUrl100"),
            )
        }
        return out
    }

    private fun artists(q: String): List<OnlineArtist> {
        val results = fetch("term=${Recommendations.enc(q)}&entity=musicArtist&limit=$ARTISTS") ?: return emptyList()
        return (0 until results.length()).mapNotNull { i ->
            val r = results.optJSONObject(i) ?: return@mapNotNull null
            val id = r.optLong("artistId").takeIf { it > 0 } ?: return@mapNotNull null
            OnlineArtist(id, r.str("artistName") ?: return@mapNotNull null, r.str("primaryGenreName"))
        }
    }

    private fun albums(q: String): List<OnlineAlbum> {
        val results = fetch("term=${Recommendations.enc(q)}&entity=album&limit=$ALBUMS") ?: return emptyList()
        return (0 until results.length()).mapNotNull { i ->
            val r = results.optJSONObject(i) ?: return@mapNotNull null
            val id = r.optLong("collectionId").takeIf { it > 0 } ?: return@mapNotNull null
            OnlineAlbum(
                id, cleanAlbum(r.str("collectionName")) ?: return@mapNotNull null, r.str("artistName") ?: "",
                r.str("artworkUrl100"), r.str("releaseDate")?.take(4),
            )
        }
    }

    // iTunes names a single "X - Single" and an EP "X - EP": the suffix would end up in the folder name.
    private val SINGLE_OR_EP = Regex("""\s+-\s+(Single|EP)$""")
    private fun cleanAlbum(album: String?) = album?.replace(SINGLE_OR_EP, "")?.trim()

    private fun JSONObject.str(key: String): String? = optString(key, "").takeIf { it.isNotBlank() }
}

// Re-sorting of the iTunes song hits, cheap and local (a few dozen strings). Score, highest first:
//   * how much of the query the title + artist cover (main signal), with bonuses when the whole title was typed
//     (more when the title is just that, without feat./version suffix)
//     and when the artist was named;
//   * artists already in the library;
//   * iTunes' own rank as a tie-breaker;
//   * minus variants nobody means by default (karaoke, tribute, remix, live…), unless the query asks for them.
// Kept identical to the PC's SearchRanking.cs.
object SearchRanking {
    private val HEAVY = setOf("karaoke", "tribute", "lullaby", "berceuse", "nightcore", "instrumental", "sped", "slowed", "8bit", "made famous")
    private val MEDIUM = setOf("remix", "remixed", "live", "extended", "acoustic", "acoustique", "cover", "reprise", "rework", "bootleg", "mix")
    private val LIGHT = setOf("edit", "version", "remaster", "remastered", "radio")

    fun norm(s: String): String =
        Normalizer.normalize(s.lowercase(), Normalizer.Form.NFD)
            .replace(Regex("\\p{Mn}+"), "")
            .replace("8-bit", "8bit").replace("8 bit", "8bit")
            .replace(Regex("[^a-z0-9]+"), " ").trim()

    private fun words(s: String) = norm(s).split(' ').filter { it.isNotEmpty() }.toSet()

    // The title without what comes after "(", "[" or " - " (feat., remix, version…).
    private fun core(title: String): String {
        var t = title
        for (cut in listOf(" (", " [", " - ")) {
            val i = t.indexOf(cut)
            if (i > 0) t = t.substring(0, i)
        }
        return t
    }

    fun score(query: String, t: SimilarTrack, index: Int, count: Int, libraryArtists: Set<String>): Double {
        val q = words(query)
        if (q.isEmpty()) return 0.0
        val coreWords = words(core(t.title))
        val artistWords = words(t.artist)
        val allWords = words(t.title) + artistWords
        var s = 50.0 * q.count { it in allWords } / q.size
        if (coreWords.isNotEmpty() && q.containsAll(coreWords)) {
            s += 20
            // The plain title, with nothing after it (no feat., version, remix…): the one people mean.
            if (norm(t.title) == norm(core(t.title))) s += 6
        }
        if (artistWords.isNotEmpty() && q.containsAll(artistWords)) s += 10
        if (norm(t.artist) in libraryArtists) s += 8
        if (count > 1) s += 10.0 * (1 - index.toDouble() / (count - 1))
        val variantText = words(t.title.removePrefix(core(t.title))) + words(t.album.orEmpty()) + artistWords
        val variantPhrase = " " + norm(t.title + " " + t.album.orEmpty() + " " + t.artist) + " "
        fun has(w: String) = if (' ' in w) variantPhrase.contains(" $w ") else w in variantText
        for (w in HEAVY) if (has(w) && w !in q) s -= 40
        for (w in MEDIUM) if (has(w) && w !in q) s -= 15
        for (w in LIGHT) if (has(w) && w !in q) s -= 3
        return s
    }

    fun rankTracks(query: String, tracks: List<SimilarTrack>, libraryArtists: Set<String>): List<SimilarTrack> =
        dedup(tracks.mapIndexed { i, t -> t to score(query, t, i, tracks.size, libraryArtists) }
            .sortedByDescending { it.second }.map { it.first })

    // The same song on a single and on its album shows once (the better-ranked copy stays).
    fun dedup(tracks: List<SimilarTrack>): List<SimilarTrack> {
        val seen = HashSet<String>()
        return tracks.filter { seen.add(norm(it.title) + "|" + norm(it.artist)) }
    }

    // Artists whose whole name was typed come first, then iTunes' order.
    fun rankArtists(query: String, artists: List<OnlineArtist>): List<OnlineArtist> {
        val q = words(query)
        return artists.withIndex().sortedWith(
            compareByDescending<IndexedValue<OnlineArtist>> { q.containsAll(words(it.value.name)) }.thenBy { it.index },
        ).map { it.value }
    }

    // The query IS the artist's name (accents/case aside): the best match is the artist, not one of its songs.
    fun namesArtist(query: String, name: String) = norm(query) == norm(name)
}
