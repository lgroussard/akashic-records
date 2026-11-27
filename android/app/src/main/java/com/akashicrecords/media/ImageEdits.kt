package com.akashicrecords.media

import android.content.Context
import android.graphics.Bitmap
import android.graphics.BitmapFactory
import android.graphics.Canvas
import android.graphics.ColorMatrix
import android.graphics.ColorMatrixColorFilter
import android.graphics.ImageDecoder
import android.graphics.Matrix
import android.graphics.Paint
import android.media.ExifInterface
import android.os.Build
import com.akashicrecords.archives.HubPhotos
import com.akashicrecords.data.AkashicDatabase
import com.akashicrecords.sync.AkashicCatalog
import com.akashicrecords.sync.FileSync
import com.akashicrecords.sync.SyncService
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import java.io.File
import kotlin.math.max
import kotlin.math.roundToInt
import kotlin.random.Random

/**
 * Edits of a picture already in the app (rotation, crop, contrast, saturation), shared by the viewers of the
 * Archives photos, the journal photos, the references and the canvas pictures. Same rules as the PC, so an edit
 * made on one device is understood on the others once synced:
 *
 *  - the edited picture is a NEW file in the same folder, `<root>.edit-<8 hex>.jpg`, where `<root>` is the
 *    current name up to its first `.edit-` (else the whole name, extension included): `abc.heic` →
 *    `abc.heic.edit-1a2b3c4d.jpg`, and editing that again → `abc.heic.edit-9f8e7d6c.jpg`;
 *  - the row then points at it; nothing is deleted by an edit, so « Revenir à l'original » (the same folder + `<root>`)
 *    works on every device, until « Rendre définitif » ([makeFinal]) keeps only the current version;
 *  - on save: EXIF orientation → rotation → crop (on the rotated picture) → contrast and saturation, JPEG 92.
 */
object ImageEdits {
    private const val MARK = ".edit-"
    private const val MAX_SIDE = 4096

    /** What the editor asks for. [quarterTurns]: clockwise quarter turns (0..3). The crop is a fraction (0..1)
     * of the rotated picture. [contrast], [saturation]: -100..100, 0 = unchanged. */
    data class Params(
        val quarterTurns: Int,
        val cropLeft: Float, val cropTop: Float, val cropRight: Float, val cropBottom: Float,
        val contrast: Int, val saturation: Int,
    )

    class Saved(val path: String, val width: Int, val height: Int)

    private fun split(path: String): Pair<String, String> {
        val i = max(path.lastIndexOf('\\'), path.lastIndexOf('/'))
        return path.substring(0, i + 1) to path.substring(i + 1)
    }

    private fun root(name: String): String = name.indexOf(MARK).let { if (it >= 0) name.substring(0, it) else name }

    fun isEdited(path: String?): Boolean = path != null && split(path).second.contains(MARK)

    /** The path of the original of an edited picture (same folder, separators kept), null when not edited. */
    fun originalOf(path: String?): String? {
        if (!isEdited(path)) return null
        val (dir, name) = split(path!!)
        return dir + root(name)
    }

    /** A new edit path next to [path], in the same separator style, not taken yet on this phone. */
    fun newEditPath(context: Context, path: String): String {
        val (dir, name) = split(path)
        while (true) {
            val candidate = dir + root(name) + MARK + "%08x".format(Random.nextLong(0, 1L shl 32)) + ".jpg"
            if (MediaFiles.resolve(context, candidate)?.exists() != true) return candidate
        }
    }

    private fun exifTurn(file: File): Matrix? {
        val o = runCatching { ExifInterface(file.path).getAttributeInt(ExifInterface.TAG_ORIENTATION, ExifInterface.ORIENTATION_NORMAL) }
            .getOrDefault(ExifInterface.ORIENTATION_NORMAL)
        val m = Matrix()
        when (o) {
            ExifInterface.ORIENTATION_ROTATE_90 -> m.postRotate(90f)
            ExifInterface.ORIENTATION_ROTATE_180 -> m.postRotate(180f)
            ExifInterface.ORIENTATION_ROTATE_270 -> m.postRotate(270f)
            ExifInterface.ORIENTATION_FLIP_HORIZONTAL -> m.postScale(-1f, 1f)
            ExifInterface.ORIENTATION_FLIP_VERTICAL -> m.postScale(1f, -1f)
            ExifInterface.ORIENTATION_TRANSPOSE -> { m.postRotate(90f); m.postScale(-1f, 1f) }
            ExifInterface.ORIENTATION_TRANSVERSE -> { m.postRotate(270f); m.postScale(-1f, 1f) }
            else -> return null
        }
        return m
    }

    /** The picture upright (EXIF orientation applied), its long side at most [maxSide]. */
    fun decodeUpright(file: File, maxSide: Int): Bitmap? {
        if (!file.isFile) return null
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.P) {
            return runCatching {
                ImageDecoder.decodeBitmap(ImageDecoder.createSource(file)) { decoder, info, _ ->
                    decoder.allocator = ImageDecoder.ALLOCATOR_SOFTWARE
                    val w = info.size.width
                    val h = info.size.height
                    val long = max(w, h)
                    if (long > maxSide) {
                        val s = maxSide.toFloat() / long
                        decoder.setTargetSize((w * s).roundToInt().coerceAtLeast(1), (h * s).roundToInt().coerceAtLeast(1))
                    }
                }
            }.getOrNull() ?: decodeLegacy(file, maxSide)
        }
        return decodeLegacy(file, maxSide)
    }

    private fun decodeLegacy(file: File, maxSide: Int): Bitmap? {
        val bounds = BitmapFactory.Options().apply { inJustDecodeBounds = true }
        BitmapFactory.decodeFile(file.path, bounds)
        if (bounds.outWidth <= 0 || bounds.outHeight <= 0) return null
        var sample = 1
        while (max(bounds.outWidth, bounds.outHeight) / (sample * 2) >= maxSide) sample *= 2
        var bmp = BitmapFactory.decodeFile(file.path, BitmapFactory.Options().apply { inSampleSize = sample }) ?: return null
        val long = max(bmp.width, bmp.height)
        val m = exifTurn(file) ?: Matrix()
        if (long > maxSide) m.postScale(maxSide.toFloat() / long, maxSide.toFloat() / long)
        if (!m.isIdentity) {
            val turned = Bitmap.createBitmap(bmp, 0, 0, bmp.width, bmp.height, m, true)
            if (turned !== bmp) bmp.recycle()
            bmp = turned
        }
        return bmp
    }

    /** Pixel size of a file as stored (no orientation), like ReferenceStore records it; null if unreadable. */
    fun storedSize(file: File?): Pair<Int, Int>? {
        if (file == null || !file.isFile) return null
        val b = BitmapFactory.Options().apply { inJustDecodeBounds = true }
        BitmapFactory.decodeFile(file.path, b)
        if (b.outWidth > 0 && b.outHeight > 0) return b.outWidth to b.outHeight
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.P) {
            // HEIC and the like: read the header only.
            var size: Pair<Int, Int>? = null
            runCatching {
                ImageDecoder.decodeDrawable(ImageDecoder.createSource(file)) { d, info, _ ->
                    size = info.size.width to info.size.height
                    d.setTargetSize(1, 1)
                }
            }
            return size
        }
        return null
    }

    /** Pixel size as shown (width and height swapped when the EXIF orientation turns the picture a quarter). */
    fun uprightSize(file: File?): Pair<Int, Int>? {
        val (w, h) = storedSize(file) ?: return null
        val o = runCatching { ExifInterface(file!!.path).getAttributeInt(ExifInterface.TAG_ORIENTATION, ExifInterface.ORIENTATION_NORMAL) }
            .getOrDefault(ExifInterface.ORIENTATION_NORMAL)
        val quarter = o == ExifInterface.ORIENTATION_ROTATE_90 || o == ExifInterface.ORIENTATION_ROTATE_270 ||
            o == ExifInterface.ORIENTATION_TRANSPOSE || o == ExifInterface.ORIENTATION_TRANSVERSE
        return if (quarter) h to w else w to h
    }

    /** Contrast (around mid-grey) and saturation in one matrix, for the preview and the saved file alike. */
    fun colorMatrix(contrast: Int, saturation: Int): ColorMatrix {
        val cm = ColorMatrix().apply { setSaturation(1f + saturation / 100f) }
        val f = 1f + contrast / 100f
        val t = 128f * (1f - f)
        cm.postConcat(ColorMatrix(floatArrayOf(
            f, 0f, 0f, 0f, t,
            0f, f, 0f, 0f, t,
            0f, 0f, f, 0f, t,
            0f, 0f, 0f, 1f, 0f,
        )))
        return cm
    }

    /** Writes the edited picture of [source] next to [currentPath] and returns its path and pixel size. */
    suspend fun save(context: Context, source: File, currentPath: String, p: Params): Saved = withContext(Dispatchers.IO) {
        val src = try {
            decodeUpright(source, MAX_SIDE)
        } catch (e: OutOfMemoryError) {
            System.gc()
            decodeUpright(source, 2560)
        } ?: error("Image illisible.")
        try {
            val turns = ((p.quarterTurns % 4) + 4) % 4
            val rw = if (turns % 2 == 1) src.height else src.width
            val rh = if (turns % 2 == 1) src.width else src.height
            val cl = (p.cropLeft.coerceIn(0f, 1f) * rw).roundToInt().coerceIn(0, rw - 1)
            val ct = (p.cropTop.coerceIn(0f, 1f) * rh).roundToInt().coerceIn(0, rh - 1)
            val cr = (p.cropRight.coerceIn(0f, 1f) * rw).roundToInt().coerceIn(cl + 1, rw)
            val cb = (p.cropBottom.coerceIn(0f, 1f) * rh).roundToInt().coerceIn(ct + 1, rh)
            val outW = cr - cl
            val outH = cb - ct
            // Rotation and crop as one transform, then the colour matrix, in a single draw.
            val m = Matrix()
            when (turns) {
                1 -> { m.postRotate(90f); m.postTranslate(src.height.toFloat(), 0f) }
                2 -> { m.postRotate(180f); m.postTranslate(src.width.toFloat(), src.height.toFloat()) }
                3 -> { m.postRotate(270f); m.postTranslate(0f, src.width.toFloat()) }
            }
            m.postTranslate(-cl.toFloat(), -ct.toFloat())
            val out = Bitmap.createBitmap(outW, outH, Bitmap.Config.ARGB_8888)
            try {
                val paint = Paint(Paint.FILTER_BITMAP_FLAG or Paint.ANTI_ALIAS_FLAG)
                if (p.contrast != 0 || p.saturation != 0) paint.colorFilter = ColorMatrixColorFilter(colorMatrix(p.contrast, p.saturation))
                Canvas(out).apply {
                    drawColor(android.graphics.Color.WHITE) // a transparent PNG gets a white ground in the JPEG
                    drawBitmap(src, m, paint)
                }
                val newPath = newEditPath(context, currentPath)
                val target = requireNotNull(MediaFiles.resolve(context, newPath))
                target.parentFile?.mkdirs()
                val part = File(target.path + ".part")
                part.outputStream().use { if (!out.compress(Bitmap.CompressFormat.JPEG, 92, it)) error("Échec de l'encodage.") }
                if (!part.renameTo(target)) { part.delete(); error("Impossible d'écrire le fichier.") }
                Saved(newPath, outW, outH)
            } finally {
                out.recycle()
            }
        } finally {
            src.recycle()
        }
    }

    /** Points the row at [saved] (sizes updated where the row keeps them), then lets the sync send it. */
    suspend fun apply(context: Context, table: String, id: Int, saved: Saved) = withContext(Dispatchers.IO) {
        val db = AkashicDatabase.get(context)
        setPath(db, table, id, saved.path, saved.width to saved.height)
        // The new file stays on this phone (an Archives photo is otherwise only on the hub) until the sync sends it.
        if (table == "Photo") FileSync.setKeep(db.openHelper.writableDatabase, saved.path, true)
        SyncService.runNow(context)
    }

    /**
     * Puts the original back (the edits stay on disk). Returns the original's path, or the message to show
     * when it cannot be done here. An Archives photo's original may be only on the hub: the viewer fetches it.
     */
    suspend fun revert(context: Context, table: String, id: Int, currentPath: String): Result<String> = withContext(Dispatchers.IO) {
        val original = originalOf(currentPath) ?: return@withContext Result.failure(IllegalStateException("Cette image n'a pas été modifiée."))
        val file = MediaFiles.resolve(context, original)?.takeIf { it.isFile }
        if (file == null && table != "Photo") {
            return@withContext Result.failure(IllegalStateException("L'original n'est pas encore sur ce téléphone : synchronise puis réessaie."))
        }
        setPath(AkashicDatabase.get(context), table, id, original, uprightSize(file))
        SyncService.runNow(context)
        Result.success(original)
    }

    /** The confirmation shown before [makeFinal]. */
    const val FINAL_QUESTION = "Rendre cette modification définitive ? L'original et les versions précédentes seront supprimés " +
        "de ce téléphone, et « Revenir à l'original » ne sera plus possible."

    /** The columns that may point at a picture: a file one of them names is never deleted by [makeFinal]. */
    private val imageColumns = (
        listOf(
            "Photo" to "ImagePath", "JournalPhoto" to "ImagePath", "RefImage" to "ImagePath", "CanvasElement" to "ImagePath",
            "Recipe" to "CoverImagePath", "Artwork" to "CoverImagePath", "Poem" to "ImagePath",
        ) + AkashicCatalog.fileColumns
        ).distinct()

    private fun pathKey(path: String) = path.replace('\\', '/').trimStart('/').lowercase()

    /** Every path the database still names (normalised by [pathKey]). */
    private fun referencedKeys(db: AkashicDatabase): Set<String> {
        val sql = db.openHelper.readableDatabase
        val keys = HashSet<String>()
        for ((table, column) in imageColumns) {
            val exists = runCatching {
                sql.query("SELECT COUNT(*) FROM pragma_table_info(?) WHERE name = ?", arrayOf<Any?>(table, column))
                    .use { c -> c.moveToFirst() && c.getLong(0) > 0 }
            }.getOrDefault(false)
            if (!exists) continue
            sql.query("SELECT `$column` FROM `$table` WHERE `$column` IS NOT NULL AND `$column` <> ''").use { c ->
                while (c.moveToNext()) keys.add(pathKey(c.getString(0)))
            }
        }
        return keys
    }

    private fun currentPath(db: AkashicDatabase, table: String, id: Int): String? {
        require(table in listOf("Photo", "JournalPhoto", "RefImage", "CanvasElement")) { "Table inconnue : $table" }
        return db.openHelper.readableDatabase.query("SELECT ImagePath FROM `$table` WHERE Id = ?", arrayOf<Any?>(id))
            .use { c -> if (c.moveToFirst() && !c.isNull(0)) c.getString(0) else null }
    }

    /**
     * « Rendre définitif » (same rules as the PC): the current edit is copied to a clean name in the same folder,
     * `<base>-<8 hex>.jpg` (`<base>` = `<root>` without its extension: `abc.heic` → `abc-1a2b3c4d.jpg`), the row
     * points at it (sizes unchanged), then the original and every `<root>.edit-*` are deleted from THIS phone —
     * only the files no row names any more. Returns the new path, or the message to show.
     */
    suspend fun makeFinal(context: Context, table: String, id: Int): Result<String> = withContext(Dispatchers.IO) {
        runCatching {
            val db = AkashicDatabase.get(context)
            val path = currentPath(db, table, id) ?: error("Image introuvable.")
            if (!isEdited(path)) error("Cette image n'a pas été modifiée.")
            val (dir, name) = split(path)
            val root = root(name)
            // The edited file: here, or (an Archives photo only on the hub) fetched like the viewer does.
            val source = MediaFiles.resolve(context, path)?.takeIf { it.isFile }
                ?: if (table == "Photo") db.photoDao().getById(id)?.let { p ->
                    HubPhotos.original(context, p, HubPhotos.load(db).hashes[HubPhotos.key(p)])
                }?.takeIf { it.isFile } else null
            source ?: error("L'image modifiée n'est pas sur ce téléphone (hors ligne ?) : synchronise puis réessaie.")

            val base = root.lastIndexOf('.').let { if (it > 0) root.substring(0, it) else root }
            var newPath: String
            var target: File
            do {
                newPath = dir + base + "-" + "%08x".format(Random.nextLong(0, 1L shl 32)) + ".jpg"
                target = requireNotNull(MediaFiles.resolve(context, newPath))
            } while (target.exists())
            target.parentFile?.mkdirs()
            val part = File(target.path + ".part")
            source.inputStream().use { input -> part.outputStream().use { input.copyTo(it) } }
            if (!part.renameTo(target)) { part.delete(); error("Impossible d'écrire le fichier.") }

            setPath(db, table, id, newPath, null)
            if (table == "Photo") FileSync.setKeep(db.openHelper.writableDatabase, newPath, true)

            // The original and the earlier edits, when nothing points at them any more.
            val folder = MediaFiles.resolve(context, dir + root)?.parentFile
            val referenced = referencedKeys(db)
            val newKey = pathKey(newPath)
            folder?.listFiles()?.forEach { f ->
                if (!f.isFile || !(f.name == root || f.name.startsWith(root + MARK))) return@forEach
                val rel = dir + f.name
                if (pathKey(rel) == newKey || pathKey(rel) in referenced) return@forEach
                if (f.delete()) forgetCachedImages(rel)
            }
            SyncService.runNow(context)
            newPath
        }
    }

    private suspend fun setPath(db: AkashicDatabase, table: String, id: Int, path: String, size: Pair<Int, Int>?) {
        when (table) {
            "Photo" -> db.photoDao().setImagePath(id, path)
            "JournalPhoto" -> db.journalPhotoDao().setImagePath(id, path)
            "RefImage" -> db.refImageDao().getById(id)?.let { r ->
                db.refImageDao().update(
                    if (size != null) r.copy(ImagePath = path, Width = size.first, Height = size.second) else r.copy(ImagePath = path),
                )
            }
            "CanvasElement" -> db.canvasElementDao().getById(id)?.let { e ->
                // The display width stays; the height follows the new picture's proportions.
                val w = e.Width
                val h = if (w != null && size != null && size.first > 0) w * size.second / size.first else e.Height
                db.canvasElementDao().update(e.copy(ImagePath = path, Height = h))
            }
            else -> error("Table inconnue : $table")
        }
    }
}
