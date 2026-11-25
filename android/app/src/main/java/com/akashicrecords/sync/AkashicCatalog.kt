package com.akashicrecords.sync

/**
 * What Akashic synchronises: every table, in an order where a table comes after the ones it points at (a poem
 * after its collection). Same list as the desktop's src/AkashicRecords.Sync/AkashicCatalog.cs: change both
 * together. See that file for the reasoning (references, match keys, what is left out and why).
 */
object AkashicCatalog {
    private fun t(
        name: String,
        match: List<String> = emptyList(),
        refs: Map<String, String> = emptyMap(),
        kindRefs: List<KindRef> = emptyList(),
        matchColumn: String? = null,
        matchPrefix: Int = Int.MAX_VALUE,
        decimals: List<String> = emptyList(),
    ) = SyncTable(
        name, name, matchColumn = matchColumn, matchPrefix = matchPrefix, matchColumns = match, refs = refs, kindRefs = kindRefs,
        decimalTextColumns = decimals,
    )

    /** The columns holding the path of a file of the app folder (see FileSync). */
    val fileColumns: List<Pair<String, String>> = listOf(
        "Recipe" to "CoverImagePath", "Artwork" to "CoverImagePath", "WatchlistItem" to "CoverImagePath",
        "Poem" to "ImagePath", "JournalPhoto" to "ImagePath", "Photo" to "ImagePath", "CanvasElement" to "ImagePath",
        "RefImage" to "ImagePath", "Book" to "FilePath", "Book" to "CoverPath", "Ticket" to "FilePath",
    )

    private val created = listOf("CreatedAt")

    val tables: List<SyncTable> = listOf(
        // ---- Journaux
        t("JournalEntry", matchColumn = "EntryDate", matchPrefix = 10), // one entry per day
        t("Recipe", created),
        t("Recueil", created),
        t("Poem", created, refs = mapOf("RecueilId" to "Recueil")),
        t("JournalPhoto", listOf("EntryId", "ImagePath"), refs = mapOf("EntryId" to "JournalEntry")),

        // ---- Collections
        t("Artwork", created),
        t("EvaluationAxis", listOf("Name")),
        t("ArtworkEvaluation", listOf("ArtworkId", "AxisId"), refs = mapOf("ArtworkId" to "Artwork", "AxisId" to "EvaluationAxis")),
        t("WatchlistItem", listOf("AddedAt")),

        // ---- Archives
        t("PhotoAlbum", created),
        t("Photo", listOf("ImagePath"), refs = mapOf("AlbumId" to "PhotoAlbum")),

        // ---- Lecture, references (written on the phone)
        t("Book", listOf("AddedAt"), refs = mapOf("ArtworkId" to "Artwork")),
        t("RefBoard", created),
        t("RefImage", listOf("ImagePath"), refs = mapOf("BoardId" to "RefBoard")),

        // ---- Observations: owned by an artwork, a poem, a journal entry... (OwnerKind names the table)
        t("Observation", created, refs = mapOf("ArtworkId" to "Artwork"), kindRefs = listOf(KindRef("OwnerId", "OwnerKind"))),
        t("BookHighlight", created, refs = mapOf("BookId" to "Book", "ObservationId" to "Observation")),

        // ---- Organisation
        t("ArtisticProject", created),
        t("CanvasElement", refs = mapOf("ProjectId" to "ArtisticProject", "ObservationId" to "Observation")),
        t(
            "CanvasConnector", listOf("FromElementId", "ToElementId"),
            refs = mapOf("ProjectId" to "ArtisticProject", "FromElementId" to "CanvasElement", "ToElementId" to "CanvasElement"),
        ),
        t("Reference", listOf("ObservationId", "ProjectId"), refs = mapOf("ObservationId" to "Observation", "ProjectId" to "ArtisticProject")),
        t("PersonalProject", created),
        t("ProjectTask", listOf("ProjectId", "Text"), refs = mapOf("ProjectId" to "PersonalProject")),
        t("Transition", created),
        t("TransitionStep", listOf("TransitionId", "Text"), refs = mapOf("TransitionId" to "Transition")),
        t("Mood", listOf("Name")),

        // ---- Calendrier
        t("Birthday", listOf("Name")),
        t("CalendarEvent", created),
        t(
            "CalendarEventLink", listOf("OwnerType", "OwnerId", "Kind", "PrimaryId"),
            kindRefs = listOf(
                KindRef("OwnerId", "OwnerType", mapOf("Event" to "CalendarEvent", "Birthday" to "Birthday")),
                // The linked item. For an observation the link stores its owner's id, and the observation's own id beside it.
                KindRef(
                    "PrimaryId", "Kind",
                    mapOf(
                        "Artwork" to "Artwork", "JournalEntry" to "JournalEntry", "Poem" to "Poem", "Recipe" to "Recipe",
                        "Photo" to "Photo", "Recueil" to "Recueil", "Observation" to "Artwork",
                    ),
                ),
                KindRef("SecondaryId", "Kind", mapOf("Observation" to "Observation")),
            ),
        ),
        t("Ticket", created, refs = mapOf("EventId" to "CalendarEvent")),

        // ---- Budget
        t("BudgetCategory", listOf("Name"), decimals = listOf("MonthlyCap")),
        t("BudgetPlan", created),
        t("BudgetPlanLine", listOf("PlanId", "CategoryId"), refs = mapOf("PlanId" to "BudgetPlan", "CategoryId" to "BudgetCategory"), decimals = listOf("MonthlyAmount")),
        t("BudgetRule", listOf("Keyword"), refs = mapOf("CategoryId" to "BudgetCategory")),
        t("BudgetTransaction", listOf("ExternalId"), refs = mapOf("CategoryId" to "BudgetCategory"), decimals = listOf("Amount")), // the bank line own id

        // ---- Written on the phone
        t("CrosswordGame", created),
        t("ShoppingItem", created),
    )
}
