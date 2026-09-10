using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AkashicRecords.Domain;

namespace AkashicRecords.App;

// Ambient, discreet desktop projection of the day's film pick (§32-34). Dumb widget: MainWindow
// supplies the already-chosen film. Hides on close (like MusicPlayerWindow) and only truly closes
// via ForceClose() on app exit.
public partial class FilmOfTheDayWidget : Window
{
    // Set only via ForceClose() when the app is genuinely exiting; otherwise closing hides the window.
    private bool _isExiting;

    // Raised when the user clicks the ✕ button so MainWindow can persist ShowFilmOfTheDay=false
    // and uncheck the tray item.
    public event Action? HideRequested;

    // Raised continuously while the widget moves so MainWindow can keep its in-memory position current.
    public event Action<double, double>? PositionChanged;

    // Raised once when a drag finishes so MainWindow can persist the final position to disk
    // (one write per drag rather than one per LocationChanged).
    public event Action<double, double>? PositionCommitted;

    private WatchlistItem? _film;
    private string? _filmCover;
    private WatchlistItem? _anim;
    private string? _animCover;
    private bool _showingAnimation;

    public FilmOfTheDayWidget()
    {
        InitializeComponent();
        LocationChanged += (_, _) => PositionChanged?.Invoke(Left, Top);
    }

    // Supplies both daily picks (film + animated film). MainWindow resolves the covers.
    // Prefers showing the film pick; clicking the poster flips to the animation pick and back.
    public void ShowPicks(WatchlistItem? film, string? filmCover, WatchlistItem? anim, string? animCover)
    {
        _film = film;
        _filmCover = filmCover;
        _anim = anim;
        _animCover = animCover;
        // Prefer showing the animated-film pick; fall back to the film pick when there's no animation.
        _showingAnimation = anim is not null;
        Render();
    }

    private void Render()
    {
        var current = _showingAnimation ? _anim : _film;
        var cover = _showingAnimation ? _animCover : _filmCover;
        FlipHint.Visibility = _film is not null && _anim is not null ? Visibility.Visible : Visibility.Collapsed;

        if (current is null) return;

        InfoTitleText.Text = current.Title;
        InfoCategoryText.Text = _showingAnimation ? "Film d'animation" : "Film";
        InfoPriorityText.Text = PriorityLabel(current.Priority);

        if (cover is not null && File.Exists(cover))
        {
            PosterImage.Source = LoadImage(cover);
            PosterImage.Visibility = Visibility.Visible;
            PosterFallback.Visibility = Visibility.Collapsed;
        }
        else
        {
            PosterImage.Source = null;
            PosterImage.Visibility = Visibility.Collapsed;
            PosterFallback.Text = current.Title;
            PosterFallback.Visibility = Visibility.Visible;
        }
    }

    private static string PriorityLabel(WatchPriority priority) => priority switch
    {
        WatchPriority.ShouldWatchSomeday => "À voir un jour",
        WatchPriority.WantToWatch => "Envie de voir",
        WatchPriority.NeedToWatch => "À voir absolument",
        _ => string.Empty
    };

    // Flip to the other category's pick if it exists.
    private void PosterButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (_showingAnimation && _film is not null) _showingAnimation = false;
        else if (!_showingAnimation && _anim is not null) _showingAnimation = true;
        else return;
        Render();
    }

    private void HideButton_OnClick(object sender, RoutedEventArgs e)
    {
        Hide();
        HideRequested?.Invoke();
    }

    // Only DragMove when the click didn't originate on an interactive control, otherwise
    // buttons would never receive the mouse (capture steals it first).
    private void Root_OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (IsInteractiveOrigin(e.OriginalSource as DependencyObject)) return;
        try
        {
            // DragMove blocks until the mouse button is released; on return a drag ran, so
            // persist the final position once (skipped if DragMove throws).
            DragMove();
            PositionCommitted?.Invoke(Left, Top);
        }
        catch { /* DragMove throws if button already released */ }
    }

    private static bool IsInteractiveOrigin(DependencyObject? source)
    {
        while (source is not null)
        {
            if (source is ButtonBase or TextBoxBase) return true;
            source = VisualTreeHelper.GetParent(source);
        }
        return false;
    }

    // Loads fully into memory and releases the file handle immediately, so the cover file
    // can be deleted/replaced afterwards without "file in use" errors.
    private static BitmapImage LoadImage(string fullPath)
    {
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.DecodePixelWidth = 200;
        bitmap.UriSource = new Uri(fullPath);
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }

    // Lets MainWindow truly close the widget on real app exit.
    public void ForceClose()
    {
        _isExiting = true;
        Close();
    }

    // Closing (e.g. Alt+F4) hides instead of tearing down, so it can be reopened; only a real
    // app exit (ForceClose) lets it actually close.
    private void FilmOfTheDayWidget_OnClosing(object? sender, CancelEventArgs e)
    {
        if (_isExiting) return;
        e.Cancel = true;
        Hide();
        HideRequested?.Invoke();
    }
}
