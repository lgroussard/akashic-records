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

    // userAsked: the Similaires list opened by the user. Its iTunes requests count like a search (the larger share), not
    // like background ones — the automatic chaining of suggestions has often used those up already.
    fun similar(title: String, artist: String?, limit: Int = MAX_RESULTS, userAsked: Boolean = false): List<SimilarTrack> {
        if (title.isBlank()) return emptyList()
        // A downloaded song is named after its YouTube video ("Daft Punk - Get Lucky (Official Audio) ft. Pharrell…",
        // artist = the channel): searched as is, iTunes and MusicBrainz matched other songs, so its suggestions drifted
        // away from it. The seed is brought back to the plain title + artist a suggestion carries.
        val (clean, cleanArtist) = seed(title, artist)
        val query = if (cleanArtist.isBlank()) clean else "$clean $cleanArtist"
        val cacheKey = "${query.lowercase()}|$limit"
        synchronized(similarCache) { similarCache[cacheKey] }?.let { return it }
        // A list cut short by the iTunes limit is not kept, so the next try can complete it.
        val refusalsBefore = ItunesGate.refusalCount()
        return similarUncached(clean, query, cleanArtist.ifBlank { null }, limit, userAsked).also {
            if (it.isNotEmpty() && ItunesGate.refusalCount() == refusalsBefore) synchronized(similarCache) { similarCache[cacheKey] = it }
        }
    }

    private fun similarUncached(clean: String, query: String, artist: String?, limit: Int, userAsked: Boolean): List<SimilarTrack> {
        val result = ArrayList<SimilarTrack>()
        val seen = HashSet<String>()
        try {
            val hits = itunesSearch(query, 3, userAsked = userAsked)
            var itunesGenre: String? = null
            hits.firstOrNull()?.let { seed ->
                itunesGenre = seed.genre
                add(result, seen, seed.title, seed.artist, seed.page, seed.preview, seed.durationMs)
                seed.collectionId?.let { cid ->
                    for (t in itunesAlbum(cid, maxOf(1, limit - 3), userAsked)) add(result, seen, t.title, t.artist, t.page, t.preview, t.durationMs)
                }
            }

            // Fielded query when the artist is known: a loose one often landed on a namesake recording (another genre).
            val mbQuery = if (artist.isNullOrBlank()) clean else "recording:\"${mbEscape(clean)}\" AND artist:\"${mbEscape(artist)}\""
            val genre = musicBrainzGenre(mbQuery) ?: (if (mbQuery != query) musicBrainzGenre(query) else null) ?: itunesGenre
            if (!genre.isNullOrBlank()) {
                if (!artist.isNullOrBlank() && result.size < limit) {
                    for ((t, a) in musicBrainzSearch("genre:\"$genre\" AND artist:\"${artist.trim()}\"", limit - result.size)) {
                        addEnriched(result, seen, t, a, userAsked)
                    }
                }
                if (result.size < limit) {
                    for ((t, a) in musicBrainzSearch("genre:\"$genre\"", limit - result.size + 2)) {
                        if (result.size >= limit) break
                        addEnriched(result, seen, t, a, userAsked)
                    }
                }
                if (result.size < limit) {
                    for (t in itunesSearch(genre.trim(), limit - result.size, userAsked = userAsked)) add(result, seen, t.title, t.artist, t.page, t.preview, t.durationMs)
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

    // A MusicBrainz suggestion, completed with its iTunes clip while the background budget allows. Past it the title
    // is kept without a clip (the full track still comes from yt-dlp): one iTunes request per suggestion is what used
    // to eat the whole minute and lock the search out.
    private fun addEnriched(list: MutableList<SimilarTrack>, seen: MutableSet<String>, title: String, artist: String, userAsked: Boolean) {
        // User-asked: clips only while 3 requests stay free for a search typed meanwhile.
        val available = if (userAsked) ItunesGate.searchWaitMs(4) == 0L else ItunesGate.backgroundAvailable()
        if (!available) { add(list, seen, title, artist, null, null, null); return }
        val hit = itunesSearch(if (artist.isBlank()) title else "$title $artist", 1, userAsked = userAsked).firstOrNull()
        add(list, seen, title, artist, hit?.page, hit?.preview, hit?.durationMs)
    }

    private fun itunesSearch(query: String, cap: Int, country: String? = null, userAsked: Boolean = false): List<Hit> {
        val json = get("https://itunes.apple.com/search?term=${enc(query)}&entity=song&limit=$cap" + (country?.let { "&country=$it" } ?: ""), search = userAsked) ?: return emptyList()
        return parseItunes(JSONObject(json).optJSONArray("results"), cap)
    }

    private fun itunesAlbum(collectionId: Long, limit: Int, userAsked: Boolean = false): List<Hit> {
        val json = get("https://itunes.apple.com/lookup?id=$collectionId&entity=song&limit=$limit", search = userAsked) ?: return emptyList()
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
    internal fun get(url: String, gated: Boolean = true, search: Boolean = false): String? {
        val itunes = ItunesGate.isItunes(url)
        if (itunes && gated && !ItunesGate.tryAcquire(search = search)) return null
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

    // yt-dlp file names look like "Title [youtubeId]"; a leading track number ("01. ", "5.", "07 - ") is dropped too.
    fun cleanTitle(raw: String): String {
        var t = raw.trim()
        t = Regex("\\s*\\[[A-Za-z0-9_-]{11}\\]\\s*$").replace(t, "") // the YouTube id
        val bracket = t.indexOf('[')
        if (bracket > 0 && !t.substring(bracket).contains(']')) t = t.substring(0, bracket).trim()
        t = Regex("^\\d{1,2}\\s*(\\.|-|–)\\s*").replace(t, "").trim()
        return t
    }

    private fun mbEscape(s: String) = s.replace("\\", "").replace("\"", "")

    // Words that only describe the video, not the song: "(Official Audio)", "[HD]", "| Lyrics"…
    private val NOISE = Regex(
        "\\b(official|officiel|audio|video|vidéo|clip|lyrics?|paroles|hd|hq|4k|remaster(ed)?|visuali[sz]er|mv|explicit)\\b",
        RegexOption.IGNORE_CASE,
    )

    // Desktop SeedOf: (title, artist) of a song as the music APIs know it, from a library file name / tags or a
    // suggestion (already clean: left as is). Steps: video noise in brackets dropped, "ft./feat." credits dropped,
    // "Artist - Title" split (the artist side found by the known artist, else YouTube's usual order), channel suffixes
    // ("VEVO", " - Topic") dropped from the artist.
    fun seed(rawTitle: String, rawArtist: String?): Pair<String, String> {
        var t = cleanTitle(rawTitle)
        // A web slug ("mika-ring-ring", "vianney-je-men-vais-clip-officiel"): words back.
        if (' ' !in t && t.count { it == '-' || it == '_' } >= 2) t = t.replace('-', ' ').replace('_', ' ').trimEnd('.')
        // Bracketed parts that are only video noise; other brackets ("(Remix)", "(Live)") stay.
        t = Regex("[\\(\\[]([^\\)\\]]*)[\\)\\]]").replace(t) { m -> if (NOISE.containsMatchIn(m.groupValues[1])) " " else m.value }
        // The same noise left bare at the end: "… Lyrics", "… Extreme HD", "… clip officiel".
        t = Regex("(\\s+${NOISE.pattern})+\\s*$", RegexOption.IGNORE_CASE).replace(t, "")
        t = t.substringBefore(" | ").substringBefore(" // ")
        t = Regex("\\s+(ft\\.?|feat\\.?|featuring)\\s+.*$", RegexOption.IGNORE_CASE).replace(t, "")
        t = Regex("\\s+").replace(t, " ").trim().trim('-', '–', '—', ' ')

        var artist = (rawArtist ?: "").trim()
        artist = Regex("(?i)\\s*(vevo|official|- topic)$").replace(artist, "").trim()
        // A channel name squeezed together ("DaftPunk" from "DaftPunkVEVO"): spaces back between the words.
        if (' ' !in artist) artist = Regex("(?<=\\p{Ll})(?=\\p{Lu})").replace(artist, " ")
        // "Artist - Title" (also "Artist - Title - Album"): keep the title side.
        val parts = t.split(Regex("\\s+[-–—]\\s+")).map { it.trim() }.filter { it.isNotEmpty() }
        if (parts.size >= 2) {
            fun same(a: String, b: String) = a.isNotBlank() && b.isNotBlank() &&
                a.lowercase().filter { it.isLetterOrDigit() }.let { x -> b.lowercase().filter { it.isLetterOrDigit() }.let { y -> x.contains(y) || y.contains(x) } }
            when {
                // The written name ("Daft Punk") over a squeezed channel one ("DaftPunk" from "DaftPunkVEVO").
                artist.isBlank() || same(parts[0], artist) -> { artist = parts[0]; t = parts[1] }
                same(parts[1], artist) -> t = parts[0]
                // A channel name that is not the artist: YouTube's usual order wins.
                else -> { artist = parts[0]; t = parts[1] }
            }
        }
        return (t.ifBlank { cleanTitle(rawTitle) }) to artist
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
