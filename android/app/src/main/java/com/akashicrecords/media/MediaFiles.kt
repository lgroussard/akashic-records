package com.akashicrecords.media

import android.content.Context
import android.graphics.Bitmap
import android.graphics.BitmapFactory
import android.net.Uri
import android.util.LruCache
import android.webkit.MimeTypeMap
import androidx.compose.foundation.Image
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Box
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.ImageBitmap
import androidx.compose.ui.graphics.asImageBitmap
import androidx.compose.ui.layout.ContentScale
import androidx.compose.ui.platform.LocalContext
import androidx.compose.material3.Text
import androidx.compose.ui.unit.sp
import com.akashicrecords.Palette
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import java.io.File
import java.util.UUID

// Mobile counterpart of the desktop MediaStorage. The app files dir plays the role of the
// desktop <exe> folder: media lives in <filesDir>/media/<sub>/<guid>.<ext>, and the DB stores
// the same relative form as the desktop ("media\photos\<guid>.jpg"), so paths imported from
// the PC resolve as soon as the matching media files are imported too.
object MediaFiles {
    fun resolve(context: Context, relative: String?): File? {
        if (relative.isNullOrBlank()) return null
        val clean = relative.replace('\\', '/').trimStart('/')
        return File(context.filesDir, clean)
    }

    // Copies a picked image into media/<sub>/ and returns the relative path for the DB.
    fun import(context: Context, source: Uri, sub: String): String {
        val resolver = context.contentResolver
        val ext = resolver.getType(source)
            ?.let { MimeTypeMap.getSingleton().getExtensionFromMimeType(it) }
            ?.let { ".$it" } ?: ".jpg"
        // An early import of the PC's data could leave an empty FILE where this folder goes: moved aside first.
        for (d in listOf(File(context.filesDir, "media"), File(context.filesDir, "media/$sub"))) {
            if (d.isFile) { if (d.length() == 0L) d.delete() else d.renameTo(File(d.path + ".ancien")) }
        }
        val dir = File(context.filesDir, "media/$sub").apply { mkdirs() }
        val name = UUID.randomUUID().toString().replace("-", "") + ext
        resolver.openInputStream(source)!!.use { input ->
            File(dir, name).outputStream().use { input.copyTo(it) }
        }
        return "media\\$sub\\$name"
    }

    fun delete(context: Context, relative: String?) {
        resolve(context, relative)?.takeIf { it.isFile }?.delete()
    }

    // Downsampled decode, like the desktop's DecodePixelWidth.
    fun decode(file: File, maxSide: Int): Bitmap? {
        if (!file.isFile) return null
        val bounds = BitmapFactory.Options().apply { inJustDecodeBounds = true }
        BitmapFactory.decodeFile(file.path, bounds)
        var sample = 1
        while (bounds.outWidth / (sample * 2) >= maxSide || bounds.outHeight / (sample * 2) >= maxSide) sample *= 2
        return BitmapFactory.decodeFile(file.path, BitmapFactory.Options().apply { inSampleSize = sample })
    }

    /**
     * A decode of [file] of at most [maxPixels] pixels (halving the size until it fits), or null when that would
     * not give a long side above [longerThan] (no gain over what is already shown).
     */
    fun decodeWithin(file: File, maxPixels: Long, longerThan: Int): Bitmap? {
        if (!file.isFile) return null
        val bounds = BitmapFactory.Options().apply { inJustDecodeBounds = true }
        BitmapFactory.decodeFile(file.path, bounds)
        if (bounds.outWidth <= 0 || bounds.outHeight <= 0) return null
        var sample = 1
        while ((bounds.outWidth.toLong() / sample) * (bounds.outHeight.toLong() / sample) > maxPixels) sample *= 2
        if (maxOf(bounds.outWidth, bounds.outHeight) / sample <= longerThan) return null
        return BitmapFactory.decodeFile(file.path, BitmapFactory.Options().apply { inSampleSize = sample })
    }
}

// Decoded images kept in memory, so a cover or a photo is read and decoded once instead of every time it
// scrolls back into view or its screen is reopened. Keyed by path + size; the file's modification time rides
// along so a replaced file (same path) is decoded again. Bounded to an eighth of the app's heap (48 MB at
// most): the least recently shown images are dropped first.
private class CachedImage(val image: ImageBitmap, val bytes: Int, val modified: Long)

private val imageCache = object : LruCache<String, CachedImage>(
    (Runtime.getRuntime().maxMemory() / 8).coerceAtMost(48L * 1024 * 1024).toInt(),
) {
    override fun sizeOf(key: String, value: CachedImage) = value.bytes
}

// Shows a media image stored under a desktop-style relative path, or a placeholder when the
// file is not on the phone (e.g. a PC-imported entry whose media were not imported).
@Composable
fun LocalImage(relativePath: String?, modifier: Modifier = Modifier, maxSide: Int = 1024, crop: Boolean = true) {
    val context = LocalContext.current
    FileImage("$relativePath", modifier, maxSide, crop) { MediaFiles.resolve(context, relativePath) }
}

/** Drops the memory-cached images of the file [relativePath] (any size; `\` and `/` alike, case ignored). */
fun forgetCachedImages(relativePath: String) {
    val want = relativePath.replace('\\', '/').trimStart('/').lowercase()
    for (key in imageCache.snapshot().keys) {
        if (key.substringBeforeLast('|').replace('\\', '/').trimStart('/').lowercase() == want) imageCache.remove(key)
    }
}

/** The memory-cached image of [name] at [maxSide], if it was shown recently (no file access). */
fun cachedImage(name: String, maxSide: Int): ImageBitmap? = imageCache.get("$name|$maxSide")?.image

/**
 * [name] at [maxSide] from the memory cache, else read from the file that [file] finds, decoded and cached.
 * Null when there is no such file.
 */
internal suspend fun loadCachedImage(name: String, maxSide: Int, file: suspend () -> File?): ImageBitmap? =
    withContext(Dispatchers.IO) {
        val key = "$name|$maxSide"
        val f = runCatching { file() }.getOrNull()?.takeIf { it.isFile } ?: return@withContext null
        val modified = f.lastModified()
        (imageCache.get(key)?.takeIf { it.modified == modified }
            ?: MediaFiles.decode(f, maxSide)?.let { CachedImage(it.asImageBitmap(), it.byteCount, modified) }
                ?.also { imageCache.put(key, it) })?.image
    }

/**
 * An image read from a file that [file] finds (possibly fetching it first), decoded once and kept in memory
 * under [name]. [placeholder] shows while nothing is decoded yet (null: an empty tile, then "image absente").
 */
@Composable
fun FileImage(
    name: String,
    modifier: Modifier = Modifier,
    maxSide: Int = 1024,
    crop: Boolean = true,
    placeholder: ImageBitmap? = null,
    file: suspend () -> File?,
) {
    val key = "$name|$maxSide"
    // Starts on the cached image when there is one: no placeholder flash, no decode.
    var image by remember(key) { mutableStateOf(imageCache.get(key)?.image) }
    var missing by remember(key) { mutableStateOf(false) }
    LaunchedEffect(key) {
        image = loadCachedImage(name, maxSide, file)
        missing = image == null
    }
    val shown = image ?: placeholder
    if (shown != null) {
        Image(
            shown,
            contentDescription = null,
            contentScale = if (crop) ContentScale.Crop else ContentScale.Fit,
            modifier = modifier,
        )
    } else {
        Box(modifier.background(Palette.Surface3), contentAlignment = Alignment.Center) {
            if (missing) Text("image absente", fontSize = 10.sp, color = Palette.TextFaint)
        }
    }
}
