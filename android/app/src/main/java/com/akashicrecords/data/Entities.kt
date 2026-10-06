package com.akashicrecords.data

import androidx.room.Entity
import androidx.room.PrimaryKey
import androidx.room.TypeConverter
import java.time.LocalDate
import java.time.LocalDateTime

// Column names mirror the desktop schema (SchemaInitializer) exactly, so the same
// akashic.db opens unchanged. Dates are ISO strings (as the desktop stores TEXT);
// enum-backed columns are plain Int (ordinal). Booleans are native (Room -> INTEGER).

// ---- Collections ----
@Entity(tableName = "Artwork")
data class Artwork(
    @PrimaryKey(autoGenerate = true) val Id: Int = 0,
    val Title: String,
    val Category: Int,
    val Tier: Int,
    val CreatedAt: String,
    val CoverImagePath: String?,
    val WatchPriority: Int,
)

@Entity(tableName = "EvaluationAxis")
data class EvaluationAxis(
    @PrimaryKey(autoGenerate = true) val Id: Int = 0,
    val Name: String,
)

@Entity(tableName = "ArtworkEvaluation")
data class ArtworkEvaluation(
    @PrimaryKey(autoGenerate = true) val Id: Int = 0,
    val ArtworkId: Int,
    val AxisId: Int,
    val Score: Int,
)

@Entity(tableName = "Observation")
data class Observation(
    @PrimaryKey(autoGenerate = true) val Id: Int = 0,
    val ArtworkId: Int,
    val Subject: String,
    val Content: String,
    val CreatedAt: String,
)

// ---- Journaux ----
@Entity(tableName = "JournalEntry")
data class JournalEntry(
    @PrimaryKey(autoGenerate = true) val Id: Int = 0,
    val Title: String,
    val Text: String,
    val EntryDate: String,
    val Tags: String,
)

@Entity(tableName = "JournalPhoto")
data class JournalPhoto(
    @PrimaryKey(autoGenerate = true) val Id: Int = 0,
    val EntryId: Int,
    val ImagePath: String,
)

@Entity(tableName = "Recipe")
data class Recipe(
    @PrimaryKey(autoGenerate = true) val Id: Int = 0,
    val Title: String,
    val Category: String,
    val Ingredients: String,
    val Instructions: String,
    val Notes: String,
    val Tags: String,
    val CoverImagePath: String?,
    val CreatedAt: String,
)

@Entity(tableName = "Recueil")
data class Recueil(
    @PrimaryKey(autoGenerate = true) val Id: Int = 0,
    val Title: String,
    val CreatedAt: String,
    val Summary: String,
)

@Entity(tableName = "Poem")
data class Poem(
    @PrimaryKey(autoGenerate = true) val Id: Int = 0,
    val RecueilId: Int?,
    val Title: String,
    val Text: String,
    val Tags: String,
    val ImagePath: String?,
    val CreatedAt: String,
    val TextAlignment: String,
    val Margin: Double,
    val FontSize: Double,
    val FontFamily: String,
    val Bold: Boolean,
    val Italic: Boolean,
    val RichContent: String,
    val EditorWidth: Double,
)

// ---- Canvas / artistic projects ----
@Entity(tableName = "ArtisticProject")
data class ArtisticProject(
    @PrimaryKey(autoGenerate = true) val Id: Int = 0,
    val Title: String,
    val CreatedAt: String,
)

@Entity(tableName = "Reference")
data class Reference(
    @PrimaryKey(autoGenerate = true) val Id: Int = 0,
    val ObservationId: Int,
    val ProjectId: Int,
)

@Entity(tableName = "CanvasElement")
data class CanvasElement(
    @PrimaryKey(autoGenerate = true) val Id: Int = 0,
    val ProjectId: Int,
    val Type: Int,
    val X: Double,
    val Y: Double,
    val TextContent: String?,
    val ImagePath: String?,
    val ObservationId: Int?,
    val Width: Double?,
    val Height: Double?,
)

@Entity(tableName = "CanvasConnector")
data class CanvasConnector(
    @PrimaryKey(autoGenerate = true) val Id: Int = 0,
    val ProjectId: Int,
    val FromElementId: Int,
    val ToElementId: Int,
)

// ---- Organisation ----
@Entity(tableName = "PersonalProject")
data class PersonalProject(
    @PrimaryKey(autoGenerate = true) val Id: Int = 0,
    val Title: String,
    val Description: String,
    val Status: Int,
    val Deadline: String?,
    val CreatedAt: String,
)

@Entity(tableName = "ProjectTask")
data class ProjectTask(
    @PrimaryKey(autoGenerate = true) val Id: Int = 0,
    val ProjectId: Int,
    val Text: String,
    val IsDone: Boolean,
    val SortOrder: Int,
)

@Entity(tableName = "Transition")
data class Transition(
    @PrimaryKey(autoGenerate = true) val Id: Int = 0,
    val Title: String,
    val Description: String,
    val CurrentState: String,
    val DesiredState: String,
    val Notes: String,
    val Resources: String,
    val ReminderText: String,
    val ShowOnDesktop: Boolean,
    val CreatedAt: String,
)

@Entity(tableName = "TransitionStep")
data class TransitionStep(
    @PrimaryKey(autoGenerate = true) val Id: Int = 0,
    val TransitionId: Int,
    val Text: String,
    val IsDone: Boolean,
    val SortOrder: Int,
)

// ---- Archives (photos) ----
@Entity(tableName = "PhotoAlbum")
data class PhotoAlbum(
    @PrimaryKey(autoGenerate = true) val Id: Int = 0,
    val Name: String,
    val CreatedAt: String,
)

@Entity(tableName = "Photo")
data class Photo(
    @PrimaryKey(autoGenerate = true) val Id: Int = 0,
    val ImagePath: String,
    val Title: String,
    val Description: String,
    val TakenDate: String?,
    val Tags: String,
    val IsFavorite: Boolean,
    val AlbumId: Int?,
    val ImportedAt: String,
)

// ---- Music ----
@Entity(tableName = "MusicTrack")
data class MusicTrack(
    @PrimaryKey(autoGenerate = true) val Id: Int = 0,
    val FilePath: String,
    val Title: String,
    val Artist: String,
    val DurationSeconds: Double?,
    val CoverImagePath: String?,
    val Album: String,
    val AddedAt: String,
)

@Entity(tableName = "Mood")
data class Mood(
    @PrimaryKey(autoGenerate = true) val Id: Int = 0,
    val Name: String,
)

@Entity(tableName = "TrackMood")
data class TrackMood(
    @PrimaryKey(autoGenerate = true) val Id: Int = 0,
    val TrackId: Int,
    val MoodId: Int,
)

@Entity(tableName = "Playlist")
data class Playlist(
    @PrimaryKey(autoGenerate = true) val Id: Int = 0,
    val Name: String,
    val CreatedAt: String,
)

@Entity(tableName = "PlaylistTrack")
data class PlaylistTrack(
    @PrimaryKey(autoGenerate = true) val Id: Int = 0,
    val PlaylistId: Int,
    val TrackId: Int,
    val SortOrder: Int,
)

// ---- Downloader ----
@Entity(tableName = "DownloadItem")
data class DownloadItem(
    @PrimaryKey(autoGenerate = true) val Id: Int = 0,
    val Url: String,
    val Title: String,
    val Status: Int,
    val AddedAt: String,
    val FolderId: Int?,
)

@Entity(tableName = "DownloadFolder")
data class DownloadFolder(
    @PrimaryKey(autoGenerate = true) val Id: Int = 0,
    val Name: String,
    val Subfolder: String?,
    val SortOrder: Int,
)

// ---- Watchlist / Budget / Calendar ----
@Entity(tableName = "WatchlistItem")
data class WatchlistItem(
    @PrimaryKey(autoGenerate = true) val Id: Int = 0,
    val Title: String,
    val Category: Int,
    val Priority: Int,
    val CoverImagePath: String?,
    val AddedAt: String,
)

@Entity(tableName = "BudgetCategory")
data class BudgetCategory(
    @PrimaryKey(autoGenerate = true) val Id: Int = 0,
    val Name: String,
    val Color: String,
    val MonthlyCap: String?,
    val SortOrder: Int,
)

@Entity(tableName = "BudgetTransaction")
data class BudgetTransaction(
    @PrimaryKey(autoGenerate = true) val Id: Int = 0,
    val Date: String,
    val Label: String,
    val Amount: String,
    val CategoryId: Int?,
    val Source: String,
    val ExternalId: String?,
    val ImportedAt: String,
)

@Entity(tableName = "BudgetRule")
data class BudgetRule(
    @PrimaryKey(autoGenerate = true) val Id: Int = 0,
    val Keyword: String,
    val CategoryId: Int,
)

@Entity(tableName = "BudgetPlan")
data class BudgetPlan(
    @PrimaryKey(autoGenerate = true) val Id: Int = 0,
    val Name: String,
    val Notes: String,
    val CreatedAt: String,
)

@Entity(tableName = "BudgetPlanLine")
data class BudgetPlanLine(
    @PrimaryKey(autoGenerate = true) val Id: Int = 0,
    val PlanId: Int,
    val CategoryId: Int,
    val MonthlyAmount: String,
)

@Entity(tableName = "Birthday")
data class Birthday(
    @PrimaryKey(autoGenerate = true) val Id: Int = 0,
    val Name: String,
    val Month: Int,
    val Day: Int,
    val BirthYear: Int?,
    val Notes: String,
)

@Entity(tableName = "CalendarEvent")
data class CalendarEvent(
    @PrimaryKey(autoGenerate = true) val Id: Int = 0,
    val Title: String,
    val Kind: Int,
    val Date: String,
    val EndDate: String?,
    val StartHour: Int?,
    val StartMinute: Int?,
    val RecurringYearly: Boolean,
    val Location: String,
    val Notes: String,
    val CreatedAt: String,
)

@Entity(tableName = "CalendarEventLink")
data class CalendarEventLink(
    @PrimaryKey(autoGenerate = true) val Id: Int = 0,
    val OwnerType: String,
    val OwnerId: Int,
    val Kind: String,
    val Section: String,
    val Title: String,
    val PrimaryId: Int,
    val SecondaryId: Int?,
    val Category: Int?,
)

// ---- Converters ----
class Converters {
    @TypeConverter
    fun fromString(value: String?): LocalDateTime? =
        value?.let { LocalDateTime.parse(it) }

    @TypeConverter
    fun toString(value: LocalDateTime?): String? = value?.toString()
}
