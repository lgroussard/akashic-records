namespace AkashicRecords.Domain;

// A schematic arrow/line between two CanvasElements on the same project's canvas.
public sealed class CanvasConnector
{
    public int Id { get; set; }
    public int ProjectId { get; set; }
    public int FromElementId { get; set; }
    public int ToElementId { get; set; }
}
