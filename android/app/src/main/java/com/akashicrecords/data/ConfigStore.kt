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
    fun saveJournauxTab(tab: String?) = putString(ConfigKeys.JOURNAUX_ACTIVE_TAB, tab)
    fun saveJournauxItem(item: String?) = putString(ConfigKeys.JOURNAUX_ACTIVE_ITEM, item)
    fun saveOrganisationTab(tab: String?) = putString(ConfigKeys.ORG_ACTIVE_TAB, tab)
    fun saveArchivesTab(tab: String?) = putString(ConfigKeys.ARCHIVES_ACTIVE_TAB, tab)
    fun saveCollectionsCategory(c: String?) = putString(ConfigKeys.COLLECTIONS_ACTIVE_CATEGORY, c)

    private fun putString(key: String, value: String?) {
        prefs.edit().apply {
            if (value == null) remove(key) else putString(key, value)
        }.apply()
    }

    private fun SharedPreferences.getIntOr(default: Int?, key: String): Int? =
        if (contains(key)) getInt(key, 0) else default
}
