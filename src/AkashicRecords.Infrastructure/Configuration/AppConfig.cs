namespace AkashicRecords.Infrastructure.Configuration;

public sealed class AppConfig
{
    public string? LastActiveSection { get; set; }

    // Journaux sub-tab the user last had open (Personal/Recipes/Poetry/Artistic), so
    // reopening Journaux returns them there instead of defaulting to the personal diary.
    public string? JournauxActiveTab { get; set; }

    // The specific journaux item the user last viewed, so reopening Journaux returns them
    // to the exact page (not just the tab). Serialized as "<tab>:<kind>:<id>" where id is
    // optional (personal diary uses a date, stored as an ISO string).
    //   kind values: Personal, Recipe, Poem, Project
    public string? JournauxActiveItem { get; set; }

    // Organisation: last sub-tab (Projets/Transitions) + the specific project/transition id.
    public string? OrganisationActiveTab { get; set; }
    public int? OrganisationActiveProjectId { get; set; }
    public int? OrganisationActiveTransitionId { get; set; }

    // Archives: last sub-tab (Photos/Familiale) + the album/favorites filters + the photo id
    // currently shown in the viewer.
    public string? ArchivesActiveTab { get; set; }
    public int? ArchivesAlbumFilterId { get; set; }
    public bool ArchivesFavoritesOnly { get; set; }
    public int? ArchivesActivePhotoId { get; set; }

    // Collections: last category tab + the specific fiche (film) id currently open.
    public string? CollectionsActiveCategory { get; set; }
    public int? CollectionsActiveFilmId { get; set; }

    // Optional TMDB API key (v3, free) for movie/anime poster lookup. TMDB has no regional
    // restriction like the Custom Search JSON API, so it's the reliable source for film covers.
    // Leave it null/empty to use the no-key Wikipedia fallback.
    public string? TmdbApiKey { get; set; }

    // Optional RAWG API key (free, from rawg.io) for video game cover lookup. Without it,
    // video games fall back to the no-key Wikipedia search like everything else.
    public string? RawgApiKey { get; set; }

    // Ambient desktop widgets (§45): whether each is currently shown, plus the persisted
    // daily film pick and last-known widget positions.
    public bool ShowFilmOfTheDay { get; set; } = true;
    public bool ShowDesktopReminders { get; set; } = true;
    public int? FilmOfTheDayArtworkId { get; set; }
    public int? FilmOfTheDayFilmId { get; set; }
    public int? FilmOfTheDayAnimationId { get; set; }
    public DateTime? FilmOfTheDayDate { get; set; }
    public double? FilmWidgetLeft { get; set; }
    public double? FilmWidgetTop { get; set; }
    public double? RemindersWidgetLeft { get; set; }
    public double? RemindersWidgetTop { get; set; }

    // Downloader: path to a user-installed command-line tool (yt-dlp) + its arguments.
    // Empty path means "auto-detect" (tools/ folder next to the exe, then PATH).
    public string? DownloaderToolPath { get; set; }
    public string DownloaderArguments { get; set; } = "-x --audio-format mp3 --embed-thumbnail --add-metadata";
    public bool DownloaderAutoMode { get; set; }
}

