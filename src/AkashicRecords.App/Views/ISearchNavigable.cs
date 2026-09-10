using AkashicRecords.Infrastructure.Persistence;

namespace AkashicRecords.App.Views;

/// <summary>
/// Implemented by the four section views so global search can deep-link to a specific item.
/// The view stores the target if it isn't loaded yet and applies it once its Loaded handler runs.
/// </summary>
public interface ISearchNavigable
{
    void NavigateToSearchResult(SearchResult result);
}
