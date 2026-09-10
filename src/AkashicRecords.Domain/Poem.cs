namespace AkashicRecords.Domain;

// A single poem - optionally grouped under a Recueil, otherwise standalone.
public sealed class Poem
{
    public int Id { get; set; }
    public int? RecueilId { get; set; }
    public required string Title { get; set; }
    public string Text { get; set; } = string.Empty;
    // Comma-separated - simple free-form tagging, no fixed taxonomy.
    public string Tags { get; set; } = string.Empty;
    public string? ImagePath { get; set; }
    public DateTime CreatedAt { get; set; }
    // "Left", "Center", "Right" or "Justify" - stored as a string to keep this project WPF-independent.
    public string TextAlignment { get; set; } = "Center";
    // Left/right margin in pixels applied to the poem text.
    public double Margin { get; set; }
    // Per-poem text styling.
    public double FontSize { get; set; } = 19;
    public string FontFamily { get; set; } = "Georgia";
    public bool Bold { get; set; }
    public bool Italic { get; set; } = true;
    // XAML-serialized FlowDocument for per-character rich formatting; Text holds the plain text (search).
    public string RichContent { get; set; } = string.Empty;
}
