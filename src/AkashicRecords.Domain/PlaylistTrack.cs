namespace AkashicRecords.Domain;

public sealed class PlaylistTrack
{
    public int Id { get; set; }
    public int PlaylistId { get; set; }
    public int TrackId { get; set; }
    public int SortOrder { get; set; }
}
