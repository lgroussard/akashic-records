namespace AkashicRecords.Domain;

public sealed class PhotoAlbum
{
    public int Id { get; set; }
    public required string Name { get; set; }
    public DateTime CreatedAt { get; set; }
}
