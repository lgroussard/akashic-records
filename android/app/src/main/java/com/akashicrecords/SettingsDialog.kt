package com.akashicrecords

import android.net.Uri
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.layout.Column
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
import com.akashicrecords.data.DesktopImport
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext

@Composable
fun SettingsDialog(db: AkashicDatabase, onDismiss: () -> Unit, onImported: () -> Unit) {
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
            Column {
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
                            val r = withContext(Dispatchers.IO) { DesktopImport.run(context, uri, db) }
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
