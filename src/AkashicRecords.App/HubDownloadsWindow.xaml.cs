using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using AkashicRecords.App.Sync;
using AkashicRecords.Sync;

namespace AkashicRecords.App;

/// <summary>
/// The download board kept by the hub and shared by every device (the phone has its own view of it): the hub
/// downloads, then adds the track to the catalogue, and every device lists it at its next sync. Shown instead of
/// the local DownloaderWindow once this computer is paired.
///
/// What it does differently from the old board: the zone is chosen right next to the add box (a zone on the
/// board, a folder already in the music, or a new zone), zones stand side by side with the newest first (no
/// long drag down to a new zone), each card has its own 📁 menu to move it (dragging still works), and a zone
/// leaves the board by itself once its tracks are downloaded (the folder stays in the music).
/// </summary>
public partial class HubDownloadsWindow : Window
{
    private static readonly Brush ColumnBg = new SolidColorBrush(Color.FromRgb(0x20, 0x20, 0x38));
    private static readonly Brush TargetBg = new SolidColorBrush(Color.FromRgb(0x1E, 0x2B, 0x4D));
    private static readonly Brush CardBg = new SolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x45));
    private static readonly Brush Muted = new SolidColorBrush(Color.FromRgb(0x9A, 0x9A, 0xB5));
    private static readonly Brush Faint = new SolidColorBrush(Color.FromRgb(0x6B, 0x6B, 0x85));
    private static readonly Brush Warn = new SolidColorBrush(Color.FromRgb(0xF8, 0x71, 0x71));
    private static readonly Brush Accent = new SolidColorBrush(Color.FromRgb(0x3B, 0x82, 0xF6));

    private readonly DispatcherTimer _timer = new();
    private HubDownloadBoard? _board;
    private string? _error;
    private string? _targetId;       // where new tracks go; null = the music root
    private bool _showDone;
    private bool _busy;
    private HashSet<string>? _doneSeen;
    private Point _dragStart;

    /// <summary>A track downloaded by the hub has been fetched here: the music player reloads.</summary>
    public event Action? LibraryChanged;

    public HubDownloadsWindow()
    {
        InitializeComponent();
        InputBox.TextChanged += (_, _) => InputHint.Visibility = InputBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        _timer.Tick += async (_, _) => await RefreshAsync();
        IsVisibleChanged += async (_, _) =>
        {
            if (IsVisible) { await RefreshAsync(); _timer.Start(); }
            else _timer.Stop();
        };
        Render();
    }

    private static HubClient Hub()
    {
        var s = SyncService.Instance.Settings;
        return new HubClient(s.HubUrl, s.Token);
    }

    // ------------------------------------------------------------------ talking to the hub

    /// <summary>Runs [action] against the hub, then reloads the board; a failure is shown, never thrown.</summary>
    private async Task Act(Func<HubClient, Task>? action = null)
    {
        try
        {
            var board = await Task.Run(async () =>
            {
                using var hub = Hub();
                if (action is not null) await action(hub);
                return await hub.DownloadsAsync();
            });
            var done = board.Jobs.Where(j => j.Status == "done").Select(j => j.Id).ToHashSet();
            var fresh = _doneSeen is not null && done.Except(_doneSeen).Any();
            _doneSeen = done;
            _board = board;
            _error = null;
            if (fresh)
            {
                // The hub has just added a track to the catalogue: fetch it now rather than at the next pass.
                _ = SyncService.Instance.RunAsync().ContinueWith(_ => Dispatcher.Invoke(() => LibraryChanged?.Invoke()));
            }
        }
        catch (Exception ex) when (ex is HubException or System.Net.Http.HttpRequestException or TaskCanceledException)
        {
            _error = ex.Message;
        }
        Render();
    }

    private async Task RefreshAsync()
    {
        if (_busy) return;
        _busy = true;
        try { await Act(); }
        finally { _busy = false; }
        // Often while the hub works, rarely otherwise.
        _timer.Interval = TimeSpan.FromSeconds(_board?.Jobs.Any(j => j.Status is "queued" or "running") == true ? 3 : 12);
    }

    /// <summary>The zone for a folder or a new name, created on the hub if needed (the hub reuses one it has).</summary>
    private static async Task<string?> ZoneFor(HubClient hub, string? name, string folder) =>
        (await hub.AddZoneAsync(name, folder)).Id;

    // ------------------------------------------------------------------ drawing

    private HubDownloadZone? Zone(string? id) => _board?.Zones.FirstOrDefault(z => z.Id == id);

    private void Render()
    {
        var b = _board;
        if (_targetId is not null && Zone(_targetId) is null) _targetId = null; // its zone is done and gone
        var target = Zone(_targetId);
        TargetText.Text = "📁 " + (target?.Name ?? "Musique (sans dossier)") + "  ▾";
        AddButton.IsEnabled = b?.Enabled != false;

        StatusText.Foreground = _error is not null || b?.Tool.Problem is not null ? Warn : Muted;
        StatusText.Text = _error is not null ? $"Serveur injoignable : {_error}"
            : b is null ? "Lecture du tableau…"
            : !b.Enabled ? "Les téléchargements sont coupés sur le serveur."
            : b.Tool.Problem is not null ? $"Le serveur ne peut pas lancer yt-dlp : {b.Tool.Problem}"
            : b.Tool.Current is not null ? $"Le serveur télécharge : {b.Tool.Current}"
            : b.Jobs.Any(j => j.Status == "queued") ? "En attente sur le serveur…"
            : "Le titre arrive au catalogue de tous tes appareils une fois téléchargé : à écouter, ou à garder sur le téléphone.";
        ToolText.Text = b?.Tool.Version is { } v ? $"yt-dlp {v} sur le serveur" : "";

        BoardPanel.Children.Clear();
        if (b is null) return;
        var pending = b.Jobs.Where(j => j.Status != "done").ToList();
        foreach (var z in b.Zones) BoardPanel.Children.Add(Column(z, pending.Where(j => j.ZoneId == z.Id).ToList()));
        var loose = pending.Where(j => j.ZoneId is null || Zone(j.ZoneId) is null).ToList();
        if (loose.Count > 0 || b.Zones.Count == 0) BoardPanel.Children.Add(Column(null, loose));

        var done = b.Jobs.Where(j => j.Status == "done").Reverse().ToList();
        DoneToggle.Content = (_showDone ? "▾ " : "▸ ") + $"Terminés ({done.Count})";
        DoneToggle.Visibility = ClearDoneButton.Visibility = done.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        DoneScroll.Visibility = _showDone && done.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        DonePanel.Children.Clear();
        foreach (var j in done)
            DonePanel.Children.Add(new TextBlock
            {
                Text = $"✓ {j.Label}   →  Musique{(string.IsNullOrEmpty(j.Folder) ? "" : " / " + j.Folder)}",
                Foreground = Muted, FontSize = 12, Margin = new Thickness(6, 2, 0, 2), TextTrimming = TextTrimming.CharacterEllipsis,
            });
    }

    // One zone: its name and folder, "add here", rename / remove, and its cards. Cards can be dropped on it.
    private FrameworkElement Column(HubDownloadZone? zone, List<HubDownloadJob> jobs)
    {
        var isTarget = zone?.Id == _targetId;
        var body = new StackPanel();
        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
        var actions = new StackPanel { Orientation = Orientation.Horizontal };
        DockPanel.SetDock(actions, Dock.Right);
        if (zone is not null)
        {
            actions.Children.Add(Small("✎", "Renommer la zone", async () =>
            {
                var name = Prompt("Renommer la zone", "Nom", zone.Name);
                if (!string.IsNullOrWhiteSpace(name)) await Act(h => h.RenameZoneAsync(zone.Id, name.Trim()));
            }));
            actions.Children.Add(Small("✕", "Retirer la zone (ses titres vont sans dossier)", async () => await Act(h => h.RemoveZoneAsync(zone.Id))));
        }
        header.Children.Add(actions);
        var title = new StackPanel();
        title.Children.Add(new TextBlock { Text = "📁 " + (zone?.Name ?? "Musique (sans dossier)") + (jobs.Count > 0 ? $"  ·  {jobs.Count}" : ""), Foreground = Brushes.White, FontWeight = FontWeights.Bold, FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis });
        title.Children.Add(new TextBlock { Text = zone is null ? "Musique / (racine)" : $"Musique / {zone.Folder}", Foreground = Faint, FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis });
        header.Children.Add(title);
        body.Children.Add(header);

        var here = new Button
        {
            Content = isTarget ? "✓ Les ajouts vont ici" : "Ranger les ajouts ici",
            Style = (Style)FindResource("Flat"), Foreground = isTarget ? Accent : Muted, FontSize = 11,
            HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 6),
        };
        here.Click += (_, _) => { _targetId = zone?.Id; Render(); };
        body.Children.Add(here);

        var cards = new StackPanel();
        foreach (var j in jobs) cards.Children.Add(Card(j));
        if (jobs.Count == 0)
            cards.Children.Add(new TextBlock { Text = "Vide. Choisis « Ranger les ajouts ici », ou dépose une fiche.", Foreground = Faint, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(2, 4, 2, 4) });
        body.Children.Add(new ScrollViewer { Content = cards, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });

        var column = new Border
        {
            Width = 260, Margin = new Thickness(0, 0, 10, 0), Padding = new Thickness(10), CornerRadius = new CornerRadius(10),
            Background = isTarget ? TargetBg : ColumnBg, BorderBrush = isTarget ? Accent : Brushes.Transparent, BorderThickness = new Thickness(1),
            Child = body, AllowDrop = true,
        };
        column.DragOver += (_, e) => { e.Effects = e.Data.GetDataPresent(typeof(HubDownloadJob)) ? DragDropEffects.Move : DragDropEffects.None; e.Handled = true; };
        column.Drop += async (_, e) =>
        {
            if (e.Data.GetData(typeof(HubDownloadJob)) is HubDownloadJob j && j.ZoneId != zone?.Id)
                await Act(h => h.MoveDownloadAsync(j.Id, zone?.Id));
        };
        return column;
    }

    private FrameworkElement Card(HubDownloadJob job)
    {
        var running = job.Status == "running";
        var failed = job.Status == "failed";
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock { Text = job.Label, Foreground = Brushes.White, FontSize = 12, TextWrapping = TextWrapping.Wrap, MaxHeight = 34, TextTrimming = TextTrimming.CharacterEllipsis });
        panel.Children.Add(new TextBlock
        {
            Text = running ? "téléchargement en cours…" : failed ? "échec : " + (job.Error ?? "inconnu") : "en attente",
            Foreground = failed ? Warn : running ? Accent : Faint, FontSize = 11, TextWrapping = TextWrapping.Wrap, MaxHeight = 30, TextTrimming = TextTrimming.CharacterEllipsis,
            ToolTip = job.Error,
        });
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
        if (!running)
        {
            var move = Small("📁 Ranger ailleurs ▾", "Changer de zone", () => Task.CompletedTask);
            move.Click += (_, _) => OpenDestinations(move, async zoneId => await Act(h => h.MoveDownloadAsync(job.Id, zoneId)));
            buttons.Children.Add(move);
            if (failed) buttons.Children.Add(Small("↻", "Réessayer", async () => await Act(h => h.RetryDownloadAsync(job.Id))));
            buttons.Children.Add(Small("✕", "Retirer du tableau", async () => await Act(h => h.RemoveDownloadAsync(job.Id))));
        }
        panel.Children.Add(buttons);

        var card = new Border { Background = CardBg, CornerRadius = new CornerRadius(8), Padding = new Thickness(9, 7, 9, 7), Margin = new Thickness(0, 0, 0, 6), Child = panel, Cursor = running ? Cursors.Arrow : Cursors.SizeAll };
        if (!running)
        {
            card.PreviewMouseLeftButtonDown += (_, e) => _dragStart = e.GetPosition(this);
            card.PreviewMouseMove += (_, e) =>
            {
                if (e.LeftButton != MouseButtonState.Pressed || e.OriginalSource is DependencyObject d && FindParent<Button>(d) is not null) return;
                var p = e.GetPosition(this);
                if (Math.Abs(p.X - _dragStart.X) < 6 && Math.Abs(p.Y - _dragStart.Y) < 6) return;
                DragDrop.DoDragDrop(card, new DataObject(typeof(HubDownloadJob), job), DragDropEffects.Move);
            };
        }
        return card;
    }

    private Button Small(string text, string tip, Func<Task> onClick)
    {
        var b = new Button { Content = text, ToolTip = tip, Style = (Style)FindResource("Flat"), Foreground = Muted, FontSize = 11, Margin = new Thickness(0, 0, 4, 0) };
        b.Click += async (_, _) => await onClick();
        return b;
    }

    private static T? FindParent<T>(DependencyObject d) where T : DependencyObject
    {
        for (var cur = d; cur is not null; cur = cur is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(cur) : LogicalTreeHelper.GetParent(cur))
            if (cur is T t) return t;
        return null;
    }

    // ------------------------------------------------------------------ where it goes

    // The choices: no folder, the zones on the board, a folder already in the music (picked from a list), a new zone.
    private void OpenDestinations(FrameworkElement anchor, Func<string?, Task> onPicked)
    {
        var menu = new ContextMenu { PlacementTarget = anchor, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        void Add(string header, Func<Task> action, bool bold = false)
        {
            var item = new MenuItem { Header = header, FontWeight = bold ? FontWeights.Bold : FontWeights.Normal };
            item.Click += async (_, _) => await action();
            menu.Items.Add(item);
        }
        Add("📁 Musique (sans dossier)", () => onPicked(null));
        if (_board?.Zones.Count > 0)
        {
            menu.Items.Add(new Separator());
            foreach (var z in _board.Zones) Add($"📁 {z.Name}", () => onPicked(z.Id), z.Id == _targetId);
        }
        menu.Items.Add(new Separator());
        Add("📂 Dossier de ta musique…", async () =>
        {
            var folder = PickFolder(_board?.Folders ?? []);
            if (folder is null) return;
            string? id = null;
            await Act(async h => id = await ZoneFor(h, folder, folder));
            if (id is not null) await onPicked(id);
        });
        Add("＋ Nouvelle zone…", async () =>
        {
            var name = Prompt("Nouvelle zone", "Nom du dossier (dans Musique)", "");
            if (string.IsNullOrWhiteSpace(name)) return;
            string? id = null;
            await Act(async h => id = await ZoneFor(h, name.Trim(), name.Trim()));
            if (id is not null) await onPicked(id);
        });
        menu.IsOpen = true;
    }

    private void TargetButton_OnClick(object sender, RoutedEventArgs e) =>
        OpenDestinations(TargetButton, id => { _targetId = id; Render(); return Task.CompletedTask; });

    // ------------------------------------------------------------------ adding

    private async void AddButton_OnClick(object sender, RoutedEventArgs e) => await AddAsync();

    private async void InputBox_OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            e.Handled = true;
            await AddAsync();
        }
    }

    private async Task AddAsync()
    {
        var lines = InputBox.Text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        if (lines.Count == 0) return;
        InputBox.Text = "";
        var zone = _targetId;
        await Act(async h =>
        {
            foreach (var line in lines)
            {
                var link = line.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || line.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
                await h.AddDownloadAsync(link ? line : "ytsearch1:" + line.Replace("\"", ""), line, zone);
            }
        });
        _timer.Interval = TimeSpan.FromSeconds(3);
    }

    // ------------------------------------------------------------------ the rest

    private void DoneToggle_OnClick(object sender, RoutedEventArgs e) { _showDone = !_showDone; Render(); }
    private async void ClearDone_OnClick(object sender, RoutedEventArgs e) => await Act(h => h.ClearDoneDownloadsAsync());
    private void TitleBar_OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e) { if (e.ButtonState == MouseButtonState.Pressed) DragMove(); }
    private void Close_OnClick(object sender, RoutedEventArgs e) => Hide();

    // Hidden rather than closed (like the old board): it keeps its place and is shown again from the main window.
    private bool _isExiting;

    public void ForceClose()
    {
        _isExiting = true;
        _timer.Stop();
        Close();
    }

    private void Window_OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_isExiting) return;
        e.Cancel = true;
        Hide();
    }

    // ---- two small dark dialogs

    private Window Dialog(string title, UIElement content, double width)
    {
        return new Window
        {
            Title = title, Owner = this, WindowStartupLocation = WindowStartupLocation.CenterOwner, Width = width, SizeToContent = SizeToContent.Height,
            WindowStyle = WindowStyle.ToolWindow, ResizeMode = ResizeMode.NoResize, Topmost = true,
            Background = new SolidColorBrush(Color.FromRgb(0x1A, 0x1A, 0x2E)), Content = new Border { Padding = new Thickness(14), Child = content },
        };
    }

    private string? Prompt(string title, string label, string value)
    {
        var box = new TextBox { Text = value, Background = new SolidColorBrush(Color.FromRgb(0x12, 0x12, 0x1F)), Foreground = Brushes.White, CaretBrush = Brushes.White, Padding = new Thickness(6, 4, 6, 4), FontSize = 13 };
        var ok = new Button { Content = "OK", Style = (Style)FindResource("Primary"), IsDefault = true, Margin = new Thickness(0, 10, 0, 0), HorizontalAlignment = HorizontalAlignment.Right, MinWidth = 80 };
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock { Text = label, Foreground = Muted, Margin = new Thickness(0, 0, 0, 6) });
        panel.Children.Add(box);
        panel.Children.Add(ok);
        var dialog = Dialog(title, panel, 380);
        ok.Click += (_, _) => dialog.DialogResult = true;
        dialog.Loaded += (_, _) => { box.Focus(); box.SelectAll(); };
        return dialog.ShowDialog() == true ? box.Text : null;
    }

    // A folder already in the music, with a search box: the hub lists the albums of the catalogue.
    private string? PickFolder(IReadOnlyList<string> folders)
    {
        var search = new TextBox { Background = new SolidColorBrush(Color.FromRgb(0x12, 0x12, 0x1F)), Foreground = Brushes.White, CaretBrush = Brushes.White, Padding = new Thickness(6, 4, 6, 4), FontSize = 13 };
        var list = new ListBox { Height = 320, Background = new SolidColorBrush(Color.FromRgb(0x20, 0x20, 0x38)), Foreground = Brushes.White, BorderThickness = new Thickness(0), Margin = new Thickness(0, 8, 0, 0) };
        void Fill() { list.ItemsSource = folders.Where(f => search.Text.Length == 0 || f.Contains(search.Text, StringComparison.OrdinalIgnoreCase)).ToList(); }
        search.TextChanged += (_, _) => Fill();
        Fill();
        var ok = new Button { Content = "Ranger ici", Style = (Style)FindResource("Primary"), IsDefault = true, Margin = new Thickness(0, 10, 0, 0), HorizontalAlignment = HorizontalAlignment.Right, MinWidth = 100 };
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock { Text = "Chercher un dossier de ta musique", Foreground = Muted, Margin = new Thickness(0, 0, 0, 6) });
        panel.Children.Add(search);
        panel.Children.Add(list);
        panel.Children.Add(ok);
        var dialog = Dialog("Dossier de ta musique", panel, 420);
        ok.Click += (_, _) => { if (list.SelectedItem is not null) dialog.DialogResult = true; };
        list.MouseDoubleClick += (_, _) => { if (list.SelectedItem is not null) dialog.DialogResult = true; };
        dialog.Loaded += (_, _) => search.Focus();
        return dialog.ShowDialog() == true ? list.SelectedItem as string : null;
    }
}
