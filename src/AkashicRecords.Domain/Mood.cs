namespace AkashicRecords.Domain;

// User-definable feeling/ambiance a track evokes (e.g. Mélancolique, Apaisante, Énergique).
public sealed class Mood
{
    public int Id { get; set; }
    public required string Name { get; set; }
}
