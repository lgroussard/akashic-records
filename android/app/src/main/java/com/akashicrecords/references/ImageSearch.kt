package com.akashicrecords.references

import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import org.json.JSONObject
import java.net.HttpURLConnection
import java.net.URL
import java.net.URLEncoder

// An image found online, before it is saved to a board.
data class ImageHit(
    val source: String,
    val thumbUrl: String,
    val fullUrl: String,
    val title: String,
    val author: String,
    val pageUrl: String?,
    val width: Int, // 0 when the source does not say (the tile then takes the picture's own shape)
    val height: Int,
)

// Free sources with a real public API, no key, reachable by an app (no bot wall):
//  - Wikimedia Commons: photos, art, scans of everything;
//  - Cleveland Museum of Art: its open-access collection (CC0), artworks in good resolution.
// Not here: Are.na (its API now wants an account token) and the Art Institute of Chicago (its image
// server refuses anything but a browser) — both were tried.
object ImageSearch {
    const val COMMONS = "Wikimedia Commons"
    const val CMA = "Cleveland Museum of Art"
    val SOURCES = listOf(COMMONS, CMA)

    private const val PER_PAGE = 24
    // Generic client name: Wikimedia asks automated clients for a descriptive User-Agent.
    internal const val UA = "AkashicRecords/1.0 (personal reference boards; Android)"

    // One page of results (page from 1). Throws on network errors, so the screen can say which source failed.
    suspend fun search(source: String, query: String, page: Int): List<ImageHit> = withContext(Dispatchers.IO) {
        val q = URLEncoder.encode(query.trim(), "UTF-8")
        when (source) {
            COMMONS -> commons(q, page)
            CMA -> cma(q, page)
            else -> emptyList()
        }
    }

    private fun commons(q: String, page: Int): List<ImageHit> {
        val offset = (page - 1) * PER_PAGE
        // filetype:bitmap = photos and paintings, no PDFs, sounds or diagrams in SVG.
        val search = q + URLEncoder.encode(" filetype:bitmap", "UTF-8")
        val json = JSONObject(
            get(
                "https://commons.wikimedia.org/w/api.php?action=query&format=json&generator=search" +
                    "&gsrsearch=$search&gsrnamespace=6&gsrlimit=$PER_PAGE&gsroffset=$offset" +
                    "&prop=imageinfo&iiprop=url%7Csize%7Cextmetadata&iiextmetadatafilter=Artist%7CObjectName&iiurlwidth=480",
            ),
        )
        val pages = json.optJSONObject("query")?.optJSONObject("pages") ?: return emptyList()
        return pages.keys().asSequence().map { pages.getJSONObject(it) }
            .sortedBy { it.optInt("index") }
            .mapNotNull { p ->
                val info = p.optJSONArray("imageinfo")?.optJSONObject(0) ?: return@mapNotNull null
                val meta = info.optJSONObject("extmetadata")
                fun metaText(key: String) = meta?.optJSONObject(key)?.optString("value").orEmpty().stripHtml()
                val fileTitle = p.optString("title").removePrefix("File:").substringBeforeLast('.')
                ImageHit(
                    source = COMMONS,
                    thumbUrl = info.optString("thumburl").ifBlank { info.optString("url") },
                    fullUrl = info.optString("url"),
                    title = metaText("ObjectName").ifBlank { fileTitle },
                    author = metaText("Artist"),
                    pageUrl = info.optString("descriptionurl").ifBlank { null },
                    width = info.optInt("width"), height = info.optInt("height"),
                )
            }.toList()
    }

    private fun cma(q: String, page: Int): List<ImageHit> {
        val skip = (page - 1) * PER_PAGE
        val json = JSONObject(
            get("https://openaccess-api.clevelandart.org/api/artworks/?q=$q&has_image=1&cc0=1&limit=$PER_PAGE&skip=$skip" +
                "&fields=id,title,creators,url,images"),
        )
        val data = json.optJSONArray("data") ?: return emptyList()
        return (0 until data.length()).mapNotNull { i ->
            val a = data.getJSONObject(i)
            val images = a.optJSONObject("images") ?: return@mapNotNull null
            val web = images.optJSONObject("web") ?: return@mapNotNull null
            val webUrl = web.optString("url").ifBlank { return@mapNotNull null }
            // "Utagawa Hiroshige (Japanese, 1797–1858)" → "Utagawa Hiroshige"
            val author = a.optJSONArray("creators")?.optJSONObject(0)?.optString("description").orEmpty().substringBefore(" (").trim()
            ImageHit(
                source = CMA,
                thumbUrl = webUrl,
                fullUrl = images.optJSONObject("print")?.optString("url")?.ifBlank { null } ?: webUrl,
                title = a.optString("title"),
                author = author,
                pageUrl = a.optString("url").ifBlank { null },
                width = web.optString("width").toIntOrNull() ?: 0, height = web.optString("height").toIntOrNull() ?: 0,
            )
        }
    }

    private fun get(url: String): String {
        val c = URL(url).openConnection() as HttpURLConnection
        c.connectTimeout = 10_000
        c.readTimeout = 15_000
        c.setRequestProperty("User-Agent", UA)
        c.setRequestProperty("Accept", "application/json")
        try {
            if (c.responseCode !in 200..299) error("HTTP ${c.responseCode}")
            return c.inputStream.use { it.readBytes().decodeToString() }
        } finally {
            c.disconnect()
        }
    }

    private fun String.stripHtml() = replace(Regex("<[^>]*>"), "").replace("&amp;", "&").replace("&quot;", "\"")
        .replace("&#039;", "'").replace("&nbsp;", " ").replace(Regex("\\s+"), " ").trim()
}
