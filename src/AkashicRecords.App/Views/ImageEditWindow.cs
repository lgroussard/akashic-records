using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using ShapePath = System.Windows.Shapes.Path;
using System.Windows.Threading;
using SkiaSharp;

namespace AkashicRecords.App.Views;

// The small picture editor behind « Modifier »: quarter turns, contrast, saturation and a free crop drawn on the
// preview. It only gathers the settings (`Result`); ImageEditing applies them to the full-resolution file.
public sealed class ImageEditWindow : Window
{
    private const int PreviewMaxSide = 1400;

    private readonly SKBitmap _working;      // upright, scaled down: the preview's source
    private readonly int _fullWidth, _fullHeight; // upright full-resolution size, for the size line
    private SKBitmap? _rotated;              // _working turned by _turns
    private int _turns;
    private Rect? _crop;                     // normalised on the rotated picture

    private readonly Image _image = new() { Stretch = Stretch.Fill };
    private readonly Canvas _overlay = new() { Background = Brushes.Transparent, Cursor = Cursors.Cross, ClipToBounds = true };
    private readonly Grid _surface = new();
    private readonly Viewbox _viewbox = new() { Stretch = Stretch.Uniform, Margin = new Thickness(16) };
    private readonly ShapePath _shade = new() { Fill = new SolidColorBrush(Color.FromArgb(0x99, 0, 0, 0)), IsHitTestVisible = false };
    private readonly Rectangle _frame = new() { Stroke = Brushes.White, IsHitTestVisible = false };
    private readonly Slider _contrast = NewSlider();
    private readonly Slider _saturation = NewSlider();
    private readonly TextBlock _contrastValue = ValueText();
    private readonly TextBlock _saturationValue = ValueText();
    private readonly TextBlock _sizeText;
    private readonly Button _noCrop;
    private readonly DispatcherTimer _renderTimer = new() { Interval = TimeSpan.FromMilliseconds(40) };

    public ImageEditSettings? Result { get; private set; }

    /// <summary>`oriented` is the upright full picture; the window keeps a scaled copy and disposes it.</summary>
    public ImageEditWindow(SKBitmap oriented, string storedPath)
    {
        _fullWidth = oriented.Width;
        _fullHeight = oriented.Height;
        _working = ImageEditing.Shrink(oriented, PreviewMaxSide);
        oriented.Dispose();

        Title = "Modifier l'image";
        Background = Brush("AppBgBrush");
        Foreground = Brush("TextBrush");
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        Width = SystemParameters.WorkArea.Width * 0.8;
        Height = SystemParameters.WorkArea.Height * 0.85;
        MinWidth = 640;
        MinHeight = 420;

        // Preview: the picture at its own pixel size, scaled as a whole by the Viewbox, so the overlay works in
        // preview pixels whatever the window size.
        _surface.Children.Add(_image);
        _overlay.Children.Add(_shade);
        _overlay.Children.Add(_frame);
        _surface.Children.Add(_overlay);
        _viewbox.Child = _surface;
        _viewbox.SizeChanged += (_, _) => DrawCrop();
        var previewArea = new Border { Background = Brush("Surface1Brush"), CornerRadius = new CornerRadius(8), Margin = new Thickness(16, 16, 8, 16), Child = _viewbox };
        _overlay.MouseLeftButtonDown += Overlay_OnMouseDown;
        _overlay.MouseMove += Overlay_OnMouseMove;
        _overlay.MouseLeftButtonUp += Overlay_OnMouseUp;
        _overlay.LostMouseCapture += (_, _) => _drag = DragMode.None;

        // Side panel.
        var panel = new StackPanel { Margin = new Thickness(8, 16, 16, 16) };
        panel.Children.Add(Header("ROTATION"));
        var turns = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 18) };
        var left = SideButton("⟲", () => Turn(-1));
        left.ToolTip = "Tourner d'un quart de tour vers la gauche";
        left.FontSize = 18;
        var right = SideButton("⟳", () => Turn(1));
        right.ToolTip = "Tourner d'un quart de tour vers la droite";
        right.FontSize = 18;
        right.Margin = new Thickness(8, 0, 0, 0);
        turns.Children.Add(left);
        turns.Children.Add(right);
        panel.Children.Add(turns);

        panel.Children.Add(SliderRow("CONTRASTE", _contrast, _contrastValue));
        panel.Children.Add(SliderRow("SATURATION", _saturation, _saturationValue));

        panel.Children.Add(Header("RECADRAGE"));
        panel.Children.Add(new TextBlock
        {
            Text = "Trace un rectangle sur l'image. Tire ses bords pour l'ajuster, glisse-le pour le déplacer, ou trace-en un autre.",
            TextWrapping = TextWrapping.Wrap, Foreground = Brush("TextMutedBrush"), FontSize = 11.5, Margin = new Thickness(0, 0, 0, 8),
        });
        _noCrop = SideButton("Sans recadrage", () => { _crop = null; DrawCrop(); });
        _noCrop.HorizontalAlignment = HorizontalAlignment.Left;
        panel.Children.Add(_noCrop);

        _sizeText = new TextBlock { Foreground = Brush("TextMutedBrush"), FontSize = 11.5, Margin = new Thickness(0, 18, 0, 0), TextWrapping = TextWrapping.Wrap };
        panel.Children.Add(_sizeText);
        if (ImageEditing.IsEdited(storedPath))
        {
            panel.Children.Add(new TextBlock
            {
                Text = "Cette image est déjà une version modifiée : l'original reste disponible avec « " + ImageEditing.RevertLabel + " ».",
                TextWrapping = TextWrapping.Wrap, Foreground = Brush("TextFaintBrush"), FontSize = 11, Margin = new Thickness(0, 8, 0, 0),
            });
        }

        var reset = SideButton("Réinitialiser", Reset);
        var cancel = SideButton("Annuler", () => { }); // IsCancel closes the dialog (Échap too)
        cancel.IsCancel = true;
        var save = new Button
        {
            Content = "Enregistrer", Padding = new Thickness(14, 6, 14, 6), Cursor = Cursors.Hand, Margin = new Thickness(8, 0, 0, 0),
            Style = Application.Current.TryFindResource("PrimaryButtonStyle") as Style,
        };
        save.Click += (_, _) => Save();
        cancel.Margin = new Thickness(8, 0, 0, 0);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(8, 0, 16, 16), HorizontalAlignment = HorizontalAlignment.Right };
        actions.Children.Add(reset);
        actions.Children.Add(cancel);
        actions.Children.Add(save);

        var side = new DockPanel { Width = 310 };
        DockPanel.SetDock(actions, Dock.Bottom);
        side.Children.Add(actions);
        side.Children.Add(new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = panel });

        var layout = new Grid();
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        layout.Children.Add(previewArea);
        Grid.SetColumn(side, 1);
        layout.Children.Add(side);
        Content = layout;

        _contrast.ValueChanged += (_, _) => ScheduleRender();
        _saturation.ValueChanged += (_, _) => ScheduleRender();
        _renderTimer.Tick += (_, _) => { _renderTimer.Stop(); RenderColours(); };
        Closed += (_, _) =>
        {
            _renderTimer.Stop();
            _rotated?.Dispose();
            _working.Dispose();
        };

        Rebuild();
    }

    // ---- Building blocks ----

    private static Brush Brush(string key) => (Brush)Application.Current.FindResource(key);

    private static Slider NewSlider() => new()
    {
        Minimum = -100, Maximum = 100, Value = 0, TickFrequency = 1, IsSnapToTickEnabled = true,
        SmallChange = 1, LargeChange = 10, Margin = new Thickness(0, 0, 0, 18),
        ToolTip = "Double-clic pour revenir à 0",
    };

    private static TextBlock ValueText() => new() { HorizontalAlignment = HorizontalAlignment.Right, FontSize = 11 };

    private static TextBlock Header(string text) => new()
    {
        Text = text, FontSize = 11, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 6),
        Foreground = (Brush)Application.Current.FindResource("TextMutedBrush"),
    };

    private static Button SideButton(string text, Action onClick)
    {
        var button = new Button
        {
            Content = text, Padding = new Thickness(14, 6, 14, 6), Cursor = Cursors.Hand,
            Style = Application.Current.TryFindResource("SecondaryButtonStyle") as Style,
        };
        button.Click += (_, _) => onClick();
        return button;
    }

    private static FrameworkElement SliderRow(string label, Slider slider, TextBlock value)
    {
        value.Foreground = (Brush)Application.Current.FindResource("TextMutedBrush");
        value.Text = "0";
        slider.ValueChanged += (_, e) => value.Text = e.NewValue > 0 ? "+" + (int)e.NewValue : ((int)e.NewValue).ToString();
        slider.MouseDoubleClick += (_, _) => slider.Value = 0;
        var head = new Grid();
        head.Children.Add(Header(label));
        head.Children.Add(value);
        var stack = new StackPanel();
        stack.Children.Add(head);
        stack.Children.Add(slider);
        return stack;
    }

    // ---- Preview ----

    // After a turn (or at start): the rotated preview, the surface size, the colours and the crop frame.
    private void Rebuild()
    {
        _rotated?.Dispose();
        _rotated = ImageEditing.Rotate(_working, _turns);
        _surface.Width = _rotated.Width;
        _surface.Height = _rotated.Height;
        RenderColours();
        DrawCrop();
    }

    private void ScheduleRender()
    {
        _renderTimer.Stop();
        _renderTimer.Start();
    }

    private void RenderColours()
    {
        if (_rotated is null) return;
        var contrast = (int)_contrast.Value;
        var saturation = (int)_saturation.Value;
        if (contrast == 0 && saturation == 0)
        {
            _image.Source = ImageEditing.ToBitmapSource(_rotated);
            return;
        }
        using var adjusted = _rotated.Copy();
        ImageEditing.AdjustColours(adjusted, contrast, saturation);
        _image.Source = ImageEditing.ToBitmapSource(adjusted);
    }

    private void Turn(int by)
    {
        _turns = ((_turns + by) % 4 + 4) % 4;
        // The crop follows the picture round.
        if (_crop is { } c)
            _crop = by > 0 ? new Rect(1 - c.Y - c.Height, c.X, c.Height, c.Width) : new Rect(c.Y, 1 - c.X - c.Width, c.Height, c.Width);
        Rebuild();
    }

    private void Reset()
    {
        _turns = 0;
        _crop = null;
        _contrast.Value = 0;
        _saturation.Value = 0;
        _renderTimer.Stop();
        Rebuild();
    }

    private void Save()
    {
        Result = new ImageEditSettings(_turns, _crop, (int)_contrast.Value, (int)_saturation.Value);
        DialogResult = true;
    }

    // ---- Crop ----

    private double PreviewWidth => _surface.Width;
    private double PreviewHeight => _surface.Height;

    /// <summary>Screen pixels per preview pixel.</summary>
    private double Scale =>
        _viewbox.ActualWidth > 0 && PreviewWidth > 0
            ? Math.Min(_viewbox.ActualWidth / PreviewWidth, _viewbox.ActualHeight / PreviewHeight)
            : 1;

    private Rect? CropPixels => _crop is { } c
        ? new Rect(c.X * PreviewWidth, c.Y * PreviewHeight, c.Width * PreviewWidth, c.Height * PreviewHeight)
        : null;

    private void SetCropPixels(Rect r)
    {
        _crop = new Rect(r.X / PreviewWidth, r.Y / PreviewHeight, r.Width / PreviewWidth, r.Height / PreviewHeight);
    }

    private void DrawCrop()
    {
        var full = new Rect(0, 0, PreviewWidth, PreviewHeight);
        var scale = Scale;
        if (CropPixels is { } r)
        {
            var shade = new GeometryGroup { FillRule = FillRule.EvenOdd };
            shade.Children.Add(new RectangleGeometry(full));
            shade.Children.Add(new RectangleGeometry(r));
            _shade.Data = shade;
            _frame.Visibility = Visibility.Visible;
            _frame.StrokeThickness = 2 / scale;
            _frame.Width = r.Width;
            _frame.Height = r.Height;
            Canvas.SetLeft(_frame, r.X);
            Canvas.SetTop(_frame, r.Y);
            _noCrop.IsEnabled = true;
        }
        else
        {
            _shade.Data = null;
            _frame.Visibility = Visibility.Collapsed;
            _noCrop.IsEnabled = false;
        }

        var (w, h) = _turns % 2 == 0 ? (_fullWidth, _fullHeight) : (_fullHeight, _fullWidth);
        if (_crop is { } c)
        {
            w = Math.Max(1, (int)Math.Round(c.Width * w));
            h = Math.Max(1, (int)Math.Round(c.Height * h));
        }
        _sizeText.Text = $"Image enregistrée : {w} × {h} px (JPEG)";
    }

    private enum DragMode { None, New, Move, Resize }

    private DragMode _drag;
    private Point _anchor;           // New: the starting corner; Move: the grab offset inside the rectangle
    private Rect _startRect;         // Resize: the rectangle when the drag began
    private bool _left, _top, _right, _bottom;

    private Point Clamp(Point p) => new(Math.Clamp(p.X, 0, PreviewWidth), Math.Clamp(p.Y, 0, PreviewHeight));

    // Which edges of the crop are under the pointer (none when it is not near the frame).
    private void HitEdges(Point p, Rect r, out bool left, out bool top, out bool right, out bool bottom)
    {
        var tolerance = 10 / Scale;
        var inY = p.Y >= r.Top - tolerance && p.Y <= r.Bottom + tolerance;
        var inX = p.X >= r.Left - tolerance && p.X <= r.Right + tolerance;
        var dl = Math.Abs(p.X - r.Left);
        var dr = Math.Abs(p.X - r.Right);
        var dt = Math.Abs(p.Y - r.Top);
        var db = Math.Abs(p.Y - r.Bottom);
        left = inY && dl <= tolerance && dl <= dr;
        right = inY && dr <= tolerance && dr < dl;
        top = inX && dt <= tolerance && dt <= db;
        bottom = inX && db <= tolerance && db < dt;
    }

    private void Overlay_OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        var p = Clamp(e.GetPosition(_overlay));
        _drag = DragMode.New;
        _anchor = p;
        if (CropPixels is { } r)
        {
            HitEdges(p, r, out _left, out _top, out _right, out _bottom);
            if (_left || _top || _right || _bottom)
            {
                _drag = DragMode.Resize;
                _startRect = r;
            }
            else if (r.Contains(p))
            {
                _drag = DragMode.Move;
                _anchor = new Point(p.X - r.X, p.Y - r.Y);
                _startRect = r;
            }
        }
        _overlay.CaptureMouse();
        e.Handled = true;
    }

    private void Overlay_OnMouseMove(object sender, MouseEventArgs e)
    {
        var p = Clamp(e.GetPosition(_overlay));
        switch (_drag)
        {
            case DragMode.New:
                if (Math.Abs(p.X - _anchor.X) * Scale < 4 && Math.Abs(p.Y - _anchor.Y) * Scale < 4) return; // still a click
                SetCropPixels(new Rect(_anchor, p));
                DrawCrop();
                return;
            case DragMode.Move:
                var x = Math.Clamp(p.X - _anchor.X, 0, PreviewWidth - _startRect.Width);
                var y = Math.Clamp(p.Y - _anchor.Y, 0, PreviewHeight - _startRect.Height);
                SetCropPixels(new Rect(x, y, _startRect.Width, _startRect.Height));
                DrawCrop();
                return;
            case DragMode.Resize:
                var l = _left ? p.X : _startRect.Left;
                var t = _top ? p.Y : _startRect.Top;
                var rr = _right ? p.X : _startRect.Right;
                var b = _bottom ? p.Y : _startRect.Bottom;
                var min = 4 / Scale;
                if (Math.Abs(rr - l) < min || Math.Abs(b - t) < min) return;
                SetCropPixels(new Rect(new Point(l, t), new Point(rr, b)));
                DrawCrop();
                return;
        }

        // Not dragging: the cursor says what a press would do.
        if (CropPixels is not { } r) { _overlay.Cursor = Cursors.Cross; return; }
        HitEdges(p, r, out var hl, out var ht, out var hr, out var hb);
        _overlay.Cursor = (hl || hr, ht || hb) switch
        {
            (true, true) => (hl && ht) || (hr && hb) ? Cursors.SizeNWSE : Cursors.SizeNESW,
            (true, false) => Cursors.SizeWE,
            (false, true) => Cursors.SizeNS,
            _ => r.Contains(p) ? Cursors.SizeAll : Cursors.Cross,
        };
    }

    private void Overlay_OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_drag == DragMode.None) return;
        _drag = DragMode.None;
        _overlay.ReleaseMouseCapture();
        // A crop covering (nearly) everything is no crop.
        if (_crop is { } c && c.Width > 0.999 && c.Height > 0.999) { _crop = null; DrawCrop(); }
        e.Handled = true;
    }
}
