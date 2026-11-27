package com.akashicrecords.journaux

import android.content.ContentValues
import android.content.Context
import android.os.Build
import android.os.Environment
import android.provider.MediaStore
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Box
import androidx.compose.material3.DropdownMenu
import androidx.compose.material3.DropdownMenuItem
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
import com.akashicrecords.media.ImageEdits
import com.akashicrecords.media.ImageEditor
import com.akashicrecords.media.ConfirmMakeFinal
import com.akashicrecords.media.MediaFiles
import com.akashicrecords.media.ZoomableLocalImage
import com.akashicrecords.media.rememberPageZoomState
import com.akashicrecords.sync.HubCompute
import com.akashicrecords.sync.SyncService
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext

// The photos of an entry shown large, one per page: swipe sideways to go through them, "Enregistrer" puts a
// copy of the shown one in the phone's gallery (Pictures/Akashic). The controls are in the bar above the photo,
// never over it.
@Composable
internal fun PhotoViewer(
    initialPaths: List<String>, start: Int, onDismiss: () -> Unit,
    // The rows the pictures come from (same order as [paths]): with them, and once paired, « ×4 » asks the hub
    // to upscale the shown one (HubCompute.requestFrom); the result arrives next to it at a sync.
    table: String? = null, ids: List<Int>? = null,
    // Told after « Modifier » or « Revenir à l'original » changed a row's picture, so the screen behind reloads.
    onChanged: () -> Unit = {},
) {
    val context = LocalContext.current
    val scope = rememberCoroutineScope()
    // Local copy: an edited picture (a new path) shows here at once.
    var paths by remember(initialPaths) { mutableStateOf(initialPaths) }
    val pager = rememberPagerState(initialPage = start.coerceIn(0, (paths.size - 1).coerceAtLeast(0))) { paths.size }
    var menu by remember { mutableStateOf(false) }
    var editing by remember { mutableStateOf<Int?>(null) } // page being edited
    var message by remember { mutableStateOf<String?>(null) }
    val paired = remember { SyncService.credentials(context) != null }
    var sending by remember { mutableStateOf(false) }
    var zoomed by remember { mutableStateOf(false) } // the shown photo is zoomed in: no swipe to the next
    var finalizing by remember { mutableStateOf<Int?>(null) } // page whose « Rendre définitif » awaits confirmation

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
                val id = ids?.getOrNull(pager.currentPage)
                if (paired && table != null && id != null) TextButton(enabled = !sending, onClick = {
                    sending = true
                    message = "Envoi…"
                    scope.launch {
                        message = HubCompute.upscaleNote(context, table, id.toLong())
                        sending = false
                    }
                }) { Text("Agrandir ×4") }
                // ⋮ : « Modifier » (rotate, crop, contrast, saturation) and, after an edit, « Revenir à l'original ».
                if (table != null && id != null) Box {
                    TextButton(enabled = !sending, onClick = { menu = true }) { Text("⋮", fontSize = 18.sp) }
                    DropdownMenu(expanded = menu, onDismissRequest = { menu = false }) {
                        DropdownMenuItem(text = { Text("Modifier") }, onClick = { menu = false; editing = pager.currentPage })
                        val page = pager.currentPage
                        val path = paths.getOrNull(page)
                        if (ImageEdits.hasPreviousStep(path)) DropdownMenuItem(text = { Text("Annuler la dernière étape") }, onClick = {
                            menu = false
                            sending = true
                            scope.launch {
                                ImageEdits.undo(context, table, id, path!!)
                                    .onSuccess { p -> paths = paths.toMutableList().also { it[page] = p }; onChanged(); message = "Version précédente rétablie." }
                                    .onFailure { message = it.message }
                                sending = false
                            }
                        })
                        if (ImageEdits.isEdited(path)) DropdownMenuItem(text = { Text("Revenir à l'original") }, onClick = {
                            menu = false
                            sending = true
                            scope.launch {
                                ImageEdits.revert(context, table, id, path!!)
                                    .onSuccess { p -> paths = paths.toMutableList().also { it[page] = p }; onChanged(); message = "Original rétabli." }
                                    .onFailure { message = it.message }
                                sending = false
                            }
                        })
                        if (ImageEdits.isEdited(path)) DropdownMenuItem(text = { Text("Rendre définitif") }, onClick = {
                            menu = false
                            finalizing = page
                        })
                    }
                }
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
            // Pinch / double tap to zoom (media/Zoomable.kt); while the shown photo is zoomed a drag moves around it
            // instead of going to the next one.
            HorizontalPager(state = pager, modifier = Modifier.fillMaxWidth().weight(1f), userScrollEnabled = !zoomed) { page ->
                val zoom = rememberPageZoomState(page == pager.currentPage) { zoomed = it }
                ZoomableLocalImage(paths[page], zoom, Modifier.fillMaxSize())
            }
        }
    }
    finalizing?.let { page ->
        val id = ids?.getOrNull(page)
        if (table != null && id != null) ConfirmMakeFinal(
            onConfirm = {
                finalizing = null
                sending = true
                message = "Finalisation…"
                scope.launch {
                    ImageEdits.makeFinal(context, table, id)
                        .onSuccess { p -> paths = paths.toMutableList().also { it[page] = p }; onChanged(); message = "Modification rendue définitive." }
                        .onFailure { message = it.message }
                    sending = false
                }
            },
            onDismiss = { finalizing = null },
        )
    }
    editing?.let { page ->
        val id = ids?.getOrNull(page)
        val path = paths.getOrNull(page)
        if (table != null && id != null && path != null) ImageEditor(
            table = table, id = id, currentPath = path,
            source = { MediaFiles.resolve(context, path) },
            onClose = { editing = null },
            onChanged = { p -> paths = paths.toMutableList().also { it[page] = p }; onChanged() },
        )
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
