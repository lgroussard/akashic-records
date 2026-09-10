namespace AkashicRecords.Domain;

// A unit of personal memory attached to an Artwork (e.g. "the pheasant in Princesse Kaguya").
// Can be linked to one or more ArtisticProjects via Reference.
public sealed class Observation
{
    public int Id { get; set; }
    public int ArtworkId { get; set; }
    public required string Subject { get; set; }
    public required string Content { get; set; }
    public DateTime CreatedAt { get; set; }
}
