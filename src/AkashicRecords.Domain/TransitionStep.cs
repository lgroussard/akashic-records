namespace AkashicRecords.Domain;

// A single step toward a Transition's desired state.
public sealed class TransitionStep
{
    public int Id { get; set; }
    public int TransitionId { get; set; }
    public required string Text { get; set; }
    public bool IsDone { get; set; }
    public int SortOrder { get; set; }
}
