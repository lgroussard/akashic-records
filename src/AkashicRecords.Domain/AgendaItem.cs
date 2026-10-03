namespace AkashicRecords.Domain;

// Where an aggregated calendar row came from. The App layer maps this to a display color/label;
// the Infrastructure aggregator stays presentation-agnostic so the toast and the view share it.
public enum AgendaSource
{
    Birthday,
    Event,
    Deadline
}

// One line in a day's agenda: a birthday, a calendar event, or a project deadline, unified so the
// calendar and the notification toast render a single sorted list. RefId points back to the source
// row (for "open it"); NullRef for items with no deep-link target.
public sealed class AgendaItem
{
    public const int NullRef = -1;

    public DateTime Date { get; set; }
    public int? Hour { get; set; }
    public int? Minute { get; set; }
    public required string Title { get; set; }
    public string Subtitle { get; set; } = string.Empty;
    public AgendaSource Source { get; set; }
    // Only set for Source == Event; drives the category color.
    public CalendarEventKind? EventKind { get; set; }
    public int RefId { get; set; } = NullRef;
    // True for the 2nd..Nth day of a multi-day event. The grid shows those rows, but the
    // notification engine skips them — a 5-day trip pings once (the first day), not five times.
    public bool IsSpanContinuation { get; set; }

    // Stable identity for "already notified" bookkeeping: same item on the same day = same key.
    public string NotificationKey =>
        $"{Source}:{RefId}:{Date:yyyy-MM-dd}";

    public string TimeText =>
        Hour is int h ? $"{h:D2}:{(Minute ?? 0):D2}" : string.Empty;
}
