namespace AkashicRecords.Domain;

public enum CanvasElementType
{
    TextNote,
    Image,
    ObservationReference
}

// An item placed on an ArtisticProject's free-form canvas.
public sealed class CanvasElement
{
    public int Id { get; set; }
    public int ProjectId { get; set; }
    public CanvasElementType Type { get; set; }
    public double X { get; set; }
    public double Y { get; set; }
    public string? TextContent { get; set; }
    public string? ImagePath { get; set; }
    public int? ObservationId { get; set; }
    public double? Width { get; set; }
    public double? Height { get; set; }
}
