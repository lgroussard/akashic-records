namespace AkashicRecords.Domain;

public enum ProjectStatus
{
    Active,
    Done,
    Archived
}

// A concrete goal-oriented project (artistic/personal/practical) with a task checklist and an
// optional deadline - distinct from a Transition, which is a gradual habit/state change.
public sealed class PersonalProject
{
    public int Id { get; set; }
    public required string Title { get; set; }
    public string Description { get; set; } = string.Empty;
    public ProjectStatus Status { get; set; }
    public DateTime? Deadline { get; set; }
    public DateTime CreatedAt { get; set; }
}
