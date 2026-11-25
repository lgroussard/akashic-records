package com.akashicrecords.sync

import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.material3.Button
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.akashicrecords.Palette
import kotlinx.coroutines.launch

// The "Synchronisation" block of the settings dialog: pairing with the hub, state of the last sync.
@Composable
fun SyncSettingsBlock() {
    val context = LocalContext.current
    val scope = rememberCoroutineScope()
    val state by SyncService.state.collectAsState()
    var url by remember { mutableStateOf(state.hubUrl) }
    var code by remember { mutableStateOf("") }
    var pairing by remember { mutableStateOf(false) }
    var message by remember { mutableStateOf<String?>(null) }

    Text("Synchronisation", fontSize = 15.sp)
    Spacer(Modifier.height(4.dp))
    Text(
        "Relie ce téléphone au hub de la maison pour que les recettes suivent entre le PC et le téléphone. " +
            "L'adresse et le code s'obtiennent sur le hub ; le code ne sert qu'une fois. Fonctionne sur le Wi-Fi de la maison.",
        fontSize = 12.sp,
        color = Palette.TextMuted,
    )
    Spacer(Modifier.height(6.dp))
    Text(message ?: state.text, fontSize = 12.sp, color = if (message == null && !state.isError) Palette.Text else Palette.TextMuted)
    Spacer(Modifier.height(6.dp))
    OutlinedTextField(
        url, { url = it },
        label = { Text("Adresse du hub") },
        placeholder = { Text("http://192.168.1.200:5199") },
        singleLine = true,
        keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Uri),
        modifier = Modifier.fillMaxWidth(),
    )
    OutlinedTextField(
        code, { code = it },
        label = { Text("Code d'appairage") },
        singleLine = true,
        keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Number),
        modifier = Modifier.fillMaxWidth(),
    )
    Spacer(Modifier.height(8.dp))
    // Two lines: three buttons side by side do not fit the dialog's width.
    run {
        Button(
            enabled = !pairing && !state.busy,
            onClick = {
                if (url.isBlank() || code.isBlank()) {
                    message = "Il faut l'adresse du hub et le code affiché par le hub."
                    return@Button
                }
                pairing = true
                message = "Appairage en cours…"
                scope.launch {
                    message = try {
                        SyncService.pair(context, url, code)
                        code = "" // used up
                        null
                    } catch (e: HubException) {
                        "Appairage impossible : ${e.message}"
                    } catch (e: Exception) {
                        "Appairage impossible : ${e.javaClass.simpleName} ${e.message.orEmpty()}"
                    }
                    pairing = false
                }
            },
        ) { Text(if (state.paired) "Appairer à nouveau" else "Appairer") }
        if (state.paired) {
            Row {
                TextButton(enabled = !state.busy, onClick = { message = null; scope.launch { SyncService.run(context) } }) { Text("Synchroniser maintenant") }
                Spacer(Modifier.width(8.dp))
                TextButton(enabled = !state.busy, onClick = { message = null; SyncService.unpair(context) }) { Text("Dissocier") }
            }
        }
    }
}
