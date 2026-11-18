namespace AkashicRecords.Domain;

// A unit of personal memory attached to an item (e.g. "the pheasant in Princesse Kaguya").
// Can be linked to one or more ArtisticProjects via Reference.
public sealed class Observation
{
    // OwnerKind values. They are SearchResultKind names, kept as text so Domain stays dependency-free
    // (same convention as CalendarEventLink.Kind).
    public const string ArtworkOwner = "Artwork";
    public const string JournalEntryOwner = "JournalEntry";
    public const string PoemOwner = "Poem";
    public const string RecipeOwner = "Recipe";
    public const string PhotoOwner = "Photo";
    public const string RefImageOwner = "RefImage";
    public const string MusicTrackOwner = "MusicTrack";

    public int Id { get; set; }
    // The owning artwork's id when OwnerKind is "Artwork", 0 otherwise. Kept for the Collections
    // queries (per-artwork lists, tier-card counts) that predate OwnerKind/OwnerId.
    public int ArtworkId { get; set; }
    public string OwnerKind { get; set; } = ArtworkOwner;
    public int OwnerId { get; set; }
    public required string Subject { get; set; }
    public required string Content { get; set; }
    public DateTime CreatedAt { get; set; }
}
