package com.akashicrecords.reading

import android.graphics.Bitmap
import android.graphics.BitmapFactory
import android.graphics.Color
import android.graphics.pdf.PdfRenderer
import android.os.ParcelFileDescriptor
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock
import kotlinx.coroutines.withContext
import java.io.File
import java.util.zip.ZipFile

// Books made of pages rather than reflowable text: scanned PDFs and comics (CBZ = a zip of page images).
// Pages are rendered one at a time, at the width asked for, off the main thread.
interface PageDoc {
    val pageCount: Int
    suspend fun render(index: Int, width: Int): Bitmap?
    fun close()

    companion object {
        fun open(file: File): PageDoc = when (BookFormat.of(file)) {
            BookFormat.PDF -> PdfDoc(file)
            BookFormat.CBZ -> CbzDoc(file)
            BookFormat.EPUB -> error("Un epub se lit dans le lecteur de texte.")
        }
    }
}

enum class BookFormat {
    EPUB, PDF, CBZ;

    companion object {
        fun of(file: File) = when (file.extension.lowercase()) { "pdf" -> PDF; "cbz" -> CBZ; else -> EPUB }

        // From the file's content (a download's name says nothing): "%PDF" header, or a zip that is an epub (has
        // META-INF/container.xml) or a comic (only images). Null when it is none of these.
        fun sniff(file: File): BookFormat? {
            val head = file.inputStream().use { s -> ByteArray(4).also { s.read(it) } }
            if (head[0] == '%'.code.toByte() && head[1] == 'P'.code.toByte() && head[2] == 'D'.code.toByte() && head[3] == 'F'.code.toByte()) return PDF
            if (head[0] != 'P'.code.toByte() || head[1] != 'K'.code.toByte()) return null
            return runCatching {
                ZipFile(file).use { zip ->
                    when {
                        zip.getEntry("META-INF/container.xml") != null -> EPUB
                        zip.entries().asSequence().any { CbzDoc.isImage(it.name) } -> CBZ
                        else -> null
                    }
                }
            }.getOrNull()
        }
    }
}

// Android's built-in PDF renderer. It allows one open page at a time, hence the lock.
private class PdfDoc(file: File) : PageDoc {
    private val fd = ParcelFileDescriptor.open(file, ParcelFileDescriptor.MODE_READ_ONLY)
    private val renderer = PdfRenderer(fd)
    private val lock = Mutex()
    override val pageCount = renderer.pageCount

    override suspend fun render(index: Int, width: Int): Bitmap? = withContext(Dispatchers.IO) {
        lock.withLock {
            runCatching {
                renderer.openPage(index).use { page ->
                    val w = width.coerceAtLeast(1)
                    val h = (w.toLong() * page.height / page.width.coerceAtLeast(1)).toInt().coerceIn(1, w * 4)
                    Bitmap.createBitmap(w, h, Bitmap.Config.ARGB_8888).also { bmp ->
                        bmp.eraseColor(Color.WHITE) // scanned pages often have no background of their own
                        page.render(bmp, null, null, PdfRenderer.Page.RENDER_MODE_FOR_DISPLAY)
                    }
                }
            }.getOrNull()
        }
    }

    override fun close() {
        runCatching { renderer.close() }
        runCatching { fd.close() }
    }
}

// A zip of images, pages in name order ("001.jpg", "002.jpg"…, natural order so "10" comes after "9").
private class CbzDoc(file: File) : PageDoc {
    private val zip = ZipFile(file)
    private val pages = zip.entries().asSequence().filter { !it.isDirectory && isImage(it.name) }.map { it.name }
        .sortedWith(compareBy<String> { it.substringBeforeLast('/') }.thenComparator { a, b -> natural(a, b) }).toList()
    private val lock = Mutex()
    override val pageCount = pages.size

    override suspend fun render(index: Int, width: Int): Bitmap? = withContext(Dispatchers.IO) {
        lock.withLock {
            runCatching {
                val entry = zip.getEntry(pages[index])
                val bounds = BitmapFactory.Options().apply { inJustDecodeBounds = true }
                zip.getInputStream(entry).use { BitmapFactory.decodeStream(it, null, bounds) }
                var sample = 1
                while (bounds.outWidth / (sample * 2) >= width) sample *= 2
                zip.getInputStream(entry).use { BitmapFactory.decodeStream(it, null, BitmapFactory.Options().apply { inSampleSize = sample }) }
            }.getOrNull()
        }
    }

    override fun close() { runCatching { zip.close() } }

    companion object {
        fun isImage(name: String) = name.substringAfterLast('.').lowercase() in setOf("jpg", "jpeg", "png", "webp", "gif") &&
            !name.substringAfterLast('/').startsWith(".") && "__MACOSX" !in name

        private fun natural(a: String, b: String): Int {
            val ra = Regex("\\d+|\\D+").findAll(a.lowercase()).map { it.value }.toList()
            val rb = Regex("\\d+|\\D+").findAll(b.lowercase()).map { it.value }.toList()
            for (i in 0 until minOf(ra.size, rb.size)) {
                val x = ra[i]; val y = rb[i]
                val c = if (x[0].isDigit() && y[0].isDigit()) (x.toBigIntegerOrNull() ?: 0.toBigInteger()).compareTo(y.toBigIntegerOrNull() ?: 0.toBigInteger()) else x.compareTo(y)
                if (c != 0) return c
            }
            return ra.size - rb.size
        }
    }
}
