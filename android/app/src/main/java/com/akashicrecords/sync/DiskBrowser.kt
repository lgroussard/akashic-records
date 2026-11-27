package com.akashicrecords.sync

import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxHeight
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.asImageBitmap
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.compose.ui.window.Dialog
import androidx.compose.ui.window.DialogProperties
import com.akashicrecords.Palette
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import org.json.JSONObject

// The server's disk folder by folder (Serveur → "Parcourir les dossiers"), as the mini-PC last measured it: every
// night, or when asked here. Read-only. Same colours and layout as the PC's browser in ServerWindow. Back goes up.

private val BrowseWarn = Color(0xFFF87171)

private val folderColors = listOf(
    Color(0xFF3B82F6), Color(0xFFF472B6), Color(0xFFF59E0B), Color(0xFF8B5CF6), Color(0xFF14B8A6),
    Color(0xFFEF4444), Color(0xFF84CC16), Color(0xFF06B6D4), Color(0xFFE879F9), Color(0xFFFB923C),
)

@Composable
internal fun DiskBrowser(path: String, onPath: (String?) -> Unit, onClose: () -> Unit) {
    val context = LocalContext.current
    val scope = rememberCoroutineScope()
    var view by remember { mutableStateOf<JSONObject?>(null) }
    var error by remember { mutableStateOf<String?>(null) }

    suspend fun load(p: String, refresh: Boolean = false) {
        val creds = SyncService.credentials(context) ?: return
        runCatching {
            withContext(Dispatchers.IO) {
                val hub = HubClient(creds.first, creds.second)
                if (refresh) hub.refreshServerDisk()
                hub.serverDisk(p)
            }
        }.onSuccess { view = it; error = null; if (it.optString("path") != p) onPath(it.optString("path")) }
            .onFailure { error = "Serveur injoignable : ${it.message}" }
    }
    LaunchedEffect(path) { load(path) }
    // While a new measure runs, look again now and then.
    val refreshing = view?.optBoolean("refreshing") == true
    LaunchedEffect(refreshing) {
        while (refreshing) { delay(15_000); load(path) }
    }
    val parent = path.trimEnd('/').substringBeforeLast('/', "").ifEmpty { "/" }
    val up = { if (path == "/" || path.isEmpty()) onPath(null) else onPath(parent) }

    Dialog(onDismissRequest = { up() }, properties = DialogProperties(usePlatformDefaultWidth = false)) {
        Column(Modifier.fillMaxSize().background(Palette.AppBg)) {
            Row(Modifier.fillMaxWidth().padding(horizontal = 4.dp), verticalAlignment = Alignment.CenterVertically) {
                TextButton(onClick = { up() }) { Text("‹ Retour") }
                Text(
                    if (path == "/") "Disque du serveur" else path.substringAfterLast('/'), fontWeight = FontWeight.Bold,
                    textAlign = TextAlign.Center, maxLines = 1, overflow = TextOverflow.Ellipsis, modifier = Modifier.weight(1f),
                )
                TextButton(onClick = onClose) { Text("Fermer") }
            }
            val v = view
            Column(Modifier.fillMaxSize().verticalScroll(rememberScrollState()).padding(horizontal = 14.dp)) {
                Text(path, fontSize = 11.sp, color = Palette.TextFaint, modifier = Modifier.padding(bottom = 8.dp))
                error?.let { Text(it, fontSize = 12.sp, color = BrowseWarn, modifier = Modifier.padding(bottom = 8.dp)) }
                if (v == null) {
                    Text("Lecture…", color = Palette.TextMuted)
                    return@Column
                }
                if (v.optString("state") == "none") {
                    Text(
                        if (refreshing) "Première mesure du disque en cours : quelques minutes. L'écran se mettra à jour tout seul."
                        else "Le disque n'a pas encore été mesuré.",
                        color = Palette.TextMuted, modifier = Modifier.padding(vertical = 8.dp),
                    )
                    if (!refreshing) TextButton(onClick = { scope.launch { load(path, refresh = true) } }) { Text("Mesurer maintenant") }
                    return@Column
                }
                val total = v.optLong("size")
                val folders = v.optJSONArray("folders")?.let { a -> (0 until a.length()).map { a.getJSONObject(it) } }.orEmpty()
                val largest = v.optJSONArray("largest")?.let { a -> (0 until a.length()).map { a.getJSONObject(it) } }.orEmpty()

                Card("CE DOSSIER") {
                    Text("${size(total)} · ${v.optLong("files")} fichiers", fontWeight = FontWeight.Bold)
                    if (v.optBoolean("catalogue"))
                        Text("Les fichiers d'Akashic, rangés comme dans l'app.", fontSize = 12.sp, color = Palette.TextMuted)
                    // The graph: what fills this folder, biggest first.
                    val loose = largest.sumOf { it.optLong("size") } + v.optLong("other_files_bytes") + v.optLong("small_folders_bytes")
                    if (total > 0) Row(Modifier.fillMaxWidth().padding(vertical = 10.dp).height(16.dp).clip(RoundedCornerShape(4.dp)).background(Palette.Surface1)) {
                        folders.forEachIndexed { i, f ->
                            val s = f.optLong("size")
                            if (s > 0) Box(Modifier.weight(s.toFloat()).fillMaxHeight().background(folderColors[i % folderColors.size]))
                        }
                        if (loose > 0) Box(Modifier.weight(loose.toFloat()).fillMaxHeight().background(Palette.TextFaint))
                    }
                    Text(
                        "Mesuré ${v.str("at")?.let(::whenText) ?: "?"}" + if (refreshing) " · nouvelle mesure en cours…" else "",
                        fontSize = 11.sp, color = Palette.TextFaint,
                    )
                    if (!refreshing) TextButton(onClick = { scope.launch { load(path, refresh = true) } }) { Text("↻ Mesurer à nouveau") }
                }

                if (folders.isNotEmpty()) Card("DOSSIERS") {
                    folders.forEachIndexed { i, f ->
                        val s = f.optLong("size")
                        val mount = f.optBoolean("mount")
                        val denied = f.optBoolean("denied")
                        val open = f.optBoolean("has_children") && !mount && !denied
                        val note = when { mount -> "autre disque, non mesuré"; denied -> "illisible"; else -> size(s) }
                        val target = (if (path == "/") "" else path) + "/" + f.optString("name")
                        BrowseRow(
                            color = if (s > 0) folderColors[i % folderColors.size] else Palette.TextFaint,
                            name = f.optString("name"), value = note, share = if (total > 0) s.toFloat() / total else 0f,
                            onClick = if (open) ({ onPath(target) }) else null,
                        )
                    }
                    val small = v.optLong("small_folders")
                    if (small > 0) Text("+ $small petits dossiers : ${size(v.optLong("small_folders_bytes"))}", fontSize = 11.sp, color = Palette.TextFaint)
                }

                if (largest.isNotEmpty()) Card("PLUS GROS FICHIERS") {
                    largest.forEach { f ->
                        FileRow(f)
                    }
                    val other = v.optLong("other_files_bytes")
                    if (other > 0) Text("+ les autres fichiers : ${size(other)}", fontSize = 11.sp, color = Palette.TextFaint)
                }
                Text(
                    "Lecture seule. Le mini-PC mesure son disque chaque nuit, ou quand tu le demandes.",
                    fontSize = 11.sp, color = Palette.TextFaint, modifier = Modifier.padding(vertical = 12.dp),
                )
            }
        }
    }
}

@Composable
private fun BrowseRow(color: Color, name: String, value: String, share: Float, onClick: (() -> Unit)?) {
    Column(
        Modifier.fillMaxWidth().clip(RoundedCornerShape(6.dp))
            .then(if (onClick != null) Modifier.clickable(onClick = onClick) else Modifier)
            .padding(vertical = 6.dp, horizontal = 4.dp),
    ) {
        Row(verticalAlignment = Alignment.CenterVertically) {
            Box(Modifier.size(10.dp).clip(RoundedCornerShape(2.dp)).background(color))
            Spacer(Modifier.width(8.dp))
            Text(name, fontSize = 14.sp, maxLines = 1, overflow = TextOverflow.Ellipsis, modifier = Modifier.weight(1f))
            Text(value + if (onClick != null) "  ›" else "", fontSize = 12.sp, color = Palette.TextMuted)
        }
        Box(Modifier.padding(start = 18.dp, top = 4.dp).fillMaxWidth().height(4.dp).clip(RoundedCornerShape(2.dp)).background(Palette.Surface1)) {
            Box(Modifier.fillMaxWidth(share.coerceIn(0.002f, 1f)).fillMaxHeight().background(color))
        }
    }
}

private fun iconOf(kind: String) = when (kind) {
    "image" -> "🖼"; "music" -> "🎵"; "book" -> "📖"; "video" -> "🎬"; "document" -> "📄"; "unknown" -> "🧩"; else -> "📦"
}

// A file: its preview (pictures and album covers of the catalogue, from the hub) or an icon, what it is (its name in
// Akashic for a blob named by its hash), where it is in Akashic, its size.
@Composable
private fun FileRow(f: JSONObject) {
    val context = LocalContext.current
    val kind = f.optString("kind", "other")
    val hash = f.str("hash")
    val label = f.str("label") ?: if (hash == null) f.optString("name") else "Fichier du serveur (hors catalogue)"
    Row(Modifier.fillMaxWidth().padding(vertical = 5.dp), verticalAlignment = Alignment.CenterVertically) {
        Box(Modifier.size(44.dp).clip(RoundedCornerShape(6.dp)).background(Palette.Surface1), contentAlignment = Alignment.Center) {
            Text(iconOf(kind), fontSize = 18.sp)
            if (hash != null && kind in setOf("image", "unknown", "music")) {
                // Over the icon once it arrives; nothing for a file the hub cannot preview.
                var thumb by remember(hash) { mutableStateOf<androidx.compose.ui.graphics.ImageBitmap?>(null) }
                LaunchedEffect(hash) {
                    thumb = withContext(Dispatchers.IO) {
                        com.akashicrecords.archives.HubPhotos.thumb(context, hash)?.let { com.akashicrecords.media.MediaFiles.decode(it, 120) }
                    }?.asImageBitmap()
                }
                thumb?.let { androidx.compose.foundation.Image(it, contentDescription = null, contentScale = androidx.compose.ui.layout.ContentScale.Crop, modifier = Modifier.size(44.dp)) }
            }
        }
        Spacer(Modifier.width(10.dp))
        Column(Modifier.weight(1f)) {
            Text(label.substringAfterLast('/'), fontSize = 13.sp, maxLines = 2, overflow = TextOverflow.Ellipsis)
            val where = if (label.contains('/') && f.str("label") != null) "dans Akashic : " + label.substringBeforeLast('/') else null
            if (where != null) Text(where, fontSize = 11.sp, color = Palette.TextFaint, maxLines = 1, overflow = TextOverflow.Ellipsis)
        }
        Spacer(Modifier.width(8.dp))
        Text(size(f.optLong("size")), fontSize = 12.sp, color = Palette.TextMuted)
    }
}
