package com.akashicrecords.reading

import android.util.Xml
import org.json.JSONArray
import org.json.JSONObject
import org.xmlpull.v1.XmlPullParser
import java.io.File
import java.net.HttpURLConnection
import java.net.URL
import java.net.URLEncoder

// "Trouver des livres": where books come from.
//   * three built-in sources, free and legal: Project Gutenberg (its official OPDS catalogue), French Wikisource
//     (books assembled into an EPUB by Wikimedia's ws-export) and Internet Archive (only freely downloadable EPUBs);
//   * the user's own OPDS catalogues (Calibre server, a library, a publisher, a paid account…), searched through
//     their OpenSearch description and browsable by section — the user picks those addresses;
//   * a direct link to an .epub file.
// Everything is fetched off the main thread by the caller. Network failures give empty results, not exceptions.

data class CatalogBook(
    val source: String,        // label shown on the row ("Gutenberg", "Wikisource", "Internet Archive", catalogue name)
    val title: String,
    val author: String,
    val coverUrl: String?,
    val downloadUrl: String?,  // direct EPUB link when known
    val archiveId: String? = null, // Internet Archive item: the EPUB file is looked up at download time
    val language: String? = null, // "fr", "en"… when the source says so
)

// What the query names: anything, a title, or an author.
enum class SearchMode { ALL, TITLE, AUTHOR }

// One search: words, what they name, and the language wanted (null = any).
data class BookQuery(val text: String, val mode: SearchMode = SearchMode.ALL, val language: String? = null)

// A browsable OPDS page: sections to open, books to download, next page.
data class OpdsPage(val title: String, val sections: List<Pair<String, String>>, val books: List<CatalogBook>, val next: String?)

data class UserCatalog(val name: String, val url: String)

object Catalog {
    const val GUTENBERG = "Gutenberg"
    const val WIKISOURCE = "Wikisource"
    const val ARCHIVE = "Internet Archive"
    val BUILT_IN = listOf(GUTENBERG, WIKISOURCE, ARCHIVE)

    private const val UA = "AkashicRecords/1.0 (personal reading app)"
    private const val LOG = "AkashicCatalog" // logcat tag: what was fetched, each redirect, the HTTP code

    // ---------------------------------------------------------------- built-in sources

    // Gutenberg's own search syntax: "a." author, "t." title, "l.fr" language. Titles come as "Germinal (French)":
    // the language goes to its own field. Gutenberg's order (most downloaded first) is kept for authors.
    fun gutenberg(q: BookQuery): List<CatalogBook> {
        val words = q.text.trim()
        val term = when (q.mode) { SearchMode.AUTHOR -> "a.$words"; SearchMode.TITLE -> "t.$words"; SearchMode.ALL -> words } +
            (q.language?.let { " l.$it" } ?: "")
        val xml = get("https://www.gutenberg.org/ebooks/search.opds/?query=${enc(term)}") ?: return emptyList()
        return parseOpds(xml, "https://www.gutenberg.org/", GUTENBERG).books.mapNotNull { b ->
            // Search entries only point at the book's own feed (".../ebooks/<n>.opds"): the EPUB and cover follow a fixed pattern.
            val id = Regex("/ebooks/(\\d+)").find(b.downloadUrl ?: b.coverUrl ?: "")?.groupValues?.get(1) ?: return@mapNotNull null
            val tag = Regex("""\s*\((French|English|German|Spanish|Italian|Portuguese|Latin|Dutch)\)$""").find(b.title)
            b.copy(
                title = tag?.let { b.title.removeRange(it.range).trim() } ?: b.title,
                language = tag?.groupValues?.get(1)?.let(::languageCode) ?: q.language ?: "en",
                downloadUrl = "https://www.gutenberg.org/ebooks/$id.epub3.images",
                coverUrl = "https://www.gutenberg.org/cache/epub/$id/pg$id.cover.medium.jpg",
            )
        }
    }

    // French only. Author: the author's page ("Auteur:Émile Zola") lists their works. Otherwise a full-text search,
    // where a hit on a sub-page ("Livre/Tome 1/Chapitre 3") stands for its work ("Livre"), once.
    fun wikisource(q: BookQuery): List<CatalogBook> {
        if (q.language != null && q.language != "fr") return emptyList()
        fun book(title: String, author: String) =
            CatalogBook(WIKISOURCE, title, author, null, "https://ws-export.wmcloud.org/?format=epub-3&lang=fr&page=${enc(title.replace(' ', '_'))}", language = "fr")

        if (q.mode == SearchMode.AUTHOR) {
            // The author page must carry every typed word in its name: the search otherwise returns the closest
            // page, often another person (e.g. "damasio" landed on an unrelated author's works).
            val typed = words(q.text)
            val found = get("https://fr.wikisource.org/w/api.php?action=query&list=search&srsearch=${enc(q.text)}&srnamespace=102&srlimit=5&format=json")
                ?.let { json ->
                    runCatching {
                        val hits = JSONObject(json).getJSONObject("query").getJSONArray("search")
                        (0 until hits.length()).map { hits.getJSONObject(it).getString("title") }
                            .firstOrNull { words(it.substringAfter(':')).containsAll(typed) }
                    }.getOrNull()
                }
                ?: return emptyList()
            val links = get("https://fr.wikisource.org/w/api.php?action=parse&page=${enc(found)}&prop=links&format=json")
                ?.let { runCatching { JSONObject(it).getJSONObject("parse").getJSONArray("links") }.getOrNull() } ?: return emptyList()
            val name = found.substringAfter(':')
            return (0 until links.length()).map { links.getJSONObject(it) }
                .filter { it.optInt("ns") == 0 && it.has("exists") }
                .map { it.getString("*") }
                .filter { '/' !in it && ',' !in it } // whole works; "Lettre…, dans Le Figaro" are articles
                .distinct()
                .map { book(it, name) }
        }
        val search = if (q.mode == SearchMode.TITLE) "intitle:${q.text}" else q.text
        val json = get("https://fr.wikisource.org/w/api.php?action=query&list=search&srsearch=${enc(search)}&srnamespace=0&srlimit=40&format=json")
            ?: return emptyList()
        val hits = runCatching { JSONObject(json).getJSONObject("query").getJSONArray("search") }.getOrNull() ?: return emptyList()
        return (0 until hits.length()).map { hits.getJSONObject(it).getString("title") }
            .map { it.substringBefore('/') }
            .distinct()
            .take(15)
            .map { book(it, "") }
    }

    // Texts that have an EPUB and are not restricted (loans under DRM are left out).
    //   * Author: the creator field OR the title — many uploads have no creator, only "Titre - Auteur" as title.
    //   * Language: not filtered — Internet Archive labels are unreliable (a French upload can say "eng"); rank() only
    //     moves matching items up. Sent in the query, it also made their search fail ("BACKEND_ERROR").
    fun archive(q: BookQuery): List<CatalogBook> {
        val words = q.text.trim()
        val field = when (q.mode) {
            SearchMode.AUTHOR -> "(creator:($words) OR title:($words))"
            SearchMode.TITLE -> "title:($words)"
            SearchMode.ALL -> "($words)"
        }
        val search = "$field AND mediatype:texts AND format:EPUB AND NOT access-restricted-item:true"
        val json = get("https://archive.org/advancedsearch.php?q=${enc(search)}&fl[]=identifier&fl[]=title&fl[]=creator&fl[]=language&sort[]=downloads+desc&rows=60&output=json")
            ?: return emptyList()
        val docs = runCatching { JSONObject(json).getJSONObject("response").getJSONArray("docs") }.getOrNull() ?: return emptyList()
        fun first(o: Any?) = if (o is JSONArray) o.optString(0) else o?.toString().orEmpty()
        return (0 until docs.length()).mapNotNull { i ->
            val d = docs.getJSONObject(i)
            val id = d.optString("identifier").takeIf { it.isNotBlank() } ?: return@mapNotNull null
            CatalogBook(
                ARCHIVE, d.optString("title", id), first(d.opt("creator")), "https://archive.org/services/img/$id", null,
                archiveId = id, language = first(d.opt("language")).takeIf { it.isNotBlank() }?.let(::languageCode),
            )
        }
    }

    private fun languageCode(name: String) = when (name.lowercase()) {
        "french", "fre", "fra", "fr" -> "fr"
        "english", "eng", "en" -> "en"
        "german", "ger", "deu" -> "de"
        "spanish", "spa" -> "es"
        "italian", "ita" -> "it"
        else -> name.lowercase().take(3)
    }

    // ---------------------------------------------------------------- relevance

    // Re-sort of one source's hits (cheap, local), like the music search: share of the typed words found in the title
    // (and in the author unless searching by title), whole title typed, author named; the source's own order breaks
    // ties. Same title + author shown once. In author mode the source already matched the author: only duplicates go.
    fun rank(q: BookQuery, books: List<CatalogBook>): List<CatalogBook> {
        val seen = HashSet<String>()
        val unique = books.filter { seen.add(norm(it.title) + "|" + norm(it.author)) }
        // Author mode: the source already matched the author; only the asked language moves books up (stable sort).
        if (q.mode == SearchMode.AUTHOR) return unique.sortedByDescending { q.language != null && it.language == q.language }
        val words = words(q.text)
        if (words.isEmpty()) return unique
        return unique.withIndex().sortedByDescending { (i, b) ->
            val title = words(b.title)
            val author = words(b.author)
            val pool = if (q.mode == SearchMode.TITLE) title else title + author
            var s = 50.0 * words.count { it in pool } / words.size
            if (title.isNotEmpty() && words.containsAll(title)) s += 20
            if (author.isNotEmpty() && author.any { it in words }) s += 8
            if (q.language != null && b.language == q.language) s += 5
            s - i * 0.3
        }.map { it.value }
    }

    private fun norm(s: String) = java.text.Normalizer.normalize(s.lowercase(), java.text.Normalizer.Form.NFD)
        .replace(Regex("\\p{Mn}+"), "").replace(Regex("[^a-z0-9]+"), " ").trim()

    private fun words(s: String) = norm(s).split(' ').filter { it.length > 1 }.toSet()

    // The item's public EPUB file, if it has one.
    private fun archiveEpub(id: String): String? {
        val json = get("https://archive.org/metadata/$id") ?: return null
        val files = runCatching { JSONObject(json).getJSONArray("files") }.getOrNull() ?: return null
        for (i in 0 until files.length()) {
            val f = files.getJSONObject(i)
            val name = f.optString("name")
            if (name.endsWith(".epub", true) && f.optString("private") != "true") return "https://archive.org/download/$id/${enc(name).replace("+", "%20")}"
        }
        return null
    }

    // ---------------------------------------------------------------- user OPDS catalogues

    // Search through the catalogue's OpenSearch description (if it declares one).
    fun searchUserCatalog(catalog: UserCatalog, query: String): List<CatalogBook> {
        val template = searchTemplate(catalog.url) ?: return emptyList()
        val xml = get(template.replace("{searchTerms}", enc(query)).replace(Regex("\\{[^}]*\\?}"), "")) ?: return emptyList()
        return parseOpds(xml, template, catalog.name).books
    }

    fun browse(catalog: UserCatalog, url: String = catalog.url): OpdsPage? = get(url)?.let { parseOpds(it, url, catalog.name) }

    private val templates = HashMap<String, String?>()

    private fun searchTemplate(feedUrl: String): String? = synchronized(templates) { templates[feedUrl] } ?: run {
        val feed = get(feedUrl) ?: return null
        val link = Regex("<link\\b[^>]*rel=\"search\"[^>]*>").findAll(feed).map { it.value }.toList()
        val direct = link.firstOrNull { "atom+xml" in it }?.let { attr(it, "href") }?.takeIf { "{searchTerms}" in it }
        val template = direct?.let { absolute(feedUrl, it) } ?: link.firstOrNull { "opensearchdescription" in it }?.let { attr(it, "href") }?.let { osd ->
            val desc = get(absolute(feedUrl, osd)) ?: return@let null
            Regex("<Url\\b[^>]*>").findAll(desc).map { it.value }
                .sortedByDescending { "atom" in it }
                .firstNotNullOfOrNull { attr(it, "template") }?.let { absolute(feedUrl, it.replace("&amp;", "&")) }
        }
        synchronized(templates) { templates[feedUrl] = template }
        template
    }

    // OPDS (Atom) page: entries with an EPUB acquisition link are books; navigation entries are sections.
    fun parseOpds(xml: String, base: String, source: String): OpdsPage {
        val p = Xml.newPullParser()
        p.setFeature(XmlPullParser.FEATURE_PROCESS_NAMESPACES, false)
        p.setInput(xml.reader())
        var feedTitle = ""
        var next: String? = null
        val sections = ArrayList<Pair<String, String>>()
        val books = ArrayList<CatalogBook>()
        var inEntry = false
        var title = ""
        var author = ""
        var content = ""
        var epub: String? = null
        var cover: String? = null
        var nav: String? = null
        var inAuthor = false
        while (true) {
            val ev = try { p.next() } catch (_: Exception) { break }
            if (ev == XmlPullParser.END_DOCUMENT) break
            val name = p.name?.substringAfter(':')
            when (ev) {
                XmlPullParser.START_TAG -> when (name) {
                    "entry" -> { inEntry = true; title = ""; author = ""; content = ""; epub = null; cover = null; nav = null }
                    "author" -> inAuthor = true
                    "title" -> runCatching { p.nextText().trim() }.getOrNull()?.let { if (inEntry) title = it else if (feedTitle.isEmpty()) feedTitle = it }
                    "name" -> if (inAuthor && author.isEmpty()) author = runCatching { p.nextText().trim() }.getOrDefault("")
                    "content", "summary" -> if (inEntry && content.isEmpty()) content = runCatching { p.nextText().trim() }.getOrDefault("")
                    "link" -> {
                        val rel = p.getAttributeValue(null, "rel").orEmpty()
                        val type = p.getAttributeValue(null, "type").orEmpty()
                        val href = p.getAttributeValue(null, "href")?.let { absolute(base, it) }
                        if (href != null) {
                            if (!inEntry && rel == "next") next = href
                            if (inEntry) when {
                                rel.contains("acquisition") && type.contains("epub") -> if (epub == null) epub = href
                                rel.contains("image") && !href.startsWith("data:") -> if (cover == null || rel.endsWith("thumbnail")) cover = href
                                type.contains("opds-catalog") || type.contains("atom+xml") -> if (nav == null) nav = href
                            }
                        }
                    }
                }
                XmlPullParser.END_TAG -> when (name) {
                    "author" -> inAuthor = false
                    "entry" -> {
                        inEntry = false
                        // Gutenberg puts the author in <content>, sometimes a download count instead: that is not an author.
                        val who = author.ifEmpty { content.takeIf { it.length < 80 && !Regex("""^\d+ downloads?$""").matches(it) }.orEmpty() }
                        when {
                            epub != null -> books += CatalogBook(source, title, who, cover, epub)
                            // Gutenberg-style search hits: the entry only links to the book's own feed.
                            nav != null && Regex("/ebooks/\\d+\\.opds$").containsMatchIn(nav!!) -> books += CatalogBook(source, title, who, cover, nav)
                            nav != null -> sections += title to nav!!
                        }
                    }
                }
            }
        }
        return OpdsPage(feedTitle, sections, books, next)
    }

    // ---------------------------------------------------------------- download

    // Downloads the book's EPUB to [into]. Follows redirects across http/https (HttpURLConnection does not).
    // Refuses what is not a zip (an HTML error page, a login page…).
    fun download(book: CatalogBook, into: File) {
        val url = book.downloadUrl?.takeIf { !it.endsWith(".opds") } ?: book.archiveId?.let(::archiveEpub)
            ?: book.downloadUrl?.let { feed -> get(feed)?.let { parseOpds(it, feed, book.source).books.firstOrNull()?.downloadUrl } }
            ?: error("Pas d'epub téléchargeable librement pour ce livre.")
        android.util.Log.d(LOG, "download ${book.source}: $url")
        downloadUrl(url, into)
    }

    fun downloadUrl(start: String, into: File) {
        var url = start
        repeat(6) {
            val c = URL(url).openConnection() as HttpURLConnection
            c.instanceFollowRedirects = false
            c.connectTimeout = 15_000
            c.readTimeout = 60_000 // ws-export builds the EPUB on request: can take a while (seen: a few seconds)
            c.setRequestProperty("User-Agent", UA)
            try {
                val code = c.responseCode
                android.util.Log.d(LOG, "  $code $url")
                if (code in 300..399) {
                    url = absolute(url, c.getHeaderField("Location") ?: error("Redirection sans destination."))
                    return@repeat
                }
                if (code !in 200..299) error("Le serveur a répondu $code.")
                c.inputStream.use { input -> into.outputStream().use { input.copyTo(it) } }
            } catch (e: java.io.IOException) {
                // Typically an Internet Archive storage server that is down: say so plainly, not "Read timed out".
                android.util.Log.w(LOG, "  no answer from $url", e)
                into.delete()
                error("Le serveur qui stocke ce livre ne répond pas (${runCatching { URL(url).host }.getOrDefault(url)}). Réessaie plus tard.")
            } finally {
                c.disconnect()
            }
            val head = into.inputStream().use { s -> ByteArray(2).also { s.read(it) } }
            if (head[0] != 'P'.code.toByte() || head[1] != 'K'.code.toByte()) {
                into.delete()
                error("Ce lien ne donne pas un fichier epub (page web, connexion requise…).")
            }
            return
        }
        error("Trop de redirections.")
    }

    // ---------------------------------------------------------------- helpers

    private fun get(url: String): String? = try {
        var target = url
        var body: String? = null
        for (hop in 0 until 5) {
            val c = URL(target).openConnection() as HttpURLConnection
            c.instanceFollowRedirects = false
            c.connectTimeout = 12_000
            c.readTimeout = 25_000
            c.setRequestProperty("User-Agent", UA)
            try {
                val code = c.responseCode
                if (code in 300..399) { target = absolute(target, c.getHeaderField("Location") ?: break); continue }
                if (code in 200..299) body = c.inputStream.bufferedReader().use { it.readText() }
            } finally {
                c.disconnect()
            }
            break
        }
        body
    } catch (_: Exception) {
        null
    }

    private fun enc(s: String) = URLEncoder.encode(s, "UTF-8")

    private fun attr(tag: String, name: String) = Regex("\\b$name\\s*=\\s*\"([^\"]*)\"").find(tag)?.groupValues?.get(1)

    fun absolute(base: String, href: String): String = runCatching { URL(URL(base), href).toString() }.getOrDefault(href)
}
