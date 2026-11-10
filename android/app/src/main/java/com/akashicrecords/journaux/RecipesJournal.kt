package com.akashicrecords.journaux

import androidx.activity.compose.BackHandler
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.PickVisualMediaRequest
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.clickable
import androidx.compose.foundation.horizontalScroll
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.akashicrecords.CardBlock
import com.akashicrecords.Palette
import com.akashicrecords.data.AkashicDatabase
import com.akashicrecords.data.Recipe
import com.akashicrecords.media.LocalImage
import com.akashicrecords.media.MediaFiles
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext

// Desktop "Journal de recettes": search (title/tags/ingredients) + category chips + list,
// then one recipe page with photo and auto-saved fields; Préc./Suiv. walk the filtered set.
@Composable
internal fun RecipesJournal(db: AkashicDatabase, initialOpen: Int? = null) {
    val scope = rememberCoroutineScope()
    var recipes by remember { mutableStateOf<List<Recipe>>(emptyList()) }
    var search by remember { mutableStateOf("") }
    var category by remember { mutableStateOf<String?>(null) }
    var openId by remember { mutableStateOf(initialOpen) }

    suspend fun reload() {
        recipes = withContext(Dispatchers.IO) { db.recipeDao().getAll() }
    }
    LaunchedEffect(Unit) { reload() }

    val categories = recipes.map { it.Category.trim() }.filter { it.isNotEmpty() }.distinct().sorted()
    val filtered = recipes.filter { r ->
        (category == null || r.Category.trim() == category) &&
            (search.isBlank() || listOf(r.Title, r.Tags, r.Ingredients).any { it.contains(search, true) })
    }

    val open = openId
    if (open != null) {
        BackHandler { openId = null; scope.launch { reload() } }
        val index = filtered.indexOfFirst { it.Id == open }
        PageNav(
            onBack = { openId = null; scope.launch { reload() } },
            onPrev = filtered.getOrNull(index - 1)?.let { p -> { openId = p.Id } },
            onNext = filtered.getOrNull(index + 1)?.takeIf { index >= 0 }?.let { n -> { openId = n.Id } },
            onDelete = null,
        )
        key(open) {
            RecipeLoader(db, open, onDeleted = { openId = null; scope.launch { reload() } }, onSaved = { scope.launch { reload() } })
        }
        return
    }

    Column(Modifier.fillMaxSize()) {
        Row(Modifier.padding(horizontal = 12.dp), verticalAlignment = Alignment.CenterVertically) {
            OutlinedTextField(
                search, { search = it },
                placeholder = { Text("Titre, tag, ingrédient") },
                singleLine = true,
                modifier = Modifier.weight(1f),
            )
            Spacer(Modifier.width(8.dp))
            Button(onClick = {
                scope.launch {
                    val id = withContext(Dispatchers.IO) {
                        db.recipeDao().add(
                            Recipe(
                                Title = "Nouvelle recette", Category = category ?: "", Ingredients = "", Instructions = "",
                                Notes = "", Tags = "", CoverImagePath = null, CreatedAt = nowIso(),
                            ),
                        ).toInt()
                    }
                    reload()
                    search = ""
                    openId = id
                }
            }) { Text("+") }
        }
        if (categories.isNotEmpty()) {
            Row(Modifier.horizontalScroll(rememberScrollState()).padding(horizontal = 12.dp)) {
                FilterChip(selected = category == null, onClick = { category = null }, label = { Text("Toutes") })
                categories.forEach { c ->
                    Spacer(Modifier.width(4.dp))
                    FilterChip(selected = category == c, onClick = { category = if (category == c) null else c }, label = { Text(c) })
                }
            }
        }
        if (filtered.isEmpty()) {
            Text(if (recipes.isEmpty()) "Aucune recette. « + » pour en créer une." else "Aucun résultat.", color = Palette.TextMuted, modifier = Modifier.padding(12.dp))
        }
        LazyColumn(Modifier.fillMaxWidth().weight(1f)) {
            items(filtered, key = { it.Id }) { r ->
                Row(
                    Modifier.fillMaxWidth().clickable { openId = r.Id }.padding(horizontal = 12.dp, vertical = 6.dp),
                    verticalAlignment = Alignment.CenterVertically,
                ) {
                    LocalImage(r.CoverImagePath, Modifier.size(48.dp).clip(RoundedCornerShape(6.dp)), maxSide = 200)
                    Spacer(Modifier.width(10.dp))
                    Column {
                        Text(r.Title, fontWeight = FontWeight.Bold)
                        if (r.Category.isNotBlank()) Text(r.Category, fontSize = 11.sp, color = Palette.TextMuted)
                    }
                }
            }
        }
    }
}

// Always reads the row fresh from the DB, so a page reopened after Préc./Suiv. never shows a stale copy.
@Composable
private fun RecipeLoader(db: AkashicDatabase, id: Int, onDeleted: () -> Unit, onSaved: () -> Unit) {
    var recipe by remember { mutableStateOf<Recipe?>(null) }
    LaunchedEffect(id) { awaitPendingWrites(); recipe = withContext(Dispatchers.IO) { db.recipeDao().getById(id) } }
    recipe?.let { RecipePage(db, it, onDeleted, onSaved) }
}

@Composable
private fun RecipePage(db: AkashicDatabase, recipe: Recipe, onDeleted: () -> Unit, onSaved: () -> Unit) {
    val context = LocalContext.current
    val scope = rememberCoroutineScope()
    var r by remember { mutableStateOf(recipe) }
    var confirm by remember { mutableStateOf(false) }

    AutoSave(r) { v -> withContext(Dispatchers.IO) { db.recipeDao().update(v) } }

    val pick = rememberLauncherForActivityResult(ActivityResultContracts.PickVisualMedia()) { uri ->
        if (uri != null) scope.launch {
            val old = r.CoverImagePath
            val rel = withContext(Dispatchers.IO) { MediaFiles.import(context, uri, "recipes") }
            r = r.copy(CoverImagePath = rel)
            withContext(Dispatchers.IO) {
                db.recipeDao().update(r)
                MediaFiles.delete(context, old) // only after the DB points at the new file
            }
            onSaved()
        }
    }

    Column(Modifier.fillMaxSize().verticalScroll(rememberScrollState()).padding(horizontal = 12.dp)) {
        LocalImage(
            r.CoverImagePath,
            Modifier.fillMaxWidth().height(180.dp).clip(RoundedCornerShape(10.dp))
                .clickable { pick.launch(PickVisualMediaRequest(ActivityResultContracts.PickVisualMedia.ImageOnly)) },
        )
        Text("Toucher la photo pour la changer", fontSize = 10.sp, color = Palette.TextFaint)
        Field("Titre", r.Title, { r = r.copy(Title = it) })
        Field("Catégorie", r.Category, { r = r.copy(Category = it) })
        Field("Ingrédients", r.Ingredients, { r = r.copy(Ingredients = it) }, minLines = 4)
        Field("Préparation", r.Instructions, { r = r.copy(Instructions = it) }, minLines = 6)
        Field("Notes personnelles", r.Notes, { r = r.copy(Notes = it) }, minLines = 2)
        Field("Tags", r.Tags, { r = r.copy(Tags = it) })
        TextButton(onClick = { confirm = true }) { Text("Supprimer la recette", color = Palette.TextMuted) }
        Spacer(Modifier.height(24.dp))
    }

    if (confirm) {
        ConfirmDelete("« ${r.Title} »", onConfirm = {
            AppScope.launch {
                MediaFiles.delete(context, r.CoverImagePath)
                db.recipeDao().deleteById(r.Id)
                withContext(Dispatchers.Main) { onDeleted() }
            }
        }, onDismiss = { confirm = false })
    }
}
