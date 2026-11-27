using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace AkashicRecords.App.Views;

// A set of photos shown large, one at a time: arrows (or the keyboard arrows, or the wheel) step through them,
// "Enregistrer sous…" writes a copy of the shown file wherever the user says. Esc or Fermer closes.
public sealed class PhotoViewerWindow : Window
{
    private readonly List<string> _paths; // full paths
    private int _index;

    private readonly Image _image = new() { Stretch = Stretch.Uniform, Margin = new Thickness(16) };
    private readonly TextBlock _counter = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 12, 0) };
    private readonly Button _previous;
    private readonly Button _next;
    private readonly IReadOnlyList<long>? _ids; // the rows behind _paths, for "Agrandir ×4" and "Modifier"
    private readonly Button? _revert;

    /// <summary>True once a picture was edited or reverted here: the caller refreshes what it shows.</summary>
    public bool ImagesChanged { get; private set; }

    public PhotoViewerWindow(IReadOnlyList<string> fullPaths, int startIndex, string? upscaleTable = null, IReadOnlyList<long>? ids = null)
    {
        _paths = fullPaths.ToList();
        _ids = ids is not null && ids.Count == fullPaths.Count ? ids : null;
        _index = Math.Clamp(startIndex, 0, Math.Max(0, fullPaths.Count - 1));

        Title = "Photo";
        Background = Brush("AppBgBrush");
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        Width = SystemParameters.WorkArea.Width * 0.8;
        Height = SystemParameters.WorkArea.Height * 0.85;
        _counter.Foreground = Brush("TextMutedBrush");

        _previous = BarButton("‹  Précédente", () => Step(-1));
        _next = BarButton("Suivante  ›", () => Step(1));
        var save = BarButton("Enregistrer sous…", Save);
        var close = BarButton("Fermer", Close);

        var bar = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 14) };
        bar.Children.Add(_previous);
        bar.Children.Add(_counter);
        bar.Children.Add(_next);
        bar.Children.Add(new Border { Width = 28 });
        bar.Children.Add(save);
        bar.Children.Add(new Border { Width = 8 });
        if (upscaleTable is not null && _ids is not null)
        {
            var rows = _ids;
            var edit = BarButton(ImageEditing.EditLabel, () => Apply(ImageEditing.Edit(this, upscaleTable, (int)rows[_index])));
            edit.ToolTip = "Tourner, recadrer, régler le contraste et la saturation (l'original est gardé)";
            bar.Children.Add(edit);
            bar.Children.Add(new Border { Width = 8 });
            _revert = BarButton(ImageEditing.RevertLabel, () => Apply(ImageEditing.Revert(this, upscaleTable, (int)rows[_index])));
            _revert.ToolTip = "Remet la photo d'avant les modifications";
            bar.Children.Add(_revert);
            bar.Children.Add(new Border { Width = 8 });
        }
        if (upscaleTable is not null && _ids is not null && UpscaleAction.Available)
        {
            var rowIds = _ids;
            Button? upscale = null;
            upscale = BarButton(UpscaleAction.Label, async () => await UpscaleAction.RequestAsync(upscale, upscaleTable, rowIds[_index]));
            upscale.ToolTip = "Demande au serveur une version 4 fois plus grande, ajoutée à côté de l'originale";
            bar.Children.Add(upscale);
            bar.Children.Add(new Border { Width = 8 });
        }
        bar.Children.Add(close);

        var layout = new Grid();
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.Children.Add(_image);
        Grid.SetRow(bar, 1);
        layout.Children.Add(bar);
        Content = layout;

        PreviewKeyDown += (_, e) =>
        {
            switch (e.Key)
            {
                case Key.Left: Step(-1); e.Handled = true; break;
                case Key.Right: Step(1); e.Handled = true; break;
                case Key.Escape: Close(); e.Handled = true; break;
            }
        };
        MouseWheel += (_, e) => Step(e.Delta < 0 ? 1 : -1);

        Show(_index);
    }

    private static Brush Brush(string key) => (Brush)Application.Current.FindResource(key);

    private static Button BarButton(string text, Action onClick)
    {
        var button = new Button
        {
            Content = text,
            Padding = new Thickness(14, 6, 14, 6),
            Cursor = Cursors.Hand,
            Style = Application.Current.TryFindResource("SecondaryButtonStyle") as Style,
        };
        button.Click += (_, _) => onClick();
        return button;
    }

    // The shown row now points at another file.
    private void Apply(ImageEditOutcome? outcome)
    {
        if (outcome is null) return;
        _paths[_index] = AkashicRecords.Infrastructure.Persistence.MediaStorage.ResolveFullPath(outcome.ImagePath);
        ImagesChanged = true;
        Show(_index);
    }

    private void Step(int by)
    {
        var target = _index + by;
        if (target < 0 || target >= _paths.Count) return;
        Show(target);
    }

    private void Show(int index)
    {
        _index = index;
        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad; // the file is not kept open
            bitmap.UriSource = new Uri(_paths[index]);
            bitmap.EndInit();
            bitmap.Freeze();
            _image.Source = bitmap;
        }
        catch (Exception)
        {
            // A missing or unreadable file (a format this PC has no codec for): an empty page, the bar still works.
            _image.Source = null;
        }
        _counter.Text = $"{index + 1} / {_paths.Count}";
        _previous.IsEnabled = index > 0;
        _next.IsEnabled = index < _paths.Count - 1;
        if (_revert is not null)
            _revert.Visibility = ImageEditing.IsEdited(Path.GetFileName(_paths[index])) ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Save()
    {
        var source = _paths[_index];
        var extension = Path.GetExtension(source);
        var dialog = new SaveFileDialog
        {
            FileName = "journal-photo-" + (_index + 1) + extension,
            Filter = $"Image (*{extension})|*{extension}|Tous les fichiers|*.*",
        };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            File.Copy(source, dialog.FileName, overwrite: true);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "La photo n'a pas pu être enregistrée : " + ex.Message, "Photo");
        }
    }
}
