namespace AkashicRecords.Domain;

public sealed class MusicTrack
{
    public int Id { get; set; }
    // Path relative to the exe folder, e.g. "music/song.mp3".
    public required string FilePath { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Artist { get; set; } = string.Empty;
    public double? DurationSeconds { get; set; }
    // Path (relative to the exe folder) to a user-set cover image; mp3 metadata isn't read (no ID3 lib).
    public string? CoverImagePath { get; set; }
    // Immediate parent folder under music/, used as the album grouping (empty = loose in music/).
    public string Album { get; set; } = string.Empty;
    public DateTime AddedAt { get; set; }
}
