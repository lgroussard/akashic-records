package com.akashicrecords.sync

import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Arrangement
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
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.compose.ui.window.Dialog
import androidx.compose.ui.window.DialogProperties
import com.akashicrecords.Palette
import java.time.Duration
import java.time.Instant
import java.time.LocalDate
import java.time.ZoneId
import java.time.format.DateTimeFormatter
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import org.json.JSONObject

// "Serveur" (Plus menu): the state of the mini-PC, from the hub's GET /server. How full its disk is and with what,
// whether last night's copy to the external disk went well, the paired devices, what the hub is working on.
// Same content and colours as the PC's ServerWindow. Read-only, refreshed every 30 s while open.

private val kinds = listOf(
    Triple("music", "Musique", Color(0xFF3B82F6)),
    Triple("photos", "Photos des Archives", Color(0xFFF472B6)),
    Triple("books", "Livres", Color(0xFFF59E0B)),
    Triple("references", "Références", Color(0xFF8B5CF6)),
    Triple("other", "Autres images", Color(0xFF14B8A6)),
    Triple("hub_files", "Fichiers du serveur", Color(0xFF64748B)),
    Triple("previews", "Aperçus des photos", Color(0xFFA3A3C2)),
    Triple("tools", "Outils et modèles", Color(0xFF475569)),
)
private val Good = Color(0xFF4ADE80)
private val Late = Color(0xFFFBBF24)
private val Warn = Color(0xFFF87171)

@Composable
fun ServerScreen(onClose: () -> Unit) {
    val context = LocalContext.current
    val scope = rememberCoroutineScope()
    var status by remember { mutableStateOf<JSONObject?>(null) }
    var error by remember { mutableStateOf<String?>(null) }
    var readAt by remember { mutableStateOf<Instant?>(null) }

    suspend fun refresh() {
        val creds = SyncService.credentials(context)
        if (creds == null) { error = "Ce téléphone n'est pas relié au serveur."; return }
        runCatching { withContext(Dispatchers.IO) { HubClient(creds.first, creds.second).server() } }
            .onSuccess { status = it; error = null; readAt = Instant.now() }
            .onFailure { error = "Serveur injoignable : ${it.message}" }
    }
    LaunchedEffect(Unit) { while (true) { refresh(); delay(30_000) } }

    Dialog(onDismissRequest = onClose, properties = DialogProperties(usePlatformDefaultWidth = false)) {
        Column(Modifier.fillMaxSize().background(Palette.AppBg)) {
            Row(Modifier.fillMaxWidth().padding(horizontal = 4.dp), verticalAlignment = Alignment.CenterVertically) {
                TextButton(onClick = onClose) { Text("Fermer") }
                Text("Serveur", fontWeight = FontWeight.Bold, textAlign = TextAlign.Center, modifier = Modifier.weight(1f))
                TextButton(onClick = { scope.launch { refresh() } }) { Text("↻") }
            }
            val s = status
            Column(Modifier.fillMaxSize().verticalScroll(rememberScrollState()).padding(horizontal = 14.dp)) {
                if (s == null) {
                    Text(error ?: "Lecture de l'état du serveur…", color = if (error == null) Palette.TextMuted else Warn, modifier = Modifier.padding(vertical = 12.dp))
                    return@Column
                }
                if (error != null) Text(error!!, fontSize = 12.sp, color = Warn, modifier = Modifier.padding(bottom = 8.dp))
                DiskCard(s)
                BackupCard(s.getJSONObject("backup"))
                DevicesCard(s.getJSONArray("devices"))
                Card("TRAVAUX DU SERVEUR") {
                    WorkLine("Téléchargements de musique", s.getJSONObject("downloads"))
                    WorkLine("Agrandissements d'images", s.getJSONObject("compute"))
                }
                val total = s.getJSONObject("entities").let { e -> e.keys().asSequence().sumOf { e.getLong(it) } }
                Text(
                    "$total éléments synchronisés · hub ${s.optString("version")} · allumé depuis ${since(s.optString("started_at"))}" +
                        (readAt?.let { " · lu à " + DateTimeFormatter.ofPattern("HH:mm").withZone(ZoneId.systemDefault()).format(it) } ?: ""),
                    fontSize = 11.sp, color = Palette.TextFaint, modifier = Modifier.padding(vertical = 12.dp),
                )
            }
        }
    }
}

@Composable
private fun Card(title: String, content: @Composable () -> Unit) {
    Column(Modifier.fillMaxWidth().padding(bottom = 12.dp).clip(RoundedCornerShape(12.dp)).background(Palette.Surface2).padding(14.dp)) {
        Text(title, fontSize = 10.sp, color = Palette.TextMuted, modifier = Modifier.padding(bottom = 8.dp))
        content()
    }
}

@Composable
private fun DiskCard(s: JSONObject) = Card("DISQUE DU SERVEUR") {
    val storage = s.getJSONArray("storage").let { a -> (0 until a.length()).associate { a.getJSONObject(it).let { k -> k.getString("kind") to k } } }
    val disk = s.optJSONObject("disk")
    if (disk != null) {
        val total = disk.getLong("total")
        val free = disk.getLong("free")
        val used = total - free
        Text("${size(used)} utilisés sur ${size(total)}", fontWeight = FontWeight.Bold)
        Text("${size(free)} libres", fontSize = 12.sp, color = if (free < total / 10) Warn else Palette.TextMuted)
        // One bar: each kind in its colour, then the rest of what is used, then free.
        val known = kinds.sumOf { storage[it.first]?.optLong("bytes") ?: 0L }
        Row(Modifier.fillMaxWidth().padding(vertical = 10.dp).height(12.dp).clip(RoundedCornerShape(4.dp)).background(Palette.Surface1)) {
            kinds.forEach { (k, _, color) ->
                val b = storage[k]?.optLong("bytes") ?: 0L
                if (b > 0) Box(Modifier.weight(b.toFloat()).fillMaxHeight().background(color))
            }
            val rest = used - known
            if (rest > 0) Box(Modifier.weight(rest.toFloat()).fillMaxHeight().background(Palette.TextFaint))
            if (free > 0) Spacer(Modifier.weight(free.toFloat()))
        }
        kinds.forEach { (k, label, color) ->
            val item = storage[k] ?: return@forEach
            val b = item.optLong("bytes")
            if (b > 0) Legend(color, label, size(b) + item.optLong("files").takeIf { it > 0 }?.let { " · $it" }.orEmpty())
        }
        val rest = used - known
        if (rest > 0) Legend(Palette.TextFaint, "Système, fichiers perso, le reste", size(rest))
    }
}

@Composable
private fun Legend(color: Color, label: String, value: String) {
    Row(Modifier.fillMaxWidth().padding(vertical = 2.dp), verticalAlignment = Alignment.CenterVertically) {
        Box(Modifier.size(10.dp).clip(RoundedCornerShape(2.dp)).background(color))
        Spacer(Modifier.width(8.dp))
        Text(label, fontSize = 13.sp, modifier = Modifier.weight(1f))
        Text(value, fontSize = 12.sp, color = Palette.TextMuted)
    }
}

@Composable
private fun BackupCard(b: JSONObject) = Card("SAUVEGARDE SUR LE DISQUE EXTERNE") {
    val (label, color) = when (b.optString("state")) {
        "ok" -> "Faite" to Good
        "late" -> "En retard : la copie de cette nuit n'a pas eu lieu" to Late
        "absent" -> "Disque de sauvegarde non branché" to Warn
        "failed" -> "Échec" to Warn
        else -> "Inconnue : le script de sauvegarde du mini-PC doit être mis à jour" to Palette.TextMuted
    }
    Text(label, fontWeight = FontWeight.Bold, color = color)
    b.str("at")?.let { Text("Dernier passage : ${whenText(it)}", fontSize = 12.sp, color = Palette.TextMuted) }
    if (b.optString("state") != "ok") b.str("last_ok_at")?.let { Text("Dernière copie réussie : ${whenText(it)}", fontSize = 12.sp, color = Palette.TextMuted) }
    b.str("message")?.let { Text(it, fontSize = 12.sp, color = Palette.TextMuted) }
    if (!b.isNull("bytes") && b.has("bytes")) Text("Copie : ${size(b.getLong("bytes"))}, ${b.optLong("files")} fichiers", fontSize = 12.sp, color = Palette.TextMuted)
    if (b.has("disk_total") && !b.isNull("disk_total") && !b.isNull("disk_free")) {
        val t = b.getLong("disk_total"); val f = b.getLong("disk_free")
        Text("Disque externe : ${size(f)} libres sur ${size(t)}", fontSize = 12.sp, color = if (f < t / 10) Warn else Palette.TextMuted)
    }
}

@Composable
private fun DevicesCard(devices: org.json.JSONArray) = Card("APPAREILS") {
    var revoked = 0
    for (i in 0 until devices.length()) {
        val d = devices.getJSONObject(i)
        if (d.optBoolean("revoked")) { revoked++; continue }
        val hub = d.optBoolean("hub")
        Column(Modifier.padding(bottom = 8.dp)) {
            Text(if (hub) "Le serveur lui-même" else d.optString("name"), fontWeight = FontWeight.Bold, fontSize = 14.sp)
            val seen = if (hub) null else d.str("last_seen_at")?.let { "vu " + whenText(it) } ?: "jamais vu"
            val change = d.str("last_change_at")?.let { "modifié " + whenText(it) } ?: "aucune modification"
            Text(listOfNotNull(seen, change).joinToString(" · "), fontSize = 12.sp, color = Palette.TextMuted)
        }
    }
    if (revoked > 0) Text("$revoked appareil(s) retiré(s)", fontSize = 11.sp, color = Palette.TextFaint)
}

@Composable
private fun WorkLine(label: String, w: JSONObject) {
    val parts = buildList {
        w.optInt("running").takeIf { it > 0 }?.let { add("$it en cours") }
        w.optInt("queued").takeIf { it > 0 }?.let { add("$it en attente") }
        w.optInt("failed").takeIf { it > 0 }?.let { add("$it en échec") }
    }
    Text("$label : ${parts.ifEmpty { listOf("rien en cours") }.joinToString(", ")}", fontSize = 12.sp,
        color = if (w.optInt("failed") > 0) Late else Palette.TextMuted, modifier = Modifier.padding(bottom = 3.dp))
}

private fun JSONObject.str(name: String): String? = if (has(name) && !isNull(name)) optString(name).takeIf { it.isNotEmpty() } else null

private fun size(bytes: Long): String = when {
    bytes >= 1L shl 40 -> "%.1f To".format(bytes / (1L shl 40).toDouble())
    bytes >= 1L shl 30 -> "%.1f Go".format(bytes / (1L shl 30).toDouble())
    bytes >= 1L shl 20 -> "%d Mo".format(bytes / (1L shl 20))
    else -> "%d Ko".format(bytes / 1024)
}

/** "il y a 5 min", "hier à 03:31", "le 24/11 à 03:30". */
private fun whenText(iso: String): String {
    val t = runCatching { Instant.parse(iso) }.getOrNull()
        ?: runCatching { java.time.OffsetDateTime.parse(iso).toInstant() }.getOrNull() ?: return iso
    val ago = Duration.between(t, Instant.now())
    val local = t.atZone(ZoneId.systemDefault())
    val hm = DateTimeFormatter.ofPattern("HH:mm").format(local)
    return when {
        ago.toMinutes() < 1 -> "à l'instant"
        ago.toMinutes() < 60 -> "il y a ${ago.toMinutes()} min"
        local.toLocalDate() == LocalDate.now() -> "aujourd'hui à $hm"
        local.toLocalDate() == LocalDate.now().minusDays(1) -> "hier à $hm"
        else -> "le ${DateTimeFormatter.ofPattern("dd/MM").format(local)} à $hm"
    }
}

private fun since(iso: String): String {
    val t = runCatching { Instant.parse(iso) }.getOrNull() ?: runCatching { java.time.OffsetDateTime.parse(iso).toInstant() }.getOrNull() ?: return "?"
    val up = Duration.between(t, Instant.now())
    return when { up.toDays() >= 1 -> "${up.toDays()} j"; up.toHours() >= 1 -> "${up.toHours()} h"; else -> "${up.toMinutes()} min" }
}
