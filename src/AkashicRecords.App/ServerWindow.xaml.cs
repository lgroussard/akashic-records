using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using AkashicRecords.App.Sync;
using AkashicRecords.Sync;

namespace AkashicRecords.App;

/// <summary>
/// The state of the hub (the mini-PC), from its GET /server: how full its disk is and with what, whether last
/// night's copy to the external disk went well, which devices are paired and when each last synchronised, and the
/// downloads and computations it is working on. Read-only; refreshed every 30 s while shown.
/// </summary>
public partial class ServerWindow : Window
{
    private static Brush B(byte r, byte g, byte b) { var x = new SolidColorBrush(Color.FromRgb(r, g, b)); x.Freeze(); return x; }
    private static readonly Brush CardBg = B(0x2A, 0x2A, 0x45);
    private static readonly Brush Track = B(0x20, 0x20, 0x38);
    private static readonly Brush Muted = B(0x9A, 0x9A, 0xB5);
    private static readonly Brush Faint = B(0x6B, 0x6B, 0x85);
    private static readonly Brush Warn = B(0xF8, 0x71, 0x71);
    private static readonly Brush Late = B(0xFB, 0xBF, 0x24);
    private static readonly Brush Good = B(0x4A, 0xDE, 0x80);

    /// <summary>What each kind of file is called here, and its colour in the disk bar (same as on the phone).</summary>
    internal static readonly (string Kind, string Label, Brush Color)[] Kinds =
    {
        ("music", "Musique", B(0x3B, 0x82, 0xF6)),
        ("photos", "Photos des Archives", B(0xF4, 0x72, 0xB6)),
        ("books", "Livres", B(0xF5, 0x9E, 0x0B)),
        ("references", "Références", B(0x8B, 0x5C, 0xF6)),
        ("other", "Autres images (couvertures, journal…)", B(0x14, 0xB8, 0xA6)),
        ("hub_files", "Fichiers du serveur (agrandissements…)", B(0x64, 0x74, 0x8B)),
        ("previews", "Aperçus des photos", B(0xA3, 0xA3, 0xC2)),
        ("tools", "Outils et modèles", B(0x47, 0x55, 0x69)),
    };

    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(30) };
    private HubServerStatus? _status;
    private string? _error;
    private DateTime _readAt;
    private string? _browsePath;   // null: the overview; else the folder of the server shown in the browser
    private HubDiskView? _disk;
    private string? _diskError;

    public ServerWindow()
    {
        InitializeComponent();
        _timer.Tick += async (_, _) =>
        {
            if (_browsePath is null) await RefreshAsync();
            else if (_disk?.Refreshing == true) await BrowseAsync(_browsePath); // a new measure is on its way
        };
        IsVisibleChanged += async (_, _) =>
        {
            if (IsVisible) { await RefreshAsync(); _timer.Start(); }
            else _timer.Stop();
        };
        Render();
    }

    public async Task RefreshAsync()
    {
        var s = SyncService.Instance.Settings;
        if (!s.IsPaired) { _error = "Cet ordinateur n'est pas relié au serveur (Réglages → Synchronisation)."; Render(); return; }
        try
        {
            using var hub = new HubClient(s.HubUrl, s.Token);
            _status = await hub.ServerAsync();
            _error = null;
            _readAt = DateTime.Now;
        }
        catch (Exception e)
        {
            _error = "Serveur injoignable : " + e.Message;
        }
        Render();
    }

    // ------------------------------------------------------------------ drawing

    private void Render()
    {
        LeftPanel.Children.Clear();
        RightPanel.Children.Clear();
        // The browser takes the whole width; the overview has two columns.
        Grid.SetColumnSpan(LeftPanel, _browsePath is null ? 1 : 3);
        RightPanel.Visibility = _browsePath is null ? Visibility.Visible : Visibility.Collapsed;
        if (_browsePath is not null) { RenderBrowse(); return; }
        if (_status is null)
        {
            LeftPanel.Children.Add(Text(_error ?? "Lecture de l'état du serveur…", 13, _error is null ? Muted : Warn));
            Footer.Text = "";
            return;
        }
        var st = _status;
        Subtitle.Text = $"  ·  {st.Host}";

        // Disk: one bar, what fills it, what is left.
        var disk = Card(LeftPanel, "DISQUE DU SERVEUR");
        if (st.Disk is { } d)
        {
            var used = d.Total - d.Free;
            disk.Children.Add(Text($"{Size(used)} utilisés sur {Size(d.Total)}  ·  {Size(d.Free)} libres", 15, Brushes.White, bold: true));
            disk.Children.Add(Bar(d.Total, st.Storage, used));
            if (d.Free < d.Total / 10) disk.Children.Add(Text("Il reste moins de 10 % de place : pense à faire du tri.", 12, Warn));
        }
        foreach (var (kind, label, color) in Kinds)
        {
            var k = st.Storage.FirstOrDefault(x => x.Kind == kind);
            if (k is null || k.Bytes == 0) continue;
            disk.Children.Add(Legend(color, label, Size(k.Bytes) + (k.Files > 0 ? $"  ·  {k.Files} fichiers" : "")));
        }
        if (st.Disk is { } d2)
        {
            var rest = d2.Total - d2.Free - Kinds.Sum(k => st.Storage.FirstOrDefault(x => x.Kind == k.Kind)?.Bytes ?? 0);
            if (rest > 0) disk.Children.Add(Legend(Faint, "Système, fichiers perso et le reste", Size(rest)));
        }
        // Files deleted in the app, waiting before they are removed for good (the hub's BlobCleaner).
        if (st.Cleanup is { } cl)
        {
            var line = cl.PendingFiles > 0
                ? $"Fichiers supprimés en attente : {cl.PendingFiles} ({Size(cl.PendingBytes)}), effacés pour de bon {cl.GraceDays} jours après leur suppression"
                  + (cl.NextAt is { } next && DateTimeOffset.TryParse(next, out var at) ? $" (les premiers le {at.ToLocalTime():dd/MM})" : "")
                : "Aucun fichier supprimé en attente.";
            disk.Children.Add(Text(line, 12, Muted));
            if (cl.Brake is not null) disk.Children.Add(Text("Ménage suspendu par sécurité : " + cl.Brake, 12, Late));
        }
        var browse = new Button { Content = "Parcourir les dossiers  ›", Style = (Style)FindResource("Primary"), Margin = new Thickness(0, 10, 0, 0), HorizontalAlignment = HorizontalAlignment.Left };
        browse.Click += async (_, _) => await BrowseAsync("/");
        disk.Children.Add(browse);

        // Last copy to the external disk.
        var backup = Card(LeftPanel, "SAUVEGARDE SUR LE DISQUE EXTERNE");
        var b = st.Backup;
        var (state, color2) = b.State switch
        {
            "ok" => ("Faite", Good),
            "late" => ("En retard : la copie de cette nuit n'a pas eu lieu", Late),
            "absent" => ("Disque de sauvegarde non branché", Warn),
            "failed" => ("Échec", Warn),
            _ => ("Inconnue : le script de sauvegarde du mini-PC doit être mis à jour", Muted),
        };
        backup.Children.Add(Text(state, 15, color2, bold: true));
        if (b.At is not null) backup.Children.Add(Text($"Dernier passage : {When(b.At)}", 12, Muted));
        if (b.State != "ok" && b.LastOkAt is { Length: > 0 }) backup.Children.Add(Text($"Dernière copie réussie : {When(b.LastOkAt)}", 12, Muted));
        if (!string.IsNullOrEmpty(b.Message)) backup.Children.Add(Text(b.Message, 12, Muted));
        if (b.Bytes is { } bytes) backup.Children.Add(Text($"Copie : {Size(bytes)}, {b.Files} fichiers du catalogue" + (b.Seconds is { } sec ? $", en {Duration(sec)}" : ""), 12, Muted));
        if (b.DiskTotal is { } dt && b.DiskFree is { } df)
            backup.Children.Add(Text($"Disque externe : {Size(df)} libres sur {Size(dt)}", 12, df < dt / 10 ? Warn : Muted));

        // Devices.
        var devices = Card(RightPanel, "APPAREILS");
        foreach (var dev in st.Devices.Where(x => !x.Revoked))
        {
            var row = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
            row.Children.Add(Text(dev.Hub ? "Le serveur lui-même (téléchargements)" : dev.Name, 13, Brushes.White, bold: true));
            var seen = dev.Hub ? null : dev.LastSeenAt is null ? "jamais vu" : "vu " + When(dev.LastSeenAt);
            var change = dev.LastChangeAt is null ? "aucune modification envoyée" : "dernière modification " + When(dev.LastChangeAt);
            row.Children.Add(Text(string.Join("  ·  ", new[] { seen, change }.Where(x => x is not null)), 12, Muted));
            devices.Children.Add(row);
        }
        var revoked = st.Devices.Count(x => x.Revoked);
        if (revoked > 0) devices.Children.Add(Text($"{revoked} appareil(s) retiré(s)", 11, Faint));

        // The hub's own work.
        var work = Card(RightPanel, "TRAVAUX DU SERVEUR");
        work.Children.Add(Text("Téléchargements de musique : " + Work(st.Downloads), 12, st.Downloads.Failed > 0 ? Late : Muted));
        work.Children.Add(Text("Agrandissements d'images : " + Work(st.Compute), 12, st.Compute.Failed > 0 ? Late : Muted));

        var catalogue = Card(RightPanel, "CATALOGUE");
        var total = st.Entities.Values.Sum();
        catalogue.Children.Add(Text($"{total:N0} éléments synchronisés ({st.BlobFiles:N0} fichiers, {Size(st.BlobBytes)})", 12, Muted));

        Footer.Text = $"Hub {st.Version}  ·  allumé depuis {Since(st.StartedAt)}  ·  lu à {_readAt:HH:mm}" + (_error is null ? "" : $"  ·  {_error}");
    }

    // ------------------------------------------------------------------ the folder browser (read-only)

    /// <summary>Colours of a folder's sub-folders, in size order, in the bar and the list.</summary>
    private static readonly Brush[] FolderColors =
    {
        B(0x3B, 0x82, 0xF6), B(0xF4, 0x72, 0xB6), B(0xF5, 0x9E, 0x0B), B(0x8B, 0x5C, 0xF6), B(0x14, 0xB8, 0xA6),
        B(0xEF, 0x44, 0x44), B(0x84, 0xCC, 0x16), B(0x06, 0xB6, 0xD4), B(0xE8, 0x79, 0xF9), B(0xFB, 0x92, 0x3C),
    };

    internal async Task BrowseAsync(string path, bool refresh = false)
    {
        _browsePath = path;
        var s = SyncService.Instance.Settings;
        try
        {
            using var hub = new HubClient(s.HubUrl, s.Token);
            if (refresh) await hub.RefreshServerDiskAsync();
            _disk = await hub.ServerDiskAsync(path);
            _browsePath = _disk.Path; // a folder gone since: the deepest one still known
            _diskError = null;
        }
        catch (Exception e)
        {
            _diskError = "Serveur injoignable : " + e.Message;
        }
        Render();
    }

    private void RenderBrowse()
    {
        var top = new DockPanel { Margin = new Thickness(0, 0, 0, 12) };
        var back = new Button { Content = "‹ Vue d'ensemble", Style = (Style)FindResource("Btn"), Margin = new Thickness(0, 0, 12, 0) };
        back.Click += (_, _) => { _browsePath = null; Render(); };
        top.Children.Add(back);
        var refresh = new Button { Content = _disk?.Refreshing == true ? "Mesure en cours…" : "↻ Mesurer à nouveau", Style = (Style)FindResource("Btn"), IsEnabled = _disk?.Refreshing != true };
        refresh.Click += async (_, _) => await BrowseAsync(_browsePath ?? "/", refresh: true);
        DockPanel.SetDock(refresh, Dock.Right);
        top.Children.Add(refresh);

        // Where we are: every level is a way back up.
        var crumbs = new WrapPanel { VerticalAlignment = VerticalAlignment.Center };
        var parts = (_browsePath ?? "/").Split('/', StringSplitOptions.RemoveEmptyEntries);
        void Crumb(string label, string target, bool current)
        {
            var b = new Button { Content = label, Style = (Style)FindResource("Flat"), FontWeight = current ? FontWeights.SemiBold : FontWeights.Normal, Foreground = current ? Brushes.White : Muted };
            b.Click += async (_, _) => await BrowseAsync(target);
            crumbs.Children.Add(b);
        }
        Crumb(_disk?.Root is { Length: > 0 } r ? r : "/", "/", parts.Length == 0);
        for (var i = 0; i < parts.Length; i++)
        {
            crumbs.Children.Add(new TextBlock { Text = "›", Foreground = Faint, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(2, 0, 2, 0) });
            Crumb(parts[i], "/" + string.Join('/', parts.Take(i + 1)), i == parts.Length - 1);
        }
        top.Children.Add(crumbs);
        LeftPanel.Children.Add(top);

        if (_diskError is not null) LeftPanel.Children.Add(Text(_diskError, 12, Warn));
        var d = _disk;
        if (d is null) { LeftPanel.Children.Add(Text("Lecture…", 13, Muted)); return; }
        if (d.State == "none")
        {
            LeftPanel.Children.Add(Text(d.Refreshing
                ? "Première mesure du disque en cours : quelques minutes. L'écran se mettra à jour tout seul."
                : "Le disque n'a pas encore été mesuré. Touche « Mesurer à nouveau » (il faut que le serveur ait été mis à jour).", 13, Muted));
            return;
        }

        var head = Card(LeftPanel, "CE DOSSIER");
        head.Children.Add(Text($"{Size(d.Size)}  ·  {d.Files:N0} fichiers", 15, Brushes.White, bold: true));
        if (d.Catalogue && d.Path.EndsWith(DiskOutsideName))
            head.Children.Add(Text("Fichiers que le serveur garde sans fiche dans l'app : images envoyées à Agrandir, photos supprimées… Ceux que plus rien n'utilise sont effacés 30 jours après.", 12, Muted));
        else if (d.Catalogue)
            head.Children.Add(Text("Les fichiers d'Akashic, rangés comme dans l'app (sur le disque, ils sont nommés par empreinte).", 12, Muted));

        // The graph: what fills this folder, biggest first.
        var segments = d.Folders.Where(f => f.Size > 0).Select((f, i) => (f.Name, f.Size, Color: FolderColors[i % FolderColors.Length])).ToList();
        var loose = d.Largest.Sum(f => f.Size) + d.OtherFilesBytes;
        if (d.SmallFoldersBytes + loose > 0) segments.Add(("Fichiers et petits dossiers", d.SmallFoldersBytes + loose, Faint));
        if (d.Size > 0 && segments.Count > 0)
        {
            var bar = new Grid { Height = 18, Margin = new Thickness(0, 8, 0, 4) };
            foreach (var (name, size, color) in segments)
            {
                bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(size, GridUnitType.Star) });
                var seg = new Border { Background = color, ToolTip = $"{name} : {Size(size)}", BorderBrush = CardBg, BorderThickness = new Thickness(0, 0, 1, 0) };
                Grid.SetColumn(seg, bar.ColumnDefinitions.Count - 1);
                bar.Children.Add(seg);
            }
            head.Children.Add(new Border { CornerRadius = new CornerRadius(4), ClipToBounds = true, Child = bar });
        }
        head.Children.Add(Text($"Mesuré {When(d.At ?? "")}" + (d.Seconds is { } sec ? $" (en {Duration(sec)})" : "") + (d.Refreshing ? "  ·  nouvelle mesure en cours…" : ""), 11, Faint));

        // Sub-folders: click to go in.
        if (d.Folders.Count > 0)
        {
            var list = Card(LeftPanel, "DOSSIERS");
            for (var i = 0; i < d.Folders.Count; i++)
            {
                var f = d.Folders[i];
                var color = f.Size > 0 ? FolderColors[i % FolderColors.Length] : Faint;
                var note = f.Mount ? "autre disque ou dossier système, non mesuré" : f.Denied ? "illisible" : $"{Size(f.Size)}  ·  {f.Files:N0} fichiers";
                var row = Row(color, "📁  " + f.Name, note, d.Size > 0 ? (double)f.Size / d.Size : 0, clickable: f.HasChildren && !f.Mount && !f.Denied);
                var target = (_browsePath == "/" ? "" : _browsePath) + "/" + f.Name;
                if (f.HasChildren && !f.Mount && !f.Denied) row.MouseLeftButtonUp += async (_, _) => await BrowseAsync(target);
                list.Children.Add(row);
            }
            if (d.SmallFolders > 0) list.Children.Add(Text($"+ {d.SmallFolders} petits dossiers (moins de 1 Mo chacun) : {Size(d.SmallFoldersBytes)}", 11, Faint));
        }

        if (d.Largest.Count > 0)
        {
            var files = Card(LeftPanel, "PLUS GROS FICHIERS DE CE DOSSIER");
            foreach (var f in d.Largest) files.Children.Add(FileRow(f));
            if (d.OtherFilesBytes > 0) files.Children.Add(Text($"+ les autres fichiers : {Size(d.OtherFilesBytes)}", 11, Faint));
        }
        Footer.Text = "Lecture seule : la mesure est faite chaque nuit par le mini-PC, ou quand tu la demandes.";
    }

    private const string DiskOutsideName = "Fichiers du serveur hors catalogue"; // the hub's DiskIndex.OutsideName

    // Previews already fetched, by hash (a picture's preview never changes).
    private static readonly Dictionary<string, System.Windows.Media.Imaging.BitmapImage?> Thumbs = new();

    private static string IconOf(string kind) => kind switch
    {
        "image" => "🖼", "music" => "🎵", "book" => "📖", "video" => "🎬", "document" => "📄", "unknown" => "🧩", _ => "📦",
    };

    // A file: its preview (pictures of the catalogue) or an icon, what it is (its name in Akashic for a blob named
    // by its hash), where it is in Akashic, its size.
    private FrameworkElement FileRow(HubDiskFile f)
    {
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 4) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(52) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var box = new Border { Width = 44, Height = 44, CornerRadius = new CornerRadius(5), Background = Track, ClipToBounds = true, HorizontalAlignment = HorizontalAlignment.Left };
        box.Child = new TextBlock { Text = IconOf(f.Kind), FontSize = 18, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Foreground = Muted };
        grid.Children.Add(box);
        if (f.Hash is { } hash && f.Kind is "image" or "unknown" or "music") _ = LoadThumb(hash, box);

        var label = f.Label ?? (f.Hash is null ? f.Name : "Fichier du serveur (hors catalogue)");
        var name = label.Contains('/') ? label[(label.LastIndexOf('/') + 1)..] : label;
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = name, Foreground = Brushes.White, FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis });
        var where = f.Label is { } l && l.Contains('/') ? "dans Akashic : " + l[..l.LastIndexOf('/')] : f.Hash is not null ? f.Name[..12] + "…" : null;
        if (where is not null) text.Children.Add(new TextBlock { Text = where, Foreground = Faint, FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis });
        // Waiting to be removed for good (the hub's clean-up), or kept for shared computing.
        if (f.Note is { } note)
            text.Children.Add(new TextBlock { Text = note, Foreground = note.StartsWith("Supprimé") ? Late : Muted, FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis });
        Grid.SetColumn(text, 1); grid.Children.Add(text);

        var size = new TextBlock { Text = Size(f.Size), Foreground = Muted, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
        Grid.SetColumn(size, 2); grid.Children.Add(size);
        return new Border { Child = grid, Padding = new Thickness(6, 3, 6, 3) };
    }

    private static async Task LoadThumb(string hash, Border box)
    {
        if (!Thumbs.TryGetValue(hash, out var image))
        {
            try
            {
                var s = SyncService.Instance.Settings;
                using var hub = new HubClient(s.HubUrl, s.Token);
                var bytes = await hub.ThumbAsync(hash);
                if (bytes is not null)
                {
                    image = new System.Windows.Media.Imaging.BitmapImage();
                    image.BeginInit();
                    image.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                    image.DecodePixelWidth = 96;
                    image.StreamSource = new System.IO.MemoryStream(bytes);
                    image.EndInit();
                    image.Freeze();
                }
            }
            catch (Exception) { return; } // hub out of reach: tried again next time
            Thumbs[hash] = image;
        }
        if (image is not null) box.Child = new Image { Source = image, Stretch = Stretch.UniformToFill };
    }

    // One line of the browser: colour, name, size, and a thin bar for its share of the folder.
    private static FrameworkElement Row(Brush color, string name, string value, double share, bool clickable)
    {
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 2) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(160) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(230) });
        grid.Children.Add(new Border { Width = 10, Height = 10, CornerRadius = new CornerRadius(2), Background = color, VerticalAlignment = VerticalAlignment.Center });
        var label = new TextBlock { Text = name, Foreground = Brushes.White, FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(label, 1); grid.Children.Add(label);
        var track = new Grid { Height = 6, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 8, 0) };
        track.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(share, 0.001), GridUnitType.Star) });
        track.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(1 - share, 0.001), GridUnitType.Star) });
        track.Children.Add(new Border { Background = color, CornerRadius = new CornerRadius(3) });
        var rest = new Border { Background = Track, CornerRadius = new CornerRadius(3) };
        Grid.SetColumn(rest, 1); track.Children.Add(rest);
        Grid.SetColumn(track, 2); grid.Children.Add(track);
        var v = new TextBlock { Text = value + (clickable ? "   ›" : ""), Foreground = Muted, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right };
        Grid.SetColumn(v, 3); grid.Children.Add(v);
        var border = new Border { Child = grid, Padding = new Thickness(6, 5, 6, 5), CornerRadius = new CornerRadius(5), Background = Brushes.Transparent, Cursor = clickable ? Cursors.Hand : null };
        if (clickable)
        {
            border.MouseEnter += (_, _) => border.Background = Track;
            border.MouseLeave += (_, _) => border.Background = Brushes.Transparent;
        }
        return border;
    }

    private static string Work(HubWork w)
    {
        var parts = new List<string>();
        if (w.Running > 0) parts.Add($"{w.Running} en cours");
        if (w.Queued > 0) parts.Add($"{w.Queued} en attente");
        if (w.Failed > 0) parts.Add($"{w.Failed} en échec");
        return parts.Count == 0 ? "rien en cours" : string.Join(", ", parts);
    }

    private static StackPanel Card(Panel parent, string title)
    {
        var inner = new StackPanel();
        inner.Children.Add(new TextBlock { Text = title, FontSize = 10, Foreground = Muted, Margin = new Thickness(0, 0, 0, 8) });
        parent.Children.Add(new Border { Background = CardBg, CornerRadius = new CornerRadius(10), Padding = new Thickness(14), Margin = new Thickness(0, 0, 0, 14), Child = inner });
        return inner;
    }

    private static TextBlock Text(string s, double size, Brush color, bool bold = false) => new()
    {
        Text = s, FontSize = size, Foreground = color, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 3),
        FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal,
    };

    // The disk as one bar: each kind of file in its colour, then the rest of what is used (system, other), then free.
    private static FrameworkElement Bar(long total, List<HubStorageKind> storage, long used)
    {
        var grid = new Grid { Height = 14, Margin = new Thickness(0, 8, 0, 10), ClipToBounds = true };
        long known = 0;
        foreach (var (kind, _, color) in Kinds)
        {
            var bytes = storage.FirstOrDefault(x => x.Kind == kind)?.Bytes ?? 0;
            if (bytes <= 0) continue;
            known += bytes;
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(bytes, GridUnitType.Star) });
            var r = new Border { Background = color };
            Grid.SetColumn(r, grid.ColumnDefinitions.Count - 1);
            grid.Children.Add(r);
        }
        var rest = Math.Max(0, used - known);
        if (rest > 0)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(rest, GridUnitType.Star) });
            var r = new Border { Background = Faint, ToolTip = $"Système et autres : {Size(rest)}" };
            Grid.SetColumn(r, grid.ColumnDefinitions.Count - 1);
            grid.Children.Add(r);
        }
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(1, total - used), GridUnitType.Star) });
        var free = new Border { Background = Track };
        Grid.SetColumn(free, grid.ColumnDefinitions.Count - 1);
        grid.Children.Add(free);
        return new Border { CornerRadius = new CornerRadius(4), Child = grid, ClipToBounds = true };
    }

    private static FrameworkElement Legend(Brush color, string label, string value)
    {
        var row = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
        row.Children.Add(new Border { Width = 10, Height = 10, CornerRadius = new CornerRadius(2), Background = color, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center });
        var v = new TextBlock { Text = value, Foreground = Muted, FontSize = 12 };
        DockPanel.SetDock(v, Dock.Right);
        row.Children.Add(v);
        row.Children.Add(new TextBlock { Text = label, Foreground = Brushes.White, FontSize = 12 });
        return row;
    }

    internal static string Size(long bytes) => bytes switch
    {
        >= 1L << 40 => $"{bytes / (double)(1L << 40):0.0} To",
        >= 1L << 30 => $"{bytes / (double)(1L << 30):0.0} Go",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):0} Mo",
        _ => $"{bytes / 1024.0:0} Ko",
    };

    private static string Duration(long seconds) => seconds < 90 ? $"{seconds} s" : $"{seconds / 60} min";

    /// <summary>"il y a 5 min", "hier à 03:31", "le 24/11 à 03:30".</summary>
    internal static string When(string iso)
    {
        if (!DateTimeOffset.TryParse(iso, out var t)) return iso;
        var local = t.ToLocalTime().DateTime;
        var ago = DateTime.Now - local;
        if (ago < TimeSpan.FromMinutes(1)) return "à l'instant";
        if (ago < TimeSpan.FromHours(1)) return $"il y a {(int)ago.TotalMinutes} min";
        if (local.Date == DateTime.Today) return $"aujourd'hui à {local:HH:mm}";
        if (local.Date == DateTime.Today.AddDays(-1)) return $"hier à {local:HH:mm}";
        return $"le {local:dd/MM} à {local:HH:mm}";
    }

    private static string Since(string iso)
    {
        if (!DateTimeOffset.TryParse(iso, out var t)) return "?";
        var up = DateTimeOffset.UtcNow - t;
        return up.TotalDays >= 1 ? $"{(int)up.TotalDays} j" : up.TotalHours >= 1 ? $"{(int)up.TotalHours} h" : $"{(int)up.TotalMinutes} min";
    }

    // ------------------------------------------------------------------ window

    private async void Refresh_OnClick(object sender, RoutedEventArgs e) => await RefreshAsync();

    private void TitleBar_OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private void Close_OnClick(object sender, RoutedEventArgs e) => Hide();

    private bool _isExiting;

    private void Window_OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_isExiting) return;
        e.Cancel = true;
        Hide();
    }

    public void ForceClose()
    {
        _isExiting = true;
        _timer.Stop();
        Close();
    }
}
