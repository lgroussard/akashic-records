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

    // Budget: last sub-tab (Overview/Transactions/Plans) and the month period last shown (yyyy-MM).
    public string? BudgetActiveTab { get; set; }
    public string? BudgetActivePeriod { get; set; }

    // Optional TMDB API key (v3, free) for movie/anime/series poster lookup. TMDB has no
    // regional restriction like the Custom Search JSON API, so it's the reliable source for
    // film covers. Leave it null/empty to use the no-key Wikipedia fallback. Video games
    // never needed a key: Steam's public store search replaced RAWG (poor coverage).
    public string? TmdbApiKey { get; set; }

    // Optional Pinterest developer token (v5, free "trial" tier = 1000 req/day) used as the
    // artistic image bank: search results + the account's own boards. Null = Wikipedia fallback.
    public string? PinterestToken { get; set; }

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

    // Floating "add to library" button, shown while a suggestion not in the library plays.
    public bool ShowMusicSaveWidget { get; set; } = true;
    public double? MusicSaveWidgetLeft { get; set; }
    public double? MusicSaveWidgetTop { get; set; }

    // Downloader: path to a user-installed command-line tool (yt-dlp) + its arguments.
    // Empty path means "auto-detect" (tools/ folder next to the exe, then PATH).
    public string? DownloaderToolPath { get; set; }
    public string DownloaderArguments { get; set; } = "-x --audio-format mp3 --embed-thumbnail --add-metadata";
    public bool DownloaderAutoMode { get; set; }

    // Calendrier (§57): notifications paramétrables à la Samsung Calendar. Each kind carries its
    // own lead time in minutes, measured back from a reference moment — 09:00 on the item's day
    // for all-day rows (birthdays, deadlines, all-day events), the event's clock time for timed
    // ones. 0 = notified at the reference moment itself; a negative-effect very large value =
    // effectively "the day before/at N days". A kind the user sets to -1 is never notified.
    public bool CalendarNotificationsEnabled { get; set; } = true;
    public bool CalendarDailyToastEnabled { get; set; } = true;
    // Date of the last shown startup toast, so it appears once per day and not on every relaunch.
    public DateTime? CalendarDailyToastDate { get; set; }
    public int CalendarBirthdayLeadMinutes { get; set; } = 0;
    public int CalendarDeadlineLeadMinutes { get; set; } = 2880; // 2 jours avant une échéance
    public int CalendarLeadMinutesSortie { get; set; } = 120;
    public int CalendarLeadMinutesPlan { get; set; } = 1440;
    public int CalendarLeadMinutesVoyage { get; set; } = 10080; // 1 semaine avant
    public int CalendarLeadMinutesRendezvous { get; set; } = 60;
    public int CalendarLeadMinutesAutre { get; set; } = 1440;
    // Keys ("Source:RefId:date") already notified — kept so a lead-time reminder fires once.
    // Pruned of stale keys on each notification pass.
    public List<string> CalendarNotifiedKeys { get; set; } = new();
}

