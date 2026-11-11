package com.akashicrecords.games

import android.widget.Toast
import androidx.activity.compose.BackHandler
import androidx.compose.foundation.Image
import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.ui.graphics.Color
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.res.painterResource
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.akashicrecords.HomeIcons
import com.akashicrecords.Palette
import com.akashicrecords.data.AkashicDatabase
import com.akashicrecords.data.Modules

// A game of the Jeux section. [key] = its tab key ("Jeux.<key>" in the modules, the tab of its home-screen icon).
data class Game(val key: String, val label: String, val description: String, val glyphRes: Int, val color: Long, val homeIconId: String)

val GAMES = listOf(
    Game("Crosswords", "Mots croisés", "Grilles de 7 à 9 cases, ou importées (.ipuz)", com.akashicrecords.R.drawable.g_white_crossword, 0xFF3CB6C9, "pin_crossword"),
)

// Plus → Jeux: every game in one place, each with a button to put its own icon on the home screen.
// [openGame]: the game to open at once (from its home-screen icon or widget); [openItem]: the grid to open in it.
@Composable
fun GamesSection(db: AkashicDatabase, disabled: Set<String>, openGame: String? = null, openItem: Int? = null) {
    var open by remember(openGame) { mutableStateOf(openGame?.takeIf { key -> GAMES.any { it.key == key } }) }
    when (open) {
        "Crosswords" -> com.akashicrecords.crossword.CrosswordSection(db, onExit = { open = null }, initialOpen = openItem.takeIf { openGame == "Crosswords" })
        else -> GameList(disabled, onOpen = { open = it })
    }
}

@Composable
private fun GameList(disabled: Set<String>, onOpen: (String) -> Unit) {
    val context = LocalContext.current
    val games = GAMES.filter { Modules.isEnabled(disabled, "Jeux", it.key) }
    Column(Modifier.fillMaxSize().padding(16.dp)) {
        if (games.isEmpty()) Text("Tous les jeux sont désactivés dans les réglages.", color = Palette.TextMuted)
        games.forEach { game ->
            Row(
                Modifier.fillMaxWidth().padding(bottom = 10.dp).clip(RoundedCornerShape(12.dp)).background(Palette.Surface1)
                    .clickable { onOpen(game.key) }.padding(12.dp),
                verticalAlignment = Alignment.CenterVertically,
            ) {
                // Same look as its home-screen icon: the white glyph on a colored circle (the pin_* layer-list itself
                // cannot be drawn by Compose).
                Box(Modifier.size(48.dp).clip(CircleShape).background(Color(game.color)), contentAlignment = Alignment.Center) {
                    Image(painterResource(game.glyphRes), contentDescription = null, modifier = Modifier.size(24.dp))
                }
                Spacer(Modifier.width(12.dp))
                Column(Modifier.weight(1f)) {
                    Text(game.label, fontSize = 17.sp)
                    Text(game.description, fontSize = 12.sp, color = Palette.TextMuted)
                }
                // Its own icon on the home screen (the launcher asks to confirm).
                HomeIcons.all.firstOrNull { it.id == game.homeIconId }?.let { icon ->
                    TextButton(onClick = {
                        if (!HomeIcons.pin(context, icon)) Toast.makeText(context, "Ce lanceur ne permet pas d'ajouter d'icône.", Toast.LENGTH_LONG).show()
                    }) { Text("+ Icône", fontSize = 12.sp) }
                }
            }
        }
    }
}
