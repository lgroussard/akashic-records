namespace AkashicRecords.Infrastructure.Persistence;

public enum SearchResultKind
{
    Artwork,
    Observation,
    ArtisticProject,
    JournalEntry,
    Recipe,
    Poem,
    Photo,
    PersonalProject,
    Transition,
    MusicTrack,
    BudgetTransaction
}

/// <summary>
/// A single unified global-search hit.
/// <para><see cref="Section"/> is the nav section to switch to: "Collections", "Journaux", "Archives", "Organisation" or "Musique".</para>
/// <para><see cref="PrimaryId"/> is the id the target view should open (for an Observation this is the owning ArtworkId,
/// with the ObservationId in <see cref="SecondaryId"/>). <see cref="Category"/> carries the ArtworkCategory int for
/// Collections hits so the view can select the right tab.</para>
/// </summary>
public sealed record SearchResult(
    SearchResultKind Kind,
    string Section,
    string Title,
    string Snippet,
    int PrimaryId,
    int? SecondaryId,
    int? Category);
