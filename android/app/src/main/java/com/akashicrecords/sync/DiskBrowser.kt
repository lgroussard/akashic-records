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
internal fun DiskBrowser(path: String, onPath: (String?) -> Unit, onClose: () -> Unit, phone: Boolean = false) {
    val context = LocalContext.current
    val scope = rememberCoroutineScope()
    var view by remember { mutableStateOf<JSONObject?>(null) }
    var error by remember { mutableStateOf<String?>(null) }
    // « Ce téléphone » needs Android's all-files access: asked once, looked at again on the way back from the settings.
    var access by remember { mutableStateOf(!phone || PhoneDisk.hasAccess(context)) }
    LaunchedEffect(access) { while (!access) { delay(1_000); access = PhoneDisk.hasAccess(context) } }

    suspend fun load(p: String, refresh: Boolean = false) {
        if (phone) {
            if (!access) return
            val v = withContext(Dispatchers.IO) {
                if (refresh) PhoneDisk.startMeasure(context)
                var v = PhoneDisk.browse(context, p)
                // Never measured yet: measure now (the count of files seen shows meanwhile).
                if (v.optString("state") == "none" && !PhoneDisk.measuring) { PhoneDisk.startMeasure(context); v = v.put("refreshing", true) }
                v
            }
            view = v; error = null
            if (v.optString("path") != p) onPath(v.optString("path"))
            return
        }
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
    LaunchedEffect(path, access) { load(path) }
    // This phone being measured: the count, every second, then the result.
    var count by remember { mutableStateOf(0L) }
    LaunchedEffect(phone && view?.optBoolean("refreshing") == true) {
        while (phone && view?.optBoolean("refreshing") == true) {
            delay(1_000)
            count = PhoneDisk.filesSeen
            if (!PhoneDisk.measuring) load(path)
        }
    }
    // While a new measure runs, look again now and then.
    val refreshing = view?.optBoolean("refreshing") == true
    LaunchedEffect(refreshing) {
        while (refreshing && !phone) { delay(15_000); load(path) }
    }
    val parent = path.trimEnd('/').substringBeforeLast('/', "").ifEmpty { "/" }
    val up = { if (path == "/" || path.isEmpty()) onPath(null) else onPath(parent) }

    Dialog(onDismissRequest = { up() }, properties = DialogProperties(usePlatformDefaultWidth = false)) {
        Column(Modifier.fillMaxSize().background(Palette.AppBg)) {
            Row(Modifier.fillMaxWidth().padding(horizontal = 4.dp), verticalAlignment = Alignment.CenterVertically) {
                TextButton(onClick = { up() }) { Text("‹ Retour") }
                Text(
                    if (path == "/") (if (phone) "Ce téléphone" else "Disque du serveur") else path.substringAfterLast('/'), fontWeight = FontWeight.Bold,
                    textAlign = TextAlign.Center, maxLines = 1, overflow = TextOverflow.Ellipsis, modifier = Modifier.weight(1f),
                )
                TextButton(onClick = onClose) { Text("Fermer") }
            }
            val v = view
            Column(Modifier.fillMaxSize().verticalScroll(rememberScrollState()).padding(horizontal = 14.dp)) {
                Text(path, fontSize = 11.sp, color = Palette.TextFaint, modifier = Modifier.padding(bottom = 8.dp))
                error?.let { Text(it, fontSize = 12.sp, color = BrowseWarn, modifier = Modifier.padding(bottom = 8.dp)) }
                if (!access) {
                    Card("ACCÈS AUX FICHIERS") {
                        Text(
                            "Pour mesurer tout le téléphone, Android demande de donner à Akashic « l'accès à tous les fichiers ». " +
                                "Akashic ne fait que lire les tailles et les noms, rien n'est modifié ni envoyé.",
                            fontSize = 13.sp, color = Palette.TextMuted,
                        )
                        androidx.compose.material3.Button(onClick = { PhoneDisk.askAccess(context) }, modifier = Modifier.padding(top = 10.dp)) {
                            Text("Ouvrir le réglage")
                        }
                    }
                    return@Column
                }
                if (phone && refreshing)
                    Text(
                        "Mesure du téléphone en cours : $count fichiers vus. Une minute ou deux, l'écran se met à jour tout seul.",
                        fontSize = 13.sp, color = Color(0xFFFBBF24), modifier = Modifier.padding(bottom = 8.dp),
                    )
                if (v == null) {
                    Text("Lecture…", color = Palette.TextMuted)
                    return@Column
                }
                if (v.optString("state") == "none") {
                    if (phone) return@Column
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
                    if (phone) {
                        val t = v.optLong("disk_total"); val fr = v.optLong("disk_free")
                        if (t > 0) Text("Stockage : ${size(t - fr)} utilisés sur ${size(t)} · ${size(fr)} libres", fontSize = 12.sp, color = if (fr < t / 10) BrowseWarn else Palette.TextMuted)
                    }
                    if (v.optBoolean("catalogue") && v.optString("path").endsWith("hors catalogue"))
                        Text("Fichiers que le serveur garde sans fiche dans l'app. Ceux que plus rien n'utilise sont effacés 30 jours après.", fontSize = 12.sp, color = Palette.TextMuted)
                    else if (v.optBoolean("catalogue"))
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
                        val note = when { mount -> "autre disque, non mesuré"; denied -> if (s > 0) size(s) + " · non lisible" else "illisible"; else -> size(s) }
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
                        FileRow(f, local = phone)
                    }
                    val other = v.optLong("other_files_bytes")
                    if (other > 0) Text("+ les autres fichiers : ${size(other)}", fontSize = 11.sp, color = Palette.TextFaint)
                }
                Text(
                    if (phone) "Lecture seule. « ${PhoneDisk.UNSEEN} » : ce qu'Android ne laisse lire à aucune appli. Le dossier Android/data est fermé de la même façon."
                    else "Lecture seule. Le mini-PC mesure son disque chaque nuit, ou quand tu le demandes.",
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
private fun FileRow(f: JSONObject, local: Boolean = false) {
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
            } else if (local && kind == "image") {
                // A picture of this phone: read from its file, small.
                var thumb by remember(label) { mutableStateOf<androidx.compose.ui.graphics.ImageBitmap?>(null) }
                LaunchedEffect(label) {
                    thumb = withContext(Dispatchers.IO) { runCatching { com.akashicrecords.media.MediaFiles.decode(java.io.File(label), 120) }.getOrNull() }?.asImageBitmap()
                }
                thumb?.let { androidx.compose.foundation.Image(it, contentDescription = null, contentScale = androidx.compose.ui.layout.ContentScale.Crop, modifier = Modifier.size(44.dp)) }
            }
        }
        Spacer(Modifier.width(10.dp))
        Column(Modifier.weight(1f)) {
            Text(label.substringAfterLast('/'), fontSize = 13.sp, maxLines = 2, overflow = TextOverflow.Ellipsis)
            val where = if (!local && label.contains('/') && f.str("label") != null) "dans Akashic : " + label.substringBeforeLast('/') else null
            if (where != null) Text(where, fontSize = 11.sp, color = Palette.TextFaint, maxLines = 1, overflow = TextOverflow.Ellipsis)
            // Waiting to be removed for good (the hub's clean-up), or kept for shared computing.
            f.str("note")?.let { note ->
                Text(note, fontSize = 11.sp, color = if (note.startsWith("Supprimé")) Color(0xFFFBBF24) else Palette.TextMuted, maxLines = 1, overflow = TextOverflow.Ellipsis)
            }
        }
        Spacer(Modifier.width(8.dp))
        Text(size(f.optLong("size")), fontSize = 12.sp, color = Palette.TextMuted)
    }
}
