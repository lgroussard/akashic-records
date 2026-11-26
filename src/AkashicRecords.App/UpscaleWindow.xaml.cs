using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AkashicRecords.App.Sync;
using AkashicRecords.Sync;

namespace AkashicRecords.App;

/// <summary>
/// Upscaling images through the hub's shared computing: the images and their results stay on the hub, the work is
/// done by the strongest device around (this PC's graphics card when Akashic runs here, the hub itself otherwise).
/// Any image can be dropped here; nothing is added to the other sections.
/// </summary>
public partial class UpscaleWindow : Window
{
    private static readonly Brush CardBg = new SolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x45));
    private static readonly Brush SelectedBg = new SolidColorBrush(Color.FromRgb(0x1E, 0x2B, 0x4D));
    private static readonly Brush Muted = new SolidColorBrush(Color.FromRgb(0x9A, 0x9A, 0xB5));
    private static readonly Brush Warn = new SolidColorBrush(Color.FromRgb(0xF8, 0x71, 0x71));
    private static readonly Brush Good = new SolidColorBrush(Color.FromRgb(0x4A, 0xDE, 0x80));
    private static readonly Brush Accent = new SolidColorBrush(Color.FromRgb(0x3B, 0x82, 0xF6));

    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly List<string> _picked = new();
    private HubComputeBoard? _board;
    private string? _error;
    private string? _selectedId;
    private bool _showBefore;
    private bool _busy;
    private int _previewVersion;

    public UpscaleWindow()
    {
        InitializeComponent();
        WorkerBox.IsChecked = ComputeService.Instance.Worker;
        ComputeService.Instance.StateChanged += OnWorkerChanged;
        _timer.Tick += async (_, _) => await RefreshAsync();
        IsVisibleChanged += async (_, _) =>
        {
            if (IsVisible) { await RefreshAsync(); _timer.Start(); }
            else _timer.Stop();
        };
        Render();
    }

    private void OnWorkerChanged() => Dispatcher.BeginInvoke(RenderWorker);

    // ------------------------------------------------------------------ talking to the hub

    private static HubClient Hub()
    {
        var s = SyncService.Instance.Settings;
        return new HubClient(s.HubUrl, s.Token);
    }

    private async Task Act(Func<HubClient, Task>? action = null)
    {
        try
        {
            _board = await Task.Run(async () =>
            {
                using var hub = Hub();
                if (action is not null) await action(hub);
                return await hub.ComputeAsync();
            });
            _error = null;
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
        try
        {
            var before = Selected()?.Status;
            await Act();
            // The selected one just finished: show its result.
            if (before is not null && before != "done" && Selected()?.Status == "done") { _showBefore = false; _ = ShowPreviewAsync(); }
        }
        finally { _busy = false; }
    }

    // ------------------------------------------------------------------ drawing

    private HubComputeJob? Selected() => _board?.Jobs.FirstOrDefault(j => j.Id == _selectedId);

    private void Render()
    {
        var b = _board;
        if (!SyncService.Instance.Settings.IsPaired)
        {
            StatusText.Text = "Cet ordinateur n'est pas appairé au serveur : rien à faire ici (Réglages → Synchronisation).";
            StatusText.Foreground = Warn;
            GoButton.IsEnabled = false;
            return;
        }

        // Models: keep the choice across refreshes.
        var chosen = (ModelBox.SelectedItem as ComboBoxItem)?.Tag as string;
        var names = b?.Models.Select(m => m.Name).ToList() ?? new();
        if (!names.SequenceEqual(ModelBox.Items.OfType<ComboBoxItem>().Select(i => (string)i.Tag)))
        {
            ModelBox.Items.Clear();
            foreach (var m in b?.Models ?? new())
                ModelBox.Items.Add(new ComboBoxItem { Content = m.Label, Tag = m.Name });
            ModelBox.SelectedItem = ModelBox.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == chosen)
                                    ?? ModelBox.Items.OfType<ComboBoxItem>().FirstOrDefault();
        }
        // One model per feature: nothing to choose, so no chooser (it comes back if the hub ever has several).
        ModelLabel.Visibility = ModelBox.Visibility = ModelBox.Items.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        RenderModelHint();
        RenderPicked();
        RenderWorker();

        StatusText.Foreground = _error is not null ? Warn : Muted;
        StatusText.Text = _error is not null ? $"Serveur injoignable : {_error}"
            : b is null ? "Lecture de la liste…"
            : !b.Enabled ? "Le calcul partagé est coupé sur le serveur."
            : b.Models.Count == 0 ? "Le serveur n'a encore aucun modèle (dossier compute/models)."
            : "Présents pour calculer : " + (b.Workers.Count == 0 ? "personne" : string.Join(", ", b.Workers.Select(w => Who(w.Name) + (w.Busy ? " (occupé)" : ""))));

        JobsPanel.Children.Clear();
        if (b is null) return;
        if (b.Jobs.Count == 0)
            JobsPanel.Children.Add(new TextBlock { Text = "Aucune demande pour l'instant.", Foreground = Muted, FontSize = 12, Margin = new Thickness(2, 4, 0, 0) });
        foreach (var j in b.Jobs) JobsPanel.Children.Add(Card(j));
        if (_selectedId is null && b.Jobs.FirstOrDefault() is { } first) Select(first.Id);
        RenderPreviewButtons();
    }

    private string Label(string model) => _board?.Models.FirstOrDefault(m => m.Name == model)?.Label ?? model;

    private void RenderModelHint()
    {
        var model = _board?.Models.FirstOrDefault(m => m.Name == (ModelBox.SelectedItem as ComboBoxItem)?.Tag as string);
        if (model is null) { ModelHint.Text = ""; return; }
        var who = model.Runners is { Count: > 0 } r ? "Fait maintenant par : " + string.Join(", ", r.Select(Who)) + "."
            : model.Hub ? "Le serveur peut le faire lui-même (lent)." : "Attendra qu'un appareil capable soit allumé.";
        ModelHint.Text = $"{model.Description} {who}".Trim();
    }

    private void RenderPicked()
    {
        DropText.Text = _picked.Count == 0 ? "Dépose des images ici, ou clique pour choisir"
            : _picked.Count == 1 ? Path.GetFileName(_picked[0]) : $"{_picked.Count} images choisies";
        DropText.Foreground = _picked.Count == 0 ? Muted : Brushes.White;
        GoButton.IsEnabled = _picked.Count > 0 && ModelBox.SelectedItem is not null && _board?.Enabled != false;
    }

    private void RenderWorker()
    {
        var c = ComputeService.Instance;
        WorkerText.Foreground = c.Problem is not null ? Warn : Muted;
        WorkerText.Text = !c.Worker ? "Ce PC ne prend pas de travail : le serveur ou un autre appareil s'en charge."
            : c.Current is not null ? $"En cours ici : {c.Current}"
            : c.Problem ?? "Prêt : ce PC prend les demandes avec sa carte graphique. Le programme et les modèles viennent du serveur.";
    }

    private Border Card(HubComputeJob j)
    {
        var (status, color) = j.Status switch
        {
            "queued" => ("En attente", Muted),
            "running" => ($"En cours sur {Who(j.WorkerName)}…", Accent),
            "done" => ($"Fait par {Who(j.WorkerName)} en {Seconds(j.Seconds)} · {Mb(j.ResultSize)}" + Placed(j), j.AttachError is null ? Good : Warn),
            "failed" => ($"Échec : {j.Error}", Warn),
            _ => (j.Status, Muted),
        };
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock { Text = j.InputName, Foreground = Brushes.White, FontSize = 12, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
        panel.Children.Add(new TextBlock { Text = $"{Label(j.Model)} · {j.AddedAt.Replace('T', ' ')[..16]}", Foreground = Muted, FontSize = 10.5 });
        panel.Children.Add(new TextBlock { Text = status, Foreground = color, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) });

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
        if (j.Status == "failed") actions.Children.Add(SmallButton("Relancer", async () => await Act(h => h.RetryComputeAsync(j.Id))));
        if (j.Status != "running")
            actions.Children.Add(SmallButton("Retirer", async () =>
            {
                if (_selectedId == j.Id) { _selectedId = null; PreviewImage.Source = null; }
                await Act(h => h.RemoveComputeAsync(j.Id));
            }));
        if (actions.Children.Count > 0) panel.Children.Add(actions);

        var card = new Border
        {
            Background = j.Id == _selectedId ? SelectedBg : CardBg,
            BorderBrush = j.Id == _selectedId ? Accent : Brushes.Transparent,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8), Padding = new Thickness(10, 8, 10, 8), Margin = new Thickness(0, 0, 0, 6),
            Cursor = Cursors.Hand, Child = panel,
        };
        card.MouseLeftButtonUp += (_, e) => { if (e.OriginalSource is not Button) Select(j.Id); };
        return card;
    }

    private Button SmallButton(string text, Func<Task> click)
    {
        var b = new Button { Content = text, Style = (Style)FindResource("Flat"), FontSize = 11, Margin = new Thickness(0, 0, 4, 0) };
        b.Click += async (_, e) => { e.Handled = true; await click(); };
        return b;
    }

    /// <summary>The hub works under the name "hub": shown as the server.</summary>
    private static string Who(string? name) => name is null or "hub" ? "le serveur" : name;

    /// <summary>For a picture of the app: whether the result was put next to its original.</summary>
    private static string Placed(HubComputeJob j) =>
        j.AttachType is null ? "" : j.AttachError is not null ? $" · pas rangée : {j.AttachError}" : j.AttachedPath is not null ? " · rangée à côté de l'original" : "";

    private static string Seconds(double? s) => s is null ? "?" : s < 60 ? $"{s:0.#} s" : $"{(int)(s / 60)} min {(int)(s % 60):00} s";
    private static string Mb(long? bytes) => bytes is null ? "" : $"{bytes / 1024d / 1024d:0.#} Mo";

    // ------------------------------------------------------------------ preview

    private void Select(string id)
    {
        if (_selectedId == id) return;
        _selectedId = id;
        _showBefore = false;
        Render();
        _ = ShowPreviewAsync();
    }

    private void RenderPreviewButtons()
    {
        var j = Selected();
        var done = j?.Status == "done";
        BeforeButton.IsEnabled = j is not null;
        AfterButton.IsEnabled = OpenButton.IsEnabled = SaveButton.IsEnabled = done;
        BeforeButton.Background = AfterButton.Background = null;
        PreviewTitle.Text = j is null ? "" : _showBefore || !done ? $"Avant : {j.InputName}" : $"Après : {j.ResultName}";
    }

    private async Task ShowPreviewAsync()
    {
        var version = ++_previewVersion;
        var j = Selected();
        RenderPreviewButtons();
        PreviewImage.Source = null;
        if (j is null) { PreviewText.Text = "Choisis une demande pour la voir."; return; }
        var before = _showBefore || j.Status != "done";
        PreviewText.Text = before && j.Status != "done" ? "Le résultat s'affichera ici une fois prêt." : "Chargement…";
        try
        {
            var path = before
                ? await ComputeService.Instance.LocalCopyAsync(j.InputHash, Path.GetExtension(j.InputName).ToLowerInvariant())
                : await ComputeService.Instance.LocalCopyAsync(j.ResultHash!, ".png");
            var image = await Task.Run(() =>
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.UriSource = new Uri(path);
                bmp.DecodePixelWidth = 2000; // enough for the window; the file keeps its full size
                bmp.EndInit();
                bmp.Freeze();
                return bmp;
            });
            if (version != _previewVersion) return;
            PreviewImage.Source = image;
            if (j.Status == "done") PreviewText.Text = "";
        }
        catch (Exception ex) when (ex is HubException or IOException or NotSupportedException or System.Net.Http.HttpRequestException)
        {
            if (version == _previewVersion) PreviewText.Text = "Aperçu impossible : " + ex.Message;
        }
    }

    private void BeforeButton_OnClick(object sender, RoutedEventArgs e) { _showBefore = true; _ = ShowPreviewAsync(); }
    private void AfterButton_OnClick(object sender, RoutedEventArgs e) { _showBefore = false; _ = ShowPreviewAsync(); }

    private async void OpenButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (Selected() is not { Status: "done" } j) return;
        try
        {
            var path = await ComputeService.Instance.LocalCopyAsync(j.ResultHash!, ".png");
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is HubException or IOException or System.ComponentModel.Win32Exception)
        {
            StatusText.Text = "Ouverture impossible : " + ex.Message;
            StatusText.Foreground = Warn;
        }
    }

    private async void SaveButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (Selected() is not { Status: "done" } j) return;
        var dialog = new Microsoft.Win32.SaveFileDialog { FileName = j.ResultName, Filter = "Image PNG|*.png", DefaultExt = ".png" };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            var path = await ComputeService.Instance.LocalCopyAsync(j.ResultHash!, ".png");
            File.Copy(path, dialog.FileName, overwrite: true);
            StatusText.Text = $"Enregistré : {dialog.FileName}";
            StatusText.Foreground = Good;
        }
        catch (Exception ex) when (ex is HubException or IOException or UnauthorizedAccessException)
        {
            StatusText.Text = "Enregistrement impossible : " + ex.Message;
            StatusText.Foreground = Warn;
        }
    }

    // ------------------------------------------------------------------ new requests

    private static bool IsImage(string path) =>
        new[] { ".jpg", ".jpeg", ".png", ".webp" }.Contains(Path.GetExtension(path).ToLowerInvariant());

    private void Pick(IEnumerable<string> paths)
    {
        var images = paths.Where(p => File.Exists(p) && IsImage(p)).ToList();
        _picked.Clear();
        _picked.AddRange(images);
        if (images.Count == 0)
        {
            StatusText.Text = "Formats acceptés : jpg, png, webp.";
            StatusText.Foreground = Warn;
        }
        RenderPicked();
    }

    private void DropZone_OnClick(object sender, MouseButtonEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Multiselect = true, Filter = "Images|*.jpg;*.jpeg;*.png;*.webp" };
        if (dialog.ShowDialog(this) == true) Pick(dialog.FileNames);
    }

    private void DropZone_OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        DropZone.BorderBrush = Accent;
        e.Handled = true;
    }

    private void DropZone_OnDragLeave(object sender, DragEventArgs e) => DropZone.BorderBrush = new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x5A));

    private void DropZone_OnDrop(object sender, DragEventArgs e)
    {
        DropZone_OnDragLeave(sender, e);
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files) Pick(files);
    }

    private async void GoButton_OnClick(object sender, RoutedEventArgs e)
    {
        if ((ModelBox.SelectedItem as ComboBoxItem)?.Tag is not string model || _picked.Count == 0) return;
        var files = _picked.ToList();
        GoButton.IsEnabled = false;
        StatusText.Foreground = Muted;
        StatusText.Text = files.Count == 1 ? "Envoi de l'image au serveur…" : $"Envoi de {files.Count} images au serveur…";
        try
        {
            string? last = null;
            foreach (var f in files) last = (await Task.Run(() => ComputeService.Instance.RequestAsync(f, model))).Id;
            _picked.Clear();
            await Act();
            if (last is not null) Select(last);
        }
        catch (Exception ex) when (ex is HubException or IOException or System.Net.Http.HttpRequestException)
        {
            StatusText.Text = "Envoi impossible : " + ex.Message;
            StatusText.Foreground = Warn;
        }
        RenderPicked();
    }

    private void ModelBox_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        RenderModelHint();
        RenderPicked();
    }

    private void WorkerBox_OnChanged(object sender, RoutedEventArgs e)
    {
        if (WorkerBox.IsChecked != ComputeService.Instance.Worker) ComputeService.Instance.SetWorker(WorkerBox.IsChecked == true);
    }

    // ------------------------------------------------------------------ window

    private void TitleBar_OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private void Close_OnClick(object sender, RoutedEventArgs e) => Hide();

    // Hidden, not closed: kept for the next time (its timer stops while hidden). Closed for real on app exit.
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
        ComputeService.Instance.StateChanged -= OnWorkerChanged;
        Close();
    }
}
