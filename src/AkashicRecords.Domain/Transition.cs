namespace AkashicRecords.Domain;

// A gradual day-to-day life change (e.g. "become vegan", "learn guzheng"): current-state -> desired-state,
// tracked via steps. Can optionally surface later as an ambient desktop reminder note (ShowOnDesktop +
// ReminderText) - the desktop window itself is built elsewhere; this only stores the data.
public sealed class Transition
{
    public int Id { get; set; }
    public required string Title { get; set; }
    public string Description { get; set; } = string.Empty;
    public string CurrentState { get; set; } = string.Empty;
    public string DesiredState { get; set; } = string.Empty;
    public string Notes { get; set; } = string.Empty;
    public string Resources { get; set; } = string.Empty;
    public string ReminderText { get; set; } = string.Empty;
    public bool ShowOnDesktop { get; set; }
    public DateTime CreatedAt { get; set; }
}
