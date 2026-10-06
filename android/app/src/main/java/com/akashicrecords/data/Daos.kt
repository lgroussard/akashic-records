package com.akashicrecords.data

import androidx.room.Dao
import androidx.room.Delete
import androidx.room.Insert
import androidx.room.Query
import androidx.room.Update

// One DAO per entity. Idiom mirrors the desktop repos: Add returns the new rowid
// (call-sites must back-fill entity.Id themselves — see repo notes), reads are
// ordered, deletes by id. Child rows are removed manually (PRAGMA foreign_keys
// is NOT enabled on desktop either).

@Dao
interface ArtworkDao {
    @Insert
    suspend fun add(e: Artwork): Long

    @Update
    suspend fun update(e: Artwork)

    @Query("DELETE FROM Artwork WHERE Id = :id")
    suspend fun deleteById(id: Int)

    @Query("SELECT * FROM Artwork ORDER BY Title")
    suspend fun getAll(): List<Artwork>

    @Query("SELECT * FROM Artwork WHERE Id = :id")
    suspend fun getById(id: Int): Artwork?
}

@Dao
interface EvaluationAxisDao {
    @Insert
    suspend fun add(e: EvaluationAxis): Long

    @Query("DELETE FROM EvaluationAxis WHERE Id = :id")
    suspend fun deleteById(id: Int)

    @Query("SELECT * FROM EvaluationAxis ORDER BY Name")
    suspend fun getAll(): List<EvaluationAxis>
}

@Dao
interface ArtworkEvaluationDao {
    @Insert
    suspend fun add(e: ArtworkEvaluation): Long

    @Query("DELETE FROM ArtworkEvaluation WHERE Id = :id")
    suspend fun deleteById(id: Int)

    @Query("SELECT * FROM ArtworkEvaluation WHERE ArtworkId = :artworkId")
    suspend fun forArtwork(artworkId: Int): List<ArtworkEvaluation>
}

@Dao
interface ObservationDao {
    @Insert
    suspend fun add(e: Observation): Long

    @Update
    suspend fun update(e: Observation)

    @Query("DELETE FROM Observation WHERE Id = :id")
    suspend fun deleteById(id: Int)

    @Query("SELECT * FROM Observation WHERE ArtworkId = :artworkId ORDER BY CreatedAt")
    suspend fun forArtwork(artworkId: Int): List<Observation>
}

@Dao
interface JournalEntryDao {
    @Insert
    suspend fun add(e: JournalEntry): Long

    @Update
    suspend fun update(e: JournalEntry)

    @Query("DELETE FROM JournalEntry WHERE Id = :id")
    suspend fun deleteById(id: Int)

    @Query("SELECT * FROM JournalEntry ORDER BY EntryDate")
    suspend fun getAll(): List<JournalEntry>

    @Query("SELECT * FROM JournalEntry WHERE Id = :id")
    suspend fun getById(id: Int): JournalEntry?
}

@Dao
interface JournalPhotoDao {
    @Insert
    suspend fun add(e: JournalPhoto): Long

    @Query("DELETE FROM JournalPhoto WHERE Id = :id")
    suspend fun deleteById(id: Int)

    @Query("SELECT * FROM JournalPhoto WHERE EntryId = :entryId")
    suspend fun forEntry(entryId: Int): List<JournalPhoto>
}

@Dao
interface RecipeDao {
    @Insert
    suspend fun add(e: Recipe): Long

    @Update
    suspend fun update(e: Recipe)

    @Query("DELETE FROM Recipe WHERE Id = :id")
    suspend fun deleteById(id: Int)

    @Query("SELECT * FROM Recipe ORDER BY Title")
    suspend fun getAll(): List<Recipe>

    @Query("SELECT * FROM Recipe WHERE Id = :id")
    suspend fun getById(id: Int): Recipe?
}

@Dao
interface RecueilDao {
    @Insert
    suspend fun add(e: Recueil): Long

    @Update
    suspend fun update(e: Recueil)

    @Query("DELETE FROM Recueil WHERE Id = :id")
    suspend fun deleteById(id: Int)

    @Query("SELECT * FROM Recueil ORDER BY Title")
    suspend fun getAll(): List<Recueil>
}

@Dao
interface PoemDao {
    @Insert
    suspend fun add(e: Poem): Long

    @Update
    suspend fun update(e: Poem)

    @Query("DELETE FROM Poem WHERE Id = :id")
    suspend fun deleteById(id: Int)

    @Query("SELECT * FROM Poem ORDER BY Title")
    suspend fun getAll(): List<Poem>

    @Query("SELECT * FROM Poem WHERE Id = :id")
    suspend fun getById(id: Int): Poem?

    @Query("SELECT * FROM Poem WHERE RecueilId = :recueilId ORDER BY Title")
    suspend fun forRecueil(recueilId: Int): List<Poem>
}

@Dao
interface ArtisticProjectDao {
    @Insert
    suspend fun add(e: ArtisticProject): Long

    @Update
    suspend fun update(e: ArtisticProject)

    @Query("DELETE FROM ArtisticProject WHERE Id = :id")
    suspend fun deleteById(id: Int)

    @Query("SELECT * FROM ArtisticProject ORDER BY Title")
    suspend fun getAll(): List<ArtisticProject>
}

@Dao
interface ReferenceDao {
    @Insert
    suspend fun add(e: Reference): Long

    @Query("DELETE FROM Reference WHERE Id = :id")
    suspend fun deleteById(id: Int)

    @Query("SELECT * FROM Reference WHERE ProjectId = :projectId")
    suspend fun forProject(projectId: Int): List<Reference>
}

@Dao
interface CanvasElementDao {
    @Insert
    suspend fun add(e: CanvasElement): Long

    @Update
    suspend fun update(e: CanvasElement)

    @Query("DELETE FROM CanvasElement WHERE Id = :id")
    suspend fun deleteById(id: Int)

    @Query("SELECT * FROM CanvasElement WHERE ProjectId = :projectId")
    suspend fun forProject(projectId: Int): List<CanvasElement>
}

@Dao
interface CanvasConnectorDao {
    @Insert
    suspend fun add(e: CanvasConnector): Long

    @Update
    suspend fun update(e: CanvasConnector)

    @Query("DELETE FROM CanvasConnector WHERE Id = :id")
    suspend fun deleteById(id: Int)

    @Query("SELECT * FROM CanvasConnector WHERE ProjectId = :projectId")
    suspend fun forProject(projectId: Int): List<CanvasConnector>
}

@Dao
interface PersonalProjectDao {
    @Insert
    suspend fun add(e: PersonalProject): Long

    @Update
    suspend fun update(e: PersonalProject)

    @Query("DELETE FROM PersonalProject WHERE Id = :id")
    suspend fun deleteById(id: Int)

    @Query("SELECT * FROM PersonalProject ORDER BY CreatedAt")
    suspend fun getAll(): List<PersonalProject>

    @Query("SELECT * FROM PersonalProject WHERE Id = :id")
    suspend fun getById(id: Int): PersonalProject?
}

@Dao
interface ProjectTaskDao {
    @Insert
    suspend fun add(e: ProjectTask): Long

    @Update
    suspend fun update(e: ProjectTask)

    @Query("DELETE FROM ProjectTask WHERE Id = :id")
    suspend fun deleteById(id: Int)

    @Query("SELECT * FROM ProjectTask WHERE ProjectId = :projectId ORDER BY SortOrder")
    suspend fun forProject(projectId: Int): List<ProjectTask>
}

@Dao
interface TransitionDao {
    @Insert
    suspend fun add(e: Transition): Long

    @Update
    suspend fun update(e: Transition)

    @Query("DELETE FROM Transition WHERE Id = :id")
    suspend fun deleteById(id: Int)

    @Query("SELECT * FROM Transition ORDER BY CreatedAt")
    suspend fun getAll(): List<Transition>

    @Query("SELECT * FROM Transition WHERE Id = :id")
    suspend fun getById(id: Int): Transition?
}

@Dao
interface TransitionStepDao {
    @Insert
    suspend fun add(e: TransitionStep): Long

    @Update
    suspend fun update(e: TransitionStep)

    @Query("DELETE FROM TransitionStep WHERE Id = :id")
    suspend fun deleteById(id: Int)

    @Query("SELECT * FROM TransitionStep WHERE TransitionId = :transitionId ORDER BY SortOrder")
    suspend fun forTransition(transitionId: Int): List<TransitionStep>
}

@Dao
interface PhotoAlbumDao {
    @Insert
    suspend fun add(e: PhotoAlbum): Long

    @Update
    suspend fun update(e: PhotoAlbum)

    @Query("DELETE FROM PhotoAlbum WHERE Id = :id")
    suspend fun deleteById(id: Int)

    @Query("SELECT * FROM PhotoAlbum ORDER BY CreatedAt")
    suspend fun getAll(): List<PhotoAlbum>
}

@Dao
interface PhotoDao {
    @Insert
    suspend fun add(e: Photo): Long

    @Update
    suspend fun update(e: Photo)

    @Query("DELETE FROM Photo WHERE Id = :id")
    suspend fun deleteById(id: Int)

    @Query("SELECT * FROM Photo ORDER BY ImportedAt")
    suspend fun getAll(): List<Photo>

    @Query("SELECT * FROM Photo WHERE Id = :id")
    suspend fun getById(id: Int): Photo?
}

@Dao
interface MusicTrackDao {
    @Insert
    suspend fun add(e: MusicTrack): Long

    @Update
    suspend fun update(e: MusicTrack)

    @Query("DELETE FROM MusicTrack WHERE Id = :id")
    suspend fun deleteById(id: Int)

    @Query("SELECT * FROM MusicTrack ORDER BY Album, Title")
    suspend fun getAll(): List<MusicTrack>
}

@Dao
interface MoodDao {
    @Insert
    suspend fun add(e: Mood): Long

    @Query("DELETE FROM Mood WHERE Id = :id")
    suspend fun deleteById(id: Int)

    @Query("SELECT * FROM Mood ORDER BY Name")
    suspend fun getAll(): List<Mood>
}

@Dao
interface TrackMoodDao {
    @Insert
    suspend fun add(e: TrackMood): Long

    @Query("DELETE FROM TrackMood WHERE Id = :id")
    suspend fun deleteById(id: Int)

    @Query("SELECT * FROM TrackMood WHERE TrackId = :trackId")
    suspend fun forTrack(trackId: Int): List<TrackMood>
}

@Dao
interface PlaylistDao {
    @Insert
    suspend fun add(e: Playlist): Long

    @Query("DELETE FROM Playlist WHERE Id = :id")
    suspend fun deleteById(id: Int)

    @Query("SELECT * FROM Playlist ORDER BY CreatedAt")
    suspend fun getAll(): List<Playlist>
}

@Dao
interface PlaylistTrackDao {
    @Insert
    suspend fun add(e: PlaylistTrack): Long

    @Query("DELETE FROM PlaylistTrack WHERE Id = :id")
    suspend fun deleteById(id: Int)

    @Query("SELECT * FROM PlaylistTrack WHERE PlaylistId = :playlistId ORDER BY SortOrder")
    suspend fun forPlaylist(playlistId: Int): List<PlaylistTrack>
}

@Dao
interface DownloadItemDao {
    @Insert
    suspend fun add(e: DownloadItem): Long

    @Update
    suspend fun update(e: DownloadItem)

    @Query("DELETE FROM DownloadItem WHERE Id = :id")
    suspend fun deleteById(id: Int)

    @Query("SELECT * FROM DownloadItem ORDER BY AddedAt")
    suspend fun getAll(): List<DownloadItem>
}

@Dao
interface DownloadFolderDao {
    @Insert
    suspend fun add(e: DownloadFolder): Long

    @Update
    suspend fun update(e: DownloadFolder)

    @Query("DELETE FROM DownloadFolder WHERE Id = :id")
    suspend fun deleteById(id: Int)

    @Query("SELECT * FROM DownloadFolder ORDER BY SortOrder")
    suspend fun getAll(): List<DownloadFolder>
}

@Dao
interface WatchlistItemDao {
    @Insert
    suspend fun add(e: WatchlistItem): Long

    @Update
    suspend fun update(e: WatchlistItem)

    @Query("DELETE FROM WatchlistItem WHERE Id = :id")
    suspend fun deleteById(id: Int)

    @Query("SELECT * FROM WatchlistItem ORDER BY AddedAt")
    suspend fun getAll(): List<WatchlistItem>
}

@Dao
interface BudgetCategoryDao {
    @Insert
    suspend fun add(e: BudgetCategory): Long

    @Update
    suspend fun update(e: BudgetCategory)

    @Query("DELETE FROM BudgetCategory WHERE Id = :id")
    suspend fun deleteById(id: Int)

    @Query("SELECT * FROM BudgetCategory ORDER BY SortOrder")
    suspend fun getAll(): List<BudgetCategory>
}

@Dao
interface BudgetTransactionDao {
    @Insert
    suspend fun add(e: BudgetTransaction): Long

    @Update
    suspend fun update(e: BudgetTransaction)

    @Query("DELETE FROM BudgetTransaction WHERE Id = :id")
    suspend fun deleteById(id: Int)

    @Query("SELECT * FROM BudgetTransaction ORDER BY Date")
    suspend fun getAll(): List<BudgetTransaction>
}

@Dao
interface BudgetRuleDao {
    @Insert
    suspend fun add(e: BudgetRule): Long

    @Update
    suspend fun update(e: BudgetRule)

    @Query("DELETE FROM BudgetRule WHERE Id = :id")
    suspend fun deleteById(id: Int)

    @Query("SELECT * FROM BudgetRule")
    suspend fun getAll(): List<BudgetRule>
}

@Dao
interface BudgetPlanDao {
    @Insert
    suspend fun add(e: BudgetPlan): Long

    @Update
    suspend fun update(e: BudgetPlan)

    @Query("DELETE FROM BudgetPlan WHERE Id = :id")
    suspend fun deleteById(id: Int)

    @Query("SELECT * FROM BudgetPlan ORDER BY CreatedAt")
    suspend fun getAll(): List<BudgetPlan>
}

@Dao
interface BudgetPlanLineDao {
    @Insert
    suspend fun add(e: BudgetPlanLine): Long

    @Update
    suspend fun update(e: BudgetPlanLine)

    @Query("DELETE FROM BudgetPlanLine WHERE Id = :id")
    suspend fun deleteById(id: Int)

    @Query("SELECT * FROM BudgetPlanLine WHERE PlanId = :planId")
    suspend fun forPlan(planId: Int): List<BudgetPlanLine>
}

@Dao
interface BirthdayDao {
    @Insert
    suspend fun add(e: Birthday): Long

    @Update
    suspend fun update(e: Birthday)

    @Query("DELETE FROM Birthday WHERE Id = :id")
    suspend fun deleteById(id: Int)

    @Query("SELECT * FROM Birthday ORDER BY Month, Day")
    suspend fun getAll(): List<Birthday>
}

@Dao
interface CalendarEventDao {
    @Insert
    suspend fun add(e: CalendarEvent): Long

    @Update
    suspend fun update(e: CalendarEvent)

    @Query("DELETE FROM CalendarEvent WHERE Id = :id")
    suspend fun deleteById(id: Int)

    @Query("SELECT * FROM CalendarEvent ORDER BY Date")
    suspend fun getAll(): List<CalendarEvent>
}

@Dao
interface CalendarEventLinkDao {
    @Insert
    suspend fun add(e: CalendarEventLink): Long

    @Update
    suspend fun update(e: CalendarEventLink)

    @Query("DELETE FROM CalendarEventLink WHERE Id = :id")
    suspend fun deleteById(id: Int)

    @Query("SELECT * FROM CalendarEventLink WHERE OwnerType = :ownerType AND OwnerId = :ownerId")
    suspend fun forOwner(ownerType: String, ownerId: Int): List<CalendarEventLink>
}
