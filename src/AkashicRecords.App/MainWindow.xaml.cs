using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using AkashicRecords.App.Views;
using AkashicRecords.Infrastructure.Configuration;
using AkashicRecords.Infrastructure.Persistence;
using AkashicRecords.Infrastructure.WindowsIntegration;

namespace AkashicRecords.App;

/// <summary>
/// Interaction logic for MainWindow.xaml
/// </summary>
public partial class MainWindow : Window
{
    private const int HotkeyId = 9000;
    private const int WM_NCHITTEST = 0x0084;
    private const int HTTRANSPARENT = -1;

    private readonly ConfigService _configService = new();
    private readonly AppConfig _config;

    private GlobalHotkey? _hotkey;
    private TrayIcon? _trayIcon;
    private ScreenEdgeDock? _screenEdgeDock;
    private MusicPlayerWindow? _musicPlayer;
    private FilmOfTheDayWidget? _filmWidget;
    private DesktopReminderWidget? _reminderWidget;
    private DownloaderWindow? _downloaderWindow;
    private Views.SettingsView? _settingsView;
    private bool _isExiting;
    private string? _activeSection;
    private readonly bool _screenshotMode;

    private readonly ArtworkRepository _artworkRepository = new(new SqliteConnectionFactory());
    private readonly TransitionRepository _transitionRepository = new(new SqliteConnectionFactory());
    private readonly WatchlistItemRepository _watchlistRepository = new(new SqliteConnectionFactory());
    private readonly FilmOfTheDayService _filmOfTheDayService;
    private AkashicRecords.Domain.WatchlistItem? _todaysFilmPick;
    private AkashicRecords.Domain.WatchlistItem? _todaysAnimPick;
    private DispatcherTimer? _midnightTimer;
    private CalendarToast? _calendarToast;
    private readonly CalendarService _calendarService = new(new SqliteConnectionFactory());
    // Lead-time reminder check. Only started when a future timed reminder actually exists
    // (§45: no idle timers); stopped when none remain until the next reschedule.
    private DispatcherTimer? _calendarTimer;
    private DateTime _lastCalendarCheck = DateTime.MinValue;

    private readonly GlobalSearchService _searchService = new(new SqliteConnectionFactory());
    private readonly DispatcherTimer _searchDebounce;

    // Parameterless ctor: used by the XAML loader (StartupUri="MainWindow.xaml").
    public MainWindow()
    {
        InitializeComponent();

        _config = _configService.Load();
        _filmOfTheDayService = new FilmOfTheDayService(_watchlistRepository);
        new SchemaInitializer(new SqliteConnectionFactory()).EnsureCreated();
        FitToScreen();

        _config = _configService.Load();
        _filmOfTheDayService = new FilmOfTheDayService(_watchlistRepository);
        new SchemaInitializer(new SqliteConnectionFactory()).EnsureCreated();
        FitToScreen();

        // One-shot debounce: started on each keystroke, stopped as soon as it fires so the app
        // never keeps a timer ticking while idle (§45).
        _searchDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _searchDebounce.Tick += SearchDebounce_OnTick;

        Loaded += (_, _) => SetupWindowsIntegration();
    }

    // Screenshot-mode ctor: opens the window headless (no tray/hotkey integration), so the
    // --screenshot path can render a section and exit without ambient widgets or the hotkey.
    public MainWindow(bool screenshotMode)
    {
        _screenshotMode = screenshotMode;
        InitializeComponent();

        _config = _configService.Load();
        _filmOfTheDayService = new FilmOfTheDayService(_watchlistRepository);
        new SchemaInitializer(new SqliteConnectionFactory()).EnsureCreated();
        FitToScreen();

        _searchDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _searchDebounce.Tick += SearchDebounce_OnTick;
    }

    private void FitToScreen()
    {
        // Use full screen bounds, not WorkArea — WorkArea excludes our own AppBar reservation.
        Left = 0;
        Top = 0;
        Width = SystemParameters.PrimaryScreenWidth;
        Height = SystemParameters.PrimaryScreenHeight;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;
        HwndSource.FromHwnd(hwnd)!.AddHook(HitTestHook);
        _screenEdgeDock = new ScreenEdgeDock(hwnd);
    }

    // Lets clicks pass through to whatever is behind the window, except over the
    // header (always interactive) or the open section's content area.
    private IntPtr HitTestHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WM_NCHITTEST) return IntPtr.Zero;

        var screenPoint = new Point(unchecked((short)(long)lParam), unchecked((short)((long)lParam >> 16)));

        if (IsPointInside(HeaderBar, screenPoint) || (_activeSection is not null && IsPointInside(SectionBackdrop, screenPoint)))
        {
            return IntPtr.Zero;
        }

        handled = true;
        return new IntPtr(HTTRANSPARENT);
    }

    private static bool IsPointInside(FrameworkElement element, Point screenPoint)
    {
        var topLeft = element.PointToScreen(new Point(0, 0));
        var bounds = new Rect(topLeft, new Size(element.ActualWidth, element.ActualHeight));
        return bounds.Contains(screenPoint);
    }

    private void SetupWindowsIntegration()
    {
        _hotkey = new GlobalHotkey(HotkeyId, GlobalHotkey.ModControl | GlobalHotkey.ModAlt, GlobalHotkey.VkSpace);
        _hotkey.Pressed += ToggleVisibility;

        _trayIcon = new TrayIcon("Archives Akashiques", StartupManager.IsEnabled());
        _trayIcon.ToggleRequested += ToggleVisibility;
        _trayIcon.ExitRequested += ExitApplication;
        _trayIcon.StartWithWindowsChanged += StartupManager.SetEnabled;
        _trayIcon.FilmWidgetToggled += OnFilmWidgetToggled;
        _trayIcon.RemindersToggled += OnRemindersToggled;
        _trayIcon.DownloaderRequested += ToggleDownloader;

        InitializeAmbientWidgets();
        BootstrapCalendarNotifications();

        DockHeaderToScreenEdge();
    }

    // --- Ambient desktop widgets (§32-34, §45) -------------------------------
    // Computed on demand at startup / toggle only - no idle timers.

    private void InitializeAmbientWidgets()
    {
        ComputeTodaysFilms();
        var hasFilm = _todaysFilmPick is not null || _todaysAnimPick is not null;
        _trayIcon?.SetFilmChecked(_config.ShowFilmOfTheDay && hasFilm);
        if (_config.ShowFilmOfTheDay && hasFilm) ShowFilmWidget();
        ScheduleMidnightRefresh();

        var reminders = LoadReminderItems();
        _trayIcon?.SetRemindersChecked(_config.ShowDesktopReminders && reminders.Count > 0);
        if (_config.ShowDesktopReminders && reminders.Count > 0) ShowReminderWidget(reminders);
    }

    // Resolves today's two picks (film + animated film) and persists them when they change.
    private void ComputeTodaysFilms()
    {
        var film = _filmOfTheDayService.PickForDay(AkashicRecords.Domain.WatchlistCategory.Film, _config.FilmOfTheDayFilmId, _config.FilmOfTheDayDate);
        var anim = _filmOfTheDayService.PickForDay(AkashicRecords.Domain.WatchlistCategory.FilmAnimation, _config.FilmOfTheDayAnimationId, _config.FilmOfTheDayDate);

        var changed = false;
        if (_config.FilmOfTheDayFilmId != film?.Id) { _config.FilmOfTheDayFilmId = film?.Id; changed = true; }
        if (_config.FilmOfTheDayAnimationId != anim?.Id) { _config.FilmOfTheDayAnimationId = anim?.Id; changed = true; }
        if (_config.FilmOfTheDayDate?.Date != DateTime.Today) { _config.FilmOfTheDayDate = DateTime.Today; changed = true; }
        if (changed) _configService.Save(_config);

        _todaysFilmPick = film;
        _todaysAnimPick = anim;
    }

    private static string? ResolveCover(AkashicRecords.Domain.WatchlistItem? item)
        => item?.CoverImagePath is { } path ? MediaStorage.ResolveFullPath(path) : null;

    // Fires just after the next midnight to refresh the pick while the app keeps running.
    private void ScheduleMidnightRefresh()
    {
        _midnightTimer ??= new DispatcherTimer();
        _midnightTimer.Stop();
        _midnightTimer.Tick -= MidnightTimer_OnTick;
        _midnightTimer.Tick += MidnightTimer_OnTick;
        var now = DateTime.Now;
        _midnightTimer.Interval = now.Date.AddDays(1).AddMinutes(1) - now;
        _midnightTimer.Start();
    }

    private void MidnightTimer_OnTick(object? sender, EventArgs e)
    {
        _midnightTimer?.Stop();
        ComputeTodaysFilms();
        _filmWidget?.ShowPicks(_todaysFilmPick, ResolveCover(_todaysFilmPick), _todaysAnimPick, ResolveCover(_todaysAnimPick));
        ScheduleMidnightRefresh();
    }

    private void ShowFilmWidget()
    {
        if (_todaysFilmPick is null && _todaysAnimPick is null) return;

        var filmCover = ResolveCover(_todaysFilmPick);
        var animCover = ResolveCover(_todaysAnimPick);

        if (_filmWidget is null)
        {
            _filmWidget = new FilmOfTheDayWidget();
            _filmWidget.HideRequested += OnFilmWidgetHidden;
            _filmWidget.PositionChanged += (l, t) => { _config.FilmWidgetLeft = l; _config.FilmWidgetTop = t; };
            _filmWidget.PositionCommitted += (l, t) => { _config.FilmWidgetLeft = l; _config.FilmWidgetTop = t; _configService.Save(_config); };
            _filmWidget.Loaded += (_, _) => PositionFilmWidget();
            _filmWidget.ShowPicks(_todaysFilmPick, filmCover, _todaysAnimPick, animCover);
            _filmWidget.Show();
        }
        else
        {
            _filmWidget.ShowPicks(_todaysFilmPick, filmCover, _todaysAnimPick, animCover);
            _filmWidget.Show();
            _filmWidget.Activate();
        }
    }

    private void PositionFilmWidget()
    {
        if (_filmWidget is null) return;
        if (_config.FilmWidgetLeft is { } left && _config.FilmWidgetTop is { } top)
        {
            _filmWidget.Left = left;
            _filmWidget.Top = top;
        }
        else
        {
            // Default bottom-right, raised above where the music player docks so they don't overlap.
            _filmWidget.Left = SystemParameters.PrimaryScreenWidth - _filmWidget.ActualWidth - 24;
            _filmWidget.Top = SystemParameters.PrimaryScreenHeight - _filmWidget.ActualHeight - 220;
        }
    }

    private void OnFilmWidgetHidden()
    {
        _config.ShowFilmOfTheDay = false;
        _configService.Save(_config);
        _trayIcon?.SetFilmChecked(false);
    }

    private void OnFilmWidgetToggled(bool on)
    {
        _config.ShowFilmOfTheDay = on;
        _configService.Save(_config);

        if (on)
        {
            ComputeTodaysFilms();
            if (_todaysFilmPick is not null || _todaysAnimPick is not null) ShowFilmWidget();
        }
        else
        {
            _filmWidget?.Hide();
        }
    }

    private IReadOnlyList<(string Title, string Text)> LoadReminderItems() =>
        _transitionRepository.GetDesktopReminders()
            .Select(t => (t.Title, t.ReminderText))
            .ToList();

    private void ShowReminderWidget(IReadOnlyList<(string Title, string Text)> items)
    {
        if (_reminderWidget is null)
        {
            _reminderWidget = new DesktopReminderWidget();
            _reminderWidget.HideRequested += OnReminderWidgetHidden;
            _reminderWidget.RefreshRequested += OnReminderRefreshRequested;
            _reminderWidget.PositionChanged += (l, t) => { _config.RemindersWidgetLeft = l; _config.RemindersWidgetTop = t; };
            _reminderWidget.PositionCommitted += (l, t) => { _config.RemindersWidgetLeft = l; _config.RemindersWidgetTop = t; _configService.Save(_config); };
            _reminderWidget.Loaded += (_, _) => PositionReminderWidget();
            _reminderWidget.SetReminders(items);
            _reminderWidget.Show();
        }
        else
        {
            _reminderWidget.SetReminders(items);
            _reminderWidget.Show();
            _reminderWidget.Activate();
        }
    }

    private void PositionReminderWidget()
    {
        if (_reminderWidget is null) return;
        if (_config.RemindersWidgetLeft is { } left && _config.RemindersWidgetTop is { } top)
        {
            _reminderWidget.Left = left;
            _reminderWidget.Top = top;
        }
        else
        {
            // Default top-right.
            _reminderWidget.Left = SystemParameters.PrimaryScreenWidth - _reminderWidget.ActualWidth - 24;
            _reminderWidget.Top = 60;
        }
    }

    private void OnReminderWidgetHidden()
    {
        _config.ShowDesktopReminders = false;
        _configService.Save(_config);
        _trayIcon?.SetRemindersChecked(false);
    }

    private void OnReminderRefreshRequested() => _reminderWidget?.SetReminders(LoadReminderItems());

    private void OnRemindersToggled(bool on)
    {
        _config.ShowDesktopReminders = on;
        _configService.Save(_config);

        if (on) ShowReminderWidget(LoadReminderItems());
        else _reminderWidget?.Hide();
    }

    // --- Calendrier notifications (§57) ---------------------------------------
    // Startup + midnight "toast du matin" (today's agenda) plus per-kind lead-time reminders,
    // all configurable from CalendarView's ⚙ panel. The 60s check timer only runs while a
    // future reminder actually exists (§45: no idle timers when nothing can fire).

    private void BootstrapCalendarNotifications()
    {
        if (_screenshotMode) return;
        CheckCalendarReminders();
        RescheduleCalendarTimer();
    }

    // Called by CalendarView whenever an item or a lead time changes: re-check immediately
    // (a brand-new event may already be due) and re-arm the timer for the new schedule.
    public void RescheduleCalendarNotifications()
    {
        if (_screenshotMode) return;
        CheckCalendarReminders();
        RescheduleCalendarTimer();
    }

    private void CalendarTimer_OnTick(object? sender, EventArgs e) => CheckCalendarReminders();

    private void CheckCalendarReminders()
    {
        if (!_config.CalendarNotificationsEnabled)
        {
            _calendarTimer?.Stop();
            return;
        }

        var now = DateTime.Now;
        var changed = false;

        // Morning toast: on first check of a new day (covers both startup and a midnight
        // rollover while the app keeps running), at most once per calendar day.
        if (now.Date != _lastCalendarCheck.Date)
        {
            _lastCalendarCheck = now;
            if (_config.CalendarDailyToastEnabled && _config.CalendarDailyToastDate?.Date != now.Date)
            {
                _config.CalendarDailyToastDate = now;
                changed = true;
                var todays = _calendarService.GetRange(now.Date, now.Date);
                if (todays.Count > 0) ShowCalendarToast("Aujourd'hui", todays, 0);
            }
        }

        var due = new List<AkashicRecords.Domain.AgendaItem>();
        foreach (var item in _calendarService.GetRange(now.Date, now.Date.AddDays(35)))
        {
            if (item.IsSpanContinuation) continue; // a multi-day event pings once, on its first day
            if (ReminderFireTime(item) is not { } when) continue;
            var key = item.NotificationKey;
            if (_config.CalendarNotifiedKeys.Contains(key)) continue;
            if (when > now) continue;

            // Already surfaced by today's morning toast AND due before that toast showed: the
            // morning card covered it. A reminder whose lead moment is later in the day (e.g. the
            // 18h ping for a 20h sortie, after an 8h morning toast) still fires at its own time.
            if (_config.CalendarDailyToastEnabled && item.Date.Date == now.Date
                && _config.CalendarDailyToastDate is { } toastAt && toastAt.Date == now.Date
                && when <= toastAt) continue;

            // Long-past (app launched hours late): mark notified quietly — not a "reminder" anymore.
            if (when < now.AddHours(-3))
            {
                _config.CalendarNotifiedKeys.Add(key);
                changed = true;
                continue;
            }

            due.Add(item);
            _config.CalendarNotifiedKeys.Add(key);
            changed = true;
        }

        if (PruneNotifiedKeys()) changed = true;
        if (changed) _configService.Save(_config);

        if (due.Count > 0)
            ShowCalendarToast("Rappel", due.OrderBy(i => i.Date).ThenBy(i => i.Hour ?? -1).ToList(), 60);
    }

    // When a reminder should pop: lead minutes back from the item's reference moment — its clock
    // time when timed, 09:00 on the day otherwise. -1 lead (per kind) disables that kind.
    private DateTime? ReminderFireTime(AkashicRecords.Domain.AgendaItem item)
    {
        var lead = item.Source switch
        {
            AkashicRecords.Domain.AgendaSource.Birthday => _config.CalendarBirthdayLeadMinutes,
            AkashicRecords.Domain.AgendaSource.Deadline => _config.CalendarDeadlineLeadMinutes,
            AkashicRecords.Domain.AgendaSource.Event => item.EventKind switch
            {
                AkashicRecords.Domain.CalendarEventKind.Sortie => _config.CalendarLeadMinutesSortie,
                AkashicRecords.Domain.CalendarEventKind.Plan => _config.CalendarLeadMinutesPlan,
                AkashicRecords.Domain.CalendarEventKind.Voyage => _config.CalendarLeadMinutesVoyage,
                AkashicRecords.Domain.CalendarEventKind.Rendezvous => _config.CalendarLeadMinutesRendezvous,
                _ => _config.CalendarLeadMinutesAutre,
            },
            _ => -1,
        };
        if (lead < 0) return null;

        var reference = item.Hour is int h
            ? item.Date.Date.AddHours(h).AddMinutes(item.Minute ?? 0)
            : item.Date.Date.AddHours(9);
        return reference.AddMinutes(-lead);
    }

    // Drop keys older than two days so the persisted list can't grow forever.
    private bool PruneNotifiedKeys()
    {
        var cutoff = DateTime.Today.AddDays(-2);
        return _config.CalendarNotifiedKeys.RemoveAll(key =>
        {
            var idx = key.LastIndexOf(':');
            return idx < 0
                   || !DateTime.TryParseExact(key[(idx + 1)..], "yyyy-MM-dd",
                       System.Globalization.CultureInfo.InvariantCulture,
                       System.Globalization.DateTimeStyles.None, out var date)
                   || date < cutoff;
        }) > 0;
    }

    private void RescheduleCalendarTimer()
    {
        if (!_config.CalendarNotificationsEnabled)
        {
            _calendarTimer?.Stop();
            return;
        }

        var now = DateTime.Now;
        var anyFuture = _calendarService.GetRange(now.Date, now.Date.AddDays(35))
            .Any(item => !item.IsSpanContinuation
                         && !_config.CalendarNotifiedKeys.Contains(item.NotificationKey)
                         && ReminderFireTime(item) is { } when && when > now);

        if (!anyFuture)
        {
            _calendarTimer?.Stop();
            return;
        }

        _calendarTimer ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(60) };
        _calendarTimer.Tick -= CalendarTimer_OnTick;
        _calendarTimer.Tick += CalendarTimer_OnTick;
        _calendarTimer.Start();
    }

    private void ShowCalendarToast(string header, IReadOnlyList<AkashicRecords.Domain.AgendaItem> items, int autoCloseSeconds)
    {
        _calendarToast ??= new CalendarToast();
        _calendarToast.ItemClicked -= OnCalendarToastItemClicked;
        _calendarToast.ItemClicked += OnCalendarToastItemClicked;

        var lines = items.Select(i => new CalendarToast.ToastLine(
            CalendarView.ColorFor(i),
            i.TimeText,
            i.Title,
            i.Subtitle,
            i.NotificationKey)).ToList();

        _calendarToast.SetItems(header, lines, autoCloseSeconds);
        if (!_calendarToast.IsVisible) _calendarToast.Show();
        _calendarToast.UpdateLayout();
        // Bottom-right, raised above where the film widget docks so the two never overlap.
        _calendarToast.Left = SystemParameters.PrimaryScreenWidth - _calendarToast.ActualWidth - 24;
        _calendarToast.Top = SystemParameters.PrimaryScreenHeight - _calendarToast.ActualHeight - 360;
        _calendarToast.Activate();
    }

    // Clicking a toast row deep-links into the calendar section.
    private void OnCalendarToastItemClicked(string key)
    {
        foreach (var button in new[] { OrganisationNav, JournauxNav, CollectionsNav, ArchivesNav, BudgetNav, CalendarNav })
        {
            button.IsChecked = button == CalendarNav;
        }
        _activeSection = "Calendrier";
        UpdateSectionContent();
    }

    // Reserves the header's strip of screen space so maximized/snapped windows leave it free.
    private void DockHeaderToScreenEdge()
    {
        var (left, top, right, bottom) = HeaderScreenBounds();
        _screenEdgeDock?.DockTop(left, top, right, bottom);
    }

    // Single source of truth for where the header actually renders on screen (physical
    // pixels via PointToScreen), shared by hit-testing and the appbar reservation so
    // the two can never drift apart.
    private (int Left, int Top, int Right, int Bottom) HeaderScreenBounds()
    {
        var topLeft = HeaderBar.PointToScreen(new Point(0, 0));
        var bottomRight = HeaderBar.PointToScreen(new Point(HeaderBar.ActualWidth, HeaderBar.ActualHeight));
        return ((int)Math.Round(topLeft.X), (int)Math.Round(topLeft.Y), (int)Math.Round(bottomRight.X), (int)Math.Round(bottomRight.Y));
    }

    private void ToggleVisibility()
    {
        if (IsVisible)
        {
            var (left, top, right, _) = HeaderScreenBounds();
            _screenEdgeDock?.ReleaseSpace(left, top, right);
            Hide();
        }
        else
        {
            Show();
            Activate();
            DockHeaderToScreenEdge();
        }
    }

    // Toggles the floating music player. It's a separate top-level window so it keeps
    // playing while the main window is hidden; created lazily on first click.
    private void MusicButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (_musicPlayer is null)
        {
            _musicPlayer = new MusicPlayerWindow();
            // Position it bottom-right of the primary screen once it has measured itself.
            _musicPlayer.Loaded += (_, _) =>
            {
                _musicPlayer.Left = SystemParameters.PrimaryScreenWidth - _musicPlayer.ActualWidth - 24;
                _musicPlayer.Top = SystemParameters.PrimaryScreenHeight - _musicPlayer.ActualHeight - 24;
            };
            _musicPlayer.Show();
            return;
        }

        if (_musicPlayer.IsVisible)
        {
            _musicPlayer.Hide();
        }
        else
        {
            _musicPlayer.Show();
            _musicPlayer.Activate();
        }
    }

    // Toggles the settings window (created lazily). Separate top-level window so it survives
    // the main window hiding; closed only on real app exit.
    private void SettingsButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (_settingsView is null)
        {
            _settingsView = new Views.SettingsView(_config, _configService);
            _settingsView.Show();
            return;
        }

        if (_settingsView.IsVisible)
        {
            _settingsView.Hide();
        }
        else
        {
            _settingsView.Show();
            _settingsView.Activate();
        }
    }

    // Toggles the downloader queue window (queue-only utility; created lazily). Separate top-level
    // window so it survives the main window hiding; closed only on real app exit.
    private void ToggleDownloader()
    {
        if (_downloaderWindow is null)
        {
            _downloaderWindow = new DownloaderWindow(_config, _configService);
            _downloaderWindow.LibraryChanged += () => _musicPlayer?.ReloadLibrary();
            _downloaderWindow.Loaded += (_, _) =>
            {
                _downloaderWindow.Left = SystemParameters.PrimaryScreenWidth - _downloaderWindow.ActualWidth - 24;
                _downloaderWindow.Top = 80;
            };
            _downloaderWindow.Show();
            return;
        }

        if (_downloaderWindow.IsVisible)
        {
            _downloaderWindow.Hide();
        }
        else
        {
            _downloaderWindow.Show();
            _downloaderWindow.Activate();
        }
    }

    private void NavButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton clicked || clicked.Tag is not string sectionName) return;
        foreach (var button in new[] { OrganisationNav, JournauxNav, CollectionsNav, ArchivesNav, BudgetNav, CalendarNav, AtelierNav })
        {
            if (button != clicked) button.IsChecked = false;
        }

        _activeSection = clicked.IsChecked == true ? sectionName : null;
        UpdateSectionContent();
    }

    // --- Global search (§48/§51) ---------------------------------------------

    private void SearchBox_OnTextChanged(object sender, TextChangedEventArgs e)
    {
        SearchPlaceholder.Visibility = SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;

        _searchDebounce.Stop();
        if (SearchBox.Text.Trim().Length == 0)
        {
            CloseSearchPopup();
            return;
        }
        _searchDebounce.Start();
    }

    private void SearchDebounce_OnTick(object? sender, EventArgs e)
    {
        _searchDebounce.Stop();
        RunSearch();
    }

    private void RunSearch()
    {
        var query = SearchBox.Text.Trim();
        if (query.Length == 0)
        {
            CloseSearchPopup();
            return;
        }

        var results = _searchService.Search(query);
        SearchResultsList.ItemsSource = results;
        SearchResultsList.Visibility = results.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        NoResultsText.Visibility = results.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        SearchPopup.IsOpen = true;
    }

    private void CloseSearchPopup()
    {
        _searchDebounce.Stop();
        SearchPopup.IsOpen = false;
    }

    private void SearchBox_OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape when SearchPopup.IsOpen:
                CloseSearchPopup();
                e.Handled = true; // don't let Escape bubble up and close the open section
                break;
            case Key.Down when SearchPopup.IsOpen && SearchResultsList.Items.Count > 0:
                SearchResultsList.SelectedIndex = 0;
                (SearchResultsList.ItemContainerGenerator.ContainerFromIndex(0) as ListBoxItem)?.Focus();
                e.Handled = true;
                break;
            case Key.Enter when SearchPopup.IsOpen:
                var target = SearchResultsList.SelectedItem as SearchResult
                             ?? SearchResultsList.Items.OfType<SearchResult>().FirstOrDefault();
                if (target is not null) ActivateSearchResult(target);
                e.Handled = true;
                break;
        }
    }

    private void SearchResultsList_OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && SearchResultsList.SelectedItem is SearchResult result)
        {
            ActivateSearchResult(result);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            CloseSearchPopup();
            SearchBox.Focus();
            e.Handled = true;
        }
    }

    private void SearchResultsList_OnMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (SearchResultsList.SelectedItem is SearchResult result) ActivateSearchResult(result);
    }

    private void ActivateSearchResult(SearchResult result)
    {
        CloseSearchPopup();
        SearchBox.Text = string.Empty;
        NavigateToSearchResult(result);
    }

    // Maps a result's Section to the nav toggle + active section, (re)creates the view, and forwards
    // the deep-link to it. Music results just surface the floating player (and start the track).
    public void NavigateToSearchResult(SearchResult result)
    {
        if (result.Section == "Musique")
        {
            ShowMusicPlayerForTrack(result.PrimaryId);
            return;
        }

        var nav = result.Section switch
        {
            "Organisation" => OrganisationNav,
            "Journaux" => JournauxNav,
            "Collections" => CollectionsNav,
            "Archives" => ArchivesNav,
            "Budget" => BudgetNav,
            _ => null
        };
        if (nav is null) return;

        if (_activeSection != result.Section)
        {
            foreach (var button in new[] { OrganisationNav, JournauxNav, CollectionsNav, ArchivesNav })
            {
                button.IsChecked = button == nav;
            }
            _activeSection = result.Section;
            UpdateSectionContent();
        }

        // The freshly-created view isn't loaded yet: it stores the target and applies it on Loaded.
        if (SectionContent.Content is ISearchNavigable navigable)
        {
            navigable.NavigateToSearchResult(result);
        }
    }

    private void ShowMusicPlayerForTrack(int trackId)
    {
        if (_musicPlayer is null)
        {
            _musicPlayer = new MusicPlayerWindow();
            _musicPlayer.Loaded += (_, _) =>
            {
                _musicPlayer.Left = SystemParameters.PrimaryScreenWidth - _musicPlayer.ActualWidth - 24;
                _musicPlayer.Top = SystemParameters.PrimaryScreenHeight - _musicPlayer.ActualHeight - 24;
                _musicPlayer.PlayTrackById(trackId);
            };
            _musicPlayer.Show();
            return;
        }

        if (!_musicPlayer.IsVisible) _musicPlayer.Show();
        _musicPlayer.Activate();
        _musicPlayer.PlayTrackById(trackId);
    }

    // Closes whichever section is open (Organisation/Journaux/Collections/Archives), leaving just the header bar.
    // Bubbling KeyDown (not tunneling) so a section's own overlay/dialog gets first chance to handle Escape.
    private void MainWindow_OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || _activeSection is null) return;

        foreach (var button in new[] { OrganisationNav, JournauxNav, CollectionsNav, ArchivesNav, BudgetNav, CalendarNav, AtelierNav })
        {
            button.IsChecked = false;
        }

        _activeSection = null;
        UpdateSectionContent();
        e.Handled = true;
    }

    private void UpdateSectionContent()
    {
        SectionContent.Content = _activeSection switch
        {
            "Organisation" => new OrganisationView(_config),
            "Journaux" => new JournauxView(_config),
            "Collections" => new CollectionsView(_config),
            "Archives" => new ArchivesView(_config),
            "Budget" => new BudgetView(_config),
            "Calendrier" => new CalendarView(_config, RescheduleCalendarNotifications),
            "Atelier" => new AtelierView(_config),
            _ => null
        };

        // Only paint/block the backdrop when a section is actually open.
        SectionBackdrop.Background = _activeSection is null
            ? System.Windows.Media.Brushes.Transparent
            : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(0xEE, 0x1A, 0x1A, 0x2E));

        if (_activeSection is not null)
        {
            _config.LastActiveSection = _activeSection;
        }
    }

    // Headless capture: opens the named section and, if a sub-tab is given, selects it so the
    // rendered view is the real one (see App --screenshot). Runs without the tray/hotkey integration.
    public void OpenSectionForScreenshot(string section, string? subTab)
    {
        var nav = section switch
        {
            "Organisation" => OrganisationNav,
            "Journaux" => JournauxNav,
            "Collections" => CollectionsNav,
            "Archives" => ArchivesNav,
            "Budget" => BudgetNav,
            "Calendrier" => CalendarNav,
            "Atelier" => AtelierNav,
            _ => null
        };
        if (nav is null) return;

        nav.IsChecked = true;
        _activeSection = section;

        // Tell the view it's being created for a headless capture so it doesn't reopen a
        // persisted fiche over the screenshot view (RestoreActiveItem runs on Loaded, before
        // ShowTabForScreenshot is called below).
        if (SectionContent.Content is CollectionsView collView)
        {
            collView.ScreenshotMode = true;
        }

        UpdateSectionContent();

        if (subTab is { } tab)
        {
            // Views that expose a sub-tab capture path use it directly. The view isn't Loaded yet,
            // so it stores the request and applies it on Loaded.
            switch (SectionContent.Content)
            {
                case OrganisationView org when tab is "Projets" or "Transitions":
                    org.ShowTabForScreenshot(tab);
                    break;
                case JournauxView jour when tab is "Personal" or "Recipes" or "Poetry" or "Artistic":
                    jour.ShowTabForScreenshot(tab);
                    break;
                case CollectionsView coll when tab is "Film" or "FilmAnimation" or "TvSeries" or "Anime" or "Livre" or "VideoGame" or "Watchlist":
                    coll.ShowTabForScreenshot(tab);
                    break;
                case BudgetView bud when tab is "Overview" or "Transactions" or "Plans":
                    bud.ShowTabForScreenshot(tab);
                    break;
                case CalendarView cal when tab is "Event" or "Settings" or "Edit" or "Picker" or "Hover":
                    cal.ShowPaneForScreenshot(tab);
                    break;
                case JournauxView jour when tab.StartsWith("Poetry:"):
                    // Reaches the two poetry sub-panes the plain "Poetry" capture cannot: the opened
                    // book and the poem page (see JournauxView.ShowPoetryPaneForScreenshot).
                    jour.ShowTabForScreenshot("Poetry");
                    jour.ShowPoetryPaneForScreenshot(tab["Poetry:".Length..]);
                    break;
                case AtelierView atel when tab is "Cartes" or "Chrono":
                    atel.ShowTabForScreenshot(tab);
                    break;
            }
        }
    }

    // Headless capture of the calendar link picker: the popup renders in a detached visual tree,
    // so App.RunScreenshot grabs this element instead of the window content (see App.xaml.cs).
    public FrameworkElement? CalendarPickerElementForScreenshot() =>
        SectionContent.Content is CalendarView cal ? cal.PickerElementForScreenshot() : null;

    // Same trick for the link hover-preview card (see CalendarView.PreviewCardForScreenshot).
    public FrameworkElement? CalendarPreviewCardForScreenshot() =>
        SectionContent.Content is CalendarView cal ? cal.PreviewCardForScreenshot() : null;

    private void ExitApplication()
    {
        _isExiting = true;
        _configService.Save(_config);
        _screenEdgeDock?.Remove();
        _hotkey?.Dispose();
        _trayIcon?.Dispose();
        if (_musicPlayer is not null)
        {
            _musicPlayer.ShutdownPlayer();
            _musicPlayer.ForceClose();
        }
        _filmWidget?.ForceClose();
        _reminderWidget?.ForceClose();
        _downloaderWindow?.ForceClose();
        _settingsView?.ForceClose();
        _calendarToast?.ForceClose();
        _midnightTimer?.Stop();
        _calendarTimer?.Stop();
        Application.Current.Shutdown();
    }

    private void MainWindow_OnClosing(object? sender, CancelEventArgs e)
    {
        // Closing the window (e.g. Alt+F4) hides it instead of exiting the process;
        // only the tray "Exit" menu actually terminates the app.
        if (_isExiting) return;
        e.Cancel = true;
        _configService.Save(_config);
        var (left, top, right, _) = HeaderScreenBounds();
        _screenEdgeDock?.ReleaseSpace(left, top, right);
        Hide();
    }
}
