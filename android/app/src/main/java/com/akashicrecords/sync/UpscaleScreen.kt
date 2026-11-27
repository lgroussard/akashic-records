package com.akashicrecords.sync

import android.content.Context
import android.os.Build
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.PickVisualMediaRequest
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.Button
import androidx.compose.material3.DropdownMenu
import androidx.compose.material3.DropdownMenuItem
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableIntStateOf
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
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.compose.ui.window.Dialog
import androidx.compose.ui.window.DialogProperties
import com.akashicrecords.Palette
import com.akashicrecords.media.ZoomableFileImage
import com.akashicrecords.media.rememberZoomState
import java.io.File
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext

private val Good = Color(0xFF4ADE80)
private val Warn = Color(0xFFF87171)

private fun prefs(context: Context) = context.getSharedPreferences("akashic_upscale", Context.MODE_PRIVATE)

// "Agrandir des images" on the phone: pick photos, choose a model, and the PC (when Akashic runs there) or the
// server does the work. The list is the one every device sees; a result is fetched only when it is opened.
@Composable
fun UpscaleScreen(onClose: () -> Unit) {
    val context = LocalContext.current
    val scope = rememberCoroutineScope()
    val board by HubCompute.board.collectAsState()
    val error by HubCompute.error.collectAsState()
    var model by remember { mutableStateOf(prefs(context).getString("model", null)) }
    var sending by remember { mutableIntStateOf(0) }
    var message by remember { mutableStateOf<String?>(null) }
    var viewing by remember { mutableStateOf<HubCompute.Job?>(null) }
    var choosing by remember { mutableStateOf(false) }

    // Kept fresh while open: often while something is being worked on, rarely otherwise.
    LaunchedEffect(Unit) {
        while (true) {
            HubCompute.refresh(context)
            delay(if (HubCompute.board.value?.busy == true) 3_000 else 15_000)
        }
    }
    val b = board
    val chosen = b?.model(model) ?: b?.models?.firstOrNull()

    val pick = rememberLauncherForActivityResult(ActivityResultContracts.PickMultipleVisualMedia(10)) { uris ->
        val m = chosen?.name ?: return@rememberLauncherForActivityResult
        if (uris.isEmpty()) return@rememberLauncherForActivityResult
        scope.launch {
            sending = uris.size
            var failed = 0
            for (u in uris) {
                if (!HubCompute.request(context, u, m)) failed++
                sending--
            }
            message = if (failed == 0) (if (uris.size == 1) "Photo envoyée." else "${uris.size} photos envoyées.") else "$failed envoi(s) en échec : ${HubCompute.error.value}"
        }
    }

    Dialog(onDismissRequest = onClose, properties = DialogProperties(usePlatformDefaultWidth = false)) {
        Column(Modifier.fillMaxSize().background(Palette.AppBg)) {
            Row(Modifier.fillMaxWidth().padding(horizontal = 4.dp), verticalAlignment = Alignment.CenterVertically) {
                TextButton(onClick = onClose) { Text("Fermer") }
                Text("Agrandir des images", fontWeight = FontWeight.Bold, textAlign = TextAlign.Center, modifier = Modifier.weight(1f))
                Spacer(Modifier.width(72.dp))
            }
            Column(Modifier.fillMaxSize().padding(horizontal = 14.dp)) {
                // ---- Model: what it does, and who would do it now. One model per feature: the chooser only shows
                // if the hub ever offers several.
                if ((b?.models?.size ?: 0) > 1) Box {
                    Row(
                        Modifier.fillMaxWidth().clip(RoundedCornerShape(10.dp)).background(Palette.Surface2)
                            .clickable(enabled = b != null) { choosing = true }.padding(horizontal = 12.dp, vertical = 10.dp),
                        verticalAlignment = Alignment.CenterVertically,
                    ) {
                        Text("Modèle", fontSize = 12.sp, color = Palette.TextMuted)
                        Spacer(Modifier.width(10.dp))
                        Text(chosen?.label ?: "…", fontWeight = FontWeight.Bold, modifier = Modifier.weight(1f))
                        Text("Changer ▾", fontSize = 12.sp, color = Palette.Accent)
                    }
                    DropdownMenu(expanded = choosing, onDismissRequest = { choosing = false }) {
                        b?.models?.forEach { m ->
                            DropdownMenuItem(
                                text = {
                                    Column {
                                        Text(m.label, fontWeight = if (m.name == chosen?.name) FontWeight.Bold else FontWeight.Normal)
                                        Text(m.description, fontSize = 11.sp, color = Palette.TextMuted)
                                    }
                                },
                                onClick = { choosing = false; model = m.name; prefs(context).edit().putString("model", m.name).apply() },
                            )
                        }
                    }
                }
                chosen?.let { m ->
                    val pcs = m.runners.filter { it != "hub" }
                    val who = when {
                        pcs.isNotEmpty() -> "${pcs.joinToString(", ")} est là : quelques secondes par photo."
                        m.hub -> "Ton PC n'est pas là : le serveur s'en charge, plus lentement (quelques minutes par photo)."
                        else -> "Attendra que ton PC soit allumé avec Akashic ouvert."
                    }
                    Text("${m.description} $who", fontSize = 12.sp, color = Palette.TextMuted, modifier = Modifier.padding(top = 6.dp))
                }

                Button(
                    onClick = { message = null; pick.launch(PickVisualMediaRequest(ActivityResultContracts.PickVisualMedia.ImageOnly)) },
                    enabled = chosen != null && b?.enabled != false && sending == 0,
                    modifier = Modifier.fillMaxWidth().padding(top = 12.dp),
                ) { Text(if (sending > 0) "Envoi… ($sending)" else "Choisir des photos") }

                val status = when {
                    error != null && b == null -> "Serveur injoignable : $error"
                    b == null -> "Lecture de la liste…"
                    !b.enabled -> "Le calcul partagé est coupé sur le serveur."
                    b.models.isEmpty() -> "Le serveur n'a encore aucun modèle."
                    message != null -> message!!
                    else -> "Le résultat reste sur le serveur : touche une photo terminée pour la voir ou l'enregistrer."
                }
                Text(status, fontSize = 12.sp, color = if (error != null && b == null) Warn else Palette.TextMuted, modifier = Modifier.padding(vertical = 8.dp))

                // ---- The list every device sees, newest first.
                LazyColumn(Modifier.fillMaxWidth().weight(1f)) {
                    if (b != null && b.jobs.isEmpty()) item {
                        Text("Aucune demande pour l'instant.", color = Palette.TextMuted, modifier = Modifier.padding(vertical = 12.dp))
                    }
                    items(b?.jobs ?: emptyList(), key = { it.id }) { job ->
                        JobCard(
                            job, b?.model(job.model)?.label ?: job.model,
                            onOpen = { if (job.done) viewing = job },
                            onRetry = { scope.launch { HubCompute.retry(context, job.id) } },
                            onRemove = { scope.launch { HubCompute.remove(context, job.id) } },
                        )
                    }
                }
            }
        }
        viewing?.let { job -> UpscaleViewer(job, onClose = { viewing = null }) }
    }
}

@Composable
private fun JobCard(job: HubCompute.Job, label: String, onOpen: () -> Unit, onRetry: () -> Unit, onRemove: () -> Unit) {
    var menu by remember { mutableStateOf(false) }
    val (status, color) = when {
        job.waiting -> "En attente" to Palette.TextMuted
        job.running -> "En cours sur ${HubCompute.who(job.workerName)}…" to Palette.Accent
        job.done -> ("Fait par ${HubCompute.who(job.workerName)} en ${seconds(job.seconds)} · ${mb(job.resultSize)}" + placed(job)) to (if (job.attachError == null) Good else Warn)
        else -> "Échec : ${job.error ?: "inconnu"}" to Warn
    }
    Row(
        Modifier.fillMaxWidth().padding(vertical = 3.dp).clip(RoundedCornerShape(10.dp)).background(Palette.Surface1)
            .clickable(onClick = onOpen).padding(start = 12.dp, top = 8.dp, bottom = 8.dp),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        Column(Modifier.weight(1f)) {
            Text(job.inputName, fontWeight = FontWeight.SemiBold, maxLines = 1, overflow = TextOverflow.Ellipsis)
            Text("$label · ${job.addedAt.replace('T', ' ').take(16)}", fontSize = 11.sp, color = Palette.TextFaint)
            Text(status, fontSize = 12.sp, color = color, maxLines = 2, overflow = TextOverflow.Ellipsis)
        }
        if (!job.running) Box {
            TextButton(onClick = { menu = true }) { Text("⋮", fontSize = 18.sp) }
            DropdownMenu(expanded = menu, onDismissRequest = { menu = false }) {
                if (job.done) DropdownMenuItem(text = { Text("Voir") }, onClick = { menu = false; onOpen() })
                if (job.failed) DropdownMenuItem(text = { Text("Réessayer") }, onClick = { menu = false; onRetry() })
                DropdownMenuItem(text = { Text("Retirer de la liste") }, onClick = { menu = false; onRemove() })
            }
        }
    }
}

/** For a picture of the app: whether the result was put next to its original. */
private fun placed(job: HubCompute.Job) = when {
    job.attachType == null -> ""
    job.attachError != null -> " · pas rangée : ${job.attachError}"
    job.attachedPath != null -> " · rangée à côté de l'original"
    else -> ""
}

private fun seconds(s: Double?) = when {
    s == null -> "?"
    s < 60 -> "%.1f s".format(s)
    else -> "${(s / 60).toInt()} min %02d s".format((s % 60).toInt())
}

private fun mb(bytes: Long?) = if (bytes == null) "" else "%.1f Mo".format(bytes / 1024.0 / 1024.0)

// One result, large: "Avant / Après" to compare, pinch to zoom, "Enregistrer" puts it in the gallery. The controls
// are in the bar above the picture, never over it.
@Composable
private fun UpscaleViewer(job: HubCompute.Job, onClose: () -> Unit) {
    val context = LocalContext.current
    val scope = rememberCoroutineScope()
    var before by remember { mutableStateOf(false) }
    var picture by remember { mutableStateOf<File?>(null) } // the file shown (before or after)
    var note by remember { mutableStateOf<String?>("Chargement…") }
    // One zoom for both: « Avant » / « Après » compare the same spot.
    val zoom = rememberZoomState(job.id)

    LaunchedEffect(before) {
        note = "Chargement…"
        try {
            picture = if (before) HubCompute.local(context, job.inputHash, job.inputName.substringAfterLast('.').lowercase())
                   else HubCompute.local(context, job.resultHash!!, "png")
            note = null
        } catch (e: Exception) {
            note = "Aperçu impossible : ${e.message}"
        }
    }

    // Before Android 10 the gallery cannot be written to without a storage permission: the user picks the place.
    val saveAs = rememberLauncherForActivityResult(ActivityResultContracts.CreateDocument("image/png")) { uri ->
        if (uri != null) scope.launch {
            note = withContext(Dispatchers.IO) {
                runCatching {
                    val file = HubCompute.local(context, job.resultHash!!, "png")
                    context.contentResolver.openOutputStream(uri)!!.use { out -> file.inputStream().use { it.copyTo(out) } }
                    "Image enregistrée."
                }.getOrElse { "Échec : ${it.message}" }
            }
        }
    }

    Dialog(onDismissRequest = onClose, properties = DialogProperties(usePlatformDefaultWidth = false)) {
        Column(Modifier.fillMaxSize().background(Palette.AppBg)) {
            Row(Modifier.fillMaxWidth().padding(horizontal = 4.dp), verticalAlignment = Alignment.CenterVertically) {
                TextButton(onClick = onClose) { Text("Fermer") }
                Spacer(Modifier.weight(1f))
                TextButton(onClick = { before = true }) { Text("Avant", fontWeight = if (before) FontWeight.Bold else FontWeight.Normal, color = if (before) Palette.Text else Palette.TextMuted) }
                TextButton(onClick = { before = false }) { Text("Après", fontWeight = if (!before) FontWeight.Bold else FontWeight.Normal, color = if (!before) Palette.Text else Palette.TextMuted) }
                TextButton(onClick = {
                    val name = job.resultName ?: "akashic-agrandie.png"
                    if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q) {
                        scope.launch {
                            note = withContext(Dispatchers.IO) {
                                runCatching {
                                    HubCompute.saveToGallery(context, HubCompute.local(context, job.resultHash!!, "png"), name)
                                    "Enregistrée dans Images/Akashic."
                                }.getOrElse { "Échec : ${it.message}" }
                            }
                        }
                    } else saveAs.launch(name)
                }) { Text("Enregistrer") }
            }
            Text(
                note ?: (if (before) "Avant : ${job.inputName}" else "Après : ${job.resultName}") + "  ·  pince ou double tap pour zoomer",
                fontSize = 12.sp, color = Palette.TextMuted, textAlign = TextAlign.Center,
                modifier = Modifier.fillMaxWidth().padding(bottom = 4.dp),
            )
            Box(Modifier.fillMaxWidth().weight(1f)) {
                picture?.let { f -> ZoomableFileImage(f.path, zoom, Modifier.fillMaxSize()) { f } }
            }
        }
    }
}
