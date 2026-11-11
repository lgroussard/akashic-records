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
        Book::class,
        BookHighlight::class,
        RefBoard::class,
        RefImage::class,
        CrosswordGame::class,
    ],
    version = 5,
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
    abstract fun bookDao(): BookDao
    abstract fun bookHighlightDao(): BookHighlightDao
    abstract fun refBoardDao(): RefBoardDao
    abstract fun refImageDao(): RefImageDao
    abstract fun crosswordGameDao(): CrosswordGameDao

    companion object {
        @Volatile
        private var instance: AkashicDatabase? = null

        fun get(context: Context): AkashicDatabase =
            instance ?: Room.databaseBuilder(
                context.applicationContext,
                AkashicDatabase::class.java,
                "akashic.db",
            ).addMigrations(MIGRATION_1_2, MIGRATION_2_3, MIGRATION_3_4, MIGRATION_4_5).build().also { instance = it }

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

        // v3: the Lecture module (phone only). Same two tables in the desktop SchemaInitializer, so both schemas
        // stay identical for imports and the future sync.
        val MIGRATION_2_3 = object : Migration(2, 3) {
            override fun migrate(db: SupportSQLiteDatabase) {
                db.execSQL(
                    "CREATE TABLE IF NOT EXISTS `Book` (`Id` INTEGER PRIMARY KEY AUTOINCREMENT NOT NULL, " +
                        "`Title` TEXT NOT NULL, `Author` TEXT NOT NULL, `FilePath` TEXT NOT NULL, `CoverPath` TEXT, " +
                        "`AddedAt` TEXT NOT NULL, `LastReadAt` TEXT, `Locator` TEXT, `Progress` REAL NOT NULL, `ArtworkId` INTEGER)",
                )
                db.execSQL(
                    "CREATE TABLE IF NOT EXISTS `BookHighlight` (`Id` INTEGER PRIMARY KEY AUTOINCREMENT NOT NULL, " +
                        "`BookId` INTEGER NOT NULL, `Chapter` INTEGER NOT NULL, `Text` TEXT NOT NULL, `Note` TEXT NOT NULL, " +
                        "`Locator` TEXT NOT NULL, `CreatedAt` TEXT NOT NULL, `ObservationId` INTEGER)",
                )
            }
        }

        // v4: Références (image boards). Same two tables in the desktop SchemaInitializer.
        val MIGRATION_3_4 = object : Migration(3, 4) {
            override fun migrate(db: SupportSQLiteDatabase) {
                db.execSQL(
                    "CREATE TABLE IF NOT EXISTS `RefBoard` (`Id` INTEGER PRIMARY KEY AUTOINCREMENT NOT NULL, " +
                        "`Title` TEXT NOT NULL, `CreatedAt` TEXT NOT NULL)",
                )
                db.execSQL(
                    "CREATE TABLE IF NOT EXISTS `RefImage` (`Id` INTEGER PRIMARY KEY AUTOINCREMENT NOT NULL, " +
                        "`BoardId` INTEGER, `ImagePath` TEXT NOT NULL, `Title` TEXT NOT NULL, `Author` TEXT NOT NULL, " +
                        "`Source` TEXT NOT NULL, `SourceUrl` TEXT, `PageUrl` TEXT, `Width` INTEGER NOT NULL, " +
                        "`Height` INTEGER NOT NULL, `Note` TEXT NOT NULL, `CreatedAt` TEXT NOT NULL)",
                )
            }
        }

        // v5: Mots croisés (phone only). Same table in the desktop SchemaInitializer.
        val MIGRATION_4_5 = object : Migration(4, 5) {
            override fun migrate(db: SupportSQLiteDatabase) {
                db.execSQL(
                    "CREATE TABLE IF NOT EXISTS `CrosswordGame` (`Id` INTEGER PRIMARY KEY AUTOINCREMENT NOT NULL, " +
                        "`Title` TEXT NOT NULL, `Source` TEXT NOT NULL, `PuzzleJson` TEXT NOT NULL, `Progress` TEXT NOT NULL, " +
                        "`CreatedAt` TEXT NOT NULL, `UpdatedAt` TEXT NOT NULL, `CompletedAt` TEXT)",
                )
            }
        }
    }
}
