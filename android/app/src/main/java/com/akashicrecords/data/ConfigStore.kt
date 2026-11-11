package com.akashicrecords.data

import android.content.Context
import android.content.SharedPreferences

// Device-local settings, 1:1 keys with the desktop config.json (§ Configuration).
// Secrets (TMDB/Pinterest) stay on-device — the hub never carries them (D1).
class ConfigStore(context: Context) {
    private val prefs: SharedPreferences =
        context.applicationContext.getSharedPreferences("akashic_config", Context.MODE_PRIVATE)

    fun load(): AppConfig = AppConfig(
        lastActiveSection = prefs.getString(ConfigKeys.LAST_ACTIVE_SECTION, null),
        journauxActiveTab = prefs.getString(ConfigKeys.JOURNAUX_ACTIVE_TAB, null),
        journauxActiveItem = prefs.getString(ConfigKeys.JOURNAUX_ACTIVE_ITEM, null),
        organisationActiveTab = prefs.getString(ConfigKeys.ORG_ACTIVE_TAB, null),
        organisationActiveProjectId = prefs.getIntOr(null, ConfigKeys.ORG_ACTIVE_PROJECT_ID),
        organisationActiveTransitionId = prefs.getIntOr(null, ConfigKeys.ORG_ACTIVE_TRANSITION_ID),
        archivesActiveTab = prefs.getString(ConfigKeys.ARCHIVES_ACTIVE_TAB, null),
        archivesAlbumFilterId = prefs.getIntOr(null, ConfigKeys.ARCHIVES_ALBUM_FILTER_ID),
        archivesFavoritesOnly = prefs.getBoolean(ConfigKeys.ARCHIVES_FAVORITES_ONLY, false),
        archivesActivePhotoId = prefs.getIntOr(null, ConfigKeys.ARCHIVES_ACTIVE_PHOTO_ID),
        collectionsActiveCategory = prefs.getString(ConfigKeys.COLLECTIONS_ACTIVE_CATEGORY, null),
        collectionsActiveFilmId = prefs.getIntOr(null, ConfigKeys.COLLECTIONS_ACTIVE_FILM_ID),
        budgetActiveTab = prefs.getString(ConfigKeys.BUDGET_ACTIVE_TAB, null),
        budgetActivePeriod = prefs.getString(ConfigKeys.BUDGET_ACTIVE_PERIOD, null),
        tmdbApiKey = prefs.getString(ConfigKeys.TMDB_API_KEY, null),
        pinterestToken = prefs.getString(ConfigKeys.PINTEREST_TOKEN, null),
        showFilmOfTheDay = prefs.getBoolean(ConfigKeys.SHOW_FILM_OF_THE_DAY, true),
        showDesktopReminders = prefs.getBoolean(ConfigKeys.SHOW_DESKTOP_REMINDERS, true),
        filmOfTheDayArtworkId = prefs.getIntOr(null, ConfigKeys.FILM_OF_THE_DAY_ARTWORK_ID),
        filmOfTheDayFilmId = prefs.getIntOr(null, ConfigKeys.FILM_OF_THE_DAY_FILM_ID),
        filmOfTheDayAnimationId = prefs.getIntOr(null, ConfigKeys.FILM_OF_THE_DAY_ANIMATION_ID),
        filmOfTheDayDate = prefs.getString(ConfigKeys.FILM_OF_THE_DAY_DATE, null),
        downloaderToolPath = prefs.getString(ConfigKeys.DOWNLOADER_TOOL_PATH, null),
        downloaderArguments = prefs.getString(ConfigKeys.DOWNLOADER_ARGUMENTS, null),
        downloaderAutoMode = prefs.getBoolean(ConfigKeys.DOWNLOADER_AUTO_MODE, false),
    )

    fun saveSection(section: String?) = putString(ConfigKeys.LAST_ACTIVE_SECTION, section)
    // Phone only: the mini-player shrunk to its bubble (⌄), kept across launches.
    fun miniPlayerCollapsed() = prefs.getBoolean("mini_player_collapsed", false)
    fun saveMiniPlayerCollapsed(v: Boolean) = prefs.edit().putBoolean("mini_player_collapsed", v).apply()
    // Where the bubble was dragged, in px from its starting spot.
    fun miniBubbleOffset() = androidx.compose.ui.geometry.Offset(prefs.getFloat("mini_bubble_x", 0f), prefs.getFloat("mini_bubble_y", 0f))
    fun saveMiniBubbleOffset(o: androidx.compose.ui.geometry.Offset) =
        prefs.edit().putFloat("mini_bubble_x", o.x).putFloat("mini_bubble_y", o.y).apply()

    fun saveJournauxTab(tab: String?) = putString(ConfigKeys.JOURNAUX_ACTIVE_TAB, tab)
    fun saveJournauxItem(item: String?) = putString(ConfigKeys.JOURNAUX_ACTIVE_ITEM, item)
    fun saveOrganisationTab(tab: String?) = putString(ConfigKeys.ORG_ACTIVE_TAB, tab)
    fun saveArchivesTab(tab: String?) = putString(ConfigKeys.ARCHIVES_ACTIVE_TAB, tab)
    fun saveCollectionsCategory(c: String?) = putString(ConfigKeys.COLLECTIONS_ACTIVE_CATEGORY, c)

    // Modules switched off on THIS phone (see Modules.catalog). Same key as the desktop config, but never synced.
    fun disabledModules(): Set<String> = prefs.getStringSet(DISABLED_MODULES, null)?.toSet() ?: emptySet()
    fun setModuleEnabled(key: String, enabled: Boolean): Set<String> {
        val next = disabledModules().let { if (enabled) it - key else it + key }
        prefs.edit().putStringSet(DISABLED_MODULES, next).apply()
        return next
    }

    // Lecture (phone only): the book left open (reopened on return, like the other sections) and the reader look.
    fun lectureOpenBook(): Int? = if (prefs.contains(LECTURE_OPEN_BOOK)) prefs.getInt(LECTURE_OPEN_BOOK, 0) else null
    fun saveLectureOpenBook(id: Int?) = prefs.edit().apply { if (id == null) remove(LECTURE_OPEN_BOOK) else putInt(LECTURE_OPEN_BOOK, id) }.apply()
    fun readerLook(): Triple<Int, String, String> = Triple(
        prefs.getInt(READER_FONT_SCALE, 100), prefs.getString(READER_FONT, null) ?: "serif", prefs.getString(READER_THEME, null) ?: "dark",
    )
    fun saveReaderLook(scale: Int, font: String, theme: String) =
        prefs.edit().putInt(READER_FONT_SCALE, scale).putString(READER_FONT, font).putString(READER_THEME, theme).apply()

    // Lecture → "Trouver des livres": the user's own OPDS catalogues (name + address, in order) and the sources
    // switched off in the search. Stored as JSON.
    fun lectureCatalogs(): List<Pair<String, String>> = runCatching {
        val a = org.json.JSONArray(prefs.getString(LECTURE_CATALOGS, null) ?: "[]")
        (0 until a.length()).map { a.getJSONObject(it).let { o -> o.getString("name") to o.getString("url") } }
    }.getOrDefault(emptyList())
    fun saveLectureCatalogs(list: List<Pair<String, String>>) = putString(
        LECTURE_CATALOGS,
        org.json.JSONArray(list.map { (n, u) -> org.json.JSONObject().put("name", n).put("url", u) }).toString(),
    )
    fun lectureSourcesOff(): Set<String> = prefs.getStringSet(LECTURE_SOURCES_OFF, null)?.toSet() ?: emptySet()
    fun saveLectureSourcesOff(off: Set<String>) = prefs.edit().putStringSet(LECTURE_SOURCES_OFF, off).apply()

    // Mobile-only: folder (relative to the shared storage root) scanned as the music library.
    // Mirrors the desktop <exe>/music/ folder: top-level subfolders are albums.
    fun musicFolder(): String = prefs.getString(MUSIC_FOLDER, null) ?: DEFAULT_MUSIC_FOLDER
    fun saveMusicFolder(folder: String) = putString(MUSIC_FOLDER, folder.trim().trim('/'))

    companion object {
        const val MUSIC_FOLDER = "MobileMusicFolder"
        const val DISABLED_MODULES = "DisabledModules"
        const val LECTURE_OPEN_BOOK = "LectureOpenBook"
        const val READER_FONT_SCALE = "ReaderFontScale"
        const val READER_FONT = "ReaderFont"
        const val READER_THEME = "ReaderTheme"
        const val LECTURE_CATALOGS = "LectureCatalogs"
        const val LECTURE_SOURCES_OFF = "LectureSourcesOff"
        const val DEFAULT_MUSIC_FOLDER = "Music/Akashic"
    }

    private fun putString(key: String, value: String?) {
        prefs.edit().apply {
            if (value == null) remove(key) else putString(key, value)
        }.apply()
    }

    private fun SharedPreferences.getIntOr(default: Int?, key: String): Int? =
        if (contains(key)) getInt(key, 0) else default
}
