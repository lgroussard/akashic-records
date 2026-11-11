namespace AkashicRecords.Domain;

// Journaux → Références: image boards in place of Pinterest. Same two tables as the phone (Room v4), so
// the future sync has nothing to translate; until then each device keeps its own boards.
public sealed class RefBoard
{
    public int Id { get; set; }
    public required string Title { get; set; }
    public DateTime CreatedAt { get; set; }
}

// One saved image. BoardId null = « À trier » (found or dropped, not filed yet).
public sealed class RefImage
{
    public int Id { get; set; }
    public int? BoardId { get; set; }
    // Relative to the exe folder: media\references\<file>.
    public required string ImagePath { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Author { get; set; } = string.Empty;
    // "Wikimedia Commons", "Cleveland Museum of Art", "Glisser-déposer"…
    public string Source { get; set; } = string.Empty;
    public string? SourceUrl { get; set; }
    public string? PageUrl { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public string Note { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
}
