namespace AkashicRecords.Domain;

// Films the user wants to watch (kept separate from the watched Collections tier lists).
// Only films and animated films — animes are excluded from the "film du jour" concept.
public enum WatchlistCategory
{
    Film,
    FilmAnimation
}

public sealed class WatchlistItem
{
    public int Id { get; set; }
    public required string Title { get; set; }
    public WatchlistCategory Category { get; set; }
    // Reuses WatchPriority (Should/Want/Need) to weight the daily pick; None is unused here.
    public WatchPriority Priority { get; set; } = WatchPriority.WantToWatch;
    public string? CoverImagePath { get; set; }
    public DateTime AddedAt { get; set; }
}
