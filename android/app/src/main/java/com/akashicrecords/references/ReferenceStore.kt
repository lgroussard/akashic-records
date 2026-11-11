package com.akashicrecords.references

import androidx.compose.runtime.getValue
import androidx.compose.runtime.setValue

import android.content.Context
import android.graphics.BitmapFactory
import android.net.Uri
import android.webkit.MimeTypeMap
import com.akashicrecords.data.AkashicDatabase
import com.akashicrecords.data.RefImage
import com.akashicrecords.journaux.nowIso
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import java.io.File
import java.net.HttpURLConnection
import java.net.URL
import java.util.UUID

// Saving reference images: from a search hit, from an image another app shares (Pinterest, Instagram, the
// gallery…), or from a shared link (the page's preview image, og:image, as Pinterest and most sites provide).
object ReferenceStore {
    // Bumped on every save or delete, so an open Références tab refreshes after a share made over it.
    var changes by androidx.compose.runtime.mutableIntStateOf(0)
        private set

    private const val MAX_BYTES = 30L * 1024 * 1024

    fun fileOf(context: Context, img: RefImage) = File(context.filesDir, img.ImagePath)

    suspend fun saveHit(context: Context, db: AkashicDatabase, hit: ImageHit, boardId: Int?): RefImage =
        saveUrl(context, db, hit.fullUrl, hit.pageUrl, hit.title, hit.author, hit.source, boardId, fallbackUrl = hit.thumbUrl)

    // Downloads [imageUrl] (or [fallbackUrl] when the full size fails) and files it.
    suspend fun saveUrl(
        context: Context, db: AkashicDatabase, imageUrl: String, pageUrl: String?, title: String, author: String,
        source: String, boardId: Int?, fallbackUrl: String? = null,
    ): RefImage = withContext(Dispatchers.IO) {
        val (bytes, type) = runCatching { download(imageUrl) }.getOrElse { e -> fallbackUrl?.let { download(it) } ?: throw e }
        store(context, db, bytes, type, imageUrl, pageUrl, title, author, source, boardId)
    }

    // An image shared by another app (content:// Uri, readable while the share lasts).
    suspend fun saveShared(context: Context, db: AkashicDatabase, uri: Uri, title: String, boardId: Int?): RefImage = withContext(Dispatchers.IO) {
        val resolver = context.contentResolver
        val bytes = resolver.openInputStream(uri)?.use { it.readBytes() } ?: error("Image illisible.")
        store(context, db, bytes, resolver.getType(uri), null, null, title, "", "Partage", boardId)
    }

    // A shared link → the image to save: the link itself when it is an image, else the page's og:image
    // (twitter:image as a fallback) and its og:title. Null when the page has no preview image.
    data class LinkImage(val imageUrl: String, val pageUrl: String, val title: String)

    suspend fun resolveLink(link: String): LinkImage? = withContext(Dispatchers.IO) {
        val c = open(link)
        try {
            val type = c.contentType.orEmpty()
            if (type.startsWith("image/")) return@withContext LinkImage(c.url.toString(), link, "")
            val html = c.inputStream.use { input ->
                val buf = ByteArray(512 * 1024) // the <head> is enough
                var n = 0
                while (n < buf.size) { val r = input.read(buf, n, buf.size - n); if (r < 0) break; n += r }
                String(buf, 0, n)
            }
            val image = meta(html, "og:image:secure_url") ?: meta(html, "og:image") ?: meta(html, "twitter:image") ?: return@withContext null
            LinkImage(URL(c.url, image).toString(), c.url.toString(), meta(html, "og:title").orEmpty())
        } finally {
            c.disconnect()
        }
    }

    suspend fun delete(context: Context, db: AkashicDatabase, img: RefImage) = withContext(Dispatchers.IO) {
        db.refImageDao().deleteById(img.Id)
        fileOf(context, img).delete()
        changes++
    }

    private suspend fun store(
        context: Context, db: AkashicDatabase, bytes: ByteArray, mime: String?, sourceUrl: String?, pageUrl: String?,
        title: String, author: String, source: String, boardId: Int?,
    ): RefImage {
        val bounds = BitmapFactory.Options().apply { inJustDecodeBounds = true }
        BitmapFactory.decodeByteArray(bytes, 0, bytes.size, bounds)
        if (bounds.outWidth <= 0 || bounds.outHeight <= 0) error("Ce n'est pas une image lisible.")
        val ext = mime?.substringBefore(';')?.let { MimeTypeMap.getSingleton().getExtensionFromMimeType(it) } ?: "jpg"
        val dir = File(context.filesDir, "references").apply { mkdirs() }
        val name = UUID.randomUUID().toString().replace("-", "") + "." + ext
        File(dir, name).writeBytes(bytes)
        val img = RefImage(
            BoardId = boardId, ImagePath = "references/$name", Title = title.trim(), Author = author.trim(), Source = source,
            SourceUrl = sourceUrl, PageUrl = pageUrl, Width = bounds.outWidth, Height = bounds.outHeight, Note = "", CreatedAt = nowIso(),
        )
        return img.copy(Id = db.refImageDao().add(img).toInt()).also { changes++ }
    }

    private fun download(url: String): Pair<ByteArray, String?> {
        val c = open(url)
        try {
            val type = c.contentType
            if (type != null && !type.startsWith("image/")) error("Le lien ne mène pas à une image.")
            if (c.contentLengthLong > MAX_BYTES) error("Image trop lourde.")
            val bytes = c.inputStream.use { it.readBytes() }
            return bytes to type
        } finally {
            c.disconnect()
        }
    }

    // GET following redirects across http/https (HttpURLConnection alone does not), e.g. pin.it short links.
    private fun open(url: String): HttpURLConnection {
        var target = URL(url)
        repeat(6) {
            val c = target.openConnection() as HttpURLConnection
            c.instanceFollowRedirects = false
            c.connectTimeout = 10_000
            c.readTimeout = 20_000
            c.setRequestProperty("User-Agent", "Mozilla/5.0 (Linux; Android) " + ImageSearch.UA)
            val code = c.responseCode
            if (code in 300..399) {
                val next = c.getHeaderField("Location") ?: error("Redirection sans destination.")
                c.disconnect()
                target = URL(target, next)
            } else {
                if (code !in 200..299) { c.disconnect(); error("HTTP $code") }
                return c
            }
        }
        error("Trop de redirections.")
    }

    // <meta property|name="key" content="…"> in either attribute order.
    private fun meta(html: String, key: String): String? {
        val k = Regex.escape(key)
        val tag = Regex("<meta[^>]+(?:property|name)\\s*=\\s*[\"']$k[\"'][^>]*>", RegexOption.IGNORE_CASE).find(html)?.value ?: return null
        return Regex("content\\s*=\\s*[\"']([^\"']+)[\"']", RegexOption.IGNORE_CASE).find(tag)?.groupValues?.get(1)
            ?.replace("&amp;", "&")?.replace("&quot;", "\"")?.replace("&#39;", "'")?.trim()?.ifBlank { null }
    }
}
