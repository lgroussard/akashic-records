namespace AkashicRecords.Domain;

// Placeholder for the Journaux "canvas" artistic project - canvas layout data isn't modeled
// yet (Phase 4 in the spec); this only exists now so Observations have something to reference.
public sealed class ArtisticProject
{
    public int Id { get; set; }
    public required string Title { get; set; }
    public DateTime CreatedAt { get; set; }
}
