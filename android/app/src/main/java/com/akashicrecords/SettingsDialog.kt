package com.akashicrecords

import android.net.Uri
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.Switch
import androidx.compose.ui.Alignment
import androidx.compose.foundation.Image
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.ui.res.painterResource
import androidx.compose.foundation.background
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.ui.draw.clip
import com.akashicrecords.widgets.WidgetCatalog
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.height
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Button
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.*
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.akashicrecords.data.AkashicDatabase
import com.akashicrecords.data.ConfigStore
import com.akashicrecords.data.DesktopImport
import com.akashicrecords.data.Modules
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext

@Composable
fun SettingsDialog(
    db: AkashicDatabase,
    config: ConfigStore,
    disabledModules: Set<String>,
    onModulesChanged: (Set<String>) -> Unit,
    onDismiss: () -> Unit,
    onImported: () -> Unit,
) {
    val context = LocalContext.current
    val scope = rememberCoroutineScope()
    var picked by remember { mutableStateOf<Uri?>(null) }
    var busy by remember { mutableStateOf(false) }
    var status by remember { mutableStateOf<String?>(null) }

    val pick = rememberLauncherForActivityResult(ActivityResultContracts.OpenDocument()) { uri ->
        if (uri != null) picked = uri
    }

    AlertDialog(
        onDismissRequest = { if (!busy) onDismiss() },
        title = { Text("Réglages") },
        text = {
            Column(Modifier.verticalScroll(rememberScrollState())) {
                ModuleSwitches(disabledModules) { key, on -> onModulesChanged(config.setModuleEnabled(key, on)) }
                HorizontalDivider(color = Palette.BorderStrong, modifier = Modifier.padding(vertical = 12.dp))
                HomeIconButtons()
                HorizontalDivider(color = Palette.BorderStrong, modifier = Modifier.padding(vertical = 12.dp))
                WidgetSwitches()
                HorizontalDivider(color = Palette.BorderStrong, modifier = Modifier.padding(vertical = 12.dp))
                Text("Importer les données du PC", fontSize = 15.sp)
                Spacer(Modifier.height(4.dp))
                Text(
                    "Choisis le fichier akashic.db du PC (dossier data\\ à côté de l'exe), ou un .zip du dossier " +
                        "de l'app contenant data\\ et media\\ pour récupérer aussi les photos. Ferme l'app sur le PC " +
                        "avant de copier le fichier. Le dossier music\\ est ignoré.",
                    fontSize = 12.sp,
                    color = Palette.TextMuted,
                )
                Spacer(Modifier.height(8.dp))
                Button(onClick = { pick.launch(arrayOf("*/*")) }, enabled = !busy) { Text("Choisir un fichier…") }
                status?.let {
                    Spacer(Modifier.height(8.dp))
                    Text(it, fontSize = 12.sp)
                }
            }
        },
        confirmButton = { TextButton(onClick = onDismiss, enabled = !busy) { Text("Fermer") } },
    )

    picked?.let { uri ->
        AlertDialog(
            onDismissRequest = { picked = null },
            title = { Text("Remplacer les données du téléphone ?") },
            text = {
                Text(
                    "Toutes les données de l'app sur ce téléphone (journaux, recettes, poèmes, ambiances…) seront " +
                        "remplacées par celles du PC. Ce qui a été écrit uniquement sur le téléphone sera perdu. " +
                        "Les fichiers audio ne sont pas touchés.",
                )
            },
            confirmButton = {
                TextButton(onClick = {
                    picked = null
                    busy = true
                    status = "Import en cours…"
                    scope.launch {
                        status = try {
                            val r = withContext(Dispatchers.IO) { DesktopImport.run(context, uri, db).also { com.akashicrecords.reading.ReadingLinks.relinkAfterImport(context, db) } }
                            onImported()
                            "Import terminé : ${r.rows} lignes dans ${r.tables} tables, ${r.mediaFiles} fichiers média."
                        } catch (e: Exception) {
                            "Échec de l'import, rien n'a été modifié : ${e.message}"
                        }
                        busy = false
                    }
                }) { Text("Remplacer") }
            },
            dismissButton = { TextButton(onClick = { picked = null }) { Text("Annuler") } },
        )
    }
}

// One switch per section, its sub-tabs indented below it (greyed while the section itself is off).
// Each switch shows its own entry; a section left on with every sub-tab off is hidden all the same.
@Composable
private fun ModuleSwitches(disabled: Set<String>, onToggle: (String, Boolean) -> Unit) {
    Text("Modules de ce téléphone", fontSize = 15.sp)
    Text(
        "Désactive ce que tu n'utilises pas ici : la barre du bas, le menu Plus et les onglets s'adaptent. " +
            "Rien n'est supprimé, et le PC garde ses propres réglages.",
        fontSize = 12.sp,
        color = Palette.TextMuted,
    )
    Modules.catalog.forEach { section ->
        val sectionOn = section.key !in disabled
        ModuleRow(section.label, sectionOn, enabled = true, indent = 0) { onToggle(section.key, it) }
        section.children.forEach { child ->
            ModuleRow(child.label, child.key !in disabled, enabled = sectionOn, indent = 20) { onToggle(child.key, it) }
        }
    }
}

@Composable
private fun ModuleRow(label: String, checked: Boolean, enabled: Boolean, indent: Int, onChange: (Boolean) -> Unit) {
    Row(
        Modifier.fillMaxWidth().padding(start = indent.dp, top = 0.dp, bottom = 0.dp),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        Text(
            label,
            fontSize = if (indent == 0) 14.sp else 13.sp,
            color = if (enabled) Palette.Text else Palette.TextFaint,
            modifier = Modifier.weight(1f),
        )
        Switch(checked = checked, enabled = enabled, onCheckedChange = onChange)
    }
}

// One switch per home-screen widget: off = hidden from the launcher's widget picker.
@Composable
private fun WidgetSwitches() {
    val context = LocalContext.current
    Text("Widgets", fontSize = 15.sp)
    Text(
        "Ceux activés apparaissent dans la liste des widgets du téléphone (appui long sur l'accueil → Widgets → Akashic).",
        fontSize = 12.sp,
        color = Palette.TextMuted,
    )
    WidgetCatalog.all.forEach { e ->
        var on by remember { mutableStateOf(WidgetCatalog.isEnabled(context, e)) }
        Row(Modifier.fillMaxWidth().padding(vertical = 2.dp), verticalAlignment = Alignment.CenterVertically) {
            Column(Modifier.weight(1f)) {
                Text(e.label, fontSize = 14.sp)
                Text(e.description, fontSize = 11.sp, color = Palette.TextFaint)
            }
            Switch(checked = on, onCheckedChange = {
                on = it
                WidgetCatalog.setEnabled(context, e, it)
            })
        }
    }
}

// Colored home-screen icons for Musique, Poésie and Calendrier.
@Composable
private fun HomeIconButtons() {
    val context = LocalContext.current
    var message by remember { mutableStateOf<String?>(null) }
    Text("Icônes sur l'écran d'accueil", fontSize = 15.sp)
    Text("Une icône qui ouvre directement la section.", fontSize = 12.sp, color = Palette.TextMuted)
    HomeIcons.all.forEach { icon ->
        Row(Modifier.fillMaxWidth().padding(vertical = 3.dp), verticalAlignment = Alignment.CenterVertically) {
            // Preview of the icon (the adaptive-icon resource itself is for the launcher only).
            androidx.compose.foundation.layout.Box(
                Modifier.size(36.dp).clip(CircleShape).background(androidx.compose.ui.graphics.Color(icon.color)),
                contentAlignment = Alignment.Center,
            ) {
                Image(painterResource(icon.glyphRes), contentDescription = null, modifier = Modifier.size(20.dp))
            }
            Spacer(Modifier.width(10.dp))
            Column(Modifier.weight(1f)) {
                Text(icon.label, fontSize = 14.sp)
                Text(icon.description, fontSize = 11.sp, color = Palette.TextFaint)
            }
            TextButton(onClick = {
                message = if (HomeIcons.pin(context, icon)) null else "Ton écran d'accueil ne permet pas d'ajouter des icônes."
            }) { Text("Ajouter") }
        }
    }
    message?.let { Text(it, fontSize = 12.sp, color = Palette.TextMuted) }
}
