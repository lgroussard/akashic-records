using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AkashicRecords.Domain;
using AkashicRecords.Infrastructure.Configuration;
using AkashicRecords.Infrastructure.Persistence;
using AkashicRecords.Infrastructure.Web;
using Microsoft.Win32;

namespace AkashicRecords.App.Views;

public partial class CollectionsView : UserControl, ISearchNavigable
{
    private static readonly Tier[] TierOrder = { Tier.S, Tier.A, Tier.B, Tier.C, Tier.D };

    // Uniform card size for every tier item, so the list reads as a consistent grid.
    private const double ChipWidth = 118, CoverHeight = 96;

    // Display-only wrappers so list items can carry their db Id for the delete buttons.
    private sealed record EvaluationItem(int Id, string Text);
    private sealed record ObservationItem(int Id, string Text);

    private readonly ArtworkRepository _artworkRepository = new(new SqliteConnectionFactory());
    private readonly EvaluationAxisRepository _axisRepository = new(new SqliteConnectionFactory());
    private readonly ArtworkEvaluationRepository _evaluationRepository = new(new SqliteConnectionFactory());
    private readonly ObservationRepository _observationRepository = new(new SqliteConnectionFactory());
    private readonly WatchlistItemRepository _watchlistRepository = new(new SqliteConnectionFactory());
    private readonly ImageSearchService _imageSearchService;
    private readonly MediaStorage _mediaStorage = new();

    private static readonly Dictionary<ArtworkCategory, string> CategoryLabels = new()
    {
        [ArtworkCategory.Film] = "FILMS",
        [ArtworkCategory.FilmAnimation] = "FILMS D'ANIMATION",
        [ArtworkCategory.Anime] = "ANIME/MANGA",
        [ArtworkCategory.Livre] = "LIVRES",
        [ArtworkCategory.VideoGame] = "JEUX VIDÉO"
    };

    // Subtitle shown under the section header, per category.
    private static readonly Dictionary<ArtworkCategory, string> CategorySubtitles = new()
    {
        [ArtworkCategory.Film] = "Vos œuvres classées par tier — cliquez une œuvre pour sa fiche.",
        [ArtworkCategory.FilmAnimation] = "Vos œuvres classées par tier — cliquez une œuvre pour sa fiche.",
        [ArtworkCategory.Anime] = "Vos œuvres classées par tier — cliquez une œuvre pour sa fiche.",
        [ArtworkCategory.Livre] = "Vos œuvres classées par tier — cliquez une œuvre pour sa fiche.",
        [ArtworkCategory.VideoGame] = "Vos jeux classées par tier — cliquez un jeu pour sa fiche."
    };

    private ArtworkCategory _selectedCategory = ArtworkCategory.Film;
    private Artwork? _selectedFilm;

    // Set by ShowTabForScreenshot so RestoreActiveItem (which runs on Loaded) doesn't reopen a
    // persisted fiche over the screenshot view (e.g. watchlist, or a category with no work).
    private bool _screenshotMode;

    // Public setter so MainWindow can flag screenshot mode before the view's Loaded fires.
    public bool ScreenshotMode
    {
        get => _screenshotMode;
        set => _screenshotMode = value;
    }

    // State for the "add work" modal, since the artwork doesn't exist yet while the modal is open.
    private Tier _pendingAddTier = Tier.B;
    private string? _pendingAddCoverPath;

    private readonly AppConfig _config;

    private SearchResult? _pendingSearchResult;

    // Two-finger horizontal touchpad swipes arrive as the native WM_MOUSEHWHEEL message, which WPF's
    // MouseWheel event never surfaces - needs a raw window-message hook to catch it.
    private const int WM_MOUSEHWHEEL = 0x020E;
    private HwndSource? _horizontalWheelSource;

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

    public CollectionsView(AppConfig config)
    {
        InitializeComponent();
        _config = config;
        _imageSearchService = new ImageSearchService(config.TmdbApiKey);
        Loaded += (_, _) =>
        {
            RestoreActiveItem();
            RefreshArtworks();
        };
        Loaded += (_, _) => ApplyPendingSearchResult();
        Loaded += (_, _) => HookHorizontalWheel();
        Unloaded += (_, _) => UnhookHorizontalWheel();
    }

    // Records the exact category + fiche the user last viewed so reopening Collections
    // returns them to the same view, not just the default category.
    private void SaveActiveItem()
    {
        _config.CollectionsActiveCategory = _selectedCategory.ToString();
        _config.CollectionsActiveFilmId = _selectedFilm?.Id;
    }

    // Reopens the exact category + fiche the user last viewed.
    private void RestoreActiveItem()
    {
        if (!string.IsNullOrEmpty(_config.CollectionsActiveCategory) &&
            Enum.TryParse<ArtworkCategory>(_config.CollectionsActiveCategory, out var category))
        {
            _selectedCategory = category;
            ApplyCategoryHeader();
            foreach (var tab in new[] { FilmsTab, FilmAnimationTab, AnimesTab, LivresTab, VideoGameTab, WatchlistTab })
            {
                // WatchlistTab's tag ("Watchlist") isn't an ArtworkCategory, so match via TryParse.
                tab.IsChecked = Enum.TryParse<ArtworkCategory>((string)tab.Tag, out var tabCategory) && tabCategory == _selectedCategory;
            }
        }

        if (_screenshotMode) return;

        if (_config.CollectionsActiveFilmId is { } filmId)
        {
            var artwork = _artworkRepository.GetById(filmId);
            if (artwork is not null) OpenFiche(artwork);
        }
    }

    // --- Global search deep-link (ISearchNavigable) ---

    public void NavigateToSearchResult(SearchResult result)
    {
        if (IsLoaded) ApplySearchResult(result);
        else _pendingSearchResult = result;
    }

    private void ApplyPendingSearchResult()
    {
        if (_pendingSearchResult is { } result)
        {
            _pendingSearchResult = null;
            ApplySearchResult(result);
        }
    }

    private void ApplySearchResult(SearchResult result)
    {
        if (result.Category is int categoryInt)
        {
            var category = (ArtworkCategory)categoryInt;
            foreach (var tab in new[] { FilmsTab, FilmAnimationTab, AnimesTab, LivresTab, VideoGameTab })
            {
                tab.IsChecked = Enum.Parse<ArtworkCategory>((string)tab.Tag) == category;
            }
            WatchlistTab.IsChecked = false;
            ListRoot.Visibility = Visibility.Visible;
            WatchlistRoot.Visibility = Visibility.Collapsed;
            _selectedCategory = category;
            ApplyCategoryHeader();
            RefreshArtworks();
        }

        var artwork = _artworkRepository.GetById(result.PrimaryId);
        if (artwork is null) return;
        OpenFiche(artwork);

        // Observation hit: best-effort select/scroll its row within the opened fiche.
        if (result.Kind == SearchResultKind.Observation && result.SecondaryId is int observationId)
        {
            var match = (ObservationsList.ItemsSource as IEnumerable<ObservationItem>)
                ?.FirstOrDefault(o => o.Id == observationId);
            if (match is not null)
            {
                ObservationsList.SelectedItem = match;
                ObservationsList.ScrollIntoView(match);
            }
        }
    }

    // Updates the section title and subtitle to match the currently selected category.
    private void ApplyCategoryHeader()
    {
        CategoryHeaderText.Text = $"COLLECTIONS — {CategoryLabels[_selectedCategory]}";
        CategorySubText.Text = CategorySubtitles.TryGetValue(_selectedCategory, out var sub) ? sub : CategorySubtitles[ArtworkCategory.Film];
    }

    // Switches to a Collections category through the real path, for headless capture (see App --screenshot).
    // For a category, opens the first work so the fiche is visible, not just the tier list.
    public void ShowTabForScreenshot(string category)
    {
        _screenshotMode = true;

        if (category == "Watchlist")
        {
            CategoryTab_OnClick(WatchlistTab, new RoutedEventArgs());
            CloseFiche();
            return;
        }

        foreach (var tab in new[] { FilmsTab, FilmAnimationTab, AnimesTab, LivresTab, VideoGameTab, WatchlistTab })
        {
            tab.IsChecked = tab.Tag is string t && t == category;
        }

        _selectedCategory = Enum.Parse<ArtworkCategory>(category);
        ApplyCategoryHeader();
        ListRoot.Visibility = Visibility.Visible;
        WatchlistRoot.Visibility = Visibility.Collapsed;
        RefreshArtworks();

        // Auto-open the first work so the fiche is visible, not just the tier list.
        var films = _artworkRepository.GetByCategory(_selectedCategory);
        if (films.Count > 0) OpenFiche(films[0]);
    }

    private void CategoryTab_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton clicked || clicked.Tag is not string tag) return;

        foreach (var tab in new[] { FilmsTab, FilmAnimationTab, AnimesTab, LivresTab, VideoGameTab, WatchlistTab })
        {
            tab.IsChecked = tab == clicked;
        }

        if (tag == "Watchlist")
        {
            ListRoot.Visibility = Visibility.Collapsed;
            WatchlistRoot.Visibility = Visibility.Visible;
            CloseFiche();
            RefreshWatchlist();
            return;
        }

        ListRoot.Visibility = Visibility.Visible;
        WatchlistRoot.Visibility = Visibility.Collapsed;
        CloseFiche();
        _selectedCategory = Enum.Parse<ArtworkCategory>(tag);
        ApplyCategoryHeader();
        SaveActiveItem();
        RefreshArtworks();
    }

    private void TierScroll_OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is ScrollViewer sv)
        {
            sv.ScrollToHorizontalOffset(sv.HorizontalOffset - e.Delta);
            e.Handled = true;
        }
    }

    private void HookHorizontalWheel()
    {
        if (_horizontalWheelSource is null && PresentationSource.FromVisual(this) is HwndSource source)
        {
            _horizontalWheelSource = source;
            _horizontalWheelSource.AddHook(HorizontalWheelHook);
        }
    }

    private void UnhookHorizontalWheel()
    {
        if (_horizontalWheelSource is not null)
        {
            _horizontalWheelSource.RemoveHook(HorizontalWheelHook);
            _horizontalWheelSource = null;
        }
    }

    // Routes a horizontal touchpad swipe to whichever tier ScrollViewer sits under the cursor.
    private IntPtr HorizontalWheelHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WM_MOUSEHWHEEL || ListRoot.Visibility != Visibility.Visible) return IntPtr.Zero;

        var delta = (short)((wParam.ToInt64() >> 16) & 0xFFFF);
        var x = (short)(lParam.ToInt64() & 0xFFFF);
        var y = (short)((lParam.ToInt64() >> 16) & 0xFFFF);

        if (InputHitTest(PointFromScreen(new Point(x, y))) is DependencyObject hit &&
            FindAncestorScrollViewer(hit) is { } sv)
        {
            sv.ScrollToHorizontalOffset(sv.HorizontalOffset + delta);
            handled = true;
        }
        return IntPtr.Zero;
    }

    private static ScrollViewer? FindAncestorScrollViewer(DependencyObject? node)
    {
        while (node is not null and not ScrollViewer)
            node = VisualTreeHelper.GetParent(node);
        return node as ScrollViewer;
    }

    private void RefreshArtworks()
    {
        var films = _artworkRepository.GetByCategory(_selectedCategory);
        var ratings = _evaluationRepository.GetAverageScoresByCategory(_selectedCategory);
        var observationCounts = _observationRepository.GetCountsByCategory(_selectedCategory);

        var tierItems = new Dictionary<Tier, StackPanel>
        {
            [Tier.S] = STierItems,
            [Tier.A] = ATierItems,
            [Tier.B] = BTierItems,
            [Tier.C] = CTierItems,
            [Tier.D] = DTierItems
        };

        foreach (var tier in TierOrder)
        {
            var panel = tierItems[tier];
            panel.Children.Clear();

            foreach (var film in films.Where(f => f.Tier == tier))
            {
                panel.Children.Add(CreateChip(film, ratings, observationCounts));
            }
        }
    }

    // A work card: cover on top (or a placeholder), title always visible below it, plus a
    // rating/observation-count preview badge row - never cover-OR-title like the previous design.
    private Button CreateChip(Artwork film, IReadOnlyDictionary<int, double> ratings, IReadOnlyDictionary<int, int> observationCounts)
    {
        var chip = new Button
        {
            Style = (Style)FindResource("WorkChipButtonStyle"),
            Padding = new Thickness(0)
        };

        var cover = new Border
        {
            Width = ChipWidth,
            Height = CoverHeight,
            CornerRadius = new CornerRadius(9, 9, 0, 0),
            ClipToBounds = true
        };
        cover.Background = film.CoverImagePath is { } path
            ? new ImageBrush(LoadImage(MediaStorage.ResolveFullPath(path))) { Stretch = Stretch.Uniform }
            : (Brush)FindResource("Surface3Brush");

        var title = new TextBlock
        {
            Text = film.Title,
            FontSize = 12.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = (Brush)FindResource("TextBrush"),
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextAlignment = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center
        };

        var meta = new StackPanel { Margin = new Thickness(8, 7, 8, 9), HorizontalAlignment = HorizontalAlignment.Stretch };
        meta.Children.Add(title);

        var badges = new List<string>();
        if (ratings.TryGetValue(film.Id, out var avg)) badges.Add($"★ {avg:0.0}");
        if (observationCounts.TryGetValue(film.Id, out var obsCount) && obsCount > 0) badges.Add($"📝 {obsCount}");

        if (badges.Count > 0)
        {
            var badgesPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(0, 5, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Center
            };
            foreach (var badge in badges)
            {
                badgesPanel.Children.Add(new TextBlock
                {
                    Text = badge,
                    FontSize = 10.5,
                    Foreground = (Brush)FindResource("TextMutedBrush"),
                    Margin = new Thickness(0, 0, 8, 0)
                });
            }
            meta.Children.Add(badgesPanel);
        }

        var content = new StackPanel { Width = ChipWidth };
        content.Children.Add(cover);
        content.Children.Add(meta);
        chip.Content = content;

        chip.Click += (_, _) => OpenFiche(film);
        chip.ContextMenu = BuildItemContextMenu(film);
        return chip;
    }

    // Right-click menu on a tier item: rename or delete that work.
    private ContextMenu BuildItemContextMenu(Artwork film)
    {
        var menu = new ContextMenu();

        var rename = new MenuItem { Header = "Rename" };
        rename.Click += (_, _) => RenameItem(film);
        menu.Items.Add(rename);

        var delete = new MenuItem { Header = "Delete" };
        delete.Click += (_, _) => DeleteItem(film);
        menu.Items.Add(delete);

        return menu;
    }

    private void RenameItem(Artwork film)
    {
        var newTitle = PromptForText("Rename", $"New name for \"{film.Title}\":", film.Title);
        if (newTitle is null) return;

        newTitle = newTitle.Trim();
        if (newTitle.Length == 0 || newTitle == film.Title) return;

        _artworkRepository.UpdateTitle(film.Id, newTitle);
        film.Title = newTitle;
        if (_selectedFilm?.Id == film.Id) FicheTitleText.Text = newTitle;
        RefreshArtworks();
    }

    private void DeleteItem(Artwork film)
    {
        if (MessageBox.Show($"Delete \"{film.Title}\"? This cannot be undone.", "Delete item",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;

        if (film.CoverImagePath is { } path) MediaStorage.DeleteFile(path);
        _artworkRepository.Delete(film.Id);

        if (_selectedFilm?.Id == film.Id) CloseFiche();
        RefreshArtworks();
    }

    // Small modal text prompt; returns null if cancelled.
    private string? PromptForText(string title, string label, string initial)
    {
        var window = new Window
        {
            Title = title,
            Width = 320,
            Height = 160,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = Window.GetWindow(this)
        };
        var textBox = new TextBox { Text = initial, Margin = new Thickness(0, 0, 0, 8) };
        var okButton = new Button { Content = "OK", Width = 80, IsDefault = true };
        okButton.Click += (_, _) => window.DialogResult = true;
        var cancelButton = new Button { Content = "Cancel", Width = 80, Margin = new Thickness(8, 0, 0, 0), IsCancel = true };
        window.Content = new StackPanel
        {
            Margin = new Thickness(16),
            Children =
            {
                new TextBlock { Text = label, Margin = new Thickness(0, 0, 0, 6) },
                textBox,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Children = { okButton, cancelButton }
                }
            }
        };
        textBox.Focus();
        textBox.SelectAll();
        return window.ShowDialog() == true ? textBox.Text : null;
    }

    // --- Add work modal ---

    private void OpenAddModalButton_OnClick(object sender, RoutedEventArgs e) => OpenAddModal(null);

    private void OpenAddModal(Tier? presetTier)
    {
        _pendingAddTier = presetTier ?? Tier.B;
        _pendingAddCoverPath = null;
        AddTitleInput.Clear();
        AddCoverPathText.Text = "Aucune (recherche automatique)";
        ApplyTierPillSelection(AddTierPillsPanel, _pendingAddTier);

        AddOverlay.Visibility = Visibility.Visible;
        AddOverlay.Focus();
        AddTitleInput.Focus();
    }

    private void CloseAddModal() => AddOverlay.Visibility = Visibility.Collapsed;

    private void CloseAddModalButton_OnClick(object sender, RoutedEventArgs e) => CloseAddModal();
    private void CancelAddButton_OnClick(object sender, RoutedEventArgs e) => CloseAddModal();

    private void AddScrim_OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e) => CloseAddModal();

    // Escape closes only the add modal - it never bubbles to close the fiche/section behind it.
    private void AddOverlay_OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            CloseAddModal();
            e.Handled = true;
        }
    }

    private void AddTierPill_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button clicked || clicked.Tag is not string tag) return;
        _pendingAddTier = Enum.Parse<Tier>(tag);
        ApplyTierPillSelection(AddTierPillsPanel, _pendingAddTier);
    }

    private void ChooseAddCoverButton_OnClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "Image files|*.png;*.jpg;*.jpeg;*.bmp;*.gif" };
        if (dialog.ShowDialog() != true) return;

        _pendingAddCoverPath = dialog.FileName;
        AddCoverPathText.Text = Path.GetFileName(dialog.FileName);
    }

    private async void ConfirmAddButton_OnClick(object sender, RoutedEventArgs e)
    {
        var title = AddTitleInput.Text.Trim();
        if (title.Length == 0) return;

        var artwork = new Artwork { Title = title, Category = _selectedCategory, Tier = _pendingAddTier, CreatedAt = DateTime.Now };
        artwork.Id = _artworkRepository.Add(artwork);

        CloseAddModal();
        RefreshArtworks();

        if (_pendingAddCoverPath is { } coverPath)
        {
            var relativePath = _mediaStorage.ImportCover(coverPath);
            _artworkRepository.UpdateCoverImage(artwork.Id, relativePath);
            artwork.CoverImagePath = relativePath;
            RefreshArtworks();
        }
        else
        {
            await TryFetchCoverAsync(artwork);
        }
    }

    // Auto-fetches a cover for a freshly added item from the web (best effort, offline-safe).
    private async Task TryFetchCoverAsync(Artwork artwork)
    {
        var result = await _imageSearchService.TryFindImageAsync(artwork.Title, SearchKindFor(artwork.Category));
        if (result is null) return;

        // The item may have been deleted while the fetch was in flight.
        if (_artworkRepository.GetById(artwork.Id) is null) return;

        var relativePath = _mediaStorage.ImportCoverFromBytes(result.Value.Data, result.Value.Extension);
        _artworkRepository.UpdateCoverImage(artwork.Id, relativePath);
        artwork.CoverImagePath = relativePath;

        RefreshArtworks();
        if (_selectedFilm?.Id == artwork.Id) RefreshCoverImage();
    }

    private static ImageSearchKind SearchKindFor(ArtworkCategory category) => category switch
    {
        ArtworkCategory.Film => ImageSearchKind.Film,
        ArtworkCategory.FilmAnimation => ImageSearchKind.AnimatedFilm,
        ArtworkCategory.Anime => ImageSearchKind.Anime,
        ArtworkCategory.Livre => ImageSearchKind.Book,
        ArtworkCategory.VideoGame => ImageSearchKind.VideoGame,
        _ => ImageSearchKind.Generic
    };

    // Highlights the selected tier pill by setting Background/Foreground directly (TierPillButtonStyle
    // forwards Button.Background via TemplateBinding, so this is safe/visible - see App.xaml).
    private void ApplyTierPillSelection(Panel panel, Tier selected)
    {
        var accent = (Brush)FindResource("AccentGradientBrush");
        var surface2 = (Brush)FindResource("Surface2Brush");
        var textMuted = (Brush)FindResource("TextMutedBrush");

        foreach (var child in panel.Children)
        {
            if (child is not Button pill || pill.Tag is not string tag) continue;
            var isSelected = tag == selected.ToString();
            pill.Background = isSelected ? accent : surface2;
            pill.Foreground = isSelected ? Brushes.White : textMuted;
        }
    }

    private void OpenFiche(Artwork film)
    {
        _selectedFilm = film;
        SaveActiveItem();
        FicheTitleText.Text = film.Title;

        ApplyTierPillSelection(FicheTierPillsPanel, film.Tier);

        RefreshCoverImage();

        AxisPicker.ItemsSource = _axisRepository.GetAll();
        NewAxisNameInput.Clear();

        RefreshEvaluations();
        RefreshObservations();

        // Hide the tier list behind the fiche so it doesn't clutter the view.
        ListRoot.Visibility = Visibility.Collapsed;

        FicheOverlay.Visibility = Visibility.Visible;
        FicheOverlay.Focus();
    }

    private void CloseFiche()
    {
        _selectedFilm = null;
        FicheOverlay.Visibility = Visibility.Collapsed;
        // Show the tier list again when the fiche is closed.
        ListRoot.Visibility = Visibility.Visible;
    }

    private void CloseFicheButton_OnClick(object sender, RoutedEventArgs e) => CloseFiche();

    private void FicheScrim_OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e) => CloseFiche();

    // Escape closes the currently open fiche - the only overlay/dialog this app has right now.
    private void FicheOverlay_OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            CloseFiche();
            e.Handled = true;
        }
    }

    private void FicheTierPill_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button clicked || clicked.Tag is not string tag || _selectedFilm is null) return;

        var tier = Enum.Parse<Tier>(tag);
        ApplyTierPillSelection(FicheTierPillsPanel, tier);
        _artworkRepository.UpdateTier(_selectedFilm.Id, tier);
        _selectedFilm.Tier = tier;
        RefreshArtworks();
    }

    private void RefreshCoverImage()
    {
        if (_selectedFilm?.CoverImagePath is { } path)
        {
            var bitmap = LoadImage(MediaStorage.ResolveFullPath(path));
            FicheCoverImage.Source = bitmap;
            FicheCoverImage.Visibility = Visibility.Visible;
            RemoveCoverButton.Visibility = Visibility.Visible;
            FicheCoverThumb.Background = new ImageBrush(bitmap) { Stretch = Stretch.UniformToFill };
        }
        else
        {
            FicheCoverImage.Visibility = Visibility.Collapsed;
            RemoveCoverButton.Visibility = Visibility.Collapsed;
            FicheCoverThumb.Background = (Brush)FindResource("Surface3Brush");
        }
    }

    private void SetCoverImageButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (_selectedFilm is null) return;

        var dialog = new OpenFileDialog { Filter = "Image files|*.png;*.jpg;*.jpeg;*.bmp;*.gif" };
        if (dialog.ShowDialog() != true) return;

        var relativePath = _mediaStorage.ImportCover(dialog.FileName);
        _artworkRepository.UpdateCoverImage(_selectedFilm.Id, relativePath);
        _selectedFilm.CoverImagePath = relativePath;

        RefreshCoverImage();
        RefreshArtworks();
    }

    private void RemoveCoverImageButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (_selectedFilm?.CoverImagePath is not { } path) return;

        _artworkRepository.UpdateCoverImage(_selectedFilm.Id, null);
        _selectedFilm.CoverImagePath = null;
        MediaStorage.DeleteFile(path);

        RefreshCoverImage();
        RefreshArtworks();
    }

    private void RefreshEvaluations()
    {
        if (_selectedFilm is null) return;

        var axesById = _axisRepository.GetAll().ToDictionary(a => a.Id);
        var evaluations = _evaluationRepository.GetByArtwork(_selectedFilm.Id);
        EvaluationsList.ItemsSource = evaluations
            .Where(ev => axesById.ContainsKey(ev.AxisId))
            .Select(ev => new EvaluationItem(ev.Id, $"{axesById[ev.AxisId].Name}: {ev.Score}/5"))
            .ToList();
    }

    private void AddEvaluationButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (_selectedFilm is null) return;

        var newAxisName = NewAxisNameInput.Text.Trim();
        int axisId;
        if (newAxisName.Length > 0)
        {
            axisId = _axisRepository.Add(new EvaluationAxis { Name = newAxisName });
        }
        else if (AxisPicker.SelectedItem is EvaluationAxis selectedAxis)
        {
            axisId = selectedAxis.Id;
        }
        else
        {
            return;
        }

        var score = int.Parse(((ComboBoxItem)ScoreInput.SelectedItem).Content.ToString()!);
        _evaluationRepository.Set(_selectedFilm.Id, axisId, score);

        NewAxisNameInput.Clear();
        AxisPicker.ItemsSource = _axisRepository.GetAll();
        RefreshEvaluations();
    }

    private void DeleteEvaluationButton_OnClick(object sender, RoutedEventArgs e)
    {
        var id = (int)((Button)sender).Tag;
        _evaluationRepository.Delete(id);
        RefreshEvaluations();
    }

    private void RefreshObservations()
    {
        if (_selectedFilm is null) return;

        ObservationsList.ItemsSource = _observationRepository.GetByArtwork(_selectedFilm.Id)
            .Select(o => new ObservationItem(o.Id, $"{o.Subject}: {o.Content}"))
            .ToList();
    }

    private void AddObservationButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (_selectedFilm is null) return;

        var subject = ObservationSubjectInput.Text.Trim();
        var content = ObservationContentInput.Text.Trim();
        if (subject.Length == 0 || content.Length == 0) return;

        _observationRepository.Add(new Observation
        {
            ArtworkId = _selectedFilm.Id,
            Subject = subject,
            Content = content,
            CreatedAt = DateTime.Now
        });

        ObservationSubjectInput.Clear();
        ObservationContentInput.Clear();
        RefreshObservations();
    }

    private void DeleteObservationButton_OnClick(object sender, RoutedEventArgs e)
    {
        var id = (int)((Button)sender).Tag;
        _observationRepository.Delete(id);
        RefreshObservations();
    }

    // --- Watchlist ("À voir") ---

    private async void AddWatchlistButton_OnClick(object sender, RoutedEventArgs e)
    {
        var title = WatchlistTitleInput.Text.Trim();
        if (title.Length == 0) return;

        var category = WatchlistCategoryPicker.SelectedIndex == 1 ? WatchlistCategory.FilmAnimation : WatchlistCategory.Film;
        var priority = WatchlistPriorityPicker.SelectedIndex switch
        {
            0 => WatchPriority.ShouldWatchSomeday,
            2 => WatchPriority.NeedToWatch,
            _ => WatchPriority.WantToWatch
        };

        var item = new WatchlistItem { Title = title, Category = category, Priority = priority, AddedAt = DateTime.Now };
        _watchlistRepository.Add(item);
        WatchlistTitleInput.Clear();
        RefreshWatchlist();

        // Auto-fetch a cover for the freshly added item (best effort, offline-safe).
        await TryFetchWatchlistCoverAsync(item);
    }

    // Auto-fetches a cover for a watchlist item from the web (best effort, offline-safe).
    private async Task TryFetchWatchlistCoverAsync(WatchlistItem item)
    {
        var kind = item.Category == WatchlistCategory.FilmAnimation
            ? ImageSearchKind.AnimatedFilm
            : ImageSearchKind.Film;
        var result = await _imageSearchService.TryFindImageAsync(item.Title, kind);
        if (result is null) return;

        var relativePath = _mediaStorage.ImportCoverFromBytes(result.Value.Data, result.Value.Extension);
        _watchlistRepository.UpdateCover(item.Id, relativePath);
        item.CoverImagePath = relativePath;
        RefreshWatchlist();
    }

    private void RefreshWatchlist()
    {
        WatchlistPanel.Children.Clear();
        foreach (var item in _watchlistRepository.GetAll())
        {
            WatchlistPanel.Children.Add(BuildWatchlistRow(item));
        }
    }

    private FrameworkElement BuildWatchlistRow(WatchlistItem item)
    {
        var row = new Grid { Margin = new Thickness(0, 0, 0, 8) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });   // cover
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); // info
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });   // actions

        // Cover thumbnail on the left.
        var cover = new Border
        {
            Width = 56,
            Height = 80,
            CornerRadius = new CornerRadius(6, 6, 0, 0),
            Margin = new Thickness(0, 0, 10, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        if (item.CoverImagePath is { } coverPath)
        {
            try
            {
                cover.Background = new ImageBrush(LoadImage(MediaStorage.ResolveFullPath(coverPath)))
                {
                    Stretch = Stretch.UniformToFill
                };
            }
            catch
            {
                cover.Background = (Brush)FindResource("Surface3Brush");
            }
        }
        else
        {
            cover.Background = (Brush)FindResource("Surface3Brush");
        }
        Grid.SetColumn(cover, 0);
        row.Children.Add(cover);

        // Info in the middle.
        var info = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        info.Children.Add(new TextBlock { Text = item.Title, Foreground = (Brush)FindResource("TextBrush"), FontSize = 13, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
        info.Children.Add(new TextBlock
        {
            Text = item.Category == WatchlistCategory.Film ? "Film" : "Film d'animation",
            Foreground = (Brush)FindResource("TextMutedBrush"),
            FontSize = 10
        });
        Grid.SetColumn(info, 1);
        row.Children.Add(info);

        // Actions on the right.
        var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };

        var priority = new ComboBox { Width = 150, Margin = new Thickness(0, 0, 6, 0), Style = (Style)FindResource("DarkComboBoxStyle") };
        priority.Items.Add("À voir un jour");
        priority.Items.Add("Envie de voir");
        priority.Items.Add("À voir absolument");
        priority.SelectedIndex = item.Priority switch
        {
            WatchPriority.ShouldWatchSomeday => 0,
            WatchPriority.NeedToWatch => 2,
            _ => 1
        };
        priority.SelectionChanged += (_, _) =>
        {
            var p = priority.SelectedIndex switch
            {
                0 => WatchPriority.ShouldWatchSomeday,
                2 => WatchPriority.NeedToWatch,
                _ => WatchPriority.WantToWatch
            };
            _watchlistRepository.UpdatePriority(item.Id, p);
        };
        actions.Children.Add(priority);

        var coverButton = new Button
        {
            Content = item.CoverImagePath is null ? "Affiche…" : "Affiche ✓",
            Margin = new Thickness(0, 0, 6, 0),
            Style = (Style)FindResource("SecondaryButtonStyle")
        };
        coverButton.Click += (_, _) =>
        {
            var dialog = new OpenFileDialog { Filter = "Image files|*.png;*.jpg;*.jpeg;*.bmp;*.gif" };
            if (dialog.ShowDialog() != true) return;
            var old = item.CoverImagePath;
            var relative = _mediaStorage.ImportCover(dialog.FileName);
            _watchlistRepository.UpdateCover(item.Id, relative);
            item.CoverImagePath = relative;
            if (old is not null) MediaStorage.DeleteFile(old);
            coverButton.Content = "Affiche ✓";
            RefreshWatchlist();
        };
        actions.Children.Add(coverButton);

        var deleteButton = new Button { Content = "✕", Style = (Style)FindResource("MiniButtonStyle") };
        deleteButton.Click += (_, _) =>
        {
            if (item.CoverImagePath is { } cover) MediaStorage.DeleteFile(cover);
            _watchlistRepository.Delete(item.Id);
            RefreshWatchlist();
        };
        actions.Children.Add(deleteButton);

        Grid.SetColumn(actions, 2);
        row.Children.Add(actions);
        return row;
    }
}
