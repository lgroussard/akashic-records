package com.akashicrecords.widgets

import android.content.ComponentName
import android.content.Context
import android.content.pm.PackageManager

// Every Akashic widget, so each can be switched on/off in Réglages → Widgets. Switching one off
// disables its receiver: it disappears from the launcher's widget picker (and an already placed
// copy stops loading). Default: all on.
object WidgetCatalog {
    data class Entry(val label: String, val description: String, val receiver: Class<*>)

    val all = listOf(
        Entry("Agenda", "Programme du jour et prochains anniversaires", AgendaWidgetReceiver::class.java),
        Entry("Journal", "La page du jour du journal personnel", JournalWidgetReceiver::class.java),
        Entry("Poésie", "Un poème du jour, présenté comme une page", PoemWidgetReceiver::class.java),
        Entry("Recettes", "Une recette du jour", RecipeWidgetReceiver::class.java),
        Entry("Musique", "Lecture en cours, suivant, téléchargement", MusicWidgetReceiver::class.java),
        Entry("Collections", "Un film « À voir » pour ce soir", WatchlistWidgetReceiver::class.java),
        Entry("Organisation", "Projets actifs et rappels", OrganisationWidgetReceiver::class.java),
        Entry("Archives", "Une photo souvenir du jour", PhotoWidgetReceiver::class.java),
        Entry("Mots croisés", "La grille en cours, à reprendre d'un geste", CrosswordWidgetReceiver::class.java),
    )

    fun isEnabled(context: Context, e: Entry): Boolean =
        context.packageManager.getComponentEnabledSetting(ComponentName(context, e.receiver)) !=
            PackageManager.COMPONENT_ENABLED_STATE_DISABLED

    fun setEnabled(context: Context, e: Entry, enabled: Boolean) {
        context.packageManager.setComponentEnabledSetting(
            ComponentName(context, e.receiver),
            if (enabled) PackageManager.COMPONENT_ENABLED_STATE_ENABLED else PackageManager.COMPONENT_ENABLED_STATE_DISABLED,
            PackageManager.DONT_KILL_APP,
        )
    }
}
