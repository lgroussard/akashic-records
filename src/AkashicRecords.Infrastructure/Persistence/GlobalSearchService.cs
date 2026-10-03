using AkashicRecords.Domain;

namespace AkashicRecords.Infrastructure.Persistence;

/// <summary>
/// Cross-section global search (spec §48/§51). Queries every content repository via its GetAll-style
/// method and filters in memory (case-insensitive Contains) - fine at personal scale, no indexing needed.
/// Results are ranked title-exact &gt; title-prefix &gt; title-contains &gt; body-contains and capped.
/// </summary>
public sealed class GlobalSearchService
{
    private readonly ArtworkRepository _artworkRepository;
    private readonly ObservationRepository _observationRepository;
    private readonly ArtisticProjectRepository _artisticProjectRepository;
    private readonly JournalEntryRepository _journalEntryRepository;
    private readonly RecipeRepository _recipeRepository;
    private readonly PoemRepository _poemRepository;
    private readonly PhotoRepository _photoRepository;
    private readonly PersonalProjectRepository _personalProjectRepository;
    private readonly TransitionRepository _transitionRepository;
    private readonly MusicTrackRepository _musicTrackRepository;
    private readonly BudgetTransactionRepository _budgetTxRepository;

    public GlobalSearchService(SqliteConnectionFactory connectionFactory)
    {
        _artworkRepository = new ArtworkRepository(connectionFactory);
        _observationRepository = new ObservationRepository(connectionFactory);
        _artisticProjectRepository = new ArtisticProjectRepository(connectionFactory);
        _journalEntryRepository = new JournalEntryRepository(connectionFactory);
        _recipeRepository = new RecipeRepository(connectionFactory);
        _poemRepository = new PoemRepository(connectionFactory);
        _photoRepository = new PhotoRepository(connectionFactory);
        _personalProjectRepository = new PersonalProjectRepository(connectionFactory);
        _transitionRepository = new TransitionRepository(connectionFactory);
        _musicTrackRepository = new MusicTrackRepository(connectionFactory);
        _budgetTxRepository = new BudgetTransactionRepository(connectionFactory);
    }

    public IReadOnlyList<SearchResult> Search(string query, int maxResults = 60)
    {
        var q = query?.Trim().ToLowerInvariant() ?? string.Empty;
        if (q.Length == 0) return Array.Empty<SearchResult>();

        var scored = new List<(int Rank, SearchResult Result)>();

        void Add(int? rank, Func<SearchResult> factory)
        {
            if (rank is int r) scored.Add((r, factory()));
        }

        // --- Collections: artworks (all categories) + observations ---
        foreach (var category in Enum.GetValues<ArtworkCategory>())
        {
            foreach (var art in _artworkRepository.GetByCategory(category))
            {
                Add(Rank(q, art.Title), () => new SearchResult(
                    SearchResultKind.Artwork, "Collections", art.Title, Snippet(q, art.Title),
                    art.Id, null, (int)art.Category));
            }
        }

        foreach (var obs in _observationRepository.GetAllWithArtwork())
        {
            Add(Rank(q, obs.Subject, obs.Content), () => new SearchResult(
                SearchResultKind.Observation, "Collections", $"{obs.ArtworkTitle} — {obs.Subject}",
                Snippet(q, obs.Subject, obs.Content), obs.ArtworkId, obs.ObservationId, (int)obs.ArtworkCategory));
        }

        // --- Journaux: artistic projects, journal entries, recipes, poems ---
        foreach (var project in _artisticProjectRepository.GetAll())
        {
            Add(Rank(q, project.Title), () => new SearchResult(
                SearchResultKind.ArtisticProject, "Journaux", project.Title, Snippet(q, project.Title),
                project.Id, null, null));
        }

        foreach (var entry in _journalEntryRepository.GetAll())
        {
            var title = entry.Title.Length > 0 ? entry.Title : entry.EntryDate.ToString("yyyy-MM-dd");
            Add(Rank(q, entry.Title, entry.Text, entry.Tags), () => new SearchResult(
                SearchResultKind.JournalEntry, "Journaux", title, Snippet(q, entry.Title, entry.Text, entry.Tags),
                entry.Id, null, null));
        }

        foreach (var recipe in _recipeRepository.GetAll())
        {
            Add(Rank(q, recipe.Title, recipe.Ingredients, recipe.Instructions, recipe.Notes, recipe.Tags, recipe.Category), () => new SearchResult(
                SearchResultKind.Recipe, "Journaux", recipe.Title,
                Snippet(q, recipe.Title, recipe.Ingredients, recipe.Instructions, recipe.Notes, recipe.Tags, recipe.Category),
                recipe.Id, null, null));
        }

        foreach (var poem in _poemRepository.GetAll())
        {
            Add(Rank(q, poem.Title, poem.Text, poem.Tags), () => new SearchResult(
                SearchResultKind.Poem, "Journaux", poem.Title, Snippet(q, poem.Title, poem.Text, poem.Tags),
                poem.Id, null, null));
        }

        // --- Archives: photos ---
        foreach (var photo in _photoRepository.GetAll())
        {
            var title = photo.Title.Length > 0 ? photo.Title : "(photo sans titre)";
            Add(Rank(q, photo.Title, photo.Description, photo.Tags), () => new SearchResult(
                SearchResultKind.Photo, "Archives", title, Snippet(q, photo.Title, photo.Description, photo.Tags),
                photo.Id, null, null));
        }

        // --- Organisation: projects + transitions ---
        foreach (var project in _personalProjectRepository.GetAll())
        {
            Add(Rank(q, project.Title, project.Description), () => new SearchResult(
                SearchResultKind.PersonalProject, "Organisation", project.Title, Snippet(q, project.Title, project.Description),
                project.Id, null, null));
        }

        foreach (var transition in _transitionRepository.GetAll())
        {
            Add(Rank(q, transition.Title, transition.Description, transition.CurrentState, transition.DesiredState, transition.Notes, transition.Resources, transition.ReminderText), () => new SearchResult(
                SearchResultKind.Transition, "Organisation", transition.Title,
                Snippet(q, transition.Title, transition.Description, transition.CurrentState, transition.DesiredState, transition.Notes, transition.Resources, transition.ReminderText),
                transition.Id, null, null));
        }

        // --- Musique: tracks ---
        foreach (var track in _musicTrackRepository.GetAll())
        {
            var title = track.Artist.Length > 0 ? $"{track.Title} — {track.Artist}" : track.Title;
            Add(Rank(q, track.Title, track.Artist), () => new SearchResult(
                SearchResultKind.MusicTrack, "Musique", title, Snippet(q, track.Title, track.Artist),
                track.Id, null, null));
        }

        // --- Budget: imported transactions ---
        foreach (var tx in _budgetTxRepository.GetAll())
        {
            Add(Rank(q, tx.Label), () => new SearchResult(
                SearchResultKind.BudgetTransaction, "Budget", tx.Label, Snippet(q, tx.Label),
                tx.Id, null, null));
        }

        return scored
            .OrderBy(s => s.Rank)
            .ThenBy(s => s.Result.Title, StringComparer.CurrentCultureIgnoreCase)
            .Take(maxResults)
            .Select(s => s.Result)
            .ToList();
    }

    // 0 = title exact, 1 = title prefix, 2 = title contains, 3 = a body field contains; null = no match.
    private static int? Rank(string query, string? title, params string?[] body)
    {
        var t = (title ?? string.Empty).ToLowerInvariant();
        if (t.Length > 0)
        {
            if (t == query) return 0;
            if (t.StartsWith(query, StringComparison.Ordinal)) return 1;
            if (t.Contains(query, StringComparison.Ordinal)) return 2;
        }
        foreach (var field in body)
        {
            if (!string.IsNullOrEmpty(field) && field.ToLowerInvariant().Contains(query, StringComparison.Ordinal))
            {
                return 3;
            }
        }
        return null;
    }

    // The matched field (title preferred), collapsed to one line and clipped to ~80 chars.
    private static string Snippet(string query, string? title, params string?[] body)
    {
        var t = title ?? string.Empty;
        if (t.Length > 0 && t.ToLowerInvariant().Contains(query, StringComparison.Ordinal))
        {
            return Clip(t);
        }
        foreach (var field in body)
        {
            if (!string.IsNullOrEmpty(field) && field.ToLowerInvariant().Contains(query, StringComparison.Ordinal))
            {
                return Clip(field);
            }
        }
        return Clip(t);
    }

    private static string Clip(string text)
    {
        var normalized = text.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return normalized.Length <= 80 ? normalized : normalized[..80] + "…";
    }

    /// <summary>
    /// What a hit actually looks like: the item's own image (cover, photo, artwork image…) when it
    /// has one, and a body excerpt for text items. Feeds the link picker's thumbnails and the link
    /// chips' preview popups. Null when the target no longer exists — the caller greys the preview
    /// out. Poems carry their reader style so the popup can render verses the way they are read.
    /// </summary>
    public sealed record SearchPreview(string? ImagePath, string? Body, PoemView? Poem = null);

    public sealed record PoemView(string FontFamily, double FontSize, bool Italic, bool Bold, string Alignment);

    public SearchPreview? GetPreview(SearchResultKind kind, int primaryId, int? secondaryId)
    {
        return kind switch
        {
            SearchResultKind.Artwork => Pick(_artworkRepository.GetById(primaryId), a => (a.CoverImagePath, null)),
            SearchResultKind.Observation => _observationRepository.GetById(secondaryId ?? -1) is { } obs
                ? new SearchPreview(Existing(_artworkRepository.GetById(obs.ArtworkId)?.CoverImagePath), obs.Content)
                : null,
            SearchResultKind.JournalEntry => Pick(_journalEntryRepository.GetById(primaryId), e => (null, e.Text)),
            SearchResultKind.Recipe => Pick(_recipeRepository.GetById(primaryId), r => (r.CoverImagePath, FirstText(r.Ingredients, r.Instructions, r.Notes))),
            SearchResultKind.Poem => _poemRepository.GetById(primaryId) is { } poemView
                ? new SearchPreview(Existing(poemView.ImagePath), poemView.Text,
                    new PoemView(poemView.FontFamily, poemView.FontSize, poemView.Italic, poemView.Bold, poemView.TextAlignment))
                : null,
            SearchResultKind.Photo => Pick(_photoRepository.GetById(primaryId), p => (p.ImagePath, p.Description)),
            SearchResultKind.PersonalProject => Pick(_personalProjectRepository.GetById(primaryId), p => (null, p.Description)),
            SearchResultKind.Transition => Pick(_transitionRepository.GetById(primaryId), t => (null, FirstText(t.Description, t.Notes))),
            SearchResultKind.MusicTrack => Pick(_musicTrackRepository.GetById(primaryId), t => (t.CoverImagePath, FirstText(t.Artist, t.Album))),
            SearchResultKind.BudgetTransaction => Pick(_budgetTxRepository.GetAll().FirstOrDefault(t => t.Id == primaryId),
                t => (null, $"{t.Label} · {t.Date:dd/MM/yyyy} · {t.Amount:0.00}")),
            _ => null, // ArtisticProject carries nothing but a title — the chip shows the title alone.
        };
    }

    private static SearchPreview? Pick<T>(T? item, Func<T, (string? Image, string? Body)> project) where T : class
    {
        if (item is null) return null;
        var (image, body) = project(item);
        return new SearchPreview(Existing(image), body);
    }

    // A relative media path pointing at a missing file becomes null, so the UI skips the thumbnail
    // instead of showing a broken image.
    private static string? Existing(string? relativePath)
        => relativePath is { Length: > 0 } && File.Exists(MediaStorage.ResolveFullPath(relativePath))
            ? relativePath : null;

    private static string? FirstText(params string?[] fields)
        => fields.FirstOrDefault(f => !string.IsNullOrWhiteSpace(f));
}
