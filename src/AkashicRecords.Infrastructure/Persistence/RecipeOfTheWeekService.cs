using AkashicRecords.Domain;

namespace AkashicRecords.Infrastructure.Persistence;

// Picks the "plats de la semaine" from the recipe journal: a couple of recipes that stay the same from
// Monday to Sunday, so the "what do I cook?" decision is already made. A new week draws again and avoids
// last week's picks while enough other recipes exist. Computed on demand, like FilmOfTheDayService.
public sealed class RecipeOfTheWeekService
{
    public const int PicksPerWeek = 2;

    private static readonly Random Random = new();

    private readonly RecipeRepository _recipeRepository;

    public RecipeOfTheWeekService(RecipeRepository recipeRepository)
    {
        _recipeRepository = recipeRepository;
    }

    // Monday of the week the day falls in.
    public static DateTime WeekStart(DateTime day) =>
        day.Date.AddDays(-(((int)day.DayOfWeek + 6) % 7));

    // The picks for the current week. Within the same week the stored picks are kept (topped up if one was
    // deleted or more recipes exist now); on a new week they are only what the draw steers away from.
    public IReadOnlyList<Recipe> PickForWeek(IReadOnlyList<int> previousIds, DateTime? previousWeekStart)
    {
        var all = _recipeRepository.GetAll();
        var sameWeek = previousWeekStart?.Date == WeekStart(DateTime.Today);
        var keep = sameWeek
            ? previousIds.Select(id => all.FirstOrDefault(r => r.Id == id)).OfType<Recipe>().ToList()
            : new List<Recipe>();
        return Draw(all, keep, sameWeek ? Array.Empty<int>() : previousIds);
    }

    // A fresh draw on request ("not in the mood for these"), avoiding the current picks when possible.
    public IReadOnlyList<Recipe> Reroll(IReadOnlyList<int> currentIds) =>
        Draw(_recipeRepository.GetAll(), new List<Recipe>(), currentIds);

    private static IReadOnlyList<Recipe> Draw(IReadOnlyList<Recipe> all, List<Recipe> keep, IReadOnlyList<int> avoid)
    {
        var picks = keep.Take(PicksPerWeek).ToList();
        var others = all.Where(r => picks.All(p => p.Id != r.Id)).OrderBy(_ => Random.Next()).ToList();
        // Recipes to avoid go last, so they only come back when nothing else is left.
        var pool = others.Where(r => !avoid.Contains(r.Id)).Concat(others.Where(r => avoid.Contains(r.Id)));
        picks.AddRange(pool.Take(PicksPerWeek - picks.Count));
        return picks;
    }
}
