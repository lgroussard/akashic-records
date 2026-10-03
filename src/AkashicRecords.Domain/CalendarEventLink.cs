namespace AkashicRecords.Domain;

// A link from a calendar entry (an event or a birthday) to any other item of the app (poem,
// project, observation, photo…). Carries the same coordinates a global-search hit uses, so
// MainWindow.NavigateToSearchResult can open it unchanged. Kind is the SearchResultKind enum name,
// kept as text so this layer stays free of Infrastructure (the App layer turns it back into a
// SearchResult for navigation). Title is a display snapshot; the view refreshes it live when the
// target still exists and greys it when gone. OwnerType/OwnerId name the calendar entry the link
// hangs off — a separate row per owner type, since event and birthday ids share a rowid space.
public enum CalendarLinkOwnerType { Event, Birthday }

public sealed class CalendarEventLink
{
    public int Id { get; set; }
    public CalendarLinkOwnerType OwnerType { get; set; } = CalendarLinkOwnerType.Event;
    public int OwnerId { get; set; }
    public required string Kind { get; set; }
    public required string Section { get; set; }
    public required string Title { get; set; }
    public int PrimaryId { get; set; }
    public int? SecondaryId { get; set; }
    public int? Category { get; set; }
}
