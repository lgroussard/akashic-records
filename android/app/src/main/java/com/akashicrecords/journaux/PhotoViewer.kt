package com.akashicrecords.journaux

import android.content.ContentValues
import android.content.Context
import android.os.Build
import android.os.Environment
import android.provider.MediaStore
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.pager.HorizontalPager
import androidx.compose.foundation.pager.rememberPagerState
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.*
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.compose.ui.window.Dialog
import androidx.compose.ui.window.DialogProperties
import com.akashicrecords.Palette
import com.akashicrecords.media.LocalImage
import com.akashicrecords.media.MediaFiles
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext

// The photos of an entry shown large, one per page: swipe sideways to go through them, "Enregistrer" puts a
// copy of the shown one in the phone's gallery (Pictures/Akashic). The controls are in the bar above the photo,
// never over it.
@Composable
internal fun PhotoViewer(paths: List<String>, start: Int, onDismiss: () -> Unit) {
    val context = LocalContext.current
    val scope = rememberCoroutineScope()
    val pager = rememberPagerState(initialPage = start.coerceIn(0, (paths.size - 1).coerceAtLeast(0))) { paths.size }
    var message by remember { mutableStateOf<String?>(null) }

    // Before Android 10 the gallery cannot be written to without a storage permission: the user picks the place.
    val saveAs = rememberLauncherForActivityResult(ActivityResultContracts.CreateDocument("image/*")) { uri ->
        if (uri != null) scope.launch {
            message = withContext(Dispatchers.IO) {
                runCatching {
                    val file = requireNotNull(MediaFiles.resolve(context, paths[pager.currentPage]))
                    context.contentResolver.openOutputStream(uri)!!.use { out -> file.inputStream().use { it.copyTo(out) } }
                    "Photo enregistrée."
                }.getOrElse { "Échec : ${it.message}" }
            }
        }
    }

    Dialog(onDismissRequest = onDismiss, properties = DialogProperties(usePlatformDefaultWidth = false)) {
        Column(Modifier.fillMaxSize().background(Palette.AppBg)) {
            Row(Modifier.fillMaxWidth().padding(horizontal = 4.dp), verticalAlignment = Alignment.CenterVertically) {
                TextButton(onClick = onDismiss) { Text("Fermer") }
                Text(
                    message ?: "${pager.currentPage + 1} / ${paths.size}",
                    fontSize = 13.sp,
                    color = Palette.TextMuted,
                    textAlign = TextAlign.Center,
                    modifier = Modifier.weight(1f),
                )
                TextButton(onClick = {
                    val path = paths[pager.currentPage]
                    if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q) {
                        scope.launch {
                            message = withContext(Dispatchers.IO) {
                                runCatching { saveToGallery(context, path); "Enregistrée dans Images/Akashic." }
                                    .getOrElse { "Échec : ${it.message}" }
                            }
                        }
                    } else {
                        saveAs.launch(exportName(path))
                    }
                }) { Text("Enregistrer") }
            }
            // A new page clears the "saved" note of the previous one.
            LaunchedEffect(pager.currentPage) { message = null }
            HorizontalPager(state = pager, modifier = Modifier.fillMaxWidth().weight(1f)) { page ->
                LocalImage(paths[page], Modifier.fillMaxSize(), maxSide = 2048, crop = false)
            }
        }
    }
}

private fun exportName(relativePath: String): String =
    "akashic-" + relativePath.replace('\\', '/').substringAfterLast('/')

private fun mimeOf(name: String): String = when (name.substringAfterLast('.', "").lowercase()) {
    "png" -> "image/png"
    "webp" -> "image/webp"
    "gif" -> "image/gif"
    "heic", "heif" -> "image/heic"
    else -> "image/jpeg"
}

// A copy of the file in the shared Pictures/Akashic folder: visible in the gallery, kept if the app is removed.
private fun saveToGallery(context: Context, relativePath: String) {
    val file = requireNotNull(MediaFiles.resolve(context, relativePath)) { "fichier introuvable" }
    val name = exportName(relativePath)
    val resolver = context.contentResolver
    val values = ContentValues().apply {
        put(MediaStore.Images.Media.DISPLAY_NAME, name)
        put(MediaStore.Images.Media.MIME_TYPE, mimeOf(name))
        put(MediaStore.Images.Media.RELATIVE_PATH, Environment.DIRECTORY_PICTURES + "/Akashic")
        put(MediaStore.Images.Media.IS_PENDING, 1)
    }
    val uri = requireNotNull(resolver.insert(MediaStore.Images.Media.getContentUri(MediaStore.VOLUME_EXTERNAL_PRIMARY), values)) { "galerie indisponible" }
    try {
        resolver.openOutputStream(uri)!!.use { out -> file.inputStream().use { it.copyTo(out) } }
        resolver.update(uri, ContentValues().apply { put(MediaStore.Images.Media.IS_PENDING, 0) }, null, null)
    } catch (e: Exception) {
        resolver.delete(uri, null, null) // no half-written file left in the gallery
        throw e
    }
}
