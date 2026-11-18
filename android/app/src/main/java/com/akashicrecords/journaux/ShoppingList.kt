package com.akashicrecords.journaux

import androidx.activity.compose.BackHandler
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextDecoration
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.akashicrecords.Palette
import com.akashicrecords.data.AkashicDatabase
import com.akashicrecords.data.Recipe
import com.akashicrecords.data.ShoppingItem
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import kotlin.math.roundToInt

// The shopping checklist, opened from the recipe list (🛒). It is filled from recipes — pick some, say for how
// many meals and people, and their ingredients are scaled and added up — or by hand, then ticked off in the
// shop. Phone only; see ShoppingItem.

// Portions the recipe gives as written: its own field, else a "Pour 4 personnes" line in the ingredients.
internal fun servingsOf(r: Recipe): Int? = r.Servings.takeIf { it > 0 } ?: ShoppingParser.servingsIn(r.Ingredients)

// Adds a recipe's ingredients, scaled by [factor], to the list. A product already waiting on the list (not
// ticked, same name and unit) grows instead of appearing twice.
private suspend fun addRecipeToList(db: AkashicDatabase, recipe: Recipe, factor: Double) {
    val dao = db.shoppingItemDao()
    val waiting = dao.getAll().filter { !it.IsDone }.associateBy { ShoppingParser.key(it.Name, it.Unit) }.toMutableMap()
    for (line in ShoppingParser.parse(recipe.Ingredients)) {
        val key = ShoppingParser.key(line.name, line.unit)
        val quantity = line.quantity?.times(factor)
        val existing = waiting[key]
        if (existing == null) {
            val item = ShoppingItem(
                Name = line.name, Quantity = quantity, Unit = line.unit, IsDone = false,
                Source = recipe.Title, CreatedAt = nowIso(),
            )
            waiting[key] = item.copy(Id = dao.add(item).toInt())
            continue
        }
        val sources = existing.Source.split(" · ").filter { it.isNotBlank() }
        val merged = existing.copy(
            // An amount only adds to an amount; a line without one ("Sel, poivre") stays as it is.
            Quantity = if (existing.Quantity != null && quantity != null) existing.Quantity + quantity else existing.Quantity,
            Source = (sources + recipe.Title).distinct().joinToString(" · "),
        )
        dao.update(merged)
        waiting[key] = merged
    }
}

@Composable
internal fun ShoppingList(db: AkashicDatabase, recipes: List<Recipe>, onBack: () -> Unit) {
    val scope = rememberCoroutineScope()
    var items by remember { mutableStateOf<List<ShoppingItem>>(emptyList()) }
    var picking by remember { mutableStateOf(false) }
    var manual by remember { mutableStateOf("") }
    var confirmClear by remember { mutableStateOf(false) }

    suspend fun reload() {
        items = withContext(Dispatchers.IO) { db.shoppingItemDao().getAll() }
    }
    LaunchedEffect(Unit) { reload() }

    if (picking) {
        RecipePicker(
            recipes,
            onBack = { picking = false },
            onAdd = { chosen ->
                scope.launch {
                    withContext(Dispatchers.IO) { chosen.forEach { (recipe, factor) -> addRecipeToList(db, recipe, factor) } }
                    reload()
                    picking = false
                }
            },
        )
        return
    }

    BackHandler(onBack = onBack)
    val todo = items.filter { !it.IsDone }
    val done = items.filter { it.IsDone }

    fun toggle(item: ShoppingItem) {
        scope.launch {
            withContext(Dispatchers.IO) { db.shoppingItemDao().update(item.copy(IsDone = !item.IsDone)) }
            reload()
        }
    }

    fun addManual() {
        val text = manual.trim()
        if (text.isEmpty()) return
        manual = ""
        scope.launch {
            withContext(Dispatchers.IO) {
                db.shoppingItemDao().add(ShoppingItem(Name = text, Quantity = null, Unit = "", IsDone = false, Source = "", CreatedAt = nowIso()))
            }
            reload()
        }
    }

    Column(Modifier.fillMaxSize()) {
        Row(Modifier.fillMaxWidth().padding(horizontal = 4.dp), verticalAlignment = Alignment.CenterVertically) {
            TextButton(onClick = onBack) { Text("← Recettes") }
            Spacer(Modifier.weight(1f))
            TextButton(onClick = { picking = true }) { Text("＋ Depuis des recettes") }
        }
        Row(Modifier.padding(horizontal = 12.dp), verticalAlignment = Alignment.CenterVertically) {
            OutlinedTextField(
                manual, { manual = it },
                placeholder = { Text("Ajouter un article") },
                singleLine = true,
                modifier = Modifier.weight(1f),
            )
            Spacer(Modifier.width(8.dp))
            Button(onClick = { addManual() }, enabled = manual.isNotBlank()) { Text("+") }
        }
        LazyColumn(Modifier.fillMaxWidth().weight(1f)) {
            if (items.isEmpty()) item {
                Text(
                    "Liste vide. « ＋ Depuis des recettes » la remplit avec les ingrédients des recettes choisies.",
                    color = Palette.TextMuted, modifier = Modifier.padding(12.dp),
                )
            }
            // No item keys on purpose: a ticked line moves to the bottom block, and a keyed list would scroll after it
            // when it was the first one on screen. Without keys the view stays where the user was reading.
            items(todo) { ShoppingRow(it) { toggle(it) } }
            if (done.isNotEmpty()) {
                item {
                    Row(Modifier.fillMaxWidth().padding(start = 12.dp, top = 10.dp), verticalAlignment = Alignment.CenterVertically) {
                        Text("Dans le panier (${done.size})", fontSize = 12.sp, color = Palette.TextMuted, modifier = Modifier.weight(1f))
                        TextButton(onClick = {
                            scope.launch {
                                withContext(Dispatchers.IO) { db.shoppingItemDao().deleteDone() }
                                reload()
                            }
                        }) { Text("Retirer les cochés") }
                    }
                }
                items(done) { ShoppingRow(it) { toggle(it) } }
            }
            if (items.isNotEmpty()) item {
                TextButton(onClick = { confirmClear = true }, modifier = Modifier.padding(start = 4.dp, top = 8.dp, bottom = 24.dp)) {
                    Text("Vider la liste", color = Palette.TextMuted)
                }
            }
        }
    }

    if (confirmClear) {
        ConfirmDelete("toute la liste de courses", onConfirm = {
            scope.launch {
                withContext(Dispatchers.IO) { db.shoppingItemDao().deleteAll() }
                reload()
            }
        }, onDismiss = { confirmClear = false })
    }
}

@Composable
private fun ShoppingRow(item: ShoppingItem, onToggle: () -> Unit) {
    Row(
        Modifier.fillMaxWidth().clickable(onClick = onToggle).padding(horizontal = 4.dp),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        Checkbox(checked = item.IsDone, onCheckedChange = { onToggle() })
        Column(Modifier.padding(vertical = 4.dp)) {
            Text(
                ShoppingParser.display(item.Name, item.Quantity, item.Unit),
                color = if (item.IsDone) Palette.TextMuted else Color.Unspecified,
                textDecoration = if (item.IsDone) TextDecoration.LineThrough else null,
            )
            if (item.Source.isNotBlank()) Text(item.Source, fontSize = 11.sp, color = Palette.TextFaint)
        }
    }
}

// Pick the recipes to shop for. "Personnes" applies to all of them; each ticked recipe has its own number of
// meals, which starts at what the recipe gives as written. A recipe that does not say its portions cannot be
// scaled: its quantities go in unchanged.
@Composable
private fun RecipePicker(recipes: List<Recipe>, onBack: () -> Unit, onAdd: (List<Pair<Recipe, Double>>) -> Unit) {
    BackHandler(onBack = onBack)
    var people by rememberSaveable { mutableStateOf(1) }
    // Recipe id -> meals wanted, for the ticked recipes.
    var meals by remember { mutableStateOf<Map<Int, Int>>(emptyMap()) }

    fun factorOf(r: Recipe): Double {
        val servings = servingsOf(r) ?: return 1.0
        return (meals[r.Id] ?: 1) * people / servings.toDouble()
    }

    Column(Modifier.fillMaxSize()) {
        Row(Modifier.fillMaxWidth().padding(horizontal = 4.dp), verticalAlignment = Alignment.CenterVertically) {
            TextButton(onClick = onBack) { Text("← Liste") }
            Spacer(Modifier.weight(1f))
            Button(
                onClick = { onAdd(recipes.filter { it.Id in meals }.map { it to factorOf(it) }) },
                enabled = meals.isNotEmpty(),
                modifier = Modifier.padding(end = 8.dp),
            ) { Text("Ajouter à la liste") }
        }
        Row(Modifier.fillMaxWidth().padding(horizontal = 12.dp), verticalAlignment = Alignment.CenterVertically) {
            Text("Personnes", modifier = Modifier.weight(1f))
            Stepper(people, min = 1) { people = it }
        }
        HorizontalDivider()
        LazyColumn(Modifier.fillMaxWidth().weight(1f)) {
            if (recipes.isEmpty()) item {
                Text("Aucune recette dans le journal.", color = Palette.TextMuted, modifier = Modifier.padding(12.dp))
            }
            items(recipes, key = { it.Id }) { r ->
                val servings = servingsOf(r)
                val chosen = r.Id in meals
                fun toggle() {
                    // Starts at the number of meals the recipe gives these people as written, so nothing is scaled.
                    meals = if (chosen) meals - r.Id else meals + (r.Id to ((servings ?: 1).toDouble() / people).roundToInt().coerceAtLeast(1))
                }
                Row(
                    Modifier.fillMaxWidth().clickable { toggle() }.padding(horizontal = 4.dp),
                    verticalAlignment = Alignment.CenterVertically,
                ) {
                    Checkbox(checked = chosen, onCheckedChange = { toggle() })
                    Column(Modifier.weight(1f).padding(vertical = 6.dp)) {
                        Text(r.Title, fontWeight = FontWeight.Bold)
                        Text(
                            when {
                                servings == null -> "Portions non indiquées : quantités de la recette, sans ajustement"
                                chosen -> "Recette pour $servings portions · quantités × ${factorText(factorOf(r))}"
                                else -> "Recette pour $servings portions"
                            },
                            fontSize = 11.sp, color = Palette.TextMuted,
                        )
                    }
                    if (chosen && servings != null) {
                        Column(horizontalAlignment = Alignment.CenterHorizontally) {
                            Text("Repas", fontSize = 10.sp, color = Palette.TextFaint)
                            Stepper(meals[r.Id] ?: 1, min = 1) { meals = meals + (r.Id to it) }
                        }
                    }
                }
            }
        }
    }
}

private fun factorText(f: Double): String {
    val r = (f * 100).roundToInt() / 100.0
    return if (r == r.toInt().toDouble()) r.toInt().toString() else r.toString().replace('.', ',')
}

@Composable
private fun Stepper(value: Int, min: Int, onChange: (Int) -> Unit) {
    Row(verticalAlignment = Alignment.CenterVertically) {
        TextButton(onClick = { onChange(value - 1) }, enabled = value > min) { Text("−", fontSize = 18.sp) }
        Text(value.toString(), fontWeight = FontWeight.Bold)
        TextButton(onClick = { onChange(value + 1) }, enabled = value < 99) { Text("+", fontSize = 18.sp) }
    }
}
