namespace AkashicRecords.Domain;

// A user-defined evaluation criterion (e.g. "Visuel", "Humour", "Scenario") - the axis list
// itself is not fixed, the user can create their own instead of a hardcoded set.
public sealed class EvaluationAxis
{
    public int Id { get; set; }
    public required string Name { get; set; }
}
