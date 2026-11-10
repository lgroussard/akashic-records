package com.akashicrecords.journaux

import androidx.compose.foundation.layout.*
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.akashicrecords.Palette
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch
import java.time.LocalDate
import java.time.LocalDateTime
import java.time.format.DateTimeFormatter

// Writes that must outlive the screen that started them (last edit flushed on navigation).
internal val AppScope = CoroutineScope(SupervisorJob() + Dispatchers.IO)

// Last flush launched by an AutoSave leaving the screen; loaders join it before reading so a
// page reopened right away never shows the pre-edit row.
@Volatile
private var lastFlush: kotlinx.coroutines.Job? = null
internal suspend fun awaitPendingWrites() { lastFlush?.join() }

// The desktop writes DateTime.ToString("O") ("2026-09-12T00:00:00.0000000"); same shape here
// so DateTime.Parse on the PC reads phone-written rows unchanged once data flows back.
private val DESKTOP_O = DateTimeFormatter.ofPattern("yyyy-MM-dd'T'HH:mm:ss.SSSSSSS")

internal fun nowIso(): String = LocalDateTime.now().format(DESKTOP_O)
internal fun dateIso(d: LocalDate): String = d.atStartOfDay().format(DESKTOP_O)

// Reads only the date part, whatever the time/offset suffix.
internal fun datePart(iso: String): LocalDate? = runCatching { LocalDate.parse(iso.take(10)) }.getOrNull()

private val FR_DATE = DateTimeFormatter.ofPattern("EEEE d MMMM yyyy", java.util.Locale.FRENCH)
internal fun frenchDate(d: LocalDate): String = d.format(FR_DATE).replaceFirstChar { it.uppercase() }

// Auto-save like the desktop's save-on-blur: [save] runs 600 ms after the last change, and
// once more when the composable leaves (so the last keystrokes are never lost).
@Composable
internal fun <T> AutoSave(value: T, save: suspend (T) -> Unit) {
    val latest by rememberUpdatedState(value)
    var saved by remember { mutableStateOf(value) }
    LaunchedEffect(value) {
        if (value == saved) return@LaunchedEffect
        delay(600)
        save(value)
        saved = value
    }
    DisposableEffect(Unit) {
        onDispose {
            val pending = latest
            if (pending != saved) lastFlush = AppScope.launch { save(pending) }
        }
    }
}

@Composable
internal fun Field(label: String, value: String, onChange: (String) -> Unit, minLines: Int = 1, modifier: Modifier = Modifier) {
    OutlinedTextField(
        value = value,
        onValueChange = onChange,
        label = { Text(label) },
        singleLine = minLines == 1,
        minLines = minLines,
        modifier = modifier.fillMaxWidth().padding(vertical = 3.dp),
    )
}

@Composable
internal fun ConfirmDelete(what: String, onConfirm: () -> Unit, onDismiss: () -> Unit) {
    AlertDialog(
        onDismissRequest = onDismiss,
        title = { Text("Supprimer $what ?") },
        text = { Text("Cette suppression est définitive.") },
        confirmButton = { TextButton(onClick = { onDismiss(); onConfirm() }) { Text("Supprimer") } },
        dismissButton = { TextButton(onClick = onDismiss) { Text("Annuler") } },
    )
}

@Composable
internal fun PageNav(onBack: () -> Unit, onPrev: (() -> Unit)?, onNext: (() -> Unit)?, onDelete: (() -> Unit)?) {
    Row(Modifier.fillMaxWidth().padding(horizontal = 4.dp), verticalAlignment = Alignment.CenterVertically) {
        TextButton(onClick = onBack) { Text("← Liste") }
        Spacer(Modifier.weight(1f))
        TextButton(onClick = { onPrev?.invoke() }, enabled = onPrev != null) { Text("‹ Préc.") }
        TextButton(onClick = { onNext?.invoke() }, enabled = onNext != null) { Text("Suiv. ›") }
        if (onDelete != null) TextButton(onClick = onDelete) { Text("🗑", color = Palette.TextMuted, fontSize = 15.sp) }
    }
}
