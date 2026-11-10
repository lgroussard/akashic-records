package com.akashicrecords.data

import android.content.Context
import androidx.room.Database
import androidx.room.Room
import androidx.room.RoomDatabase
import androidx.room.TypeConverters
import androidx.room.migration.Migration
import androidx.sqlite.db.SupportSQLiteDatabase

@Database(
    entities = [
        Artwork::class,
        EvaluationAxis::class,
        ArtworkEvaluation::class,
        Observation::class,
        JournalEntry::class,
        JournalPhoto::class,
        Recipe::class,
        Recueil::class,
        Poem::class,
        ArtisticProject::class,
        Reference::class,
        CanvasElement::class,
        CanvasConnector::class,
        PersonalProject::class,
        ProjectTask::class,
        Transition::class,
        TransitionStep::class,
        PhotoAlbum::class,
        Photo::class,
        MusicTrack::class,
        Mood::class,
        TrackMood::class,
        Playlist::class,
        PlaylistTrack::class,
        DownloadItem::class,
        DownloadFolder::class,
        WatchlistItem::class,
        BudgetCategory::class,
        BudgetTransaction::class,
        BudgetRule::class,
        BudgetPlan::class,
        BudgetPlanLine::class,
        Birthday::class,
        CalendarEvent::class,
        CalendarEventLink::class,
    ],
    version = 2,
    exportSchema = true,
)
@TypeConverters(Converters::class)
abstract class AkashicDatabase : RoomDatabase() {
    abstract fun artworkDao(): ArtworkDao
    abstract fun evaluationAxisDao(): EvaluationAxisDao
    abstract fun artworkEvaluationDao(): ArtworkEvaluationDao
    abstract fun observationDao(): ObservationDao
    abstract fun journalEntryDao(): JournalEntryDao
    abstract fun journalPhotoDao(): JournalPhotoDao
    abstract fun recipeDao(): RecipeDao
    abstract fun recueilDao(): RecueilDao
    abstract fun poemDao(): PoemDao
    abstract fun artisticProjectDao(): ArtisticProjectDao
    abstract fun referenceDao(): ReferenceDao
    abstract fun canvasElementDao(): CanvasElementDao
    abstract fun canvasConnectorDao(): CanvasConnectorDao
    abstract fun personalProjectDao(): PersonalProjectDao
    abstract fun projectTaskDao(): ProjectTaskDao
    abstract fun transitionDao(): TransitionDao
    abstract fun transitionStepDao(): TransitionStepDao
    abstract fun photoAlbumDao(): PhotoAlbumDao
    abstract fun photoDao(): PhotoDao
    abstract fun musicTrackDao(): MusicTrackDao
    abstract fun moodDao(): MoodDao
    abstract fun trackMoodDao(): TrackMoodDao
    abstract fun playlistDao(): PlaylistDao
    abstract fun playlistTrackDao(): PlaylistTrackDao
    abstract fun downloadItemDao(): DownloadItemDao
    abstract fun downloadFolderDao(): DownloadFolderDao
    abstract fun watchlistItemDao(): WatchlistItemDao
    abstract fun budgetCategoryDao(): BudgetCategoryDao
    abstract fun budgetTransactionDao(): BudgetTransactionDao
    abstract fun budgetRuleDao(): BudgetRuleDao
    abstract fun budgetPlanDao(): BudgetPlanDao
    abstract fun budgetPlanLineDao(): BudgetPlanLineDao
    abstract fun birthdayDao(): BirthdayDao
    abstract fun calendarEventDao(): CalendarEventDao
    abstract fun calendarEventLinkDao(): CalendarEventLinkDao

    companion object {
        @Volatile
        private var instance: AkashicDatabase? = null

        fun get(context: Context): AkashicDatabase =
            instance ?: Room.databaseBuilder(
                context.applicationContext,
                AkashicDatabase::class.java,
                "akashic.db",
            ).addMigrations(MIGRATION_1_2).build().also { instance = it }

        // v2: phone-only poem formatting next to the PC columns (the desktop schema has the same
        // columns, so imports and the future sync carry both layouts).
        val MIGRATION_1_2 = object : Migration(1, 2) {
            override fun migrate(db: SupportSQLiteDatabase) {
                db.execSQL("ALTER TABLE `Poem` ADD COLUMN `MobileTextAlignment` TEXT")
                db.execSQL("ALTER TABLE `Poem` ADD COLUMN `MobileFontSize` REAL")
                db.execSQL("ALTER TABLE `Poem` ADD COLUMN `MobileBold` INTEGER")
                db.execSQL("ALTER TABLE `Poem` ADD COLUMN `MobileItalic` INTEGER")
                db.execSQL("ALTER TABLE `Poem` ADD COLUMN `MobileRichContent` TEXT")
            }
        }
    }
}
