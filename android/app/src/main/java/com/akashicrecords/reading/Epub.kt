package com.akashicrecords.reading

import android.util.Xml
import org.xmlpull.v1.XmlPullParser
import java.io.File
import java.net.URLDecoder
import java.util.zip.ZipFile

// Minimal EPUB 2/3 reader, enough for reflowable books: the package (title, author, cover, reading order) and the
// table of contents (EPUB 3 nav document, else the EPUB 2 NCX). Pages themselves are the book's own XHTML, shown by
// the reader's WebView straight from the zip (see ReaderView). Fixed-layout books open but are not paginated well.
data class TocEntry(val title: String, val chapter: Int, val depth: Int)

class Epub private constructor(
    val file: File,
    val title: String,
    val author: String,
    val coverPath: String?,
    // Zip paths of the reading order (spine), e.g. "OEBPS/Text/chap01.xhtml".
    val chapters: List<String>,
    val toc: List<TocEntry>,
) {
    fun read(path: String): ByteArray? = ZipFile(file).use { zip -> zip.getEntry(path)?.let { e -> zip.getInputStream(e).use { it.readBytes() } } }

    companion object {
        // Throws on anything that is not a readable EPUB (no container, no package, empty spine).
        fun open(file: File): Epub = ZipFile(file).use { zip ->
            fun text(path: String): String? = zip.getEntry(path)?.let { e -> zip.getInputStream(e).use { it.readBytes().decodeToString() } }

            val container = text("META-INF/container.xml") ?: error("Pas un epub (META-INF/container.xml absent).")
            val opfPath = Regex("full-path\\s*=\\s*\"([^\"]+)\"").find(container)?.groupValues?.get(1)
                ?: error("Epub sans fichier de description.")
            val opfDir = opfPath.substringBeforeLast('/', "")
            val opf = text(opfPath) ?: error("Fichier de description introuvable : $opfPath")

            var title = ""
            var author = ""
            var coverId: String? = null
            var ncxId: String? = null
            data class Item(val href: String, val type: String, val props: String)
            val items = HashMap<String, Item>()
            val spine = ArrayList<String>()

            parse(opf) { p ->
                when (p.name) {
                    "title" -> if (title.isEmpty()) title = p.nextText().trim()
                    "creator" -> if (author.isEmpty()) author = p.nextText().trim()
                    "meta" -> if (p.attr("name") == "cover") coverId = p.attr("content")
                    "item" -> p.attr("id")?.let { id ->
                        items[id] = Item(resolve(opfDir, p.attr("href").orEmpty()), p.attr("media-type").orEmpty(), p.attr("properties").orEmpty())
                    }
                    "spine" -> ncxId = p.attr("toc")
                    "itemref" -> if (p.attr("linear") != "no") p.attr("idref")?.let { spine += it }
                }
            }

            val chapters = spine.mapNotNull { items[it]?.href }.filter { zip.getEntry(it) != null }
            require(chapters.isNotEmpty()) { "Epub sans chapitre lisible." }

            val cover = items.values.firstOrNull { "cover-image" in it.props.split(' ') }?.href
                ?: coverId?.let { items[it]?.href }
                ?: items.values.firstOrNull { it.type.startsWith("image/") && "cover" in it.href.lowercase() }?.href

            val chapterIndex = chapters.withIndex().associate { (i, p) -> p to i }
            fun chapterOf(href: String) = chapterIndex[href.substringBefore('#')]

            val toc = ArrayList<TocEntry>()
            val nav = items.values.firstOrNull { "nav" in it.props.split(' ') }
            if (nav != null) text(nav.href)?.let { navToc(it, nav.href.substringBeforeLast('/', ""), ::chapterOf, toc) }
            if (toc.isEmpty()) {
                val ncx = ncxId?.let { items[it] } ?: items.values.firstOrNull { it.type == "application/x-dtbncx+xml" }
                if (ncx != null) text(ncx.href)?.let { ncxToc(it, ncx.href.substringBeforeLast('/', ""), ::chapterOf, toc) }
            }

            Epub(
                file,
                title.ifBlank { file.nameWithoutExtension },
                author,
                cover?.takeIf { zip.getEntry(it) != null },
                chapters,
                toc.ifEmpty { chapters.indices.map { TocEntry("Chapitre ${it + 1}", it, 0) } },
            )
        }

        // "a/b" + "../c/d.xhtml" -> "a/c/d.xhtml"; percent-escapes decoded (zip entries hold the plain names).
        internal fun resolve(base: String, href: String): String {
            val clean = runCatching { URLDecoder.decode(href.substringBefore('#').replace("+", "%2B"), "UTF-8") }.getOrDefault(href.substringBefore('#'))
            val parts = ArrayList<String>()
            if (base.isNotEmpty() && !clean.startsWith("/")) parts += base.split('/')
            for (seg in clean.trimStart('/').split('/')) {
                when (seg) {
                    "", "." -> {}
                    ".." -> if (parts.isNotEmpty()) parts.removeAt(parts.size - 1)
                    else -> parts += seg
                }
            }
            val fragment = href.substringAfter('#', "")
            return parts.joinToString("/") + if (fragment.isNotEmpty()) "#$fragment" else ""
        }

        private fun XmlPullParser.attr(name: String): String? =
            (0 until attributeCount).firstOrNull { getAttributeName(it) == name || getAttributeName(it).endsWith(":$name") }?.let { getAttributeValue(it) }

        // Visits every start tag (namespace prefixes dropped from names).
        private fun parse(xml: String, onStart: (XmlPullParser) -> Unit) {
            val p = Xml.newPullParser()
            p.setFeature(XmlPullParser.FEATURE_PROCESS_NAMESPACES, false)
            p.setInput(xml.reader())
            while (true) {
                val ev = runCatching { p.next() }.getOrElse { return }
                if (ev == XmlPullParser.END_DOCUMENT) return
                if (ev == XmlPullParser.START_TAG) {
                    val name = p.name.substringAfter(':')
                    onStart(object : XmlPullParser by p {
                        override fun getName() = name
                    })
                }
            }
        }

        // EPUB 3: <nav epub:type="toc"><ol><li><a href>…</a><ol>…</ol></li></ol></nav>.
        private fun navToc(xml: String, dir: String, chapterOf: (String) -> Int?, out: MutableList<TocEntry>) {
            val body = Regex("<nav[^>]*toc[^>]*>(.*?)</nav>", RegexOption.DOT_MATCHES_ALL).find(xml)?.groupValues?.get(1) ?: return
            var depth = -1
            Regex("<(/?)ol\\b[^>]*>|<a\\b[^>]*href\\s*=\\s*\"([^\"]*)\"[^>]*>(.*?)</a>", RegexOption.DOT_MATCHES_ALL).findAll(body).forEach { m ->
                when {
                    m.value.startsWith("<ol") -> depth++
                    m.value.startsWith("</ol") -> depth--
                    else -> {
                        val chapter = chapterOf(resolve(dir, m.groupValues[2])) ?: return@forEach
                        val label = m.groupValues[3].replace(Regex("<[^>]+>"), "").let(::unescape).trim()
                        if (label.isNotEmpty()) out += TocEntry(label, chapter, depth.coerceAtLeast(0))
                    }
                }
            }
        }

        // EPUB 2: <navPoint><navLabel><text>…</text></navLabel><content src/>…</navPoint>.
        private fun ncxToc(xml: String, dir: String, chapterOf: (String) -> Int?, out: MutableList<TocEntry>) {
            var depth = -1
            var label = ""
            Regex("<(/?)navPoint\\b[^>]*>|<text>(.*?)</text>|<content\\b[^>]*src\\s*=\\s*\"([^\"]*)\"", RegexOption.DOT_MATCHES_ALL).findAll(xml).forEach { m ->
                when {
                    m.value.startsWith("<navPoint") -> depth++
                    m.value.startsWith("</navPoint") -> depth--
                    m.value.startsWith("<text") -> label = unescape(m.groupValues[2]).trim()
                    else -> {
                        val chapter = chapterOf(resolve(dir, m.groupValues[3])) ?: return@forEach
                        if (label.isNotEmpty()) out += TocEntry(label, chapter, depth.coerceAtLeast(0))
                    }
                }
            }
        }

        private fun unescape(s: String) = s.replace("&amp;", "&").replace("&lt;", "<").replace("&gt;", ">")
            .replace("&quot;", "\"").replace("&#39;", "'").replace("&apos;", "'").replace("&nbsp;", " ")
    }
}
