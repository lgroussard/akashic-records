namespace AkashicRecords.Domain;

// One artwork's score (1-5) on one user-defined EvaluationAxis - e.g. John Wick / Visuel / 5.
public sealed class ArtworkEvaluation
{
    public int Id { get; set; }
    public int ArtworkId { get; set; }
    public int AxisId { get; set; }
    public int Score { get; set; }
}
