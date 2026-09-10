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
    private readonly MusicLibraryScanner _scanner = new(new MusicTrackRepository(new SqliteConnectionFactory()));
    private readonly ExternalDownloader _downloader = new();

    private readonly AppConfig _config;
    private readonly ConfigService _configService;

    private bool _isProcessing;
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

    private void RefreshQueue()
    {
        QueuePanel.Children.Clear();
        var items = _repository.GetAll();
        EmptyText.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        foreach (var item in items)
        {
            QueuePanel.Children.Add(BuildRow(item));
        }
    }

    private FrameworkElement BuildRow(DownloadItem item)
    {
        var row = new Grid { Margin = new Thickness(0, 0, 0, 5) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var badge = new TextBlock
        {
            Text = StatusSymbol(item.Status),
            Foreground = StatusBrush(item.Status),
            FontSize = 13,
            Width = 20,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(badge, 0);
        row.Children.Add(badge);

        var url = new TextBlock
        {
            Text = item.Url,
            Foreground = Brushes.White,
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(0, 0, 6, 0),
            ToolTip = item.Url
        };
        Grid.SetColumn(url, 1);
        row.Children.Add(url);

        var actions = new StackPanel { Orientation = Orientation.Horizontal };
        var openButton = new Button { Content = "↗", Style = SmallButton(), ToolTip = "Ouvrir dans le navigateur" };
        openButton.Click += (_, _) => OpenInBrowser(item.Url);
        var doneButton = new Button { Content = "✓", Style = SmallButton(), ToolTip = "Marquer comme terminé" };
        doneButton.Click += (_, _) =>
        {
            var next = item.Status == DownloadStatus.Done ? DownloadStatus.Queued : DownloadStatus.Done;
            _repository.UpdateStatus(item.Id, next);
            RefreshQueue();
        };
        var removeButton = new Button { Content = "✕", Style = SmallButton(), ToolTip = "Retirer de la file" };
        removeButton.Click += (_, _) => { _repository.Delete(item.Id); RefreshQueue(); };
        actions.Children.Add(openButton);
        actions.Children.Add(doneButton);
        actions.Children.Add(removeButton);
        Grid.SetColumn(actions, 2);
        row.Children.Add(actions);

        return row;
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

                StatusText.Text = $"Téléchargement : {Shorten(next.Url)}…";
                var result = await _downloader.DownloadAsync(toolPath, args, next.Url, MusicLibraryScanner.MusicFolder, ffmpegDir);

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
