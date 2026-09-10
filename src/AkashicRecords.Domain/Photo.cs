namespace AkashicRecords.Domain;

// A single archived photo. The image file lives locally under media/archive; ImagePath is
// stored relative to the exe folder so the portable app folder stays self-contained.
public sealed class Photo
{
    public int Id { get; set; }
    public required string ImagePath { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public DateTime? TakenDate { get; set; }
    public string Tags { get; set; } = string.Empty;
    public bool IsFavorite { get; set; }
    public int? AlbumId { get; set; }
    public DateTime ImportedAt { get; set; }
}
