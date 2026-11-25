namespace AkashicRecords.Sync;

/// <summary>
/// What Akashic synchronises: every table, in an order where a table comes after the ones it points
/// at (a poem after its collection), with for each one:
///  - the columns that are row ids of other items (`Refs`, `KindRefs`): translated on the wire;
///  - what makes two rows "the same thing" when two devices each already had it (`Match…`): the
///    creation timestamp where there is one - it is written once and never edited, and the phone was
///    seeded from the PC's rows, so a copy edited on one side still carries it;
///  - nothing else: file paths ("media\recipes\x.jpg") have the same form on the PC and the phone and
///    travel as they are; the files themselves are a separate matter.
/// The phone has the same list in android/.../sync/AkashicCatalog.kt: change both together.
///
/// Not here on purpose: MusicTrack, TrackMood, Playlist, PlaylistTrack (a track is a file of one
/// device's own music folder: handled with the music itself), DownloadItem / DownloadFolder (each
/// device's own download queue), SchemaFlag (internal).
/// </summary>
public static class AkashicCatalog
{
    private static Dictionary<string, string> R(params (string Column, string Target)[] refs) => refs.ToDictionary(r => r.Column, r => r.Target);

    public static readonly IReadOnlyList<SyncTable> Tables =
    [
        // ---- Journaux
        new("JournalEntry", "JournalEntry") { MatchColumn = "EntryDate", MatchPrefix = 10 }, // one entry per day
        new("Recipe", "Recipe") { MatchColumns = ["CreatedAt"] },
        new("Recueil", "Recueil") { MatchColumns = ["CreatedAt"] },
        new("Poem", "Poem") { Refs = R(("RecueilId", "Recueil")), MatchColumns = ["CreatedAt"] },
        new("JournalPhoto", "JournalPhoto") { Refs = R(("EntryId", "JournalEntry")), MatchColumns = ["EntryId", "ImagePath"] },

        // ---- Collections
        new("Artwork", "Artwork") { MatchColumns = ["CreatedAt"] },
        new("EvaluationAxis", "EvaluationAxis") { MatchColumns = ["Name"] },
        new("ArtworkEvaluation", "ArtworkEvaluation") { Refs = R(("ArtworkId", "Artwork"), ("AxisId", "EvaluationAxis")), MatchColumns = ["ArtworkId", "AxisId"] },
        new("WatchlistItem", "WatchlistItem") { MatchColumns = ["AddedAt"] },

        // ---- Archives
        new("PhotoAlbum", "PhotoAlbum") { MatchColumns = ["CreatedAt"] },
        new("Photo", "Photo") { Refs = R(("AlbumId", "PhotoAlbum")), MatchColumns = ["ImagePath"] },

        // ---- Lecture, references (written on the phone)
        new("Book", "Book") { Refs = R(("ArtworkId", "Artwork")), MatchColumns = ["AddedAt"] },
        new("RefBoard", "RefBoard") { MatchColumns = ["CreatedAt"] },
        new("RefImage", "RefImage") { Refs = R(("BoardId", "RefBoard")), MatchColumns = ["ImagePath"] },

        // ---- Observations: owned by an artwork, a poem, a journal entry... (OwnerKind names the table)
        new("Observation", "Observation")
        {
            Refs = R(("ArtworkId", "Artwork")),
            KindRefs = [new KindRef("OwnerId", "OwnerKind")],
            MatchColumns = ["CreatedAt"],
        },
        new("BookHighlight", "BookHighlight") { Refs = R(("BookId", "Book"), ("ObservationId", "Observation")), MatchColumns = ["CreatedAt"] },

        // ---- Organisation
        new("ArtisticProject", "ArtisticProject") { MatchColumns = ["CreatedAt"] },
        new("CanvasElement", "CanvasElement") { Refs = R(("ProjectId", "ArtisticProject"), ("ObservationId", "Observation")) },
        new("CanvasConnector", "CanvasConnector") { Refs = R(("ProjectId", "ArtisticProject"), ("FromElementId", "CanvasElement"), ("ToElementId", "CanvasElement")), MatchColumns = ["FromElementId", "ToElementId"] },
        new("Reference", "Reference") { Refs = R(("ObservationId", "Observation"), ("ProjectId", "ArtisticProject")), MatchColumns = ["ObservationId", "ProjectId"] },
        new("PersonalProject", "PersonalProject") { MatchColumns = ["CreatedAt"] },
        new("ProjectTask", "ProjectTask") { Refs = R(("ProjectId", "PersonalProject")), MatchColumns = ["ProjectId", "Text"] },
        new("Transition", "Transition") { MatchColumns = ["CreatedAt"] },
        new("TransitionStep", "TransitionStep") { Refs = R(("TransitionId", "Transition")), MatchColumns = ["TransitionId", "Text"] },
        new("Mood", "Mood") { MatchColumns = ["Name"] },

        // ---- Calendrier
        new("Birthday", "Birthday") { MatchColumns = ["Name"] },
        new("CalendarEvent", "CalendarEvent") { MatchColumns = ["CreatedAt"] },
        new("CalendarEventLink", "CalendarEventLink")
        {
            KindRefs =
            [
                new KindRef("OwnerId", "OwnerType", new Dictionary<string, string> { ["Event"] = "CalendarEvent", ["Birthday"] = "Birthday" }),
                // The linked item. For an observation the link stores its owner's id, and the
                // observation's own id beside it.
                new KindRef("PrimaryId", "Kind", new Dictionary<string, string>
                {
                    ["Artwork"] = "Artwork", ["JournalEntry"] = "JournalEntry", ["Poem"] = "Poem", ["Recipe"] = "Recipe",
                    ["Photo"] = "Photo", ["Recueil"] = "Recueil", ["Observation"] = "Artwork",
                }),
                new KindRef("SecondaryId", "Kind", new Dictionary<string, string> { ["Observation"] = "Observation" }),
            ],
            MatchColumns = ["OwnerType", "OwnerId", "Kind", "PrimaryId"],
        },
        new("Ticket", "Ticket") { Refs = R(("EventId", "CalendarEvent")), MatchColumns = ["CreatedAt"] },

        // ---- Budget
        new("BudgetCategory", "BudgetCategory") { MatchColumns = ["Name"] },
        new("BudgetPlan", "BudgetPlan") { MatchColumns = ["CreatedAt"] },
        new("BudgetPlanLine", "BudgetPlanLine") { Refs = R(("PlanId", "BudgetPlan"), ("CategoryId", "BudgetCategory")), MatchColumns = ["PlanId", "CategoryId"] },
        new("BudgetRule", "BudgetRule") { Refs = R(("CategoryId", "BudgetCategory")), MatchColumns = ["Keyword"] },
        new("BudgetTransaction", "BudgetTransaction") { Refs = R(("CategoryId", "BudgetCategory")), MatchColumns = ["Date", "Label", "Amount"] },

        // ---- Written on the phone
        new("CrosswordGame", "CrosswordGame") { MatchColumns = ["CreatedAt"] },
        new("ShoppingItem", "ShoppingItem") { MatchColumns = ["CreatedAt"] },
    ];
}
