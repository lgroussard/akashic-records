using AkashicRecords.Domain;

namespace AkashicRecords.Infrastructure.Persistence;

// Picks the "film du jour" from the to-watch list (kept separate from the watched Collections).
// One pick per category (film / animated film); the same pick stays for the whole day and a fresh
// weighted-random pick only happens on a new day or if the previous pick disappeared. Weights
// favour higher priorities (Need=3 > Want=2 > Should=1). No timer (per §45) — computed on demand.
public sealed class FilmOfTheDayService
{
    private static readonly Random Random = new();

    private readonly WatchlistItemRepository _watchlistRepository;

    public FilmOfTheDayService(WatchlistItemRepository watchlistRepository)
    {
        _watchlistRepository = watchlistRepository;
    }

    public WatchlistItem? PickForDay(WatchlistCategory category, int? previouslyChosenId, DateTime? previouslyChosenDate)
    {
        var candidates = _watchlistRepository.GetByCategory(category);
        if (candidates.Count == 0) return null;

        if (previouslyChosenId is { } id && previouslyChosenDate is { } date && date.Date == DateTime.Today)
        {
            var existing = candidates.FirstOrDefault(c => c.Id == id);
            if (existing is not null) return existing;
        }

        return WeightedPick(candidates);
    }

    private static WatchlistItem WeightedPick(IReadOnlyList<WatchlistItem> candidates)
    {
        var totalWeight = candidates.Sum(c => (int)c.Priority);
        if (totalWeight <= 0) return candidates[Random.Next(candidates.Count)];

        var roll = Random.Next(totalWeight);
        var cumulative = 0;
        foreach (var candidate in candidates)
        {
            cumulative += (int)candidate.Priority;
            if (roll < cumulative) return candidate;
        }
        return candidates[^1];
    }
}
