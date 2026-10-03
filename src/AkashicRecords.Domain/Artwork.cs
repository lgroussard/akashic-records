namespace AkashicRecords.Domain;

public enum ArtworkCategory
{
    Film,
    FilmAnimation,
    Anime,
    Livre,
    VideoGame,
    // Appended last on purpose: Category persists as its int, so existing rows keep their meaning.
    TvSeries
}

// Personal "how much do I want to keep this in mind" ranking - not an objective quality score.
public enum Tier
{
    S,
    A,
    B,
    C,
    D
}

// Watchlist bucket for the "Film du jour" desktop widget - a weighted-random pick prefers
// higher buckets (Need > Want > Should). None means the work is excluded from the widget.
public enum WatchPriority
{
    None = 0,
    ShouldWatchSomeday = 1,
    WantToWatch = 2,
    NeedToWatch = 3
}

public sealed class Artwork
{
    public int Id { get; set; }
    public required string Title { get; set; }
    public ArtworkCategory Category { get; set; }
    public Tier Tier { get; set; }
    public DateTime CreatedAt { get; set; }
    // Path (relative to the exe folder) to a cover image shown on the tier list instead of the title text.
    public string? CoverImagePath { get; set; }
    public WatchPriority WatchPriority { get; set; }
}
