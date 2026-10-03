using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using AkashicRecords.Domain;
using AkashicRecords.Infrastructure.Configuration;
using AkashicRecords.Infrastructure.Downloading;
using AkashicRecords.Infrastructure.Persistence;
using Microsoft.Win32;

namespace AkashicRecords.App;

public partial class DownloaderWindow : Window
{
    private readonly DownloadItemRepository _repository = new(new SqliteConnectionFactory());
    private readonly DownloadFolderRepository _folderRepo = new(new SqliteConnectionFactory());
    private readonly MusicLibraryScanner _scanner = new(new MusicTrackRepository(new SqliteConnectionFactory()));
    private readonly ExternalDownloader _downloader = new();

    private readonly AppConfig _config;
    private readonly ConfigService _configService;

    private bool _isProcessing;
    // Live drag-drop state: the card currently grabbed and the zone bodies eligible as drop targets.
    private bool _dragActive;
    private readonly List<(Border Body, int? FolderId)> _zoneTargets = new();
    private Border? _highlightedZone;
    // Set only via ForceClose() when the app is genuinely exiting; otherwise closing hides the window.
    private bool _isExiting;

    // Raised after successful downloads register new files, so an open music player can refresh.
    public event Action? LibraryChanged;

    public DownloaderWindow(AppConfig config, ConfigService configService)
    {
        InitializeComponent();
        _config = config;
        _configService = configService;

        Loaded += (_, _) =>
        {
            ToolPathInput.Text = _config.DownloaderToolPath ?? string.Empty;
            ArgumentsInput.Text = _config.DownloaderArguments;
            AutoToggle.IsChecked = _config.DownloaderAutoMode;
            RefreshQueue();
        };
    }

    // Only DragMove when the click didn't originate on an interactive control.
    private void Root_OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (IsInteractiveOrigin(e.OriginalSource as DependencyObject)) return;
        try { DragMove(); } catch { /* DragMove throws if button already released */ }
    }

    private static bool IsInteractiveOrigin(DependencyObject? source)
    {
        while (source is not null)
        {
            if (source is ButtonBase or TextBoxBase or ScrollBar or Expander) return true;
            source = VisualTreeHelper.GetParent(source);
        }
        return false;
    }

    // --- Queue building ------------------------------------------------------

    private async void AddButton_OnClick(object sender, RoutedEventArgs e)
    {
        var lines = LinksInput.Text.Split('\n');
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in _repository.GetAll()) existing.Add(item.Url);

        var added = 0;
        foreach (var raw in lines)
        {
            var url = raw.Trim();
            if (url.Length == 0 || !LooksLikeUrl(url)) continue;
            if (!existing.Add(url)) continue; // skip duplicates within this batch and against the db
            _repository.Add(new DownloadItem { Url = url, Status = DownloadStatus.Queued, AddedAt = DateTime.Now });
            added++;
        }

        LinksInput.Text = string.Empty;
        StatusText.Text = added == 0 ? "Aucun lien valide ajouté." : $"{added} lien(s) ajouté(s).";
        RefreshQueue();

        if (added > 0 && _config.DownloaderAutoMode) await ProcessQueueAsync();
    }

    private static bool LooksLikeUrl(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    // The board: one "Unfiled" bucket (FolderId null) plus one column per placement zone. Each
    // zone is a drop target; item cards are dragged between them. A zone's meaning is the
    // music/<Subfolder> its cards download into.
    private void RefreshQueue()
    {
        var items = _repository.GetAll();
        _folders = _folderRepo.GetAll().ToList();
        _zoneTargets.Clear();
        BoardPanel.Children.Clear();

        BoardPanel.Children.Add(BuildZone(null, "Non classé", null, items.Where(i => i.FolderId is null)));
        foreach (var zone in _folders)
            BoardPanel.Children.Add(BuildZone(zone.Id, zone.Name, zone.Subfolder, items.Where(i => i.FolderId == zone.Id)));
    }

    // Zones of the current board, kept alongside the widgets so card/zone handlers resolve them
    // without a re-query. Rebuilt wholesale on every RefreshQueue.
    private List<DownloadFolder> _folders = new();

    // Builds one zone column: a titled, bordered body holding its cards. The body registers as a
    // drop target; pressing a card (not a button) starts a drag that lands on whichever body the
    // cursor is over when released.
    private FrameworkElement BuildZone(int? folderId, string name, string? subfolder, IEnumerable<DownloadItem> items)
    {
        var body = new StackPanel();
        var listHost = new StackPanel { MinHeight = 34 };
        body.Children.Add(BuildZoneHeader(folderId, name, subfolder));
        body.Children.Add(listHost);

        var visible = items.ToList();
        foreach (var item in visible)
            listHost.Children.Add(BuildCard(item, folderId));
        if (visible.Count == 0)
            listHost.Children.Add(new TextBlock
            {
                Text = "Deposer une fiche ici.",
                Foreground = new SolidColorBrush(Color.FromRgb(0x66, 0x66, 0x88)),
                FontSize = 10, FontStyle = FontStyles.Italic, Margin = new Thickness(2)
            });

        // Wraps the whole column; this Border is the measured drop rectangle.
        var shell = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0x16, 0x1C, 0x30)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x2A, 0x33, 0x52)),
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8),
            Padding = new Thickness(8, 6, 8, 8), Margin = new Thickness(0, 0, 0, 8)
        };
        shell.Child = body;
        _zoneTargets.Add((shell, folderId));
        return shell;
    }

    // Zone header: name, its meaning line, and controls to rename / set meaning / delete.
    private FrameworkElement BuildZoneHeader(int? folderId, string name, string? subfolder)
    {
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 4) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var title = new StackPanel();
        title.Children.Add(new TextBlock
        {
            Text = name, Foreground = Brushes.White, FontWeight = FontWeights.SemiBold, FontSize = 12
        });
        title.Children.Add(new TextBlock
        {
            Text = "→ music\\" + (SanitizeSubfolderName(subfolder) is { Length: > 0 } s ? s : "(racine)"),
            Foreground = new SolidColorBrush(Color.FromRgb(0x4A, 0xDE, 0x80)), FontSize = 9
        });
        Grid.SetColumn(title, 0);
        grid.Children.Add(title);

        var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        if (folderId is not null)
        {
            var id = folderId.Value;
            var rename = new Button { Content = "✎", Style = SmallButton(), ToolTip = "Renomer la zone" };
            rename.Click += (_, _) =>
            {
                var name2 = PromptForText("Renomer la zone", "Nom de la zone", _folders.First(f => f.Id == id).Name);
                if (string.IsNullOrWhiteSpace(name2)) return;
                _folderRepo.Update(id, name2.Trim(), _folders.First(f => f.Id == id).Subfolder);
                RefreshQueue();
            };
            var meaning = new Button { Content = "⚑", Style = SmallButton(), ToolTip = "Choisir le dossier de destination (sous-dossier de music\\)" };
            meaning.Click += (_, _) =>
            {
                var cur = _folders.First(f => f.Id == id);
                var sub = PromptForText("Destination", "Sous-dossier de music\\ (vide = racine)", cur.Subfolder ?? string.Empty);
                if (sub is null) return;
                _folderRepo.Update(id, cur.Name, SanitizeSubfolderName(sub) is { Length: > 0 } s2 ? s2 : null);
                RefreshQueue();
            };
            var del = new Button { Content = "✕", Style = SmallButton(), ToolTip = "Suprimer la zone (les fiches retournent a Non classé)" };
            del.Click += (_, _) =>
            {
                var current = _folders.First(f => f.Id == id);
                if (MessageBox.Show($"Suprimer la zone « {current.Name} » ? Ses fiches retourneront a « Non classé ».",
                        "Zone", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
                _folderRepo.Delete(id);
                RefreshQueue();
            };
            actions.Children.Add(rename);
            actions.Children.Add(meaning);
            actions.Children.Add(del);
        }
        Grid.SetColumn(actions, 1);
        grid.Children.Add(actions);
        return grid;
    }

    // A draggable link card: press-and-drag on its body moves it to the hovered zone. Interactive
    // children (buttons) never start a drag — see the pointer-origin guard.
    private FrameworkElement BuildCard(DownloadItem item, int? homeFolderId)
    {
        var card = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x45)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x24, 0x2A, 0x45)),
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(5),
            Padding = new Thickness(6, 3, 6, 3), Margin = new Thickness(0, 0, 0, 4), Cursor = Cursors.SizeAll
        };

        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var badge = new TextBlock
        {
            Text = StatusSymbol(item.Status), Foreground = StatusBrush(item.Status),
            FontSize = 12, Width = 18, VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(badge, 0);
        row.Children.Add(badge);

        var label = new TextBlock
        {
            Text = DisplayOf(item), Foreground = Brushes.White, FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis,
            ToolTip = item.Url
        };
        Grid.SetColumn(label, 1);
        row.Children.Add(label);

        var actions = new StackPanel { Orientation = Orientation.Horizontal };
        var open = new Button { Content = "↗", Style = SmallButton(), ToolTip = "Ouvrir dans le navigateur" };
        open.Click += (_, _) => OpenInBrowser(item.Url);
        var done = new Button { Content = "✓", Style = SmallButton(), ToolTip = "Marquer comme terminé" };
        done.Click += (_, _) =>
        {
            var next = item.Status == DownloadStatus.Done ? DownloadStatus.Queued : DownloadStatus.Done;
            _repository.UpdateStatus(item.Id, next);
            RefreshQueue();
        };
        var title = new Button { Content = "✎", Style = SmallButton(), ToolTip = "Renomer la fiche" };
        title.Click += (_, _) =>
        {
            var t = PromptForText("Renomer la fiche", "Titre", DisplayOf(item));
            if (t is null) return;
            _repository.UpdateTitle(item.Id, t.Trim());
            RefreshQueue();
        };
        var del = new Button { Content = "✕", Style = SmallButton(), ToolTip = "Retirer de la file" };
        del.Click += (_, _) => { _repository.Delete(item.Id); RefreshQueue(); };
        actions.Children.Add(open);
        actions.Children.Add(done);
        actions.Children.Add(title);
        actions.Children.Add(del);
        Grid.SetColumn(actions, 2);
        row.Children.Add(actions);

        card.Child = row;
        AttachDragToCard(card, item.Id, homeFolderId);
        return card;
    }

    // --- Card drag-and-drop ----------------------------------------------------
    // Press on the card body (never a button) grabs the mouse and highlights the zone the cursor
    // is over; releasing drops the card there. Zone membership, not a floating ghost, is what moves.

    private void AttachDragToCard(Border card, int itemId, int? homeFolderId)
    {
        Point? start = null;
        int? hovered = homeFolderId;

        card.PreviewMouseLeftButtonDown += (s, e) =>
        {
            if (IsInteractiveOrigin(e.OriginalSource as DependencyObject)) return; // let buttons act
            start = e.GetPosition(card);
            hovered = homeFolderId;
            card.CaptureMouse();
            _dragActive = true;
            e.Handled = true;
        };

        card.MouseMove += (s, e) =>
        {
            if (!_dragActive || start is null || e.LeftButton != MouseButtonState.Pressed) return;
            var local = e.GetPosition(card);
            if (Math.Abs(local.X - start.Value.X) + Math.Abs(local.Y - start.Value.Y) < 4) return; // not a real drag yet

            var cursor = card.PointToScreen(local);
            var hit = HitTestZone(cursor);
            HighlightZone(hit);
            hovered = hit;
        };

        card.MouseLeftButtonUp += (s, e) =>
        {
            if (!_dragActive) return;
            card.ReleaseMouseCapture();
            _dragActive = false;
            HighlightZone(null);
            if (hovered != homeFolderId)
            {
                _repository.UpdateFolder(itemId, hovered);
                RefreshQueue();
            }
            e.Handled = true;
        };
    }

    // Which zone shell's screen rectangle contains the given screen point (null = none).
    private int? HitTestZone(Point screenPoint)
    {
        foreach (var (shell, folderId) in _zoneTargets)
        {
            var topLeft = shell.PointToScreen(new Point(0, 0));
            if (screenPoint.X >= topLeft.X && screenPoint.X <= topLeft.X + shell.ActualWidth &&
                screenPoint.Y >= topLeft.Y && screenPoint.Y <= topLeft.Y + shell.ActualHeight)
                return folderId;
        }
        return null;
    }

    // Paints the drop-target zone with the accent border so the release destination is visible.
    private void HighlightZone(int? folderId)
    {
        foreach (var (shell, id) in _zoneTargets)
        {
            var on = id is not null && folderId is not null && id.Value == folderId.Value;
            shell.BorderBrush = on
                ? new SolidColorBrush(Color.FromRgb(0x3B, 0x82, 0xF6))
                : new SolidColorBrush(Color.FromRgb(0x2A, 0x33, 0x52));
            shell.BorderThickness = new Thickness(on ? 2 : 1);
        }
    }

    // --- Zone creation ---------------------------------------------------------

    private void NewZoneButton_OnClick(object sender, RoutedEventArgs e)
    {
        var name = PromptForText("Nouvelle zone de placement", "Nom de la zone", string.Empty);
        if (string.IsNullOrWhiteSpace(name)) return;
        var sub = SanitizeSubfolderName(name);
        try { Directory.CreateDirectory(Path.Combine(MusicLibraryScanner.MusicFolder, sub)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        _folderRepo.Add(new DownloadFolder { Name = name.Trim(), Subfolder = sub is { Length: > 0 } ? sub : null, SortOrder = _folderRepo.GetAll().Count });
        RefreshQueue();
    }

    // --- Display helpers -------------------------------------------------------

    private static string DisplayOf(DownloadItem item) =>
        !string.IsNullOrWhiteSpace(item.Title) ? item.Title : DeriveTitleFromUrl(item.Url);

    private static string DeriveTitleFromUrl(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri is not null)
        {
            var seg = (uri.Segments.LastOrDefault(s => s.Trim('/').Length > 0) ?? string.Empty).Trim('/');
            return seg.Length > 0 ? Uri.UnescapeDataString(seg) : uri.Host;
        }
        return url;
    }

    // Where an item downloads: its zone's sub-folder under music/, or the music/ root when unfiled.
    private string ResolveDownloadFolderForItem(DownloadItem item)
    {
        var zone = item.FolderId is { } fid ? _folders.FirstOrDefault(f => f.Id == fid) : null;
        var sub = SanitizeSubfolderName(zone?.Subfolder);
        return sub.Length == 0
            ? MusicLibraryScanner.MusicFolder
            : Path.Combine(MusicLibraryScanner.MusicFolder, sub);
    }

    // Coerces a user-typed string into one safe path segment under music/ (drops pasted paths,
    // traversal, illegal characters). Empty result = the music/ root.
    private static string SanitizeSubfolderName(string? raw)
    {
        var name = (raw ?? string.Empty).Trim().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (name.Length == 0) return string.Empty;
        name = name.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[^1];
        foreach (var bad in Path.GetInvalidFileNameChars())
            name = name.Replace(bad, '_');
        return name is "." or ".." ? string.Empty : name;
    }

    // Small modal text prompt; returns null on cancel.
    private string? PromptForText(string title, string label, string initial)
    {
        var window = new Window
        {
            Title = title, Width = 320, Height = 160, ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, Owner = this
        };
        var textBox = new TextBox { Text = initial, Margin = new Thickness(0, 0, 0, 8) };
        var ok = new Button { Content = "OK", Width = 80, IsDefault = true };
        ok.Click += (_, _) => window.DialogResult = true;
        var cancel = new Button { Content = "Annuler", Width = 80, Margin = new Thickness(8, 0, 0, 0), IsCancel = true };
        window.Content = new StackPanel
        {
            Margin = new Thickness(16),
            Children =
            {
                new TextBlock { Text = label, Margin = new Thickness(0, 0, 0, 6) },
                textBox,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
                    Children = { ok, cancel }
                }
            }
        };
        textBox.Focus();
        return window.ShowDialog() == true ? textBox.Text : null;
    }

    // --- Tool settings -------------------------------------------------------

    private void BrowseToolButton_OnClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "yt-dlp|yt-dlp.exe|Exécutables|*.exe|Tous|*.*" };
        if (dialog.ShowDialog() == true) ToolPathInput.Text = dialog.FileName;
    }

    private void SaveSettingsButton_OnClick(object sender, RoutedEventArgs e)
    {
        var path = ToolPathInput.Text.Trim();
        _config.DownloaderToolPath = path.Length == 0 ? null : path;
        var args = ArgumentsInput.Text.Trim();
        _config.DownloaderArguments = args.Length == 0 ? "-x --audio-format mp3" : args;
        _configService.Save(_config);
        ToolStatusText.Text = "  Enregistré.";
    }

    // --- Downloading ---------------------------------------------------------

    private async void AutoToggle_OnClick(object sender, RoutedEventArgs e)
    {
        _config.DownloaderAutoMode = AutoToggle.IsChecked == true;
        _configService.Save(_config);
        if (_config.DownloaderAutoMode) await ProcessQueueAsync();
    }

    private async void DownloadNowButton_OnClick(object sender, RoutedEventArgs e) => await ProcessQueueAsync();

    // Drains all queued items sequentially through the external tool, then refreshes the library.
    private async Task ProcessQueueAsync()
    {
        if (_isProcessing) return;

        if (!HasTool())
        {
            MessageBox.Show(
                "yt-dlp est introuvable. Lancez setup-tools.ps1 (à côté de l'application) pour l'installer, " +
                "ou indiquez son chemin dans « Réglages de l'outil ».",
                "Téléchargements", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (!_repository.GetAll().Any(i => i.Status == DownloadStatus.Queued))
        {
            StatusText.Text = "Rien à télécharger.";
            return;
        }

        _isProcessing = true;
        try
        {
            var toolPath = ResolveToolPath();
            var ffmpegDir = ResolveFfmpegDir();
            var args = string.IsNullOrWhiteSpace(_config.DownloaderArguments) ? "-x --audio-format mp3" : _config.DownloaderArguments;
            var anyDownloaded = false;

            while (true)
            {
                var next = _repository.GetAll().FirstOrDefault(i => i.Status == DownloadStatus.Queued);
                if (next is null) break;

                StatusText.Text = $"Téléchargement : {Shorten(DisplayOf(next))}…";
                var result = await _downloader.DownloadAsync(toolPath, args, next.Url, ResolveDownloadFolderForItem(next), ffmpegDir);

                if (result.Success)
                {
                    _repository.UpdateStatus(next.Id, DownloadStatus.Done);
                    anyDownloaded = true;
                }
                else
                {
                    _repository.UpdateStatus(next.Id, DownloadStatus.Failed);
                    StatusText.Text = $"Échec : {Shorten(result.Error ?? "erreur inconnue")}";
                }
                RefreshQueue();
            }

            if (anyDownloaded)
            {
                _scanner.ScanAndSync();
                LibraryChanged?.Invoke();
                if (!StatusText.Text.StartsWith("Échec")) StatusText.Text = "Téléchargements terminés.";
            }
        }
        finally
        {
            _isProcessing = false;
        }
    }

    private bool HasTool()
    {
        if (!string.IsNullOrWhiteSpace(_config.DownloaderToolPath) && File.Exists(_config.DownloaderToolPath)) return true;
        if (File.Exists(Path.Combine(AppContext.BaseDirectory, "tools", "yt-dlp.exe"))) return true;
        return ExistsOnPath("yt-dlp.exe");
    }

    private string ResolveToolPath()
    {
        if (!string.IsNullOrWhiteSpace(_config.DownloaderToolPath) && File.Exists(_config.DownloaderToolPath))
            return _config.DownloaderToolPath!;
        var local = Path.Combine(AppContext.BaseDirectory, "tools", "yt-dlp.exe");
        if (File.Exists(local)) return local;
        return "yt-dlp"; // resolved via PATH by the OS
    }

    private static string? ResolveFfmpegDir()
    {
        var toolsDir = Path.Combine(AppContext.BaseDirectory, "tools");
        return File.Exists(Path.Combine(toolsDir, "ffmpeg.exe")) ? toolsDir : null;
    }

    private static bool ExistsOnPath(string exe)
    {
        var paths = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator);
        foreach (var p in paths)
        {
            try { if (File.Exists(Path.Combine(p.Trim(), exe))) return true; }
            catch { /* malformed PATH entry */ }
        }
        return false;
    }

    // --- Manual queue actions ------------------------------------------------

    private static void OpenInBrowser(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch
        {
            MessageBox.Show("Impossible d'ouvrir ce lien.", "Téléchargements",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void CopyQueuedButton_OnClick(object sender, RoutedEventArgs e)
    {
        var builder = new StringBuilder();
        foreach (var item in _repository.GetAll())
        {
            if (item.Status == DownloadStatus.Queued) builder.AppendLine(item.Url);
        }

        var text = builder.ToString().TrimEnd();
        if (text.Length == 0)
        {
            StatusText.Text = "Aucun lien en file à copier.";
            return;
        }

        try
        {
            Clipboard.SetText(text);
            StatusText.Text = "Liens en file copiés.";
        }
        catch
        {
            StatusText.Text = "Copie impossible.";
        }
    }

    private void ClearDoneButton_OnClick(object sender, RoutedEventArgs e)
    {
        _repository.DeleteByStatus(DownloadStatus.Done);
        RefreshQueue();
    }

    private void ClearAllButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show("Vider toute la file ?", "Téléchargements",
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;

        foreach (var item in _repository.GetAll()) _repository.Delete(item.Id);
        RefreshQueue();
    }

    private void CloseButton_OnClick(object sender, RoutedEventArgs e) => Hide();

    // --- Helpers -------------------------------------------------------------

    private static string Shorten(string s)
    {
        s = s.Replace("\r", " ").Replace("\n", " ").Trim();
        return s.Length <= 60 ? s : s[..57] + "…";
    }

    private static string StatusSymbol(DownloadStatus status) => status switch
    {
        DownloadStatus.Done => "✓",
        DownloadStatus.Failed => "✕",
        _ => "○"
    };

    private static Brush StatusBrush(DownloadStatus status) => status switch
    {
        DownloadStatus.Done => new SolidColorBrush(Color.FromRgb(0x4A, 0xDE, 0x80)),
        DownloadStatus.Failed => new SolidColorBrush(Color.FromRgb(0xEF, 0x44, 0x44)),
        _ => new SolidColorBrush(Color.FromRgb(0xAA, 0xAA, 0xAA))
    };

    private Style SmallButton() => (Style)FindResource("DlSmallButton");

    // Lets MainWindow truly close the window on real app exit.
    public void ForceClose()
    {
        _isExiting = true;
        Close();
    }

    // Closing (e.g. Alt+F4) hides instead of tearing down, so it can be reopened.
    private void DownloaderWindow_OnClosing(object? sender, CancelEventArgs e)
    {
        if (_isExiting) return;
        e.Cancel = true;
        Hide();
    }
}
