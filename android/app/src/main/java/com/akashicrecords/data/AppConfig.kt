package com.akashicrecords.data

import java.time.LocalDate
import java.time.LocalDateTime

// Same keys as the desktop config.json (subset relevant to the mobile views).
// The mobile app keeps its own copy next to akashic.db in the app files dir.
data class AppConfig(
    val lastActiveSection: String? = null,
    val journauxActiveTab: String? = null,
    val journauxActiveItem: String? = null,
    val organisationActiveTab: String? = null,
    val organisationActiveProjectId: Int? = null,
    val organisationActiveTransitionId: Int? = null,
    val archivesActiveTab: String? = null,
    val archivesAlbumFilterId: Int? = null,
    val archivesFavoritesOnly: Boolean = false,
    val archivesActivePhotoId: Int? = null,
    val collectionsActiveCategory: String? = null,
    val collectionsActiveFilmId: Int? = null,
    val budgetActiveTab: String? = null,
    val budgetActivePeriod: String? = null,
    val tmdbApiKey: String? = null,
    val pinterestToken: String? = null,
    val showFilmOfTheDay: Boolean = true,
    val showDesktopReminders: Boolean = true,
    val filmOfTheDayArtworkId: Int? = null,
    val filmOfTheDayFilmId: Int? = null,
    val filmOfTheDayAnimationId: Int? = null,
    val filmOfTheDayDate: String? = null,
    val filmWidgetLeft: Double? = null,
    val filmWidgetTop: Double? = null,
    val remindersWidgetLeft: Double? = null,
    val remindersWidgetTop: Double? = null,
    val downloaderToolPath: String? = null,
    val downloaderArguments: String? = null,
    val downloaderAutoMode: Boolean = false,
)

object ConfigKeys {
    const val LAST_ACTIVE_SECTION = "LastActiveSection"
    const val JOURNAUX_ACTIVE_TAB = "JournauxActiveTab"
    const val JOURNAUX_ACTIVE_ITEM = "JournauxActiveItem"
    const val ORG_ACTIVE_TAB = "OrganisationActiveTab"
    const val ORG_ACTIVE_PROJECT_ID = "OrganisationActiveProjectId"
    const val ORG_ACTIVE_TRANSITION_ID = "OrganisationActiveTransitionId"
    const val ARCHIVES_ACTIVE_TAB = "ArchivesActiveTab"
    const val ARCHIVES_ALBUM_FILTER_ID = "ArchivesAlbumFilterId"
    const val ARCHIVES_FAVORITES_ONLY = "ArchivesFavoritesOnly"
    const val ARCHIVES_ACTIVE_PHOTO_ID = "ArchivesActivePhotoId"
    const val COLLECTIONS_ACTIVE_CATEGORY = "CollectionsActiveCategory"
    const val COLLECTIONS_ACTIVE_FILM_ID = "CollectionsActiveFilmId"
    const val BUDGET_ACTIVE_TAB = "BudgetActiveTab"
    const val BUDGET_ACTIVE_PERIOD = "BudgetActivePeriod"
    const val TMDB_API_KEY = "TmdbApiKey"
    const val PINTEREST_TOKEN = "PinterestToken"
    const val SHOW_FILM_OF_THE_DAY = "ShowFilmOfTheDay"
    const val SHOW_DESKTOP_REMINDERS = "ShowDesktopReminders"
    const val FILM_OF_THE_DAY_ARTWORK_ID = "FilmOfTheDayArtworkId"
    const val FILM_OF_THE_DAY_FILM_ID = "FilmOfTheDayFilmId"
    const val FILM_OF_THE_DAY_ANIMATION_ID = "FilmOfTheDayAnimationId"
    const val FILM_OF_THE_DAY_DATE = "FilmOfTheDayDate"
    const val DOWNLOADER_TOOL_PATH = "DownloaderToolPath"
    const val DOWNLOADER_ARGUMENTS = "DownloaderArguments"
    const val DOWNLOADER_AUTO_MODE = "DownloaderAutoMode"
}

// Parses an ISO timestamp as written by the desktop ("2026-09-12T00:00:00+02:00")
// or a plain date ("2026-09-12"). Null-safe, never throws on short forms.
object IsoDates {
    fun parse(value: String?): LocalDateTime? {
        if (value.isNullOrEmpty()) return null
        return try {
            LocalDateTime.parse(value)
        } catch (_: Exception) {
            try {
                LocalDate.parse(value).atStartOfDay()
            } catch (_: Exception) {
                null
            }
        }
    }
}
