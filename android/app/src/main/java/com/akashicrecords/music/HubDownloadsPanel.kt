package com.akashicrecords.music

import android.content.Context
import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Button
import androidx.compose.material3.DropdownMenu
import androidx.compose.material3.DropdownMenuItem
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.akashicrecords.Palette
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch

/** Where a new track (or a moved one) goes: the root, a zone of the board, an existing music folder, a new zone. */
private sealed interface Destination {
    data object Root : Destination
    data class InZone(val zone: HubDownloads.Zone) : Destination
    data class Folder(val folder: String) : Destination
    data class NewZone(val name: String) : Destination
}

private fun prefs(context: Context) = context.getSharedPreferences("akashic_downloads", Context.MODE_PRIVATE)

// The ⤓ sheet when the phone is paired: the board the hub keeps for every device. The zone is chosen right where
// the track is added (a zone of the board, a folder already in the music, or a new one), each track has its own
// menu to move it, and a zone leaves the board by itself once its tracks are downloaded.
@Composable
internal fun HubDownloadsPanel(onLibraryChanged: () -> Unit) {
    val context = LocalContext.current
    val scope = rememberCoroutineScope()
    val board by HubDownloads.board.collectAsState()
    val error by HubDownloads.error.collectAsState()
    var input by remember { mutableStateOf("") }
    var target by remember { mutableStateOf<HubDownloads.Zone?>(null) }   // null = root
    var picking by remember { mutableStateOf<((Destination) -> Unit)?>(null) }
    var showDone by remember { mutableStateOf(false) }
    var renaming by remember { mutableStateOf<HubDownloads.Zone?>(null) }

    // Kept fresh while the sheet is open: often while the hub works, rarely otherwise.
    LaunchedEffect(Unit) {
        while (true) {
            HubDownloads.refresh(context)
            delay(if (HubDownloads.board.value?.busy == true) 3_000 else 15_000)
        }
    }
    // The zone used last, as long as it is still on the board.
    LaunchedEffect(board?.zones) {
        val saved = prefs(context).getString("zone", null)
        target = board?.zones?.firstOrNull { it.id == (target?.id ?: saved) }
    }

    fun choose(zone: HubDownloads.Zone?) {
        target = zone
        prefs(context).edit().putString("zone", zone?.id).apply()
    }

    // A destination as a zone id on the hub (null = root), creating the zone when it is a folder or a new name.
    suspend fun resolve(d: Destination): HubDownloads.Zone? = when (d) {
        Destination.Root -> null
        is Destination.InZone -> d.zone
        is Destination.Folder -> HubDownloads.zoneFor(context, d.folder, d.folder)?.let { id -> HubDownloads.board.value?.zone(id) }
        is Destination.NewZone -> HubDownloads.zoneFor(context, d.name, d.name)?.let { id -> HubDownloads.board.value?.zone(id) }
    }

    fun add() {
        val v = input.trim()
        if (v.isEmpty()) return
        val isLink = v.startsWith("http://") || v.startsWith("https://")
        val query = if (isLink) v else "ytsearch1:${v.replace("\"", "")}"
        input = ""
        scope.launch {
            HubDownloads.add(context, query, v, target?.id)
            HubDownloads.followUntilDone(context)
        }
    }

    val b = board
    Column(Modifier.fillMaxSize().padding(horizontal = 14.dp)) {
        // ---- Add: what, and where it goes.
        Row(verticalAlignment = Alignment.CenterVertically) {
            OutlinedTextField(
                input, { input = it },
                placeholder = { Text("Lien, ou « titre artiste »") },
                singleLine = true,
                modifier = Modifier.weight(1f),
            )
            Spacer(Modifier.width(8.dp))
            Button(onClick = { add() }, enabled = input.isNotBlank() && b?.enabled != false) { Text("Ajouter") }
        }
        Row(
            Modifier.fillMaxWidth().padding(top = 6.dp).clip(RoundedCornerShape(10.dp)).background(Palette.Surface2)
                .clickable { picking = { d -> scope.launch { choose(resolve(d)) } } }
                .padding(horizontal = 12.dp, vertical = 10.dp),
            verticalAlignment = Alignment.CenterVertically,
        ) {
            Text("Ranger dans", fontSize = 12.sp, color = Palette.TextMuted)
            Spacer(Modifier.width(10.dp))
            Text("📁 " + (target?.name ?: "Musique (sans dossier)"), fontWeight = FontWeight.Bold, maxLines = 1, overflow = TextOverflow.Ellipsis, modifier = Modifier.weight(1f))
            Text("Changer ▾", fontSize = 12.sp, color = Palette.Accent)
        }

        // ---- What the hub is doing.
        val status = when {
            error != null && b == null -> "Serveur injoignable : $error"
            b == null -> "Lecture du tableau…"
            !b.enabled -> "Les téléchargements sont coupés sur le serveur."
            b.toolProblem != null -> "Le serveur ne peut pas lancer yt-dlp : ${b.toolProblem}"
            b.current != null -> "Le serveur télécharge : ${b.current}"
            b.busy -> "En attente sur le serveur…"
            else -> "Le serveur télécharge pour tous tes appareils ; le titre arrive au catalogue, à écouter ou à garder."
        }
        Text(status, fontSize = 12.sp, color = if (error != null || b?.toolProblem != null) Palette.Accent else Palette.TextMuted, modifier = Modifier.padding(vertical = 8.dp))
        if (b == null) return@Column

        val pending = b.items.filter { !it.done }
        val done = b.items.filter { it.done }.reversed()
        val groups = buildList {
            b.zones.forEach { z -> add(z to pending.filter { it.zoneId == z.id }) }
            val loose = pending.filter { it.zoneId == null || b.zone(it.zoneId) == null }
            if (loose.isNotEmpty()) add(null to loose)
        }

        LazyColumn(Modifier.fillMaxWidth().weight(1f)) {
            if (groups.isEmpty()) item {
                Text("Rien en cours. Ajoute un titre ci-dessus, ou avec ⤓ depuis le lecteur.", color = Palette.TextMuted, modifier = Modifier.padding(vertical = 12.dp))
            }
            groups.forEach { (zone, list) ->
                item(key = "z:${zone?.id ?: "root"}") {
                    ZoneHeader(
                        zone, list.size,
                        onPick = { choose(zone) },
                        onRename = zone?.let { { renaming = it } },
                        onRemove = zone?.let { z -> { scope.launch { HubDownloads.removeZone(context, z.id) } } },
                    )
                }
                if (list.isEmpty()) item(key = "e:${zone?.id}") {
                    Text("Vide : choisis cette zone en haut, puis ajoute.", fontSize = 12.sp, color = Palette.TextFaint, modifier = Modifier.padding(start = 26.dp, bottom = 6.dp))
                }
                items(list, key = { it.id }) { job ->
                    JobRow(
                        job,
                        onMove = { picking = { d -> scope.launch { HubDownloads.move(context, job.id, resolve(d)?.id) } } },
                        onRetry = { scope.launch { HubDownloads.retry(context, job.id); HubDownloads.followUntilDone(context) } },
                        onRemove = { scope.launch { HubDownloads.remove(context, job.id) } },
                    )
                }
            }
            if (done.isNotEmpty()) {
                item(key = "done") {
                    HorizontalDivider(color = Palette.BorderStrong, modifier = Modifier.padding(top = 8.dp))
                    Row(Modifier.fillMaxWidth().clickable { showDone = !showDone }.padding(vertical = 10.dp), verticalAlignment = Alignment.CenterVertically) {
                        Text((if (showDone) "▾ " else "▸ ") + "Terminés (${done.size})", fontWeight = FontWeight.Bold, modifier = Modifier.weight(1f))
                        if (showDone) TextButton(onClick = { scope.launch { HubDownloads.clearDone(context) } }) { Text("Vider") }
                    }
                }
                if (showDone) items(done, key = { "d:" + it.id }) { job ->
                    Column(Modifier.fillMaxWidth().padding(start = 12.dp, bottom = 8.dp)) {
                        Text("✓ " + job.label, maxLines = 1, overflow = TextOverflow.Ellipsis)
                        Text("dans " + (job.folder?.takeIf { it.isNotEmpty() }?.let { "Musique / $it" } ?: "Musique"), fontSize = 11.sp, color = Palette.TextFaint)
                    }
                }
            }
        }
        b.toolVersion?.let { Text("yt-dlp $it sur le serveur", fontSize = 10.sp, color = Palette.TextFaint, modifier = Modifier.padding(vertical = 4.dp)) }
    }

    picking?.let { onPick ->
        DestinationDialog(b, onDismiss = { picking = null }) { d -> picking = null; onPick(d) }
    }
    renaming?.let { z ->
        var name by remember(z.id) { mutableStateOf(z.name) }
        AlertDialog(
            onDismissRequest = { renaming = null },
            title = { Text("Renommer la zone") },
            text = { OutlinedTextField(name, { name = it }, singleLine = true) },
            confirmButton = { TextButton(onClick = { renaming = null; scope.launch { HubDownloads.renameZone(context, z.id, name) } }, enabled = name.isNotBlank()) { Text("Renommer") } },
            dismissButton = { TextButton(onClick = { renaming = null }) { Text("Annuler") } },
        )
    }
}

@Composable
private fun ZoneHeader(zone: HubDownloads.Zone?, count: Int, onPick: () -> Unit, onRename: (() -> Unit)?, onRemove: (() -> Unit)?) {
    var menu by remember { mutableStateOf(false) }
    Row(Modifier.fillMaxWidth().padding(top = 10.dp), verticalAlignment = Alignment.CenterVertically) {
        Column(Modifier.weight(1f).clickable(onClick = onPick)) {
            Text("📁 " + (zone?.name ?: "Musique (sans dossier)") + if (count > 0) "  ·  $count" else "", fontWeight = FontWeight.Bold, maxLines = 1, overflow = TextOverflow.Ellipsis)
            Text("Musique / " + (zone?.folder?.ifEmpty { null } ?: "…") .let { if (zone == null) "(racine)" else it }, fontSize = 11.sp, color = Palette.TextFaint, maxLines = 1)
        }
        if (onRename != null || onRemove != null) Box {
            TextButton(onClick = { menu = true }) { Text("⋮", fontSize = 18.sp) }
            DropdownMenu(expanded = menu, onDismissRequest = { menu = false }) {
                DropdownMenuItem(text = { Text("Ranger les ajouts ici") }, onClick = { menu = false; onPick() })
                onRename?.let { DropdownMenuItem(text = { Text("Renommer") }, onClick = { menu = false; it() }) }
                onRemove?.let { DropdownMenuItem(text = { Text("Retirer la zone (ses titres vont sans dossier)") }, onClick = { menu = false; it() }) }
            }
        }
    }
}

@Composable
private fun JobRow(job: HubDownloads.Item, onMove: () -> Unit, onRetry: () -> Unit, onRemove: () -> Unit) {
    var menu by remember { mutableStateOf(false) }
    Row(Modifier.fillMaxWidth().padding(start = 12.dp), verticalAlignment = Alignment.CenterVertically) {
        Column(Modifier.weight(1f).clickable { menu = true }.padding(vertical = 6.dp)) {
            Text(job.label, maxLines = 1, overflow = TextOverflow.Ellipsis)
            Text(
                when {
                    job.running -> "téléchargement en cours…"
                    job.failed -> "échec : " + (job.error ?: "inconnu")
                    else -> "en attente"
                },
                fontSize = 11.sp, color = if (job.failed) Palette.Accent else Palette.TextFaint, maxLines = 2, overflow = TextOverflow.Ellipsis,
            )
        }
        if (job.failed) TextButton(onClick = onRetry) { Text("↻") }
        Box {
            TextButton(onClick = { menu = true }, enabled = !job.running) { Text("⋮", fontSize = 18.sp) }
            DropdownMenu(expanded = menu, onDismissRequest = { menu = false }) {
                DropdownMenuItem(text = { Text("Ranger ailleurs…") }, onClick = { menu = false; onMove() }, enabled = !job.running)
                if (job.failed) DropdownMenuItem(text = { Text("Réessayer") }, onClick = { menu = false; onRetry() })
                DropdownMenuItem(text = { Text("Retirer") }, onClick = { menu = false; onRemove() }, enabled = !job.running)
            }
        }
    }
}

// Where to put it: the zones of the board, the folders already in the music (searchable), or a new zone.
@Composable
private fun DestinationDialog(board: HubDownloads.Board?, onDismiss: () -> Unit, onPick: (Destination) -> Unit) {
    var search by remember { mutableStateOf("") }
    var newName by remember { mutableStateOf("") }
    val zoneFolders = board?.zones?.map { it.folder.lowercase() }?.toSet() ?: emptySet()
    val folders = (board?.folders ?: emptyList()).filter { it.lowercase() !in zoneFolders }
        .filter { search.isBlank() || it.contains(search, true) }
    AlertDialog(
        onDismissRequest = onDismiss,
        title = { Text("Ranger dans…") },
        text = {
            LazyColumn(Modifier.heightIn(max = 480.dp), verticalArrangement = Arrangement.spacedBy(2.dp)) {
                item {
                    Row(verticalAlignment = Alignment.CenterVertically) {
                        OutlinedTextField(newName, { newName = it }, placeholder = { Text("Nouvelle zone (nom du dossier)") }, singleLine = true, modifier = Modifier.weight(1f))
                        TextButton(onClick = { onPick(Destination.NewZone(newName.trim())) }, enabled = newName.isNotBlank()) { Text("Créer") }
                    }
                }
                item { Choice("📁 Musique (sans dossier)") { onPick(Destination.Root) } }
                if (!board?.zones.isNullOrEmpty()) {
                    item { Section("Zones en cours") }
                    items(board!!.zones, key = { "z" + it.id }) { z -> Choice("📁 ${z.name}", if (z.folder != z.name) "Musique / ${z.folder}" else null) { onPick(Destination.InZone(z)) } }
                }
                item {
                    Section("Dossiers de ta musique")
                    OutlinedTextField(search, { search = it }, placeholder = { Text("Chercher un dossier") }, singleLine = true, modifier = Modifier.fillMaxWidth())
                }
                items(folders, key = { "f$it" }) { f -> Choice("📂 $f") { onPick(Destination.Folder(f)) } }
                if (folders.isEmpty()) item { Text("Aucun dossier.", fontSize = 12.sp, color = Palette.TextFaint) }
            }
        },
        confirmButton = { TextButton(onClick = onDismiss) { Text("Fermer") } },
    )
}

@Composable
private fun Section(title: String) =
    Text(title.uppercase(), fontSize = 10.sp, fontWeight = FontWeight.Bold, color = Palette.TextFaint, modifier = Modifier.padding(top = 10.dp, bottom = 2.dp))

@Composable
private fun Choice(label: String, sub: String? = null, onClick: () -> Unit) {
    Column(Modifier.fillMaxWidth().clip(RoundedCornerShape(8.dp)).clickable(onClick = onClick).padding(horizontal = 8.dp, vertical = 9.dp)) {
        Text(label, maxLines = 1, overflow = TextOverflow.Ellipsis)
        sub?.let { Text(it, fontSize = 11.sp, color = Palette.TextFaint) }
    }
}
