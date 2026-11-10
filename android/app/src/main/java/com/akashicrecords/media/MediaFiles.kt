package com.akashicrecords.media

import android.content.Context
import android.graphics.Bitmap
import android.graphics.BitmapFactory
import android.net.Uri
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
}

// Shows a media image stored under a desktop-style relative path, or a placeholder when the
// file is not on the phone (e.g. a PC-imported entry whose media were not imported).
@Composable
fun LocalImage(relativePath: String?, modifier: Modifier = Modifier, maxSide: Int = 1024, crop: Boolean = true) {
    val context = LocalContext.current
    var bitmap by remember(relativePath) { mutableStateOf<Bitmap?>(null) }
    var missing by remember(relativePath) { mutableStateOf(false) }
    LaunchedEffect(relativePath) {
        bitmap = withContext(Dispatchers.IO) {
            MediaFiles.resolve(context, relativePath)?.let { MediaFiles.decode(it, maxSide) }
        }
        missing = bitmap == null
    }
    val bmp = bitmap
    if (bmp != null) {
        Image(
            bmp.asImageBitmap(),
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
