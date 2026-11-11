package com.akashicrecords.data

// A section of the app, or one of its sub-tabs, that the user can switch off on this phone.
data class AppModule(val key: String, val label: String, val children: List<AppModule> = emptyList())

// What exists on the phone app, and which parts of it the user has switched off.
// The choice is per device (ConfigStore, never synced). Keys are the desktop's ("Section" or "Section.SubTab",
// see Modules.cs on the PC) so a module present on both apps has one name everywhere; a module that only exists on
// one platform (Budget on the PC, the future reading module on the phone) is absent from the other catalogue.
object Modules {
    // Collections tabs are stored by desktop ArtworkCategory ordinal on the phone; these are the matching keys.
    val COLLECTION_KEYS = listOf("Film", "FilmAnimation", "Anime", "Livre", "VideoGame", "TvSeries")

    val catalog = listOf(
        AppModule("Journaux", "Journaux", listOf(
            AppModule("Journaux.Personal", "Journal personnel"),
            AppModule("Journaux.Poetry", "Poésie"),
            AppModule("Journaux.Recipes", "Recettes"),
            AppModule("Journaux.Artistic", "Archive artistique"),
            AppModule("Journaux.References", "Références"),
        )),
        AppModule("Musique", "Musique"),
        AppModule("Calendrier", "Calendrier"),
        // Phone only (no PC catalogue entry): epub reading.
        AppModule("Lecture", "Lecture"),
        AppModule("Collections", "Collections", listOf(
            AppModule("Collections.Film", "Films"),
            AppModule("Collections.FilmAnimation", "Films d'animation"),
            AppModule("Collections.Anime", "Anime / manga"),
            AppModule("Collections.Livre", "Livres"),
            AppModule("Collections.VideoGame", "Jeux vidéo"),
            AppModule("Collections.TvSeries", "Séries"),
        )),
        AppModule("Organisation", "Organisation", listOf(
            AppModule("Organisation.Projets", "Projets"),
            AppModule("Organisation.Transitions", "Transitions"),
        )),
        AppModule("Archives", "Archives", listOf(
            AppModule("Archives.Photos", "Photos"),
            AppModule("Archives.Familiale", "Archive familiale"),
        )),
    )

    // A sub-tab is off when it or its section is; a section with sub-tabs is off when all of them are,
    // so the display never shows an empty shell.
    fun isEnabled(disabled: Set<String>, key: String): Boolean {
        if (disabled.isEmpty()) return true
        if (key in disabled) return false
        val dot = key.indexOf('.')
        if (dot > 0) return key.substring(0, dot) !in disabled
        val section = catalog.firstOrNull { it.key == key } ?: return true
        return section.children.isEmpty() || section.children.any { it.key !in disabled }
    }

    fun isEnabled(disabled: Set<String>, section: String, tab: String) = isEnabled(disabled, "$section.$tab")

    // The tab to open: the saved one (or the default) while it is on, else the first sub-tab that is.
    fun pickTab(disabled: Set<String>, section: String, saved: String?, default: String): String {
        val wanted = saved ?: default
        if (isEnabled(disabled, section, wanted)) return wanted
        return catalog.firstOrNull { it.key == section }?.children
            ?.map { it.key.substringAfter('.') }
            ?.firstOrNull { isEnabled(disabled, section, it) } ?: wanted
    }

    // Sub-tab chips (label to tag) without the switched-off ones.
    fun visibleTabs(disabled: Set<String>, section: String, options: List<Pair<String, String>>) =
        options.filter { (_, tag) -> isEnabled(disabled, section, tag) }
}
