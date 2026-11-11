package com.akashicrecords

import android.content.Context
import android.content.Intent
import androidx.core.content.pm.ShortcutInfoCompat
import androidx.core.content.pm.ShortcutManagerCompat
import androidx.core.graphics.drawable.IconCompat
import com.akashicrecords.calendar.Reminders

// Colored home-screen icons that open one section directly (pinned shortcuts). The launcher asks
// for confirmation, then the icon sits on the home screen like an app.
object HomeIcons {
    data class Icon(val id: String, val label: String, val description: String, val iconRes: Int, val glyphRes: Int, val color: Long, val section: String, val tab: String?)

    val all = listOf(
        Icon("pin_music", "Musique", "Ta bibliothèque musicale", R.drawable.pin_music, R.drawable.g_white_music, 0xFFF06CA8, "Musique", "Library"),
        Icon("pin_poetry", "Poésie", "Le recueil de poésie", R.drawable.pin_poetry, R.drawable.g_white_poetry, 0xFF9B7BFF, "Journaux", "Poetry"),
        Icon("pin_calendar", "Calendrier", "Le planning et les événements", R.drawable.pin_calendar, R.drawable.g_white_calendar, 0xFF34C77B, "Calendrier", null),
        Icon("pin_reading", "Lecture", "Reprendre le livre en cours", R.drawable.pin_reading, R.drawable.g_white_book, 0xFFE8A33D, "Lecture", null),
        // One per game of the Jeux section (games.GAMES), opening the game itself.
        Icon("pin_crossword", "Mots croisés", "Tes grilles de mots croisés", R.drawable.pin_crossword, R.drawable.g_white_crossword, 0xFF3CB6C9, "Jeux", "Crosswords"),
    )

    fun supported(context: Context) = ShortcutManagerCompat.isRequestPinShortcutSupported(context)

    // False when the launcher does not support it.
    fun pin(context: Context, icon: Icon): Boolean {
        if (!supported(context)) return false
        val intent = Intent(context, MainActivity::class.java).apply {
            action = Intent.ACTION_VIEW
            putExtra(Reminders.EXTRA_SECTION, icon.section)
            icon.tab?.let { putExtra(Launch.EXTRA_TAB, it) }
        }
        val info = ShortcutInfoCompat.Builder(context, icon.id)
            .setShortLabel(icon.label)
            .setLongLabel(icon.description)
            .setIcon(IconCompat.createWithResource(context, icon.iconRes))
            .setIntent(intent)
            .build()
        return ShortcutManagerCompat.requestPinShortcut(context, info, null)
    }
}
