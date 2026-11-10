package com.akashicrecords.music

import org.json.JSONArray
import org.json.JSONObject
import java.net.HttpURLConnection
import java.net.URL
import java.net.URLEncoder

// One suggested track. Preview = iTunes 30 s clip (playable directly), Page = Apple Music page.
// Album = iTunes album name when known (search results: it names the download folder).
// Artwork = small iTunes cover URL (search results only).
data class SimilarTrack(val title: String, val artist: String, val page: String?, val preview: String?, val album: String? = null, val artwork: String? = null)

// Port of the desktop MusicRecommendationService ("SIMILAIRES"), same chain, no key or account:
//   * iTunes Search -> the seed + its album neighbours (30 s preview + page URL);
//   * MusicBrainz   -> the seed's genre, then recordings of that genre (same artist first,
//     then other artists), each enriched back through iTunes for the preview;
//   * iTunes genre search fills the remaining slots.
// Best-effort everywhere: any failure returns what was gathered so far. Call off the main thread.
object Recommendations {
    private const val MAX_RESULTS = 10
    private const val MAX_DURATION_MS = 15 * 60 * 1000 // longer = compilation/set, not a single

    private data class Hit(val title: String, val artist: String, val page: String?, val preview: String?, val collectionId: Long?, val genre: String?, val durationMs: Long?, val album: String? = null)

    // Lists already built, per seed: a title heard again (or the Similaires tab reopened) costs no request.
    private val similarCache = object : LinkedHashMap<String, List<SimilarTrack>>(16, 0.75f, true) {
        override fun removeEldestEntry(eldest: MutableMap.MutableEntry<String, List<SimilarTrack>>?) = size > 60
    }

    fun similar(title: String, artist: String?, limit: Int = MAX_RESULTS): List<SimilarTrack> {
        if (title.isBlank()) return emptyList()
        val clean = cleanTitle(title)
        val query = if (artist.isNullOrBlank()) clean else "$clean ${artist.trim()}"
        val cacheKey = "${query.lowercase()}|$limit"
        synchronized(similarCache) { similarCache[cacheKey] }?.let { return it }
        // A list cut short by the iTunes limit is not kept, so the next try can complete it.
        val refusalsBefore = ItunesGate.refusalCount()
        return similarUncached(clean, query, artist, limit).also {
            if (it.isNotEmpty() && ItunesGate.refusalCount() == refusalsBefore) synchronized(similarCache) { similarCache[cacheKey] = it }
        }
    }

    private fun similarUncached(clean: String, query: String, artist: String?, limit: Int): List<SimilarTrack> {
        val result = ArrayList<SimilarTrack>()
        val seen = HashSet<String>()
        try {
            val hits = itunesSearch(query, 3)
            var itunesGenre: String? = null
            hits.firstOrNull()?.let { seed ->
                itunesGenre = seed.genre
                add(result, seen, seed.title, seed.artist, seed.page, seed.preview, seed.durationMs)
                seed.collectionId?.let { cid ->
                    for (t in itunesAlbum(cid, maxOf(1, limit - 3))) add(result, seen, t.title, t.artist, t.page, t.preview, t.durationMs)
                }
            }

            val genre = musicBrainzGenre(query) ?: itunesGenre
            if (!genre.isNullOrBlank()) {
                if (!artist.isNullOrBlank() && result.size < limit) {
                    for ((t, a) in musicBrainzSearch("genre:\"$genre\" AND artist:\"${artist.trim()}\"", limit - result.size)) {
                        addEnriched(result, seen, t, a)
                    }
                }
                if (result.size < limit) {
                    for ((t, a) in musicBrainzSearch("genre:\"$genre\"", limit - result.size + 2)) {
                        if (result.size >= limit) break
                        addEnriched(result, seen, t, a)
                    }
                }
                if (result.size < limit) {
                    for (t in itunesSearch(genre.trim(), limit - result.size)) add(result, seen, t.title, t.artist, t.page, t.preview, t.durationMs)
                }
            }
        } catch (_: Exception) {
            // Best-effort: keep what we have.
        }
        return result
    }

    // Same filters as the desktop: no clip = nothing to hear, > 15 min dropped, dedup by title.
    private fun add(list: MutableList<SimilarTrack>, seen: MutableSet<String>, title: String, artist: String, page: String?, preview: String?, durationMs: Long?) {
        if (list.size >= MAX_RESULTS || title.isBlank() || preview.isNullOrBlank()) return
        if (durationMs != null && durationMs > MAX_DURATION_MS) return
        if (!seen.add(title.trim().lowercase())) return
        list += SimilarTrack(title, artist, page, preview)
    }

    private fun addEnriched(list: MutableList<SimilarTrack>, seen: MutableSet<String>, title: String, artist: String) {
        val hit = itunesSearch(if (artist.isBlank()) title else "$title $artist", 1).firstOrNull()
        add(list, seen, title, artist, hit?.page, hit?.preview, hit?.durationMs)
    }

    private fun itunesSearch(query: String, cap: Int, country: String? = null): List<Hit> {
        val json = get("https://itunes.apple.com/search?term=${enc(query)}&entity=song&limit=$cap" + (country?.let { "&country=$it" } ?: "")) ?: return emptyList()
        return parseItunes(JSONObject(json).optJSONArray("results"), cap)
    }

    private fun itunesAlbum(collectionId: Long, limit: Int): List<Hit> {
        val json = get("https://itunes.apple.com/lookup?id=$collectionId&entity=song&limit=$limit") ?: return emptyList()
        return parseItunes(JSONObject(json).optJSONArray("results"), limit)
    }

    private fun parseItunes(results: JSONArray?, cap: Int): List<Hit> {
        val out = ArrayList<Hit>()
        if (results == null) return out
        for (i in 0 until results.length()) {
            val r = results.optJSONObject(i) ?: continue
            val name = r.str("trackName") ?: continue // the lookup's first row is the album itself
            out += Hit(
                name, r.str("artistName") ?: "", r.str("trackViewUrl"), r.str("previewUrl"),
                r.optLong("collectionId").takeIf { it > 0 }, r.str("primaryGenreName"),
                r.optLong("trackTimeMillis").takeIf { it > 0 }, r.str("collectionName"),
            )
            if (out.size >= cap) break
        }
        return out
    }

    private fun musicBrainzGenre(query: String): String? {
        val json = get("https://musicbrainz.org/ws/2/recording?query=${enc(query)}&fmt=json") ?: return null
        val recs = JSONObject(json).optJSONArray("recordings") ?: return null
        val ids = (0 until minOf(3, recs.length())).mapNotNull { recs.optJSONObject(it)?.str("id") }
        for (id in ids) {
            val detail = get("https://musicbrainz.org/ws/2/recording/$id?inc=releases+genres&fmt=json") ?: continue
            val full = JSONObject(detail)
            firstGenre(full)?.let { return it }
            val rels = full.optJSONArray("releases") ?: continue
            for (i in 0 until rels.length()) firstGenre(rels.optJSONObject(i))?.let { return it }
        }
        return null
    }

    private fun musicBrainzSearch(query: String, cap: Int): List<Pair<String, String>> {
        if (cap <= 0) return emptyList()
        val json = get("https://musicbrainz.org/ws/2/recording?query=${enc(query)}&limit=$cap&fmt=json") ?: return emptyList()
        val recs = JSONObject(json).optJSONArray("recordings") ?: return emptyList()
        val out = ArrayList<Pair<String, String>>()
        for (i in 0 until recs.length()) {
            val rec = recs.optJSONObject(i) ?: continue
            val title = rec.str("title") ?: continue
            val credits = rec.optJSONArray("artist-credit")
            val artist = (0 until (credits?.length() ?: 0)).firstNotNullOfOrNull { credits!!.optJSONObject(it)?.str("name") } ?: ""
            out += title to artist
            if (out.size >= cap) break
        }
        return out
    }

    private fun firstGenre(o: JSONObject?): String? {
        val genres = o?.optJSONArray("genres") ?: return null
        return (0 until genres.length()).firstNotNullOfOrNull { genres.optJSONObject(it)?.str("name") }
    }

    // MusicBrainz answers 403 without an explicit User-Agent.
    // iTunes requests go through ItunesGate (gated = false when the caller already took its slot, see OnlineSearch).
    internal fun get(url: String, gated: Boolean = true): String? {
        val itunes = ItunesGate.isItunes(url)
        if (itunes && gated && !ItunesGate.tryAcquire(search = false)) return null
        return try {
            val c = URL(url).openConnection() as HttpURLConnection
            c.connectTimeout = 10_000
            c.readTimeout = 15_000
            c.setRequestProperty("User-Agent", "AkashicRecords/1.0 (similar-tracks lookup)")
            c.setRequestProperty("Accept", "application/json")
            try {
                val code = c.responseCode
                if (itunes && (code == 403 || code == 429)) ItunesGate.refused()
                if (code in 200..299) c.inputStream.bufferedReader().use { it.readText() } else null
            } finally {
                c.disconnect()
            }
        } catch (_: Exception) {
            null
        }
    }

    internal fun enc(s: String) = URLEncoder.encode(s, "UTF-8")

    private fun JSONObject.str(key: String): String? = optString(key, "").takeIf { it.isNotBlank() }

    // yt-dlp file names look like "Title [youtubeId]"; a leading "01. " track number is dropped too.
    fun cleanTitle(raw: String): String {
        var t = raw.trim()
        val bracket = t.indexOf('[')
        if (bracket > 0) t = t.substring(0, bracket).trim()
        val dot = t.indexOf(". ")
        if (dot in 1..2 && t.substring(0, dot).toIntOrNull() != null) t = t.substring(dot + 2).trim()
        return t
    }

    // Desktop MatchesSimilar: punctuation-folded containment, so "artist-title [id]" file names
    // match API titles.
    fun matches(localTitle: String, similarTitle: String): Boolean {
        if (localTitle.isBlank()) return false
        fun norm(s: String): String {
            var t = s.trim()
            val b = t.indexOf('[')
            if (b > 0) t = t.substring(0, b)
            return t.lowercase().filter { it.isLetterOrDigit() }
        }
        val a = norm(localTitle)
        val b = norm(similarTitle)
        if (a.isEmpty() || b.isEmpty()) return false
        return a == b || a.contains(b) || b.contains(a)
    }
}
