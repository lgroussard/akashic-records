namespace AkashicRecords.Domain;

// A single checklist item belonging to a PersonalProject.
public sealed class ProjectTask
{
    public int Id { get; set; }
    public int ProjectId { get; set; }
    public required string Text { get; set; }
    public bool IsDone { get; set; }
    public int SortOrder { get; set; }
}
