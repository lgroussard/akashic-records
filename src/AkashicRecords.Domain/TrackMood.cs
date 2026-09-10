namespace AkashicRecords.Domain;

// Many-to-many join between a track and a mood.
public sealed class TrackMood
{
    public int Id { get; set; }
    public int TrackId { get; set; }
    public int MoodId { get; set; }
}
