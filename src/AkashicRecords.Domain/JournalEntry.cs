namespace AkashicRecords.Domain;

// A chronological personal journal entry - an archive of life, not a productivity tool (per spec).
public sealed class JournalEntry
{
    public int Id { get; set; }
    public required string Title { get; set; }
    public string Text { get; set; } = string.Empty;
    public DateTime EntryDate { get; set; }
    // Comma-separated - simple free-form tagging, no fixed taxonomy.
    public string Tags { get; set; } = string.Empty;
}
