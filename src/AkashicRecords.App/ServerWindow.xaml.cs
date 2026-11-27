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

    public ServerWindow()
    {
        InitializeComponent();
        _timer.Tick += async (_, _) => await RefreshAsync();
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
