using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AkashicRecords.Domain;
using AkashicRecords.Infrastructure.Configuration;
using AkashicRecords.Infrastructure.Persistence;
using AkashicRecords.Infrastructure.Web;
using Microsoft.Win32;

namespace AkashicRecords.App;

public enum TrackSaveState { None, Queued }

public partial class MusicPlayerWindow : Window
{
    private readonly MediaPlayer _mediaPlayer = new();
    private readonly DispatcherTimer _positionTimer;

    private readonly MusicTrackRepository _trackRepository = new(new SqliteConnectionFactory());
    private readonly MoodRepository _moodRepository = new(new SqliteConnectionFactory());
    private readonly TrackMoodRepository _trackMoodRepository = new(new SqliteConnectionFactory());
    private readonly PlaylistRepository _playlistRepository = new(new SqliteConnectionFactory());
    private readonly PlaylistTrackRepository _playlistTrackRepository = new(new SqliteConnectionFactory());
    private readonly DownloadItemRepository _downloadRepository = new(new SqliteConnectionFactory());
    private readonly DownloadFolderRepository _folderRepository = new(new SqliteConnectionFactory());
    private readonly MediaStorage _mediaStorage = new();
    private readonly MusicLibraryScanner _scanner;

    // Keyless similar-tracks lookup (iTunes + MusicBrainz), best-effort.
    private readonly MusicRecommendationService _recommendations = new();

    // Bumped on every PlayAt: a fetch that finishes after the user moved on is dropped.
    private int _playToken;
    // True from PlayAt on a fetched entry until its file opens; transport controls are inert meanwhile.
    private bool _isResolving;
    // Re-entrancy guard for the end-of-queue chain lookup (network round trip).
    private bool _advancing;

    // Chain mode (Spotify-style): the suggestions of the current track feed the next song
    // once the queue is exhausted. Refreshed with each new track.
    private List<SimilarTrack> _similarList = new();
    private int _chainIndex;

    // Album (music/<folder>) of the track this run started from: queued suggestions go to its zone.
    private string _runAlbum = string.Empty;

    // Normalized titles already heard this session, so the chain never revisits one
    // (MusicBrainz lists bounce A -> B -> A endlessly without this).
    private readonly HashSet<string> _playedKeys = new();

    // Title/artist of the current queue entry: seed of the similar lookup and of the reseed.
    private string _lastTitle = string.Empty;
    private string _lastArtist = string.Empty;

    // Bumped on every similar-list lookup so only the newest async result is rendered.
    private long _similarGeneration;

    // Everything that played or will play, in order: Prev/Next just move _currentIndex along it.
    private readonly List<QueueEntry> _queue = new();
    private int _currentIndex = -1;
    private bool _isPlaying;

    private int? _selectedMoodId;
    private string _librarySearch = string.Empty;
    private bool _updatingPositionFromTimer;

    // Set only via ForceClose() when the app is genuinely exiting; otherwise closing hides the window.
    private bool _isExiting;

    public MusicPlayerWindow()
    {
        InitializeComponent();

        // Sensible starting size; the window is resizeable (grip) so the user controls it.
        Width = SystemParameters.PrimaryScreenWidth / 5.0;

        _scanner = new MusicLibraryScanner(_trackRepository);
        ClearStreamCache();

        _mediaPlayer.Volume = VolumeSlider.Value;
        _mediaPlayer.MediaOpened += MediaPlayer_OnMediaOpened;
        _mediaPlayer.MediaEnded += MediaPlayer_OnMediaEnded;
        _mediaPlayer.MediaFailed += MediaPlayer_OnMediaFailed;

        // Runs only while something is playing; stopped on pause/stop/close to keep the app idle-quiet (§45).
        _positionTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _positionTimer.Tick += PositionTimer_OnTick;

        Loaded += (_, _) =>
        {
            _scanner.ScanAndSync();
            RefreshMoods();
            RefreshLibrary();
            RefreshQueue();
        };
    }

    // Only DragMove when the click didn't originate on an interactive control, otherwise
    // buttons/sliders/textboxes would never receive the mouse (capture steals it first).
    private void Root_OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (IsInteractiveOrigin(e.OriginalSource as DependencyObject)) return;
        try { DragMove(); } catch { /* DragMove throws if button already released */ }
    }

    private static bool IsInteractiveOrigin(DependencyObject? source)
    {
        while (source is not null)
        {
            if (source is ButtonBase or TextBoxBase or Slider or Thumb or ScrollBar) return true;
            source = VisualTreeHelper.GetParent(source);
        }
        return false;
    }

    // --- Playback ------------------------------------------------------------

    // Entry point for global search: play a single track by its id (queue = just that track).
    public void PlayTrackById(int trackId)
    {
        var track = _trackRepository.GetById(trackId);
        if (track is not null) PlayQueue(new[] { track }, 0);
    }

    private void PlayQueue(IReadOnlyList<MusicTrack> tracks, int startIndex)
    {
        _playedKeys.Clear();
        // New run: the accumulating suggestion pool restarts empty.
        _similarList = new();
        _chainIndex = 0;
        _runAlbum = tracks.Count > 0 ? tracks[Math.Clamp(startIndex, 0, tracks.Count - 1)].Album : string.Empty;
        _queue.Clear();
        _queue.AddRange(tracks.Select(t => new QueueEntry { Local = t }));
        RefreshQueue();
        if (_queue.Count == 0)
        {
            ShowNoTracks();
            return;
        }
        PlayAt(Math.Clamp(startIndex, 0, _queue.Count - 1));
    }

    private void PlayAt(int index)
    {
        if (index < 0 || index >= _queue.Count) return;
        var token = ++_playToken;
        _currentIndex = index;
        var entry = _queue[index];
        _playedKeys.Add(Normalize(entry.Title));
        _lastTitle = entry.Title;
        _lastArtist = entry.Artist;

        StatusText.Visibility = Visibility.Collapsed;
        ResetPosition();

        if (entry.Local is { } track)
        {
            _isResolving = false;
            try
            {
                OpenAndPlay(new Uri(MediaStorage.ResolveFullPath(track.FilePath)));
            }
            catch
            {
                // A bad/locked file must not crash the app; leave the player stopped.
                _isPlaying = false;
                _positionTimer.Stop();
            }
            UpdateNowPlaying(track);
        }
        else
        {
            // Silence the previous song right away; the stream URL takes a moment to resolve.
            _mediaPlayer.Stop();
            _isPlaying = false;
            _positionTimer.Stop();
            _isResolving = true;
            ShowStreamNowPlaying(entry);
            _ = PlayStreamAsync(entry, token);
        }
        UpdateSaveButton();
        RefreshQueue();
    }

    // Slider back to a neutral baseline before opening anything. Otherwise MediaOpened lowering
    // Maximum clamps Value, ValueChanged fires and is mistaken for a user seek to the end,
    // which re-fires MediaEnded and cascades through several tracks.
    private void ResetPosition()
    {
        _updatingPositionFromTimer = true;
        PositionSlider.Maximum = 1;
        PositionSlider.Value = 0;
        _updatingPositionFromTimer = false;
        ElapsedText.Text = "0:00";
        TotalText.Text = "0:00";
    }

    private void OpenAndPlay(Uri uri)
    {
        _mediaPlayer.Open(uri);
        _mediaPlayer.Play();
        _isPlaying = true;
        PlayPauseButton.Content = "⏸";
        _positionTimer.Start();
    }

    private void StopPlayback()
    {
        _mediaPlayer.Stop();
        _isPlaying = false;
        _isResolving = false;
        _positionTimer.Stop();
        PlayPauseButton.Content = "▶";
        StatusText.Visibility = Visibility.Collapsed;
        ResetPosition();
    }

    private void ShowStreamNowPlaying(QueueEntry entry)
    {
        TitleText.Text = string.IsNullOrWhiteSpace(entry.Title) ? "(sans titre)" : entry.Title;
        ArtistText.Text = entry.Artist;
        PlayPauseButton.Content = "…";
        CoverImage.Source = null;
        CoverImage.Visibility = Visibility.Collapsed;
        CoverPlaceholder.Visibility = Visibility.Visible;
        if (entry.Source is null)
        {
            StatusText.Text = "Chargement du titre…";
            StatusText.Visibility = Visibility.Visible;
        }
    }

    // Suggestion not in the library: yt-dlp fetches the full track into a temp file, played locally
    // (googlevideo links opened directly 403 intermittently). 30 s iTunes clip if the fetch fails.
    // The fetch lives on the entry: back-and-forth reuses it instead of spawning yt-dlp again.
    private async Task PlayStreamAsync(QueueEntry entry, int token)
    {
        if (entry.Source is null)
        {
            var remote = entry.Remote!;
            entry.Fetch ??= FetchAudioAsync(remote);
            var path = await entry.Fetch;
            if (token != _playToken) return;
            entry.OnPreview = path is null;
            entry.Source = path ?? remote.PreviewUrl;
        }
        _isResolving = false;
        StatusText.Visibility = Visibility.Collapsed;
        if (entry.Source is null)
        {
            StopPlayback();
            return;
        }
        try { OpenAndPlay(new Uri(entry.Source)); }
        catch { StopPlayback(); }
    }

    private void UpdateNowPlaying(MusicTrack track)
    {
        TitleText.Text = string.IsNullOrWhiteSpace(track.Title) ? "(sans titre)" : track.Title;
        ArtistText.Text = track.Artist;
        PlayPauseButton.Content = _isPlaying ? "⏸" : "▶";

        if (track.CoverImagePath is { } cover)
        {
            var full = MediaStorage.ResolveFullPath(cover);
            if (System.IO.File.Exists(full))
            {
                CoverImage.Source = LoadImage(full);
                CoverImage.Visibility = Visibility.Visible;
                CoverPlaceholder.Visibility = Visibility.Collapsed;
                return;
            }
        }
        CoverImage.Source = null;
        CoverImage.Visibility = Visibility.Collapsed;
        CoverPlaceholder.Visibility = Visibility.Visible;
    }

    private void PlayPauseButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (_currentIndex < 0)
        {
            // Nothing loaded yet: start the (filtered) library from the top.
            var tracks = CurrentLibraryTracks();
            if (tracks.Count > 0) PlayQueue(tracks, 0);
            else ShowNoTracks();
            return;
        }

        if (_isResolving) return;

        if (_isPlaying)
        {
            _mediaPlayer.Pause();
            _isPlaying = false;
            _positionTimer.Stop();
        }
        else
        {
            _mediaPlayer.Play();
            _isPlaying = true;
            _positionTimer.Start();
        }
        PlayPauseButton.Content = _isPlaying ? "⏸" : "▶";
    }

    private void PrevButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (_currentIndex < 0) return;
        // Usual player rule: past 3 s restart the song, otherwise go to the previous one.
        if (!_isResolving && (_mediaPlayer.Position.TotalSeconds > 3 || _currentIndex == 0))
        {
            _mediaPlayer.Position = TimeSpan.Zero;
            _updatingPositionFromTimer = true;
            PositionSlider.Value = 0;
            _updatingPositionFromTimer = false;
            ElapsedText.Text = "0:00";
            return;
        }
        if (_currentIndex > 0) PlayAt(_currentIndex - 1);
    }

    private async void NextButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (_currentIndex < 0) return;
        if (!await AdvanceAsync()) StopPlayback();
    }

    private async void MediaPlayer_OnMediaEnded(object? sender, EventArgs e)
    {
        if (!await AdvanceAsync()) StopPlayback();
    }

    // Unplayable file: a fetched suggestion falls back to its 30 s iTunes clip, anything else moves on.
    private async void MediaPlayer_OnMediaFailed(object? sender, System.Windows.Media.ExceptionEventArgs e)
    {
        _isPlaying = false;
        _positionTimer.Stop();
        PlayPauseButton.Content = "▶";
        if (_currentIndex < 0 || _currentIndex >= _queue.Count) return;
        var entry = _queue[_currentIndex];
        if (entry.Remote is { PreviewUrl: { } preview } && !entry.OnPreview)
        {
            entry.OnPreview = true;
            entry.Source = preview;
            ResetPosition();
            try { OpenAndPlay(new Uri(preview)); } catch { StopPlayback(); }
            return;
        }
        if (!await AdvanceAsync()) StopPlayback();
    }

    private void MediaPlayer_OnMediaOpened(object? sender, EventArgs e)
    {
        if (!_mediaPlayer.NaturalDuration.HasTimeSpan) return;

        var total = _mediaPlayer.NaturalDuration.TimeSpan.TotalSeconds;
        PositionSlider.Maximum = total <= 0 ? 1 : total;
        TotalText.Text = FormatTime(total);

        // Persist the duration the first time we learn it, so the library can show it without re-opening.
        if (_currentIndex >= 0 && _currentIndex < _queue.Count &&
            _queue[_currentIndex].Local is { DurationSeconds: null } track)
        {
            track.DurationSeconds = total;
            _trackRepository.UpdateDuration(track.Id, total);
            RefreshLibrary();
        }

        // Fire-and-forget similar-tracks lookup for the freshly opened track.
        RefreshSimilarForCurrentTrack();
    }

    // Fills the "SIMILAIRES" section (iTunes + MusicBrainz), matched against the local library
    // so a click plays the local copy when present. Collapsed when there are no hits.
    private async void RefreshSimilarForCurrentTrack()
    {
        var seedTitle = _lastTitle;
        var seedArtist = _lastArtist;
        if (string.IsNullOrWhiteSpace(seedTitle)) return;
        var gen = ++_similarGeneration;
        var similar = await _recommendations.GetSimilarAsync(seedTitle, seedArtist);

        // A newer lookup started while this one was in flight; that one renders.
        if (gen != _similarGeneration) return;

        // Already-owned titles (pocket + full library) are not proposed again.
        var library = _trackRepository.GetAll();
        bool SameAsSeed(SimilarTrack s)
        {
            var a = Normalize(seedTitle);
            var b = Normalize(s.Title);
            return a.Length > 0 && (a.Contains(b) || b.Contains(a));
        }
        bool Owned(SimilarTrack s) =>
            SameAsSeed(s) ||
            PlayedAlready(s.Title) ||
            _queue.Any(q => q.Local is { } l && MatchesSimilar(l, s)) ||
            library.Any(t => MatchesSimilar(t, s));

        // Accumulating pool: fresh hits are appended rather than replacing the list, so a
        // thin answer (2 hits) never truncates a chain built on earlier, richer ones.
        foreach (var s in similar)
        {
            if (Owned(s)) continue;
            if (_similarList.Any(t => SameKey(t.Title, s.Title))) continue;
            _similarList.Add(s);
        }

        RenderSimilar();
    }

    // Redraws every pooled suggestion. The pool only grows within a run, so a full redraw
    // stays cheap and keeps _chainIndex aligned with the rendered rows.
    private void RenderSimilar()
    {
        SimilarPanel.Children.Clear();
        if (_similarList.Count == 0)
        {
            SimilarHeader.Visibility = Visibility.Collapsed;
            return;
        }
        SimilarHeader.Visibility = Visibility.Visible;
        foreach (var s in _similarList)
            SimilarPanel.Children.Add(BuildSimilarRow(s));
    }

    // Containment match between two suggestion titles (punctuation/case folded).
    private static bool SameKey(string a, string b)
    {
        var x = Normalize(a);
        var y = Normalize(b);
        return x.Length > 0 && (x.Contains(y) || y.Contains(x));
    }

    // Next song: the following queue entry, else the next unheard suggestion appended to the queue
    // (so Prev can come back to it). False when nothing is left. A call during a lookup is ignored.
    private async Task<bool> AdvanceAsync()
    {
        if (_advancing) return true;
        _advancing = true;
        try
        {
            if (_currentIndex < _queue.Count - 1)
            {
                PlayAt(_currentIndex + 1);
                return true;
            }

            var token = _playToken;
            var next = NextSuggestion() ?? await ReseedAsync();
            if (token != _playToken) return true;
            if (next is null) return false;
            _queue.Add(next);
            PlayAt(_queue.Count - 1);
            return true;
        }
        finally { _advancing = false; }
    }

    // First pooled suggestion not heard yet: the local copy when owned, else the streamed one.
    private QueueEntry? NextSuggestion()
    {
        while (_chainIndex < _similarList.Count)
        {
            var s = _similarList[_chainIndex++];
            if (PlayedAlready(s.Title)) continue;
            var local = _trackRepository.GetAll().FirstOrDefault(t => MatchesSimilar(t, s));
            if (local is not null) return new QueueEntry { Local = local };
            if (!string.IsNullOrWhiteSpace(s.PreviewUrl)) return new QueueEntry { Remote = s };
        }
        return null;
    }

    // Suggestions exhausted: ask again from the track that last sounded.
    private async Task<QueueEntry?> ReseedAsync()
    {
        if (string.IsNullOrWhiteSpace(_lastTitle)) return null;
        var fresh = await _recommendations.GetSimilarAsync(_lastTitle, _lastArtist, 10);
        foreach (var s in fresh)
        {
            if (PlayedAlready(s.Title)) continue;
            if (_similarList.Any(t => SameKey(t.Title, s.Title))) continue;
            _similarList.Add(s);
        }
        RenderSimilar();
        return NextSuggestion();
    }

    // True when a suggestion title matches an already-heard one. Containment-based because the
    // local scanner prefixes the folder name to titles ("AURORA - Black Water Lilies") while the
    // API returns the bare title ("Black Water Lilies") - exact key equality would miss it.
    private bool PlayedAlready(string title)
    {
        var key = Normalize(title);
        return key.Length > 0 && _playedKeys.Any(k => k.Contains(key) || key.Contains(k));
    }

    // One similar-tracks line:
    //   * present in the local library  -> a chip that plays the local .mp3 next.
    //   * otherwise                     -> "· Titre — Artiste", a ▶ that streams it next and a ⤓
    //     that queues it in the existing downloader board (yt-dlp writes the full file).
    private FrameworkElement BuildSimilarRow(SimilarTrack similar)
    {
        var container = new StackPanel { Margin = new Thickness(0, 0, 0, 3) };

        var local = _trackRepository.GetAll().FirstOrDefault(t => MatchesSimilar(t, similar));

        var label = string.IsNullOrWhiteSpace(similar.Artist)
            ? similar.Title
            : $"{similar.Title} — {similar.Artist}";

        if (local is not null)
        {
            var button = new Button
            {
                Content = label,
                Style = ChipButton(),
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Foreground = Brushes.White,
                Background = InputBg(),
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
            button.Click += (_, _) => PlayNext(new QueueEntry { Local = local });
            container.Children.Add(button);
            return container;
        }

        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        row.Children.Add(new TextBlock
        {
            Text = "· " + label,
            Foreground = new SolidColorBrush(Color.FromRgb(0xAA, 0xAA, 0xAA)),
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center
        });

        if (!string.IsNullOrWhiteSpace(similar.PreviewUrl))
        {
            var pv = new Button
            {
                Content = "▶",
                Style = SmallButton(),
                VerticalAlignment = VerticalAlignment.Center,
                ToolTip = "Écouter le titre entier"
            };
            pv.Click += (_, _) => PlayNext(new QueueEntry { Remote = similar });
            Grid.SetColumn(pv, 1);
            row.Children.Add(pv);
        }

        // Full-length file first: yt-dlp's "ytsearch1:" resolves the complete track on
        // YouTube (the iTunes m4a clip is only 30 s). Fallbacks: the iTunes clip, then the page.
        var searchQuery = $"ytsearch1:{similar.Title} {similar.Artist}".Trim();
        var downloadUrl = !string.IsNullOrWhiteSpace(similar.Title)
            ? searchQuery
            : !string.IsNullOrWhiteSpace(similar.PreviewUrl) ? similar.PreviewUrl : similar.PageUrl;
        if (!string.IsNullOrWhiteSpace(downloadUrl))
        {
            var dl = new Button
            {
                Content = "⤓",
                Style = SmallButton(),
                VerticalAlignment = VerticalAlignment.Center,
                ToolTip = "Télécharger dans la file du downloader"
            };
            var url = downloadUrl!;
            var title = similar.Title;
            dl.Click += (_, _) =>
            {
                QueueDownload(url, title);
                UpdateSaveButton();
            };
            Grid.SetColumn(dl, 2);
            row.Children.Add(dl);
        }

        container.Children.Add(row);
        return container;
    }

    // Plays a suggestion right after the current entry, so Prev returns to what was playing
    // and Next carries on with the rest of the queue. Clicking the one already playing is a no-op.
    private void PlayNext(QueueEntry entry)
    {
        if (_currentIndex >= 0 && _currentIndex < _queue.Count)
        {
            var current = _queue[_currentIndex];
            if (entry.Remote is not null && current.Remote == entry.Remote) return;
            if (entry.Local is not null && current.Local?.Id == entry.Local.Id) return;
        }
        var at = _currentIndex + 1;
        _queue.Insert(at, entry);
        PlayAt(at);
    }

    private static readonly string StreamCacheDir =
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), "AkashicRecords", "stream");

    // Full track as a local m4a: first of 3 YouTube hits under 15 min (skips album compilations),
    // two attempts since YouTube answers 403 now and then. Null when nothing came down.
    private static async Task<string?> FetchAudioAsync(SimilarTrack track)
    {
        var cfg = new ConfigService().Load();
        var toolsDir = System.IO.Path.Combine(AppContext.BaseDirectory, "tools");
        var bundled = System.IO.Path.Combine(toolsDir, "yt-dlp.exe");
        var tool = !string.IsNullOrWhiteSpace(cfg.DownloaderToolPath) && System.IO.File.Exists(cfg.DownloaderToolPath)
            ? cfg.DownloaderToolPath!
            : System.IO.File.Exists(bundled) ? bundled : "yt-dlp";
        var ffmpegDir = System.IO.File.Exists(System.IO.Path.Combine(toolsDir, "ffmpeg.exe")) ? toolsDir : null;

        // Quotes would break the single quoted argument the downloader builds.
        var query = $"ytsearch3:{track.Title} {track.Artist}".Replace("\"", string.Empty).Trim();
        const string args = "--no-warnings -f \"140/bestaudio[ext=m4a]/18\" --match-filter \"duration<900\" " +
                            "--max-downloads 1 --no-simulate --print after_move:filepath -o \"%(id)s.%(ext)s\"";

        for (var attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                // Exit code 101 is the normal "--max-downloads reached" stop: judge by the printed path.
                var result = await new AkashicRecords.Infrastructure.Downloading.ExternalDownloader()
                    .DownloadAsync(tool, args, query, StreamCacheDir, ffmpegDir).ConfigureAwait(false);
                var path = (result.Output ?? string.Empty)
                    .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(line => System.IO.Path.Combine(StreamCacheDir, line))
                    .LastOrDefault(System.IO.File.Exists);
                if (path is not null) return path;
            }
            catch { /* next attempt, then the caller's 30 s clip */ }
        }
        return null;
    }

    private static void ClearStreamCache()
    {
        if (!System.IO.Directory.Exists(StreamCacheDir)) return;
        foreach (var file in System.IO.Directory.GetFiles(StreamCacheDir))
        {
            try { System.IO.File.Delete(file); } catch { /* still open: next launch */ }
        }
    }

    private sealed class QueueEntry
    {
        public MusicTrack? Local { get; init; }
        public SimilarTrack? Remote { get; init; }
        public Task<string?>? Fetch { get; set; }
        // Local temp file once fetched, or the iTunes clip URL as fallback.
        public string? Source { get; set; }
        public bool OnPreview { get; set; }
        public string Title => Local?.Title ?? Remote?.Title ?? string.Empty;
        public string Artist => Local?.Artist ?? Remote?.Artist ?? string.Empty;
    }

    // Raised whenever the current entry changes or gets queued (drives the floating widget).
    public event Action? NowPlayingChanged;
    // Raised after an item lands in the downloader queue, so an open downloader board can redraw.
    public event Action? DownloadQueued;

    // Same item as the row ⤓: yt-dlp resolves the full track from this search query.
    private static string DownloadUrlFor(SimilarTrack track) => $"ytsearch1:{track.Title} {track.Artist}".Trim();

    // Download state of the playing suggestion; null for library tracks or nothing playing.
    public TrackSaveState? SaveableState =>
        _currentIndex >= 0 && _currentIndex < _queue.Count && _queue[_currentIndex] is { Remote: { } remote }
            ? _downloadRepository.ExistsByUrl(DownloadUrlFor(remote)) ? TrackSaveState.Queued : TrackSaveState.None
            : null;

    // Shown only on a suggestion that is not in the library yet.
    private void UpdateSaveButton()
    {
        var state = SaveableState;
        if (state is null)
        {
            SaveButton.Visibility = Visibility.Collapsed;
        }
        else
        {
            SaveButton.Visibility = Visibility.Visible;
            SaveButton.IsEnabled = state == TrackSaveState.None;
            (SaveButton.Content, SaveButton.ToolTip) = state == TrackSaveState.Queued
                ? ("✓", "Dans la file de téléchargement")
                : ("⤓", "Ajouter à la file de téléchargement");
        }
        NowPlayingChanged?.Invoke();
    }

    private void SaveButton_OnClick(object sender, RoutedEventArgs e) => SaveCurrent();

    // Adds the playing suggestion to the downloader board; downloading stays the user's call there.
    public void SaveCurrent()
    {
        if (_currentIndex < 0 || _currentIndex >= _queue.Count) return;
        if (_queue[_currentIndex].Remote is not { } remote) return;
        QueueDownload(DownloadUrlFor(remote), remote.Title);
        UpdateSaveButton();
    }

    private void QueueDownload(string url, string title)
    {
        if (_downloadRepository.ExistsByUrl(url)) return;
        _downloadRepository.Add(new DownloadItem
        {
            Url = url,
            Title = title,
            Status = DownloadStatus.Queued,
            AddedAt = DateTime.Now,
            FolderId = ZoneForRun()
        });
        DownloadQueued?.Invoke();
    }

    // The downloader zone pointing at the run's album folder, created on first use so the board
    // shows where each suggestion came from. Root-level tracks stay in "Non classé".
    private int? ZoneForRun()
    {
        if (string.IsNullOrWhiteSpace(_runAlbum)) return null;
        var zones = _folderRepository.GetAll();
        var zone = zones.FirstOrDefault(z => string.Equals(z.Subfolder?.Trim(), _runAlbum, StringComparison.OrdinalIgnoreCase));
        return zone?.Id ?? _folderRepository.Add(new DownloadFolder
        {
            Name = _runAlbum,
            Subfolder = _runAlbum,
            SortOrder = zones.Count
        });
    }

    private static bool MatchesSimilar(MusicTrack track, SimilarTrack similar)
    {
        if (string.IsNullOrWhiteSpace(track.Title)) return false;
        var t = Normalize(track.Title);
        var s = Normalize(similar.Title);
        // Containment covers the scanner's "album-title" compound titles ("adelle-rolling-in-the-deep")
        // against API titles ("Rolling in the Deep") once punctuation is folded away.
        return t == s || t.Contains(s) || s.Contains(t);
    }

    // Strips the yt-dlp " [youtubeId]" suffix and folds punctuation so filename-titles match API
    // titles: "adelle-rolling-in-the-deep" and "Rolling in the Deep" both reduce to one key.
    private static string Normalize(string s)
    {
        var t = s.Trim();
        var bracket = t.IndexOf('[');
        if (bracket > 0) t = t[..bracket].Trim();
        return new string(t.ToLowerInvariant().Where(c => char.IsLetterOrDigit(c)).ToArray());
    }

    private void PositionTimer_OnTick(object? sender, EventArgs e)
    {
        if (!_mediaPlayer.NaturalDuration.HasTimeSpan) return;
        var pos = _mediaPlayer.Position.TotalSeconds;
        _updatingPositionFromTimer = true;
        PositionSlider.Value = pos;
        _updatingPositionFromTimer = false;
        ElapsedText.Text = FormatTime(pos);
    }

    private void PositionSlider_OnValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_updatingPositionFromTimer || _currentIndex < 0 || _isResolving) return;
        _mediaPlayer.Position = TimeSpan.FromSeconds(e.NewValue);
        ElapsedText.Text = FormatTime(e.NewValue);
    }

    private void VolumeSlider_OnValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        _mediaPlayer.Volume = e.NewValue;
    }

    // --- Header buttons ------------------------------------------------------

    private void ExpandButton_OnClick(object sender, RoutedEventArgs e)
    {
        var expand = ExpandedPanel.Visibility != Visibility.Visible;
        ExpandedPanel.Visibility = expand ? Visibility.Visible : Visibility.Collapsed;
        ExpandButton.Content = expand ? "▴" : "▾";

        if (expand)
        {
            // Manual sizing so the resize grip works; give it a reasonable height if collapsed-small.
            SizeToContent = SizeToContent.Manual;
            if (double.IsNaN(Height) || Height < 320)
                Height = Math.Min(SystemParameters.WorkArea.Height * 0.6, 560);
        }
        else
        {
            // Shrink back to just the compact bar.
            SizeToContent = SizeToContent.Height;
        }
    }

    private void CloseButton_OnClick(object sender, RoutedEventArgs e) => Hide();

    // --- Library -------------------------------------------------------------

    private IReadOnlyList<MusicTrack> CurrentLibraryTracks()
    {
        IEnumerable<MusicTrack> tracks = _selectedMoodId is { } moodId ? _trackRepository.GetByMood(moodId) : _trackRepository.GetAll();
        if (!string.IsNullOrWhiteSpace(_librarySearch))
        {
            var q = _librarySearch.Trim();
            tracks = tracks.Where(t =>
                t.Title.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                t.Artist.Contains(q, StringComparison.OrdinalIgnoreCase));
        }
        return tracks.ToList();
    }

    private void LibrarySearchInput_OnTextChanged(object sender, TextChangedEventArgs e)
    {
        _librarySearch = LibrarySearchInput.Text;
        RefreshLibrary();
    }

    private void RescanButton_OnClick(object sender, RoutedEventArgs e)
    {
        _scanner.ScanAndSync();
        RefreshMoods();
        RefreshLibrary();
    }

    // No ambiance/search: album accordions. Ambiance or search active: a flat filtered list.
    private void RefreshLibrary()
    {
        LibraryPanel.Children.Clear();

        if (_selectedMoodId is null && string.IsNullOrWhiteSpace(_librarySearch))
        {
            foreach (var group in _trackRepository.GetAll()
                         .GroupBy(t => t.Album)
                         .OrderBy(g => string.IsNullOrEmpty(g.Key) ? "\uFFFF" : g.Key))
            {
                var albumTracks = group.OrderBy(t => t.Title).ToList();

                var header = new StackPanel { Orientation = Orientation.Horizontal };
                var playAlbum = new Button { Content = "▶", Style = SmallButton(), Margin = new Thickness(0, 0, 6, 0), ToolTip = "Lire l'album" };
                // Handled so playing doesn't also toggle the accordion open/closed.
                playAlbum.Click += (_, ev) => { ev.Handled = true; if (albumTracks.Count > 0) PlayQueue(albumTracks, 0); };
                header.Children.Add(playAlbum);
                header.Children.Add(new TextBlock
                {
                    Text = string.IsNullOrEmpty(group.Key) ? "Sans album" : group.Key,
                    Foreground = Brushes.White,
                    VerticalAlignment = VerticalAlignment.Center
                });

                var expander = new Expander { Header = header, Foreground = Brushes.White, Margin = new Thickness(0, 0, 0, 4) };
                var inner = new StackPanel { Margin = new Thickness(8, 4, 0, 4) };
                foreach (var track in albumTracks) inner.Children.Add(BuildTrackRow(track));
                expander.Content = inner;
                LibraryPanel.Children.Add(expander);
            }
            return;
        }

        foreach (var track in CurrentLibraryTracks())
        {
            LibraryPanel.Children.Add(BuildTrackRow(track));
        }
    }

    private FrameworkElement BuildTrackRow(MusicTrack track)
    {
        var container = new StackPanel { Margin = new Thickness(0, 0, 0, 6) };

        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var playButton = new Button { Content = "▶", Style = SmallButton(), Margin = new Thickness(0, 0, 6, 0) };
        playButton.Click += (_, _) =>
        {
            var tracks = CurrentLibraryTracks();
            var index = tracks.ToList().FindIndex(t => t.Id == track.Id);
            PlayQueue(tracks, index < 0 ? 0 : index);
        };
        Grid.SetColumn(playButton, 0);
        row.Children.Add(playButton);

        var info = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        info.Children.Add(new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(track.Title) ? "(sans titre)" : track.Title,
            Foreground = Brushes.White,
            FontSize = 12,
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        var sub = track.Artist;
        if (track.DurationSeconds is { } d) sub = sub.Length == 0 ? FormatTime(d) : $"{sub} · {FormatTime(d)}";
        info.Children.Add(new TextBlock { Text = sub, Foreground = new SolidColorBrush(Color.FromRgb(0xAA, 0xAA, 0xAA)), FontSize = 10 });
        Grid.SetColumn(info, 1);
        row.Children.Add(info);

        var actions = new StackPanel { Orientation = Orientation.Horizontal };
        var chainButton = new Button { Content = "∞", Style = SmallButton(), ToolTip = "Mode libre : la piste joue, puis les pistes similaires enchaînées" };
        var editButton = new Button { Content = "✎", Style = SmallButton() };
        var moodButton = new Button { Content = "☾", Style = SmallButton() };
        chainButton.Click += (_, _) => PlayQueue(new[] { track }, 0);
        actions.Children.Add(chainButton);
        actions.Children.Add(editButton);
        actions.Children.Add(moodButton);
        Grid.SetColumn(actions, 2);
        row.Children.Add(actions);

        container.Children.Add(row);

        var editPanel = BuildEditPanel(track);
        editPanel.Visibility = Visibility.Collapsed;
        container.Children.Add(editPanel);
        editButton.Click += (_, _) =>
            editPanel.Visibility = editPanel.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;

        var moodPanel = BuildMoodEditPanel(track);
        moodPanel.Visibility = Visibility.Collapsed;
        container.Children.Add(moodPanel);
        moodButton.Click += (_, _) =>
            moodPanel.Visibility = moodPanel.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;

        return container;
    }

    private FrameworkElement BuildEditPanel(MusicTrack track)
    {
        var panel = new StackPanel { Margin = new Thickness(30, 4, 0, 4) };

        var titleBox = new TextBox { Text = track.Title, Background = InputBg(), Foreground = Brushes.White, BorderBrush = Accent(), Padding = new Thickness(5, 2, 5, 2), Margin = new Thickness(0, 0, 0, 4) };
        var artistBox = new TextBox { Text = track.Artist, Background = InputBg(), Foreground = Brushes.White, BorderBrush = Accent(), Padding = new Thickness(5, 2, 5, 2), Margin = new Thickness(0, 0, 0, 4) };
        panel.Children.Add(new TextBlock { Text = "Titre", Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0xAA)), FontSize = 10 });
        panel.Children.Add(titleBox);
        panel.Children.Add(new TextBlock { Text = "Artiste", Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0xAA)), FontSize = 10 });
        panel.Children.Add(artistBox);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        var saveButton = new Button { Content = "Enregistrer", Style = SmallButton() };
        saveButton.Click += (_, _) =>
        {
            _trackRepository.UpdateMetadata(track.Id, titleBox.Text.Trim(), artistBox.Text.Trim());
            track.Title = titleBox.Text.Trim();
            track.Artist = artistBox.Text.Trim();
            if (_currentIndex >= 0 && _currentIndex < _queue.Count && _queue[_currentIndex].Local?.Id == track.Id) UpdateNowPlaying(track);
            RefreshLibrary();
        };
        var coverButton = new Button { Content = "Pochette…", Style = SmallButton() };
        coverButton.Click += (_, _) => PickCover(track);
        buttons.Children.Add(saveButton);
        buttons.Children.Add(coverButton);
        panel.Children.Add(buttons);

        return panel;
    }

    private void PickCover(MusicTrack track)
    {
        var dialog = new OpenFileDialog { Filter = "Images|*.jpg;*.jpeg;*.png;*.bmp;*.gif" };
        if (dialog.ShowDialog() != true) return;

        try
        {
            // Import the new cover and commit it to the DB first; only then drop the old file,
            // so a failure never leaves the track pointing at a deleted cover.
            var old = track.CoverImagePath;
            var relative = _mediaStorage.ImportMusicCover(dialog.FileName);
            _trackRepository.UpdateCover(track.Id, relative);
            track.CoverImagePath = relative;
            if (old is { }) MediaStorage.DeleteFile(old);
            if (_currentIndex >= 0 && _currentIndex < _queue.Count && _queue[_currentIndex].Local?.Id == track.Id) UpdateNowPlaying(track);
        }
        catch
        {
            // Import/DB failed: keep the old cover and DB untouched, just tell the user.
            MessageBox.Show("Impossible d'importer la pochette. L'ancienne pochette a \u00e9t\u00e9 conserv\u00e9e.",
                "Musique", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private FrameworkElement BuildMoodEditPanel(MusicTrack track)
    {
        var panel = new WrapPanel { Margin = new Thickness(30, 4, 0, 4) };
        var applied = _trackMoodRepository.GetMoodIdsForTrack(track.Id).ToHashSet();

        foreach (var mood in _moodRepository.GetAll())
        {
            var toggle = new ToggleButton
            {
                Content = mood.Name,
                IsChecked = applied.Contains(mood.Id),
                Foreground = Brushes.White,
                Background = InputBg(),
                Padding = new Thickness(6, 2, 6, 2),
                Margin = new Thickness(0, 0, 4, 4),
                Cursor = Cursors.Hand
            };
            toggle.Checked += (_, _) => _trackMoodRepository.Add(track.Id, mood.Id);
            toggle.Unchecked += (_, _) => _trackMoodRepository.Delete(track.Id, mood.Id);
            panel.Children.Add(toggle);
        }

        if (panel.Children.Count == 0)
        {
            panel.Children.Add(new TextBlock { Text = "Aucune ambiance définie.", Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0xAA)), FontSize = 10 });
        }
        return panel;
    }

    // --- Moods ---------------------------------------------------------------

    private void RefreshMoods()
    {
        MoodChipsPanel.Children.Clear();
        MoodChipsPanel.Children.Add(BuildMoodChip("Toutes", null, null));
        foreach (var mood in _moodRepository.GetAll())
        {
            MoodChipsPanel.Children.Add(BuildMoodChip(mood.Name, mood.Id, mood));
        }
    }

    // Selected ambiance stays highlighted; user ambiances get a trashcan (with confirmation).
    private FrameworkElement BuildMoodChip(string label, int? moodId, Mood? mood)
    {
        var isSelected = _selectedMoodId == moodId;
        var container = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 6, 6) };

        if (mood is not null)
        {
            var trash = new Button
            {
                Content = "🗑",
                Style = SmallButton(),
                Margin = new Thickness(0, 0, 2, 0),
                ToolTip = $"Supprimer « {mood.Name} »"
            };
            trash.Click += (_, _) =>
            {
                if (MessageBox.Show($"Supprimer l'ambiance « {mood.Name} » ?", "Musique",
                        MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
                _moodRepository.Delete(mood.Id);
                if (_selectedMoodId == mood.Id) _selectedMoodId = null;
                RefreshMoods();
                RefreshLibrary();
            };
            container.Children.Add(trash);
        }

        var chip = new Button
        {
            Content = label,
            Style = ChipButton(),
            Background = isSelected ? Accent() : InputBg()
        };
        chip.Click += (_, _) =>
        {
            _selectedMoodId = moodId;
            RefreshMoods();
            RefreshLibrary();
        };
        container.Children.Add(chip);
        return container;
    }

    private void AddMoodButton_OnClick(object sender, RoutedEventArgs e)
    {
        NewMoodInput.Text = string.Empty;
        NewMoodPopup.IsOpen = true;
        NewMoodInput.Focus();
    }

    private void CreateMoodButton_OnClick(object sender, RoutedEventArgs e)
    {
        var name = NewMoodInput.Text.Trim();
        if (name.Length == 0) return;
        _moodRepository.Add(new Mood { Name = name });
        NewMoodInput.Text = string.Empty;
        NewMoodPopup.IsOpen = false;
        RefreshMoods();
    }

    // --- Queue ---------------------------------------------------------------

    private void RefreshQueue()
    {
        QueuePanel.Children.Clear();
        for (var i = 0; i < _queue.Count; i++)
        {
            var index = i;
            var entry = _queue[i];
            var isCurrent = i == _currentIndex;
            var title = string.IsNullOrWhiteSpace(entry.Title) ? "(sans titre)" : entry.Title;

            var item = new Button
            {
                Content = entry.Remote is null || string.IsNullOrWhiteSpace(entry.Artist) ? title : $"{title} — {entry.Artist}",
                Style = ChipButton(),
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Foreground = Brushes.White,
                Background = isCurrent ? Accent() : InputBg(),
                Margin = new Thickness(0, 0, 0, 3),
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
            item.Click += (_, _) => PlayAt(index);
            QueuePanel.Children.Add(item);
        }
    }

    // --- Helpers -------------------------------------------------------------

    private Style SmallButton() => (Style)FindResource("PlayerSmallButton");
    private Style ChipButton() => (Style)FindResource("PlayerChipButton");
    private static SolidColorBrush InputBg() => new(Color.FromRgb(0x2A, 0x2A, 0x45));
    private static SolidColorBrush Accent() => new(Color.FromRgb(0x3B, 0x82, 0xF6));

    private static string FormatTime(double seconds)
    {
        if (double.IsNaN(seconds) || seconds < 0) seconds = 0;
        var ts = TimeSpan.FromSeconds(seconds);
        return ts.Hours > 0 ? $"{(int)ts.TotalHours}:{ts.Minutes:00}:{ts.Seconds:00}" : $"{ts.Minutes}:{ts.Seconds:00}";
    }

    // Loads fully into memory and releases the file handle immediately, so the cover file
    // can be deleted/replaced afterwards without "file in use" errors.
    private static BitmapImage LoadImage(string fullPath)
    {
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.UriSource = new Uri(fullPath);
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }

    // Rescans the music folder and refreshes the library list (used after new downloads land).
    public void ReloadLibrary()
    {
        _scanner.ScanAndSync();
        RefreshLibrary();
    }

    // Called on app exit (see MainWindow.ExitApplication) to release the media/timer.
    public void ShutdownPlayer()
    {
        _positionTimer.Stop();
        _mediaPlayer.MediaOpened -= MediaPlayer_OnMediaOpened;
        _mediaPlayer.MediaEnded -= MediaPlayer_OnMediaEnded;
        _mediaPlayer.Stop();
        _mediaPlayer.Close();
        ClearStreamCache();
    }

    // Lets MainWindow truly close (and dispose) the player on real app exit.
    public void ForceClose()
    {
        _isExiting = true;
        Close();
    }

    private void ShowNoTracks()
    {
        StatusText.Text = "Aucune piste. Ajoutez des .mp3 dans le dossier music puis Rescanner.";
        StatusText.Visibility = Visibility.Visible;
    }

    // Closing the floating player (e.g. Alt+F4) hides it instead of tearing it down, so it
    // can be reopened; only a real app exit (ForceClose) lets it actually close.
    private void MusicPlayerWindow_OnClosing(object? sender, CancelEventArgs e)
    {
        if (_isExiting) return;
        e.Cancel = true;
        Hide();
    }

    private void MusicPlayerWindow_OnClosed(object? sender, EventArgs e)
    {
        _positionTimer.Stop();
        _mediaPlayer.Stop();
        _mediaPlayer.Close();
        ClearStreamCache();
    }
}
