namespace AkashicRecords.Domain;

// Predefined kinds, each with a fixed calendar color (see CalendarKind in the App layer for the
// brush mapping). Kept as a closed enum — the user picked "catégories prédéfinies", not free text.
public enum CalendarEventKind
{
    Sortie,
    Plan,
    Voyage,
    Rendezvous,
    Autre
}

// A dated outing/plan/trip/appointment shown on the calendar. Date is the day; StartTime is the
// optional hour (null = all-day). RecurringYearly repeats every year on Month/Day (like an
// anniversary or a yearly reservation); non-recurring events live on their exact Date only.
public sealed class CalendarEvent
{
    public int Id { get; set; }
    public required string Title { get; set; }
    public CalendarEventKind Kind { get; set; }
    public DateTime Date { get; set; }
    // Multi-day events (a trip, a festival): the inclusive last day. Null = the event lives on
    // Date only. The aggregator materializes one agenda row per covered day, so each day of the
    // span shows the event on the grid.
    public DateTime? EndDate { get; set; }
    // Hour of day when timed; -1 sentinel stored as null. Kept as int hours+minutes to avoid
    // DateTime-time-of-day ambiguity in TEXT round-trips.
    public int? StartHour { get; set; }
    public int? StartMinute { get; set; }
    public bool RecurringYearly { get; set; }
    public string Location { get; set; } = string.Empty;
    public string Notes { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }

    // The hour a lead-time notification should fire before: timed events use the clock time,
    // all-day events have no precise moment (notified on the day only).
    public DateTime? StartDateTime =>
        StartHour is int h ? Date.Date.AddHours(h).AddMinutes(StartMinute ?? 0) : null;
}
