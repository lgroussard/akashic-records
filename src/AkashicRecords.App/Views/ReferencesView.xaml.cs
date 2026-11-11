using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AkashicRecords.Domain;
using AkashicRecords.Infrastructure.Persistence;
using AkashicRecords.Infrastructure.Web;

namespace AkashicRecords.App.Views;

// Journaux → Références (PC). Same model as the phone: boards (« À trier » = not filed), search on two free
// sources, and — the PC's « Partager → Akashic » — drag-and-drop or Ctrl+V of an image, an image file or a link.
// Boards stay per device until the server syncs them.
public partial class ReferencesView : UserControl
{
    private const int All = -1;
    private const int Unsorted = -2;
    private const double TargetColumnWidth = 240;

    private readonly RefBoardRepository _boardRepository = new(new SqliteConnectionFactory());
    private readonly RefImageRepository _imageRepository = new(new SqliteConnectionFactory());

    private IReadOnlyList<RefBoard> _boards = Array.Empty<RefBoard>();
    private IReadOnlyList<RefImage> _images = Array.Empty<RefImage>();
    private int _filter = All;

    // Search mode (null query = boards).
    private string? _query;
    private int _page = 1;
    private readonly List<ReferenceHit> _hits = new();
    private readonly HashSet<string> _enabledSources = new(ReferenceImageSearch.Sources);
    private readonly HashSet<string> _savedUrls = new();
    private CancellationTokenSource? _searchCts;

    // Inline board naming: null = closed, 0 = new board, else the board being renamed.
    private int? _naming;

    // What the viewer shows: a saved image or a search hit.
    private RefImage? _viewing;
    private ReferenceHit? _previewing;
    private bool _fillingViewer;

    // Thumbnails of web images, fetched with our own client (a descriptive User-Agent, which Wikimedia requires).
    private static readonly Dictionary<string, BitmapSource> RemoteCache = new();
    private static readonly SemaphoreSlim RemoteGate = new(6);

    public ReferencesView()
    {
        InitializeComponent();
        foreach (var source in ReferenceImageSearch.Sources)
        {
            var toggle = new ToggleButton
            {
                Content = source, IsChecked = true, Margin = new Thickness(4, 0, 0, 0),
                Style = (Style)FindResource("MiniToggleButtonStyle"),
            };
            toggle.Click += (_, _) =>
            {
                if (toggle.IsChecked == true) _enabledSources.Add(source); else _enabledSources.Remove(source);
                if (_query is not null) StartSearch(_query);
            };
            SourcesPanel.Children.Add(toggle);
        }
        Loaded += (_, _) => Refresh();
    }

    // ---- Boards ----

    public void Refresh()
    {
        _boards = _boardRepository.GetAll();
        _images = _imageRepository.GetAll();
        if (_filter >= 0 && _boards.All(b => b.Id != _filter)) _filter = All;
        BuildBoardsBar();
        if (_query is null) ShowSaved();
    }

    private void BuildBoardsBar()
    {
        BoardsBar.Children.Clear();
        BoardsBar.Visibility = _query is null ? Visibility.Visible : Visibility.Collapsed;
        AddChip($"Tout · {_images.Count}", _filter == All, () => _filter = All);
        AddChip($"À trier · {_images.Count(i => i.BoardId is null)}", _filter == Unsorted, () => _filter = Unsorted);
        foreach (var b in _boards)
        {
            var id = b.Id;
            AddChip($"{b.Title} · {_images.Count(i => i.BoardId == id)}", _filter == id, () => _filter = id);
        }

        if (_naming is not null)
        {
            var input = new TextBox
            {
                Width = 180, Margin = new Thickness(4, 0, 0, 4), Style = (Style)FindResource("DarkTextBoxStyle"),
                Text = _naming > 0 ? _boards.FirstOrDefault(b => b.Id == _naming)?.Title ?? "" : "",
            };
            input.KeyDown += (_, e) =>
            {
                if (e.Key == Key.Enter) CommitName(input.Text);
                else if (e.Key == Key.Escape) { _naming = null; BuildBoardsBar(); }
            };
            BoardsBar.Children.Add(input);
            AddAction("OK", () => CommitName(input.Text));
            Dispatcher.BeginInvoke(() => { input.Focus(); input.SelectAll(); }, System.Windows.Threading.DispatcherPriority.Input);
        }
        else
        {
            AddAction("+ Tableau", () => { _naming = 0; BuildBoardsBar(); });
            if (_filter >= 0)
            {
                AddAction("Renommer", () => { _naming = _filter; BuildBoardsBar(); });
                AddAction("Supprimer le tableau", DeleteCurrentBoard);
            }
        }
    }

    private void AddChip(string text, bool selected, Action select)
    {
        var chip = new Button
        {
            Content = text, Margin = new Thickness(0, 0, 6, 4), Style = (Style)FindResource("ChipButtonStyle"),
        };
        if (selected) chip.Background = (Brush)FindResource("AccentSoftBrush");
        chip.Click += (_, _) => { select(); BuildBoardsBar(); ShowSaved(); };
        BoardsBar.Children.Add(chip);
    }

    private void AddAction(string text, Action action)
    {
        var button = new Button { Content = text, Margin = new Thickness(4, 0, 0, 4), Style = (Style)FindResource("SecondaryButtonStyle") };
        button.Click += (_, _) => action();
        BoardsBar.Children.Add(button);
    }

    private void CommitName(string name)
    {
        name = name.Trim();
        if (name.Length > 0)
        {
            if (_naming == 0) _filter = _boardRepository.Add(name);
            else if (_naming is int id) _boardRepository.Rename(id, name);
        }
        _naming = null;
        Refresh();
    }

    private void DeleteCurrentBoard()
    {
        var board = _boards.FirstOrDefault(b => b.Id == _filter);
        if (board is null) return;
        var ok = MessageBox.Show($"Supprimer le tableau « {board.Title} » ? Ses images vont dans À trier.", "Références",
            MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (ok != MessageBoxResult.OK) return;
        _boardRepository.Delete(board.Id);
        _filter = Unsorted;
        Refresh();
    }

    private void ShowSaved()
    {
        MoreButton.Visibility = Visibility.Collapsed;
        var shown = _filter switch
        {
            All => _images,
            Unsorted => _images.Where(i => i.BoardId is null).ToList(),
            _ => _images.Where(i => i.BoardId == _filter).ToList(),
        };
        SetStatus(_images.Count == 0
            ? "Aucune image pour l'instant. Cherche-en ci-dessus, ou glisse ici une image, un fichier ou un lien " +
              "(depuis le navigateur, Pinterest…), ou colle-la avec Ctrl+V."
            : shown.Count == 0 ? "Rien dans ce tableau." : null);
        Layout(shown.Select(img => new Tile(
            img.Width, img.Height,
            () => LoadLocalAsync(img.ImagePath),
            () => OpenSaved(img))).ToList());
    }

    // ---- Masonry layout ----

    private sealed record Tile(int Width, int Height, Func<Task<BitmapSource?>> Load, Action Open);

    private List<Tile> _tiles = new();

    private void Layout(List<Tile> tiles)
    {
        _tiles = tiles;
        var width = Math.Max(200, GridScroll.ActualWidth - 20);
        var count = Math.Max(2, (int)(width / TargetColumnWidth));
        var columnWidth = width / count;
        Columns.Children.Clear();
        Columns.ColumnDefinitions.Clear();
        var stacks = new StackPanel[count];
        var heights = new double[count];
        for (var c = 0; c < count; c++)
        {
            Columns.ColumnDefinitions.Add(new ColumnDefinition());
            stacks[c] = new StackPanel { Margin = new Thickness(3, 0, 3, 0) };
            Grid.SetColumn(stacks[c], c);
            Columns.Children.Add(stacks[c]);
        }

        foreach (var tile in tiles)
        {
            var ratio = tile.Width > 0 && tile.Height > 0 ? Math.Clamp((double)tile.Height / tile.Width, 0.4, 2.5) : 1.0;
            var height = (columnWidth - 6) * ratio;
            var shortest = Array.IndexOf(heights, heights.Min());
            var image = new Image { Stretch = Stretch.UniformToFill };
            RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
            var border = new Border
            {
                Height = height, CornerRadius = new CornerRadius(8), Margin = new Thickness(0, 0, 0, 6),
                Background = (Brush)FindResource("Surface2Brush"), ClipToBounds = true, Cursor = Cursors.Hand,
                Child = image,
            };
            border.Clip = new RectangleGeometry(new Rect(0, 0, columnWidth - 6, height), 8, 8);
            border.MouseLeftButtonUp += (_, _) => tile.Open();
            stacks[shortest].Children.Add(border);
            heights[shortest] += height + 6;
            _ = FillAsync(image, border, tile, columnWidth - 6);
        }
    }

    private static async Task FillAsync(Image image, Border border, Tile tile, double width)
    {
        var bitmap = await tile.Load();
        if (bitmap is null) return;
        image.Source = bitmap;
        // The source gave no size: the tile takes the picture's own shape once it is known.
        if (tile.Width <= 0 || tile.Height <= 0)
        {
            var height = width * Math.Clamp(bitmap.PixelHeight / (double)Math.Max(1, bitmap.PixelWidth), 0.4, 2.5);
            border.Height = height;
            border.Clip = new RectangleGeometry(new Rect(0, 0, width, height), 8, 8);
        }
    }

    private double _lastLayoutWidth;

    private void GridScroll_OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (Math.Abs(e.NewSize.Width - _lastLayoutWidth) < 40) return;
        _lastLayoutWidth = e.NewSize.Width;
        Layout(_tiles);
    }

    private void GridScroll_OnScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        // Near the bottom of the results: the next page comes by itself.
        if (_query is not null && MoreButton.Visibility == Visibility.Visible && e.VerticalOffset > 0 &&
            e.VerticalOffset + e.ViewportHeight > e.ExtentHeight - 300)
            LoadMore();
    }

    // ---- Search ----

    private void SearchInput_OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        var q = SearchInput.Text.Trim();
        if (q.Length == 0) { BackToBoards(); return; }
        StartSearch(q);
    }

    private void StartSearch(string query)
    {
        _query = query;
        _page = 1;
        _hits.Clear();
        BackToBoardsButton.Visibility = Visibility.Visible;
        BoardsBar.Visibility = Visibility.Collapsed;
        Layout(new List<Tile>());
        _ = RunSearchAsync();
    }

    private bool _searching;

    private void LoadMore()
    {
        if (_searching) return;
        _page++;
        _ = RunSearchAsync();
    }

    private void MoreButton_OnClick(object sender, RoutedEventArgs e) => LoadMore();

    private async Task RunSearchAsync()
    {
        _searchCts?.Cancel();
        var cts = _searchCts = new CancellationTokenSource();
        var query = _query!;
        var page = _page;
        if (_enabledSources.Count == 0) { SetStatus("Choisis au moins une source."); return; }
        _searching = true;
        MoreButton.Visibility = Visibility.Collapsed;
        SetStatus(_hits.Count == 0 ? "Recherche…" : null);
        var sources = ReferenceImageSearch.Sources.Where(_enabledSources.Contains).ToList();
        var tasks = sources
            .Select(async s => (Source: s, Hits: await ReferenceImageSearch.SearchAsync(s, query, page, cts.Token)))
            .ToList();
        var lists = new List<IReadOnlyList<ReferenceHit>>();
        var failed = new List<string>();
        foreach (var t in tasks)
        {
            try { lists.Add((await t).Hits); }
            catch (OperationCanceledException) when (cts.IsCancellationRequested) { _searching = false; return; }
            catch (Exception) { failed.Add(sources[tasks.IndexOf(t)]); }
        }
        if (cts.IsCancellationRequested || query != _query) { _searching = false; return; }

        // Interleaved, so the first screen shows every source.
        var merged = new List<ReferenceHit>();
        for (var i = 0; i < (lists.Count == 0 ? 0 : lists.Max(l => l.Count)); i++)
            foreach (var l in lists) if (i < l.Count) merged.Add(l[i]);
        var known = _hits.Select(h => h.FullUrl).ToHashSet();
        _hits.AddRange(merged.Where(h => known.Add(h.FullUrl)));

        var status = new List<string>();
        if (failed.Count > 0) status.Add("Pas de réponse de : " + string.Join(", ", failed));
        if (_hits.Count == 0) status.Add($"Aucune image trouvée pour « {query} ».");
        SetStatus(status.Count > 0 ? string.Join(" ", status) : null);
        MoreButton.Visibility = merged.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        Layout(_hits.Select(h => new Tile(h.Width, h.Height, () => LoadRemoteAsync(h.ThumbUrl), () => OpenHit(h))).ToList());
        _searching = false;
    }

    private void BackToBoardsButton_OnClick(object sender, RoutedEventArgs e) => BackToBoards();

    private void BackToBoards()
    {
        _searchCts?.Cancel();
        _query = null;
        _hits.Clear();
        BackToBoardsButton.Visibility = Visibility.Collapsed;
        Refresh();
    }

    // ---- Viewer ----

    private void OpenSaved(RefImage img)
    {
        _viewing = img;
        _previewing = null;
        ViewerTitle.Text = img.Title;
        ViewerMeta.Text = string.Join(" · ", new[] { img.Author, img.Source }.Where(s => !string.IsNullOrWhiteSpace(s)));
        ViewerSaveButton.Visibility = Visibility.Collapsed;
        ViewerDeleteButton.Visibility = Visibility.Visible;
        ViewerSourceButton.Visibility = (img.PageUrl ?? img.SourceUrl) is null ? Visibility.Collapsed : Visibility.Visible;
        FillBoardCombo(img.BoardId);
        ViewerImage.Source = null;
        Viewer.Visibility = Visibility.Visible;
        _ = Task.Run(() => DecodeFile(MediaStorage.ResolveFullPath(img.ImagePath), 1800))
            .ContinueWith(t => { if (_viewing == img) ViewerImage.Source = t.Result; }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    private void OpenHit(ReferenceHit hit)
    {
        _viewing = null;
        _previewing = hit;
        ViewerTitle.Text = hit.Title;
        ViewerMeta.Text = string.Join(" · ", new[] { hit.Author, hit.Source }.Where(s => !string.IsNullOrWhiteSpace(s)));
        var saved = _savedUrls.Contains(hit.FullUrl);
        ViewerSaveButton.Visibility = Visibility.Visible;
        ViewerSaveButton.IsEnabled = !saved;
        ViewerSaveButton.Content = saved ? "✓ Enregistrée" : "Enregistrer";
        ViewerDeleteButton.Visibility = Visibility.Collapsed;
        ViewerSourceButton.Visibility = hit.PageUrl is null ? Visibility.Collapsed : Visibility.Visible;
        FillBoardCombo(_filter >= 0 ? _filter : null);
        ViewerImage.Source = RemoteCache.TryGetValue(hit.ThumbUrl, out var thumb) ? thumb : null;
        Viewer.Visibility = Visibility.Visible;
    }

    private void FillBoardCombo(int? selected)
    {
        _fillingViewer = true;
        ViewerBoard.Items.Clear();
        ViewerBoard.Items.Add(new ComboBoxItem { Content = "À trier", Tag = null });
        foreach (var b in _boards) ViewerBoard.Items.Add(new ComboBoxItem { Content = b.Title, Tag = b.Id });
        ViewerBoard.SelectedItem = ViewerBoard.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (int?)i.Tag == selected) ?? ViewerBoard.Items[0];
        _fillingViewer = false;
    }

    private int? SelectedBoard => (ViewerBoard.SelectedItem as ComboBoxItem)?.Tag as int?;

    private void ViewerBoard_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_fillingViewer || _viewing is null) return;
        _imageRepository.Move(_viewing.Id, SelectedBoard);
        _viewing.BoardId = SelectedBoard;
        _images = _imageRepository.GetAll();
        BuildBoardsBar();
    }

    private async void ViewerSave_OnClick(object sender, RoutedEventArgs e)
    {
        if (_previewing is not { } hit) return;
        ViewerSaveButton.IsEnabled = false;
        ViewerSaveButton.Content = "…";
        try
        {
            await SaveFromUrlAsync(hit.FullUrl, hit.ThumbUrl, hit.PageUrl, hit.Title, hit.Author, hit.Source, SelectedBoard);
            _savedUrls.Add(hit.FullUrl);
            ViewerSaveButton.Content = "✓ Enregistrée";
            Viewer.Visibility = Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            ViewerSaveButton.IsEnabled = true;
            ViewerSaveButton.Content = "Enregistrer";
            MessageBox.Show("Échec : " + ex.Message, "Références", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void ViewerSource_OnClick(object sender, RoutedEventArgs e)
    {
        var url = _viewing is not null ? _viewing.PageUrl ?? _viewing.SourceUrl : _previewing?.PageUrl;
        if (url is null) return;
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { /* no browser */ }
    }

    private void ViewerDelete_OnClick(object sender, RoutedEventArgs e)
    {
        if (_viewing is not { } img) return;
        if (MessageBox.Show("Supprimer cette image ?", "Références", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        _imageRepository.Delete(img.Id);
        MediaStorage.DeleteFile(img.ImagePath);
        Viewer.Visibility = Visibility.Collapsed;
        _viewing = null;
        Refresh();
    }

    private void ViewerClose_OnClick(object sender, RoutedEventArgs e) => Viewer.Visibility = Visibility.Collapsed;

    private void Viewer_OnBackgroundClick(object sender, MouseButtonEventArgs e) => Viewer.Visibility = Visibility.Collapsed;

    private void ViewerCard_OnMouseDown(object sender, MouseButtonEventArgs e) => e.Handled = true; // clicks on the card stay

    // ---- Drop / paste: the PC's « Partager → Akashic » ----

    private void Root_OnDragOver(object sender, DragEventArgs e)
    {
        var ok = e.Data.GetDataPresent(DataFormats.FileDrop) || e.Data.GetDataPresent(DataFormats.UnicodeText) ||
                 e.Data.GetDataPresent(DataFormats.Text) || e.Data.GetDataPresent(DataFormats.Bitmap);
        e.Effects = ok ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void Root_OnDrop(object sender, DragEventArgs e) => await ImportAsync(e.Data);

    private async void Root_OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && Viewer.Visibility == Visibility.Visible) { Viewer.Visibility = Visibility.Collapsed; e.Handled = true; return; }
        if (e.Key != Key.V || Keyboard.Modifiers != ModifierKeys.Control || e.OriginalSource is TextBox) return;
        e.Handled = true;
        await ImportAsync(Clipboard.GetDataObject());
    }

    private async Task ImportAsync(IDataObject? data)
    {
        if (data is null) return;
        var board = _filter >= 0 ? _filter : (int?)null; // into the open board, else À trier
        try
        {
            if (data.GetDataPresent(DataFormats.FileDrop) && data.GetData(DataFormats.FileDrop) is string[] files)
            {
                var added = 0;
                foreach (var file in files.Where(IsImageFile))
                {
                    var bytes = await File.ReadAllBytesAsync(file);
                    Store(bytes, Path.GetExtension(file), null, null, Path.GetFileNameWithoutExtension(file), "", "Fichier", board);
                    added++;
                }
                SetStatus(added == 0 ? "Aucune image dans ce dépôt." : $"{added} image(s) ajoutée(s).");
            }
            else if ((data.GetData(DataFormats.UnicodeText) ?? data.GetData(DataFormats.Text)) is string text &&
                     ExtractLink(text) is { } link)
            {
                SetStatus("Recherche de l'image sur la page…");
                var found = await ReferenceImageSearch.ResolveLinkAsync(link);
                if (found is null) { SetStatus("Pas d'image trouvée sur cette page : glisse plutôt l'image elle-même."); return; }
                await SaveFromUrlAsync(found.ImageUrl, null, found.PageUrl, found.Title, "", "Lien", board);
                SetStatus("Image ajoutée.");
            }
            else if (data.GetDataPresent(DataFormats.Bitmap) && data.GetData(DataFormats.Bitmap) is BitmapSource bitmap)
            {
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var stream = new MemoryStream();
                encoder.Save(stream);
                Store(stream.ToArray(), ".png", null, null, "", "", "Presse-papiers", board);
                SetStatus("Image ajoutée.");
            }
            else return;
            Refresh();
        }
        catch (Exception ex)
        {
            SetStatus("Échec : " + ex.Message);
        }
    }

    private static bool IsImageFile(string path) =>
        new[] { ".jpg", ".jpeg", ".png", ".gif", ".webp", ".bmp" }.Contains(Path.GetExtension(path).ToLowerInvariant());

    private static string? ExtractLink(string text)
    {
        var m = System.Text.RegularExpressions.Regex.Match(text, "https?://\\S+");
        return m.Success ? m.Value.TrimEnd('.', ',', ')', '"') : null;
    }

    // ---- Saving ----

    private async Task SaveFromUrlAsync(string url, string? fallback, string? pageUrl, string title, string author, string source, int? board)
    {
        (byte[] Bytes, string Extension) file;
        try { file = await ReferenceImageSearch.DownloadAsync(url); }
        catch when (fallback is not null) { file = await ReferenceImageSearch.DownloadAsync(fallback); }
        Store(file.Bytes, file.Extension, url, pageUrl, title, author, source, board);
        Refresh();
    }

    private void Store(byte[] bytes, string extension, string? sourceUrl, string? pageUrl, string title, string author, string source, int? board)
    {
        int width, height;
        using (var stream = new MemoryStream(bytes))
        {
            var frame = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None).Frames[0];
            width = frame.PixelWidth;
            height = frame.PixelHeight;
        }
        var folder = Path.Combine(AppContext.BaseDirectory, "media", "references");
        Directory.CreateDirectory(folder);
        var name = $"{Guid.NewGuid():N}{(string.IsNullOrEmpty(extension) ? ".jpg" : extension.ToLowerInvariant())}";
        File.WriteAllBytes(Path.Combine(folder, name), bytes);
        _imageRepository.Add(new RefImage
        {
            BoardId = board, ImagePath = Path.Combine("media", "references", name), Title = title.Trim(), Author = author.Trim(),
            Source = source, SourceUrl = sourceUrl, PageUrl = pageUrl, Width = width, Height = height, CreatedAt = DateTime.Now,
        });
    }

    // ---- Images ----

    private static Task<BitmapSource?> LoadLocalAsync(string relativePath) =>
        Task.Run(() => DecodeFile(MediaStorage.ResolveFullPath(relativePath), 480));

    private static BitmapSource? DecodeFile(string path, int width)
    {
        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.DecodePixelWidth = width;
            bitmap.UriSource = new Uri(path);
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch
        {
            return null;
        }
    }

    private static async Task<BitmapSource?> LoadRemoteAsync(string url)
    {
        lock (RemoteCache) if (RemoteCache.TryGetValue(url, out var cached)) return cached;
        await RemoteGate.WaitAsync();
        try
        {
            var (bytes, _) = await ReferenceImageSearch.DownloadAsync(url);
            var bitmap = await Task.Run(() =>
            {
                var b = new BitmapImage();
                b.BeginInit();
                b.CacheOption = BitmapCacheOption.OnLoad;
                b.DecodePixelWidth = 480;
                b.StreamSource = new MemoryStream(bytes);
                b.EndInit();
                b.Freeze();
                return (BitmapSource)b;
            });
            lock (RemoteCache) RemoteCache[url] = bitmap;
            return bitmap;
        }
        catch
        {
            return null;
        }
        finally
        {
            RemoteGate.Release();
        }
    }

    private void SetStatus(string? text)
    {
        StatusText.Text = text ?? "";
        StatusText.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
    }
}
