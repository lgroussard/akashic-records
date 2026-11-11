package com.akashicrecords.reading

import android.content.Context
import android.net.Uri
import com.akashicrecords.data.AkashicDatabase
import com.akashicrecords.data.Artwork
import com.akashicrecords.data.Book
import com.akashicrecords.data.BookHighlight
import com.akashicrecords.data.Observation
import com.akashicrecords.data.Reference
import com.akashicrecords.journaux.nowIso
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import java.io.File
import java.util.UUID

// Books on the phone: <filesDir>/books/<uuid>.epub, covers in <filesDir>/books/covers/. Paths are stored relative to
// filesDir with backslashes, like the media paths (MediaFiles.resolve reads both).
object ReadingStore {
    private fun booksDir(context: Context) = File(context.filesDir, "books").apply { mkdirs() }

    fun fileOf(context: Context, book: Book) = File(context.filesDir, book.FilePath.replace('\\', '/'))

    // What the place a book came from knows about it, used when the file itself says nothing useful (no title, an
    // identifier as title, no cover — typical of Internet Archive conversions and of PDFs/CBZs).
    data class Hints(val title: String? = null, val author: String? = null, val coverUrl: String? = null)

    // Copies the picked file in (epub, PDF or CBZ), then as importFile. Its display name is the fallback title.
    suspend fun import(context: Context, db: AkashicDatabase, source: Uri): Book = withContext(Dispatchers.IO) {
        val file = File(booksDir(context), UUID.randomUUID().toString().replace("-", "") + ".tmp")
        context.contentResolver.openInputStream(source)!!.use { input -> file.outputStream().use { input.copyTo(it) } }
        val shown = context.contentResolver.query(source, arrayOf(android.provider.OpenableColumns.DISPLAY_NAME), null, null, null)
            ?.use { c -> if (c.moveToFirst()) c.getString(0) else null }
        importFile(context, db, file, Hints(title = shown?.substringBeforeLast('.')))
    }

    // Downloads a book from the catalogue (or a direct link) straight into the shelf.
    suspend fun download(context: Context, db: AkashicDatabase, hints: Hints, fetch: (File) -> Unit): Book = withContext(Dispatchers.IO) {
        val file = File(booksDir(context), UUID.randomUUID().toString().replace("-", "") + ".tmp")
        try {
            fetch(file)
        } catch (e: Exception) {
            file.delete()
            throw e
        }
        importFile(context, db, file, hints)
    }

    // A title that is really a file name or an archive identifier ("lesmiserables0001hugo", a random id).
    private fun looksLikeId(title: String, file: File) =
        title.isBlank() || title == file.nameWithoutExtension || (' ' !in title.trim() && title.length > 12 && title.any { it.isDigit() })

    // [file] (a .tmp in books/) is recognised from its content, renamed with its extension, its cover extracted
    // (epub cover, or the first page of a PDF/CBZ, else the catalogue's), then the row is added.
    private suspend fun importFile(context: Context, db: AkashicDatabase, tmp: File, hints: Hints): Book {
        val format = BookFormat.sniff(tmp) ?: run {
            tmp.delete()
            throw IllegalArgumentException("Ce fichier n'est ni un epub, ni un PDF, ni une BD (CBZ).")
        }
        val name = tmp.nameWithoutExtension
        val file = File(tmp.parentFile, "$name." + format.name.lowercase()).also { tmp.renameTo(it) }
        val coversDir = File(booksDir(context), "covers").apply { mkdirs() }
        var title: String
        var author: String
        var cover: String? = null

        if (format == BookFormat.EPUB) {
            val epub = try {
                Epub.open(file)
            } catch (e: Exception) {
                file.delete()
                throw IllegalArgumentException(e.message ?: "Ce fichier n'est pas un epub lisible.")
            }
            title = epub.title
            author = epub.author
            cover = epub.coverPath?.let { path ->
                epub.read(path)?.let { bytes ->
                    val ext = path.substringAfterLast('.', "jpg").lowercase().take(4)
                    File(coversDir, "$name.$ext").writeBytes(bytes)
                    "books\\covers\\$name.$ext"
                }
            }
        } else {
            val doc = try {
                PageDoc.open(file)
            } catch (e: Exception) {
                file.delete()
                throw IllegalArgumentException("Ce fichier ne s'ouvre pas : ${e.message}")
            }
            try {
                if (doc.pageCount == 0) throw IllegalArgumentException("Ce fichier ne contient aucune page.")
                cover = doc.render(0, 400)?.let { bmp ->
                    File(coversDir, "$name.jpg").outputStream().use { bmp.compress(android.graphics.Bitmap.CompressFormat.JPEG, 85, it) }
                    "books\\covers\\$name.jpg"
                }
            } catch (e: Exception) {
                file.delete()
                throw e
            } finally {
                doc.close()
            }
            title = ""
            author = ""
        }

        if (looksLikeId(title, file)) title = hints.title?.takeIf { it.isNotBlank() } ?: title.ifBlank { "Sans titre" }
        if (author.isBlank()) author = hints.author.orEmpty()
        if (cover == null) cover = hints.coverUrl?.let { url ->
            fetchBytes(url)?.let { bytes ->
                File(coversDir, "$name.jpg").writeBytes(bytes)
                "books\\covers\\$name.jpg"
            }
        }

        val book = Book(
            Title = title, Author = author, FilePath = "books\\${file.name}", CoverPath = cover,
            AddedAt = nowIso(), LastReadAt = null, Locator = null, Progress = 0.0, ArtworkId = null,
        )
        return book.copy(Id = db.bookDao().add(book).toInt())
    }

    // Small download (a cover), following redirects; null on any failure.
    private fun fetchBytes(start: String): ByteArray? = runCatching {
        var url = start
        repeat(5) {
            val c = java.net.URL(url).openConnection() as java.net.HttpURLConnection
            c.instanceFollowRedirects = false
            c.connectTimeout = 10_000
            c.readTimeout = 20_000
            try {
                val code = c.responseCode
                if (code in 300..399) { url = Catalog.absolute(url, c.getHeaderField("Location") ?: return null); return@repeat }
                return if (code in 200..299) c.inputStream.use { it.readBytes() } else null
            } finally {
                c.disconnect()
            }
        }
        null
    }.getOrNull()

    // Where reading stopped. Overall progress: given by the epub (chapters weighted by their length), else
    // chapters (or pages) done + share of the current one.
    suspend fun savePosition(db: AkashicDatabase, bookId: Int, chapter: Int, progression: Double, chapterCount: Int, overallRead: Double? = null) {
        val book = db.bookDao().getById(bookId) ?: return
        val overall = overallRead?.coerceIn(0.0, 1.0)
            ?: if (chapterCount <= 0) 0.0 else ((chapter + progression.coerceIn(0.0, 1.0)) / chapterCount).coerceIn(0.0, 1.0)
        db.bookDao().update(book.copy(Locator = "$chapter:$progression", Progress = overall, LastReadAt = nowIso()))
    }

    fun parseLocator(locator: String?): Pair<Int, Double> {
        val parts = locator?.split(':') ?: return 0 to 0.0
        return (parts.getOrNull(0)?.toIntOrNull() ?: 0) to (parts.getOrNull(1)?.toDoubleOrNull() ?: 0.0)
    }

    // The book and its highlights go; the observations they fed stay in the Collections fiche (they are notes of
    // their own by then), and so does the fiche.
    suspend fun delete(context: Context, db: AkashicDatabase, book: Book) = withContext(Dispatchers.IO) {
        db.bookHighlightDao().deleteForBook(book.Id)
        db.bookDao().deleteById(book.Id)
        fileOf(context, book).delete()
        book.CoverPath?.let { File(context.filesDir, it.replace('\\', '/')).delete() }
    }
}

// Lecture ↔ Collections: a book has a fiche in Collections → Livres (Artwork, Category 3) and each highlight is an
// observation of that fiche, which can be referenced by artistic projects (Reference), like any other observation.
object ReadingLinks {
    const val LIVRES = 3 // desktop ArtworkCategory.Livre
    private const val DEFAULT_TIER = 2 // B, the desktop's default for a new work

    // The book's fiche: the linked one if it still exists, else a Livres fiche with the same title, else a new one
    // (with the book cover). Stored on the book.
    suspend fun ensureArtwork(context: Context, db: AkashicDatabase, book: Book): Artwork = withContext(Dispatchers.IO) {
        val artworks = db.artworkDao().getAll()
        val found = book.ArtworkId?.let { id -> artworks.firstOrNull { it.Id == id } }
            ?: artworks.firstOrNull { it.Category == LIVRES && it.Title.trim().equals(book.Title.trim(), ignoreCase = true) }
        val artwork = found ?: run {
            val cover = book.CoverPath?.let { copyCover(context, it) }
            val a = Artwork(Title = book.Title, Category = LIVRES, Tier = DEFAULT_TIER, CreatedAt = nowIso(), CoverImagePath = cover, WatchPriority = 0)
            a.copy(Id = db.artworkDao().add(a).toInt())
        }
        if (book.ArtworkId != artwork.Id) db.bookDao().update(book.copy(ArtworkId = artwork.Id))
        artwork
    }

    private fun copyCover(context: Context, relative: String): String? {
        val src = File(context.filesDir, relative.replace('\\', '/')).takeIf { it.isFile } ?: return null
        val name = UUID.randomUUID().toString().replace("-", "") + "." + src.extension
        val dir = File(context.filesDir, "media/covers").apply { mkdirs() }
        src.copyTo(File(dir, name))
        return "media\\covers\\$name"
    }

    fun observationText(h: BookHighlight) = "« ${h.Text.trim()} »" + if (h.Note.isNotBlank()) "\n\n${h.Note.trim()}" else ""

    // New highlight: saved, then mirrored as an observation of the book's fiche.
    suspend fun addHighlight(context: Context, db: AkashicDatabase, book: Book, h: BookHighlight, subject: String): BookHighlight =
        withContext(Dispatchers.IO) {
            val artwork = ensureArtwork(context, db, book)
            val draft = h.copy(Id = db.bookHighlightDao().add(h).toInt())
            val obsId = db.observationDao().add(Observation(ArtworkId = artwork.Id, Subject = subject, Content = observationText(draft), CreatedAt = draft.CreatedAt)).toInt()
            draft.copy(ObservationId = obsId).also { db.bookHighlightDao().update(it) }
        }

    // Note edited: the observation follows.
    suspend fun updateHighlight(db: AkashicDatabase, h: BookHighlight) = withContext(Dispatchers.IO) {
        db.bookHighlightDao().update(h)
        val obsId = h.ObservationId ?: return@withContext
        db.observationDao().getById(obsId)?.let { db.observationDao().update(it.copy(Content = observationText(h))) }
    }

    // Removed from the book: its observation and the project references to it go too.
    suspend fun deleteHighlight(db: AkashicDatabase, h: BookHighlight) = withContext(Dispatchers.IO) {
        h.ObservationId?.let { obsId ->
            db.referenceDao().forObservation(obsId).forEach { db.referenceDao().deleteById(it.Id) }
            db.observationDao().deleteById(obsId)
        }
        db.bookHighlightDao().deleteById(h.Id)
    }

    suspend fun projectsOf(db: AkashicDatabase, h: BookHighlight): Set<Int> = withContext(Dispatchers.IO) {
        h.ObservationId?.let { db.referenceDao().forObservation(it).map { r -> r.ProjectId }.toSet() }.orEmpty()
    }

    suspend fun setProject(db: AkashicDatabase, h: BookHighlight, projectId: Int, linked: Boolean) = withContext(Dispatchers.IO) {
        val obsId = h.ObservationId ?: return@withContext
        val existing = db.referenceDao().forObservation(obsId).filter { it.ProjectId == projectId }
        if (linked && existing.isEmpty()) db.referenceDao().add(Reference(ObservationId = obsId, ProjectId = projectId))
        if (!linked) existing.forEach { db.referenceDao().deleteById(it.Id) }
    }

    // After a PC import refilled Artwork/Observation with the PC's rows (other Ids): each book finds its fiche again
    // (or gets one) and each highlight its observation (or a new one), so nothing written on the phone is lost.
    // Project links of a recreated observation are not recoverable (the Reference rows came from the PC).
    suspend fun relinkAfterImport(context: Context, db: AkashicDatabase) = withContext(Dispatchers.IO) {
        val highlights = db.bookHighlightDao().getAll().groupBy { it.BookId }
        for (book in db.bookDao().getAll()) {
            val bookHighlights = highlights[book.Id].orEmpty()
            val stale = book.ArtworkId?.let { id -> db.artworkDao().getAll().none { it.Id == id && it.Category == LIVRES && it.Title.equals(book.Title, true) } } ?: false
            if (bookHighlights.isEmpty() && (book.ArtworkId == null || !stale)) continue
            val artwork = ensureArtwork(context, db, if (stale) book.copy(ArtworkId = null) else book)
            val observations = db.observationDao().forArtwork(artwork.Id)
            for (h in bookHighlights) {
                val text = observationText(h)
                val match = observations.firstOrNull { it.Id == h.ObservationId && it.Content == text } ?: observations.firstOrNull { it.Content == text }
                val obsId = match?.Id ?: db.observationDao().add(Observation(ArtworkId = artwork.Id, Subject = book.Title, Content = text, CreatedAt = h.CreatedAt)).toInt()
                if (obsId != h.ObservationId) db.bookHighlightDao().update(h.copy(ObservationId = obsId))
            }
        }
    }
}
