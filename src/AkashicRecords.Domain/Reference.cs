namespace AkashicRecords.Domain;

// Many-to-many link: one Observation can be referenced by several ArtisticProjects and vice versa.
public sealed class Reference
{
    public int Id { get; set; }
    public int ObservationId { get; set; }
    public int ProjectId { get; set; }
}
