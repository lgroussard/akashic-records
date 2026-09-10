namespace AkashicRecords.Domain;

// A photo attached to a JournalEntry - a journal entry can have several.
public sealed class JournalPhoto
{
    public int Id { get; set; }
    public int EntryId { get; set; }
    public required string ImagePath { get; set; }
}
