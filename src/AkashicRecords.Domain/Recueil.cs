namespace AkashicRecords.Domain;

// A named collection/anthology of poems (a "recueil") - poems can also stand alone, unassigned.
public sealed class Recueil
{
    public int Id { get; set; }
    public required string Title { get; set; }
    public DateTime CreatedAt { get; set; }
    // A short free-form note shown on the recueil's "book" page.
    public string Summary { get; set; } = string.Empty;
}
