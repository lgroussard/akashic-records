package com.akashicrecords.organisation

import android.app.DatePickerDialog
import androidx.activity.compose.BackHandler
import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.text.BasicTextField
import androidx.compose.foundation.text.KeyboardActions
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.SolidColor
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.TextStyle
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.input.ImeAction
import androidx.compose.ui.text.style.TextDecoration
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.akashicrecords.Palette
import com.akashicrecords.data.AkashicDatabase
import com.akashicrecords.data.PersonalProject
import com.akashicrecords.data.ProjectTask
import com.akashicrecords.data.Transition
import com.akashicrecords.data.TransitionStep
import com.akashicrecords.journaux.AutoSave
import com.akashicrecords.journaux.ConfirmDelete
import com.akashicrecords.journaux.Field
import com.akashicrecords.journaux.dateIso
import com.akashicrecords.journaux.datePart
import com.akashicrecords.journaux.frenchDate
import com.akashicrecords.journaux.nowIso
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import java.time.LocalDate

// Organisation on the phone: projects and transitions as a list of cards, each opening its own page where tasks /
// steps are ticked and added, and everything is edited. (Same tables as the PC: a PC import replaces them, until the
// server syncs both ways.)

private val STATUSES = listOf("Actif", "Terminé", "Archivé")

// ---- Projects ----

@Composable
fun ProjectsTab(db: AkashicDatabase) {
    val scope = rememberCoroutineScope()
    var open by remember { mutableStateOf<Int?>(null) }
    var projects by remember { mutableStateOf<List<PersonalProject>>(emptyList()) }
    var tasks by remember { mutableStateOf<Map<Int, List<ProjectTask>>>(emptyMap()) }
    var version by remember { mutableStateOf(0) }

    LaunchedEffect(version, open) {
        if (open != null) return@LaunchedEffect
        withContext(Dispatchers.IO) {
            projects = db.personalProjectDao().getAll().sortedWith(compareBy({ it.Status }, { it.Deadline ?: "9999" }, { it.Title.lowercase() }))
            tasks = projects.associate { it.Id to db.projectTaskDao().forProject(it.Id) }
        }
    }

    open?.let { id -> ProjectPage(db, id, onBack = { open = null; version++ }); return }

    Box(Modifier.fillMaxSize()) {
        if (projects.isEmpty()) Text("Aucun projet. Touche + pour en créer un.", color = Palette.TextMuted, modifier = Modifier.padding(16.dp))
        LazyColumn(Modifier.fillMaxSize(), contentPadding = PaddingValues(bottom = 88.dp)) {
            items(projects, key = { it.Id }) { p ->
                val t = tasks[p.Id].orEmpty()
                Card(onClick = { open = p.Id }) {
                    Row(verticalAlignment = Alignment.CenterVertically) {
                        Text(p.Title.ifBlank { "Sans titre" }, fontSize = 16.sp, fontWeight = FontWeight.SemiBold, modifier = Modifier.weight(1f), maxLines = 1, overflow = TextOverflow.Ellipsis)
                        StatusPill(p.Status)
                    }
                    val deadline = p.Deadline?.let(::datePart)
                    val meta = listOfNotNull(
                        deadline?.let { "Échéance : ${frenchDate(it)}" },
                        if (t.isNotEmpty()) "${t.count { it.IsDone }}/${t.size} tâches" else null,
                    ).joinToString(" · ")
                    if (meta.isNotEmpty()) Text(meta, fontSize = 12.sp, color = if (deadline != null && deadline < LocalDate.now() && p.Status == 0) Palette.Accent else Palette.TextMuted)
                    if (t.isNotEmpty()) Progress(t.count { it.IsDone }.toFloat() / t.size)
                }
            }
        }
        FloatingActionButton(
            onClick = {
                scope.launch {
                    val id = withContext(Dispatchers.IO) {
                        db.personalProjectDao().add(PersonalProject(Title = "Nouveau projet", Description = "", Status = 0, Deadline = null, CreatedAt = nowIso())).toInt()
                    }
                    open = id
                }
            },
            containerColor = Palette.Accent,
            modifier = Modifier.align(Alignment.BottomEnd).padding(16.dp),
        ) { Text("+", fontSize = 24.sp) }
    }
}

@Composable
private fun ProjectPage(db: AkashicDatabase, id: Int, onBack: () -> Unit) {
    BackHandler(onBack = onBack)
    val context = LocalContext.current
    val scope = rememberCoroutineScope()
    var project by remember { mutableStateOf<PersonalProject?>(null) }
    var tasks by remember { mutableStateOf<List<ProjectTask>>(emptyList()) }
    var confirm by remember { mutableStateOf(false) }
    LaunchedEffect(id) {
        withContext(Dispatchers.IO) {
            project = db.personalProjectDao().getById(id)
            tasks = db.projectTaskDao().forProject(id).sortedBy { it.SortOrder }
        }
    }
    val p = project ?: return
    AutoSave(p) { v -> withContext(Dispatchers.IO) { db.personalProjectDao().update(v) } }
    suspend fun reloadTasks() { tasks = withContext(Dispatchers.IO) { db.projectTaskDao().forProject(id).sortedBy { it.SortOrder } } }

    Column(Modifier.fillMaxSize().verticalScroll(rememberScrollState()).padding(horizontal = 16.dp)) {
        TextButton(onClick = onBack) { Text("← Projets") }
        TitleField(p.Title) { project = p.copy(Title = it) }
        Row(Modifier.padding(vertical = 6.dp)) {
            STATUSES.forEachIndexed { i, label ->
                FilterChip(selected = p.Status == i, onClick = { project = p.copy(Status = i) }, label = { Text(label) })
                Spacer(Modifier.width(6.dp))
            }
        }
        val deadline = p.Deadline?.let(::datePart)
        Row(verticalAlignment = Alignment.CenterVertically) {
            TextButton(onClick = {
                val d = deadline ?: LocalDate.now()
                DatePickerDialog(context, { _, y, m, day -> project = p.copy(Deadline = dateIso(LocalDate.of(y, m + 1, day))) }, d.year, d.monthValue - 1, d.dayOfMonth).show()
            }) { Text(deadline?.let { "Échéance : ${frenchDate(it)}" } ?: "+ Échéance") }
            if (deadline != null) TextButton(onClick = { project = p.copy(Deadline = null) }) { Text("✕", color = Palette.TextFaint) }
        }
        Field("Description", p.Description, { project = p.copy(Description = it) }, minLines = 2)

        Text("TÂCHES", fontSize = 11.sp, color = Palette.TextFaint, fontWeight = FontWeight.Bold, modifier = Modifier.padding(top = 14.dp, bottom = 4.dp))
        if (tasks.isNotEmpty()) Progress(tasks.count { it.IsDone }.toFloat() / tasks.size)
        tasks.forEach { t ->
            CheckRow(t.Text, t.IsDone,
                onToggle = { scope.launch { withContext(Dispatchers.IO) { db.projectTaskDao().update(t.copy(IsDone = !t.IsDone)) }; reloadTasks() } },
                onDelete = { scope.launch { withContext(Dispatchers.IO) { db.projectTaskDao().deleteById(t.Id) }; reloadTasks() } },
            )
        }
        AddLine("Nouvelle tâche") { text ->
            scope.launch {
                withContext(Dispatchers.IO) {
                    db.projectTaskDao().add(ProjectTask(ProjectId = id, Text = text, IsDone = false, SortOrder = (tasks.maxOfOrNull { it.SortOrder } ?: -1) + 1))
                }
                reloadTasks()
            }
        }
        TextButton(onClick = { confirm = true }, modifier = Modifier.padding(top = 16.dp)) { Text("Supprimer le projet", color = Palette.TextMuted) }
        Spacer(Modifier.height(32.dp))
    }
    if (confirm) ConfirmDelete("le projet « ${p.Title} » et ses tâches", onConfirm = {
        confirm = false
        scope.launch {
            withContext(Dispatchers.IO) {
                tasks.forEach { db.projectTaskDao().deleteById(it.Id) }
                db.personalProjectDao().deleteById(id)
            }
            onBack()
        }
    }, onDismiss = { confirm = false })
}

// ---- Transitions ----

@Composable
fun TransitionsTab(db: AkashicDatabase) {
    val scope = rememberCoroutineScope()
    var open by remember { mutableStateOf<Int?>(null) }
    var transitions by remember { mutableStateOf<List<Transition>>(emptyList()) }
    var steps by remember { mutableStateOf<Map<Int, List<TransitionStep>>>(emptyMap()) }
    var version by remember { mutableStateOf(0) }

    LaunchedEffect(version, open) {
        if (open != null) return@LaunchedEffect
        withContext(Dispatchers.IO) {
            transitions = db.transitionDao().getAll()
            steps = transitions.associate { it.Id to db.transitionStepDao().forTransition(it.Id) }
        }
    }

    open?.let { id -> TransitionPage(db, id, onBack = { open = null; version++ }); return }

    Box(Modifier.fillMaxSize()) {
        if (transitions.isEmpty()) Text("Aucune transition. Touche + pour en créer une.", color = Palette.TextMuted, modifier = Modifier.padding(16.dp))
        LazyColumn(Modifier.fillMaxSize(), contentPadding = PaddingValues(bottom = 88.dp)) {
            items(transitions, key = { it.Id }) { tr ->
                val s = steps[tr.Id].orEmpty()
                Card(onClick = { open = tr.Id }) {
                    Text(tr.Title.ifBlank { "Sans titre" }, fontSize = 16.sp, fontWeight = FontWeight.SemiBold, maxLines = 1, overflow = TextOverflow.Ellipsis)
                    if (tr.CurrentState.isNotBlank() || tr.DesiredState.isNotBlank()) {
                        Text("${tr.CurrentState.ifBlank { "…" }}  →  ${tr.DesiredState.ifBlank { "…" }}", fontSize = 12.sp, color = Palette.TextMuted, maxLines = 2, overflow = TextOverflow.Ellipsis)
                    }
                    if (s.isNotEmpty()) {
                        Text("${s.count { it.IsDone }}/${s.size} étapes", fontSize = 12.sp, color = Palette.TextMuted)
                        Progress(s.count { it.IsDone }.toFloat() / s.size)
                    }
                }
            }
        }
        FloatingActionButton(
            onClick = {
                scope.launch {
                    val id = withContext(Dispatchers.IO) {
                        db.transitionDao().add(
                            Transition(Title = "Nouvelle transition", Description = "", CurrentState = "", DesiredState = "", Notes = "", Resources = "", ReminderText = "", ShowOnDesktop = false, CreatedAt = nowIso()),
                        ).toInt()
                    }
                    open = id
                }
            },
            containerColor = Palette.Accent,
            modifier = Modifier.align(Alignment.BottomEnd).padding(16.dp),
        ) { Text("+", fontSize = 24.sp) }
    }
}

@Composable
private fun TransitionPage(db: AkashicDatabase, id: Int, onBack: () -> Unit) {
    BackHandler(onBack = onBack)
    val scope = rememberCoroutineScope()
    var tr by remember { mutableStateOf<Transition?>(null) }
    var steps by remember { mutableStateOf<List<TransitionStep>>(emptyList()) }
    var confirm by remember { mutableStateOf(false) }
    LaunchedEffect(id) {
        withContext(Dispatchers.IO) {
            tr = db.transitionDao().getById(id)
            steps = db.transitionStepDao().forTransition(id).sortedBy { it.SortOrder }
        }
    }
    val t = tr ?: return
    AutoSave(t) { v -> withContext(Dispatchers.IO) { db.transitionDao().update(v) } }
    suspend fun reloadSteps() { steps = withContext(Dispatchers.IO) { db.transitionStepDao().forTransition(id).sortedBy { it.SortOrder } } }

    Column(Modifier.fillMaxSize().verticalScroll(rememberScrollState()).padding(horizontal = 16.dp)) {
        TextButton(onClick = onBack) { Text("← Transitions") }
        TitleField(t.Title) { tr = t.copy(Title = it) }
        Field("Aujourd'hui", t.CurrentState, { tr = t.copy(CurrentState = it) }, minLines = 2)
        Field("Vers", t.DesiredState, { tr = t.copy(DesiredState = it) }, minLines = 2)

        Text("ÉTAPES", fontSize = 11.sp, color = Palette.TextFaint, fontWeight = FontWeight.Bold, modifier = Modifier.padding(top = 14.dp, bottom = 4.dp))
        if (steps.isNotEmpty()) Progress(steps.count { it.IsDone }.toFloat() / steps.size)
        steps.forEach { s ->
            CheckRow(s.Text, s.IsDone,
                onToggle = { scope.launch { withContext(Dispatchers.IO) { db.transitionStepDao().update(s.copy(IsDone = !s.IsDone)) }; reloadSteps() } },
                onDelete = { scope.launch { withContext(Dispatchers.IO) { db.transitionStepDao().deleteById(s.Id) }; reloadSteps() } },
            )
        }
        AddLine("Nouvelle étape") { text ->
            scope.launch {
                withContext(Dispatchers.IO) {
                    db.transitionStepDao().add(TransitionStep(TransitionId = id, Text = text, IsDone = false, SortOrder = (steps.maxOfOrNull { it.SortOrder } ?: -1) + 1))
                }
                reloadSteps()
            }
        }
        Field("Notes", t.Notes, { tr = t.copy(Notes = it) }, minLines = 2)
        Field("Ressources", t.Resources, { tr = t.copy(Resources = it) }, minLines = 2)
        TextButton(onClick = { confirm = true }, modifier = Modifier.padding(top = 16.dp)) { Text("Supprimer la transition", color = Palette.TextMuted) }
        Spacer(Modifier.height(32.dp))
    }
    if (confirm) ConfirmDelete("la transition « ${t.Title} » et ses étapes", onConfirm = {
        confirm = false
        scope.launch {
            withContext(Dispatchers.IO) {
                steps.forEach { db.transitionStepDao().deleteById(it.Id) }
                db.transitionDao().deleteById(id)
            }
            onBack()
        }
    }, onDismiss = { confirm = false })
}

// ---- Shared pieces ----

@Composable
private fun Card(onClick: () -> Unit, content: @Composable ColumnScope.() -> Unit) {
    Column(
        Modifier.fillMaxWidth().padding(horizontal = 12.dp, vertical = 5.dp).clip(RoundedCornerShape(12.dp))
            .background(Palette.Surface1).clickable(onClick = onClick).padding(14.dp),
        verticalArrangement = Arrangement.spacedBy(4.dp),
        content = content,
    )
}

@Composable
private fun StatusPill(status: Int) {
    Text(
        STATUSES.getOrElse(status) { "?" }, fontSize = 11.sp,
        color = if (status == 0) Palette.Accent else Palette.TextMuted,
        modifier = Modifier.clip(RoundedCornerShape(10.dp)).background(Palette.Surface3).padding(horizontal = 8.dp, vertical = 2.dp),
    )
}

@Composable
private fun Progress(share: Float) {
    LinearProgressIndicator(progress = { share }, Modifier.fillMaxWidth().padding(top = 2.dp).height(4.dp), color = Palette.Accent)
}

@Composable
private fun TitleField(value: String, onChange: (String) -> Unit) {
    BasicTextField(
        value = value, onValueChange = onChange,
        textStyle = TextStyle(color = Palette.Text, fontSize = 22.sp, fontWeight = FontWeight.Bold),
        cursorBrush = SolidColor(Palette.Accent),
        modifier = Modifier.fillMaxWidth().padding(vertical = 4.dp),
    )
}

@Composable
private fun CheckRow(text: String, done: Boolean, onToggle: () -> Unit, onDelete: () -> Unit) {
    Row(Modifier.fillMaxWidth().clickable(onClick = onToggle), verticalAlignment = Alignment.CenterVertically) {
        Checkbox(checked = done, onCheckedChange = { onToggle() })
        Text(
            text, modifier = Modifier.weight(1f),
            color = if (done) Palette.TextMuted else Palette.Text,
            textDecoration = if (done) TextDecoration.LineThrough else null,
        )
        TextButton(onClick = onDelete) { Text("✕", color = Palette.TextFaint) }
    }
}

@Composable
private fun AddLine(hint: String, onAdd: (String) -> Unit) {
    var text by remember { mutableStateOf("") }
    fun add() { text.trim().takeIf { it.isNotEmpty() }?.let(onAdd); text = "" }
    Row(verticalAlignment = Alignment.CenterVertically) {
        OutlinedTextField(
            text, { text = it }, placeholder = { Text(hint) }, singleLine = true, modifier = Modifier.weight(1f),
            keyboardOptions = KeyboardOptions(imeAction = ImeAction.Done), keyboardActions = KeyboardActions(onDone = { add() }),
        )
        TextButton(onClick = { add() }) { Text("Ajouter") }
    }
}
