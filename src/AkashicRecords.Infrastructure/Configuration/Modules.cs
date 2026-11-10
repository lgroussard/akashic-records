namespace AkashicRecords.Infrastructure.Configuration;

/// <summary>A section of the app, or one of its sub-tabs, that the user can switch off on this device.</summary>
public sealed record AppModule(string Key, string Label, IReadOnlyList<AppModule> Children)
{
    public AppModule(string key, string label) : this(key, label, Array.Empty<AppModule>()) { }
}

/// <summary>
/// What exists on the PC app, and which parts of it the user has switched off.
/// <para>The choice is per device: it lives in this machine's config.json and is never synced. The phone keeps its
/// own list with the same keys ("Section" or "Section.SubTab", sub-tab keys = the views' tab tags), so a module that
/// exists on both apps is named the same everywhere. A module that only exists on one platform (Budget on PC, the
/// future reading module on the phone) is simply absent from the other platform's catalogue — not just hidden.</para>
/// </summary>
public static class Modules
{
    public static readonly IReadOnlyList<AppModule> Catalog = new[]
    {
        new AppModule("Organisation", "Organisation", new[]
        {
            new AppModule("Organisation.Projets", "Projets"),
            new AppModule("Organisation.Transitions", "Transitions"),
        }),
        new AppModule("Journaux", "Journaux", new[]
        {
            new AppModule("Journaux.Personal", "Journal personnel"),
            new AppModule("Journaux.Recipes", "Journal de recettes"),
            new AppModule("Journaux.Poetry", "Recueil de poésie"),
            new AppModule("Journaux.Artistic", "Archive artistique"),
        }),
        new AppModule("Collections", "Collections", new[]
        {
            new AppModule("Collections.Film", "Films"),
            new AppModule("Collections.FilmAnimation", "Films d'animation"),
            new AppModule("Collections.TvSeries", "Séries"),
            new AppModule("Collections.Anime", "Anime / manga"),
            new AppModule("Collections.Livre", "Livres"),
            new AppModule("Collections.VideoGame", "Jeux vidéo"),
            new AppModule("Collections.Watchlist", "À voir"),
        }),
        new AppModule("Archives", "Archives", new[]
        {
            new AppModule("Archives.Photos", "Photos"),
            new AppModule("Archives.Familiale", "Archive familiale"),
        }),
        new AppModule("Budget", "Budget", new[]
        {
            new AppModule("Budget.Overview", "Vue d'ensemble"),
            new AppModule("Budget.Transactions", "Transactions"),
            new AppModule("Budget.Plans", "Plans"),
        }),
        new AppModule("Calendrier", "Calendrier"),
        new AppModule("Musique", "Musique"),
    };

    /// <summary>
    /// True when the module is on. A sub-tab is off when it, or its section, is switched off; a section with
    /// sub-tabs is off when all of them are, so the display never shows an empty shell.
    /// </summary>
    public static bool IsEnabled(this AppConfig config, string key)
    {
        var disabled = config.DisabledModules;
        if (disabled.Count == 0) return true;
        if (disabled.Contains(key)) return false;

        var dot = key.IndexOf('.');
        if (dot > 0) return !disabled.Contains(key[..dot]);

        var section = Catalog.FirstOrDefault(m => m.Key == key);
        return section is null || section.Children.Count == 0 || section.Children.Any(c => !disabled.Contains(c.Key));
    }

    public static bool IsEnabled(this AppConfig config, string section, string subTab) =>
        config.IsEnabled($"{section}.{subTab}");

    /// <summary>The first sub-tab of the section that is still on (null when none is).</summary>
    public static string? FirstEnabledTab(this AppConfig config, string section) =>
        Catalog.FirstOrDefault(m => m.Key == section)?.Children
            .Select(c => c.Key[(section.Length + 1)..])
            .FirstOrDefault(tab => config.IsEnabled(section, tab));

    public static void SetEnabled(this AppConfig config, string key, bool enabled)
    {
        config.DisabledModules.Remove(key);
        if (!enabled) config.DisabledModules.Add(key);
    }
}
