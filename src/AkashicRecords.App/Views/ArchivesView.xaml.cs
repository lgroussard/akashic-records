using System.Collections.Generic;
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
using Microsoft.Win32;

namespace AkashicRecords.App.Views;

public partial class ArchivesView : UserControl, ISearchNavigable
{
    private const double ThumbnailSize = 150;
    private const int ThumbnailDecodeWidth = 200;

    private readonly PhotoRepository _photoRepo = new(new SqliteConnectionFactory());
    private readonly PhotoAlbumRepository _albumRepo = new(new SqliteConnectionFactory());
    private readonly MediaStorage _mediaStorage = new();

    private readonly DispatcherTimer _slideshowTimer;

    // The set the viewer navigates over - always the currently shown (filtered) grid.
    private List<Photo> _filteredPhotos = new();
    private int _viewerIndex = -1;

    private bool _favoritesOnly;
    private int? _albumFilterId;
    private bool _isRebuildingCombos;

    private readonly AppConfig _config;

    private SearchResult? _pendingSearchResult;

    // Loads fully into memory and releases the file handle immediately, so the image file can be
    // deleted/replaced afterwards without "file in use" errors. decodePixelWidth > 0 downsamples
    // the decode (used for grid thumbnails) to keep memory low; 0 loads full resolution.
    private static BitmapImage LoadImage(string fullPath, int decodePixelWidth = 0)
    {
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        if (decodePixelWidth > 0) bitmap.DecodePixelWidth = decodePixelWidth;
        bitmap.UriSource = new Uri(fullPath);
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }

    public ArchivesView(AppConfig config)
    {
        InitializeComponent();
        _config = config;

        _slideshowTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _slideshowTimer.Tick += (_, _) => ShowPhotoAt(_viewerIndex + 1);

        Loaded += (_, _) =>
        {
            RestoreActiveItem();
            BuildAlbumList();
            RefreshGrid();
        };

        Loaded += (_, _) => ApplyPendingSearchResult();

        // Spec §45: no idle CPU. Stop the slideshow timer when the view leaves the visual tree.
        Unloaded += (_, _) =>
        {
            StopSlideshow();
            _slideshowTimer?.Stop();
        };
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
        // Photos tab.
        PhotosTab.IsChecked = true;
        FamilialeTab.IsChecked = false;
        PhotosRoot.Visibility = Visibility.Visible;
        FamilialeRoot.Visibility = Visibility.Collapsed;

        // Clear every filter so the target photo is guaranteed to be in the viewer's set.
        _favoritesOnly = false;
        FavoritesToggle.IsChecked = false;
        AllPhotosToggle.IsChecked = true;
        _albumFilterId = null;
        SearchInput.Text = string.Empty;
        BuildAlbumList();
        RefreshGrid();

        var photo = _filteredPhotos.FirstOrDefault(p => p.Id == result.PrimaryId);
        if (photo is not null) OpenViewer(photo);
    }

    private void SubTab_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton clicked || clicked.Tag is not string tab) return;

        PhotosTab.IsChecked = clicked == PhotosTab;
        FamilialeTab.IsChecked = clicked == FamilialeTab;

        PhotosRoot.Visibility = tab == "Photos" ? Visibility.Visible : Visibility.Collapsed;
        FamilialeRoot.Visibility = tab == "Familiale" ? Visibility.Visible : Visibility.Collapsed;

        _config.ArchivesActiveTab = tab;
    }

    // Records the exact photo the user is viewing plus the active filters, so reopening
    // Archives returns them to the same view, not just the right sub-tab.
    private void SaveActiveItem()
    {
        _config.ArchivesActiveTab ??= PhotosTab.IsChecked == true ? "Photos" : "Familiale";
        _config.ArchivesAlbumFilterId = _albumFilterId;
        _config.ArchivesFavoritesOnly = _favoritesOnly;
        _config.ArchivesActivePhotoId = CurrentPhoto?.Id;
    }

    // Reopens the exact photo the user last viewed, restoring the sub-tab, filters, and viewer.
    private void RestoreActiveItem()
    {
        PhotosTab.IsChecked = _config.ArchivesActiveTab != "Familiale";
        FamilialeTab.IsChecked = _config.ArchivesActiveTab == "Familiale";
        PhotosRoot.Visibility = PhotosTab.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        FamilialeRoot.Visibility = PhotosTab.IsChecked == true ? Visibility.Collapsed : Visibility.Visible;

        _favoritesOnly = _config.ArchivesFavoritesOnly;
        FavoritesToggle.IsChecked = _favoritesOnly;
        AllPhotosToggle.IsChecked = !_favoritesOnly;
        _albumFilterId = _config.ArchivesAlbumFilterId;

        // Restore the selected album row in the sidebar.
        foreach (UIElement child in AlbumList.Children)
        {
            if (child is not ToggleButton tb) continue;
            var tagId = tb.Tag as int?;
            bool matches = _albumFilterId is null
                ? tagId is null
                : tagId == _albumFilterId;
            if (matches)
            {
                tb.IsChecked = true;
                break;
            }
        }

        RefreshGrid();

        if (_config.ArchivesActivePhotoId is { } photoId)
        {
            var photo = _filteredPhotos.FirstOrDefault(p => p.Id == photoId);
            if (photo is not null) OpenViewer(photo);
        }
    }

    // --- Filtering / grid ---

    private List<Photo> GetFilteredPhotos()
    {
        IEnumerable<Photo> photos = _photoRepo.GetAll();

        if (_albumFilterId is int albumId)
        {
            photos = photos.Where(p => p.AlbumId == albumId);
        }
        if (_favoritesOnly)
        {
            photos = photos.Where(p => p.IsFavorite);
        }

        var query = SearchInput.Text.Trim();
        if (query.Length > 0)
        {
            photos = photos.Where(p =>
                p.Title.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                p.Tags.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                p.Description.Contains(query, StringComparison.OrdinalIgnoreCase));
        }

        return photos.ToList();
    }

    private void RefreshGrid()
    {
        _filteredPhotos = GetFilteredPhotos();

        ThumbnailPanel.Children.Clear();
        foreach (var photo in _filteredPhotos)
        {
            ThumbnailPanel.Children.Add(CreateThumbnail(photo));
        }
    }

    private Button CreateThumbnail(Photo photo)
    {
        var image = new Image
        {
            Source = LoadImage(MediaStorage.ResolveFullPath(photo.ImagePath), ThumbnailDecodeWidth),
            Stretch = Stretch.UniformToFill
        };

        var content = new Grid { Width = ThumbnailSize, Height = ThumbnailSize };
        content.Children.Add(image);

        // Favorite star, top-right.
        if (photo.IsFavorite)
        {
            content.Children.Add(new TextBlock
            {
                Text = "★",
                Foreground = Brushes.Gold,
                FontSize = 14,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 8, 8, 0)
            });
        }

        // Caption overlay at the bottom (only when there is a title).
        if (!string.IsNullOrEmpty(photo.Title))
        {
            content.Children.Add(new TextBlock
            {
                Text = photo.Title,
                Foreground = Brushes.White,
                FontSize = 11.5,
                FontWeight = FontWeights.Medium,
                TextWrapping = TextWrapping.Wrap,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Bottom,
                Padding = new Thickness(8, 0, 8, 8),
                Background = new LinearGradientBrush(
                    new GradientStopCollection
                    {
                        new GradientStop(Color.FromArgb(0, 0, 0, 0), 0),
                        new GradientStop(Color.FromArgb(0xBF, 0, 0, 0), 1)
                    },
                    90)
            });
        }

        var button = new Button
        {
            Content = content,
            Padding = new Thickness(0),
            Margin = new Thickness(0, 0, 12, 12),
            Background = new SolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x45)),
            BorderBrush = BorderStrong,
            BorderThickness = new Thickness(1),
            Cursor = Cursors.Hand
        };
        button.Click += (_, _) => OpenViewer(photo);
        return button;
    }

    private static readonly SolidColorBrush BorderStrong = new(Color.FromRgb(0x24, 0xFF, 0xFF));

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
        var cancelButton = new Button { Content = "Annuler", Width = 80, Margin = new Thickness(8, 0, 0, 0), IsCancel = true };
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
        return window.ShowDialog() == true ? textBox.Text : null;
    }

    private void SearchInput_OnTextChanged(object sender, TextChangedEventArgs e)
    {
        SearchWatermark.Visibility = string.IsNullOrEmpty(SearchInput.Text)
            ? Visibility.Visible
            : Visibility.Collapsed;
        RefreshGrid();
    }

    private void FilterToggle_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton clicked || clicked.Tag is not string tag) return;

        if (tag == "Favorites")
        {
            _favoritesOnly = clicked.IsChecked == true;
            _config.ArchivesFavoritesOnly = _favoritesOnly;
            FavoritesToggle.IsChecked = _favoritesOnly;
            AllPhotosToggle.IsChecked = !_favoritesOnly;
        }
        else
        {
            // "Toutes" — clearing the favorites filter.
            _favoritesOnly = false;
            _config.ArchivesFavoritesOnly = false;
            FavoritesToggle.IsChecked = false;
        }

        RefreshGrid();
    }

    // --- Albums sidebar ---

    private void BuildAlbumList()
    {
        AlbumList.Children.Clear();

        var albums = _albumRepo.GetAll().OrderBy(a => a.Name).ToList();
        var allPhotos = _photoRepo.GetAll().ToList();

        // "Tout" row (no album filter).
        var allRow = BuildAlbumRow("Tout", allPhotos.Count, null);
        allRow.IsChecked = _albumFilterId is null && !_favoritesOnly;
        AlbumList.Children.Add(allRow);

        foreach (var album in albums)
        {
            var count = allPhotos.Count(p => p.AlbumId == album.Id);
            var row = BuildAlbumRow(album.Name, count, album.Id);
            row.IsChecked = _albumFilterId == album.Id && !_favoritesOnly;
            AlbumList.Children.Add(row);
        }
    }

    private static readonly LinearGradientBrush AlbumSwatchBrush = new(
        new GradientStopCollection
        {
            new GradientStop(Color.FromRgb(0x2A, 0x2A, 0x4A), 0),
            new GradientStop(Color.FromRgb(0x1A, 0x1A, 0x30), 1)
        });

    private ToggleButton BuildAlbumRow(string title, int count, int? albumId)
    {
        var swatch = new Border
        {
            Width = 26,
            Height = 26,
            CornerRadius = new CornerRadius(6),
            Background = AlbumSwatchBrush,
            BorderBrush = (Brush)FindResource("AppBorderBrush"),
            BorderThickness = new Thickness(1)
        };

        var titleBlock = new TextBlock
        {
            Text = title,
            FontSize = 13.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = (Brush)FindResource("TextBrush"),
            VerticalAlignment = VerticalAlignment.Center
        };

        var countBlock = new TextBlock
        {
            Text = count.ToString(),
            FontSize = 11,
            Foreground = (Brush)FindResource("TextMutedBrush"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0)
        };

        var stack = new StackPanel { Orientation = Orientation.Horizontal };
        stack.Children.Add(swatch);
        stack.Children.Add(titleBlock);
        stack.Children.Add(countBlock);

        var row = new ToggleButton
        {
            Content = stack,
            Padding = new Thickness(10, 8, 10, 8),
            Margin = new Thickness(0, 0, 0, 2),
            Cursor = Cursors.Hand,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Tag = albumId,
            Style = AlbumRowStyle()
        };
        row.Click += (_, _) => SelectAlbumRow(row, albumId);
        return row;
    }

    private static readonly SolidColorBrush AlbumRowSelectedBg =
        new(Color.FromRgb(0x29, 0x5B, 0x8C));
    private static readonly SolidColorBrush AlbumRowHoverBg =
        new(Color.FromRgb(0x1E, 0x1E, 0x36));

    private static Style AlbumRowStyle()
    {
        var style = new Style(typeof(ToggleButton));
        style.Setters.Add(new Setter(BackgroundProperty, Brushes.Transparent));
        style.Setters.Add(new Setter(ForegroundProperty, (Brush)Application.Current.FindResource("TextBrush")));
        style.Setters.Add(new Setter(PaddingProperty, new Thickness(10, 8, 10, 8)));
        style.Setters.Add(new Setter(CursorProperty, Cursors.Hand));
        style.Setters.Add(new Setter(HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch));

        var template = new ControlTemplate(typeof(ToggleButton));
        var border = new FrameworkElementFactory(typeof(Border), "Bg");
        border.SetValue(BackgroundProperty, new TemplateBindingExtension(BackgroundProperty));
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(8));
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        border.AppendChild(presenter);
        template.VisualTree = border;

        var selectedTrigger = new Trigger
        {
            Property = ToggleButton.IsCheckedProperty,
            Value = true,
            Setters =
            {
                new Setter(BackgroundProperty, AlbumRowSelectedBg),
                new Setter(ForegroundProperty, Brushes.White)
            }
        };
        var hoverTrigger = new Trigger
        {
            Property = ToggleButton.IsMouseOverProperty,
            Value = true,
            Setters =
            {
                new Setter(BackgroundProperty, AlbumRowHoverBg),
                new Setter(ForegroundProperty, Brushes.White)
            }
        };
        template.Triggers.Add(selectedTrigger);
        template.Triggers.Add(hoverTrigger);
        style.Setters.Add(new Setter(TemplateProperty, template));
        return style;
    }

    private void SelectAlbumRow(ToggleButton clicked, int? albumId)
    {
        // Uncheck every other row (they are not a radio group).
        foreach (UIElement child in AlbumList.Children)
        {
            if (child is ToggleButton tb && tb != clicked) tb.IsChecked = false;
        }

        _albumFilterId = albumId;
        _config.ArchivesAlbumFilterId = albumId;
        _favoritesOnly = false;
        _config.ArchivesFavoritesOnly = false;
        FavoritesToggle.IsChecked = false;
        AllPhotosToggle.IsChecked = true;

        RefreshGrid();
    }

    // --- New album ---

    private void NewAlbumButton_OnClick(object sender, RoutedEventArgs e)
    {
        var name = PromptForText("Nouvel album", "Nom de l'album", string.Empty);
        if (string.IsNullOrEmpty(name)) return;

        name = name.Trim();
        _albumRepo.Add(new PhotoAlbum { Name = name, CreatedAt = DateTime.Now });
        BuildAlbumList();
        RefreshGrid();
    }

    // --- Import ---

    private void ImportButton_OnClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Multiselect = true,
            Filter = "Image files|*.jpg;*.jpeg;*.png;*.gif;*.bmp;*.webp"
        };
        if (dialog.ShowDialog() != true) return;

        foreach (var fileName in dialog.FileNames)
        {
            var relativePath = _mediaStorage.ImportArchivePhoto(fileName);
            _photoRepo.Add(new Photo
            {
                ImagePath = relativePath,
                AlbumId = _albumFilterId,
                ImportedAt = DateTime.Now
            });
        }

        RefreshGrid();
        BuildAlbumList();
    }

    // --- Fullscreen viewer ---

    private void OpenViewer(Photo photo)
    {
        _viewerIndex = _filteredPhotos.FindIndex(p => p.Id == photo.Id);
        if (_viewerIndex < 0) return;

        ViewerOverlay.Visibility = Visibility.Visible;
        ShowPhotoAt(_viewerIndex);
        ViewerOverlay.Focus();
    }

    private void ShowPhotoAt(int index)
    {
        if (_filteredPhotos.Count == 0)
        {
            CloseViewer();
            return;
        }

        // Wrap around so the slideshow loops and arrow keys never dead-end.
        _viewerIndex = (index % _filteredPhotos.Count + _filteredPhotos.Count) % _filteredPhotos.Count;
        var photo = _filteredPhotos[_viewerIndex];

        SaveActiveItem();
        ViewerImage.Source = LoadImage(MediaStorage.ResolveFullPath(photo.ImagePath));
        ViewerCounterText.Text = $"{_viewerIndex + 1} / {_filteredPhotos.Count}";

        _isRebuildingCombos = true;
        ViewerTitleInput.Text = photo.Title;
        ViewerDescriptionInput.Text = photo.Description;
        ViewerDatePicker.SelectedDate = photo.TakenDate;
        ViewerTagsInput.Text = photo.Tags;
        ViewerFavoriteButton.Content = photo.IsFavorite ? "★" : "☆";
        ViewerAlbumCombo.SelectedIndex = ViewerAlbumCombo.Items.Cast<ComboBoxItem>().ToList()
            .FindIndex(i => (int?)i.Tag == photo.AlbumId);
        if (ViewerAlbumCombo.SelectedIndex < 0) ViewerAlbumCombo.SelectedIndex = 0;
        _isRebuildingCombos = false;
    }

    private Photo? CurrentPhoto =>
        _viewerIndex >= 0 && _viewerIndex < _filteredPhotos.Count ? _filteredPhotos[_viewerIndex] : null;

    private void CloseViewer()
    {
        StopSlideshow();
        ViewerOverlay.Visibility = Visibility.Collapsed;
        ViewerImage.Source = null;
        _viewerIndex = -1;
    }

    private void CloseViewerButton_OnClick(object sender, RoutedEventArgs e) => CloseViewer();

    private void PreviousButton_OnClick(object sender, RoutedEventArgs e) => ShowPhotoAt(_viewerIndex - 1);
    private void NextButton_OnClick(object sender, RoutedEventArgs e) => ShowPhotoAt(_viewerIndex + 1);

    // Escape closes the viewer; arrows navigate. Handled so Escape doesn't bubble up and close
    // the whole Archives section while the viewer is open.
    private void ViewerOverlay_OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                CloseViewer();
                e.Handled = true;
                break;
            case Key.Left:
                ShowPhotoAt(_viewerIndex - 1);
                e.Handled = true;
                break;
            case Key.Right:
                ShowPhotoAt(_viewerIndex + 1);
                e.Handled = true;
                break;
            case Key.Delete:
                DeleteCurrentPhoto();
                e.Handled = true;
                break;
        }
    }

    private void SlideshowButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (_slideshowTimer.IsEnabled) StopSlideshow();
        else StartSlideshow();
    }

    private void StartSlideshow()
    {
        _slideshowTimer.Start();
        SlideshowButton.Content = "❚❚ Pause";
    }

    private void StopSlideshow()
    {
        _slideshowTimer.Stop();
        SlideshowButton.Content = "▶ Slideshow";
    }

    private void ViewerFavoriteButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (CurrentPhoto is not { } photo) return;

        photo.IsFavorite = !photo.IsFavorite;
        _photoRepo.SetFavorite(photo.Id, photo.IsFavorite);
        ViewerFavoriteButton.Content = photo.IsFavorite ? "★" : "☆";

        // A photo unfavorited while filtering to favorites should leave the current set.
        if (_favoritesOnly && !photo.IsFavorite)
        {
            RefreshGrid();
            ShowPhotoAt(_viewerIndex);
        }
    }

    private void SaveMetadataButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (CurrentPhoto is not { } photo) return;

        photo.Title = ViewerTitleInput.Text.Trim();
        photo.Description = ViewerDescriptionInput.Text.Trim();
        photo.TakenDate = ViewerDatePicker.SelectedDate;
        photo.Tags = ViewerTagsInput.Text.Trim();

        _photoRepo.UpdateMetadata(photo.Id, photo.Title, photo.Description, photo.TakenDate, photo.Tags);
        ViewerCounterText.Text = $"{_viewerIndex + 1} / {_filteredPhotos.Count}";
        RefreshGrid();
    }

    private void ViewerAlbumCombo_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isRebuildingCombos || CurrentPhoto is not { } photo) return;

        var albumId = (ViewerAlbumCombo.SelectedItem as ComboBoxItem)?.Tag as int?;
        photo.AlbumId = albumId;
        _photoRepo.SetAlbum(photo.Id, albumId);

        // Reassigning out of the currently filtered album drops it from the set.
        if (_albumFilterId is not null && _albumFilterId != albumId)
        {
            RefreshGrid();
            ShowPhotoAt(_viewerIndex);
        }
    }

    private void DeletePhotoButton_OnClick(object sender, RoutedEventArgs e) => DeleteCurrentPhoto();

    private void DeleteCurrentPhoto()
    {
        if (CurrentPhoto is not { } photo) return;

        var confirm = MessageBox.Show(
            "Supprimer définitivement cette photo ?", "Confirmation",
            MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes) return;

        var deletedIndex = _viewerIndex;

        // Release the fullscreen Image handle before touching the file. LoadImage already
        // freezes each frame, but clearing the Source drops the last reference just in case.
        ViewerImage.Source = null;

        // Delete the FILE first: if it fails the DB row is left intact so the photo isn't orphaned.
        try
        {
            MediaStorage.DeleteFile(photo.ImagePath);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"Impossible de supprimer le fichier image :\n{ex.Message}\n\nLa photo n'a pas été supprimée.",
                "Erreur", MessageBoxButton.OK, MessageBoxImage.Error);
            // Restore the image we just cleared so the viewer stays consistent.
            ShowPhotoAt(deletedIndex);
            return;
        }

        _photoRepo.Delete(photo.Id);

        RefreshGrid();

        if (_filteredPhotos.Count == 0) CloseViewer();
        else ShowPhotoAt(deletedIndex);
    }
}

