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
// When the PC is paired, a second part asks the AI (FLUX Kontext, run by a PC with ComfyUI through the hub): a
// sentence, optionally a zone painted on the preview and/or named in a few words, or « Restaurer ». That is gathered
// in `AiResult`; the result comes back later as an edit of the picture (« Revenir à l'original » still works).
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

    /// <summary>What to ask the AI, when the window was closed with « Envoyer à l'IA » or « Restaurer ».</summary>
    public ImageAiRequest? AiResult { get; private set; }

    // The AI zone and the parts hidden from the AI: brush strokes in preview pixels (only on the unturned picture: the
    // masks match the stored picture).
    private readonly List<(List<Point> Points, double Size, bool Hide)> _strokes = new();
    // Plain buttons switched on and off (the app's button styles are made for Button only, not ToggleButton).
    private readonly Button _brush = new()
    {
        Content = "Peindre la zone", Padding = new Thickness(14, 6, 14, 6), Cursor = Cursors.Hand,
        ToolTip = "Peins sur l'image l'endroit à changer : sa place et sa taille. Sans zone, toute l'image suit la phrase.",
    };
    private readonly Button _hideBrush = new()
    {
        Content = "Cacher à l'IA", Padding = new Thickness(14, 6, 14, 6), Cursor = Cursors.Hand,
        ToolTip = "Peins ce que l'IA ne doit pas voir : elle ne reçoit que le reste, et les parties cachées reviennent telles quelles.",
    };
    private enum Brushing { None, Zone, Hide }
    private Brushing _brushing;
    private bool _painting => _brushing != Brushing.None;

    private void SetPainting(Brushing tool)
    {
        _brushing = tool;
        _brush.Content = tool == Brushing.Zone ? "✓ Peindre la zone" : "Peindre la zone";
        _hideBrush.Content = tool == Brushing.Hide ? "✓ Cacher à l'IA" : "Cacher à l'IA";
        _overlay.Cursor = tool != Brushing.None ? Cursors.Pen : Cursors.Cross;
    }
    private readonly Slider _brushSize = new() { Minimum = 4, Maximum = 160, Value = 40, Width = 120, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
    private readonly TextBox _prompt = new() { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 60, Margin = new Thickness(0, 0, 0, 8) };
    private readonly TextBox _maskText = new() { Margin = new Thickness(0, 0, 0, 8) };
    private readonly CheckBox _erase = new() { Content = "Effacer d'abord ce qu'il y a dans la zone", Margin = new Thickness(0, 0, 0, 10) };
    private readonly ComboBox _tries = new() { Width = 70, Margin = new Thickness(8, 0, 16, 0), VerticalAlignment = VerticalAlignment.Center };
    private readonly ComboBox _engine = new() { Width = 170, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
    private readonly Canvas _paint = new() { IsHitTestVisible = false };
    private List<Point>? _currentStroke;

    /// <summary>`oriented` is the upright full picture; the window keeps a scaled copy and disposes it.</summary>
    public ImageEditWindow(SKBitmap oriented, string storedPath, bool aiAvailable = false)
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
        _overlay.Children.Add(_paint);
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

        if (aiAvailable) AddAiPart(panel);

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
        // The AI zone is drawn on the stored picture's orientation: it does not survive a turn.
        _strokes.Clear();
        DrawStrokes();
        SetPainting(Brushing.None);
        _brush.IsEnabled = _hideBrush.IsEnabled = _turns == 0;
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
        if (_painting)
        {
            _currentStroke = [p];
            _strokes.Add((_currentStroke, _brushSize.Value * Math.Max(PreviewWidth, PreviewHeight) / 1000, _brushing == Brushing.Hide));
            DrawStrokes();
            _overlay.CaptureMouse();
            e.Handled = true;
            return;
        }
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
        if (_painting)
        {
            _overlay.Cursor = Cursors.Pen;
            if (_currentStroke is not null && e.LeftButton == MouseButtonState.Pressed)
            {
                _currentStroke.Add(p);
                DrawStrokes();
            }
            return;
        }
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
        if (_currentStroke is not null)
        {
            _currentStroke = null;
            _overlay.ReleaseMouseCapture();
            e.Handled = true;
            return;
        }
        if (_drag == DragMode.None) return;
        _drag = DragMode.None;
        _overlay.ReleaseMouseCapture();
        // A crop covering (nearly) everything is no crop.
        if (_crop is { } c && c.Width > 0.999 && c.Height > 0.999) { _crop = null; DrawCrop(); }
        e.Handled = true;
    }

    // ---- AI ----

    private void AddAiPart(StackPanel panel)
    {
        panel.Children.Add(new Separator { Margin = new Thickness(0, 18, 0, 14) });
        panel.Children.Add(Header("MODIFIER AVEC L'IA"));
        panel.Children.Add(new TextBlock
        {
            Text = "Écris ce qu'il faut changer, en anglais de préférence (« replace the blue ball in her hand with a banana »). " +
                   "Peins la zone pour dire où (et à quelle taille) ; sans zone, toute l'image suit la phrase. Hors de la zone, l'image ne bouge pas.\n" +
                   "L'IA ne voit pas ta peinture : décris précisément ce qu'il faut enlever ou changer (« remove the woman with short black hair " +
                   "on the right edge of the photo »), pas « la zone surlignée ». Une phrase précise réussit bien plus souvent.",
            TextWrapping = TextWrapping.Wrap, Foreground = Brush("TextMutedBrush"), FontSize = 11.5, Margin = new Thickness(0, 0, 0, 8),
        });
        panel.Children.Add(_prompt);

        var brushRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
        _brush.Style = Application.Current.TryFindResource("SecondaryButtonStyle") as Style;
        _brush.Click += (_, _) => SetPainting(_brushing == Brushing.Zone ? Brushing.None : Brushing.Zone);
        brushRow.Children.Add(_brush);
        _brushSize.ToolTip = "Taille du pinceau";
        brushRow.Children.Add(_brushSize);
        panel.Children.Add(brushRow);
        var clear = SideButton("Effacer la zone peinte", () => ClearStrokes(hide: false));
        clear.HorizontalAlignment = HorizontalAlignment.Left;
        clear.Margin = new Thickness(0, 0, 0, 10);
        panel.Children.Add(clear);

        // What the AI must not see: painted dark; it gets only the rest, and the hidden parts come back unchanged.
        var hideRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };
        _hideBrush.Style = Application.Current.TryFindResource("SecondaryButtonStyle") as Style;
        _hideBrush.Click += (_, _) => SetPainting(_brushing == Brushing.Hide ? Brushing.None : Brushing.Hide);
        hideRow.Children.Add(_hideBrush);
        var showAll = SideButton("Tout montrer", () => ClearStrokes(hide: true));
        showAll.Margin = new Thickness(8, 0, 0, 0);
        showAll.ToolTip = "Enlever les parties cachées : l'IA verra toute l'image.";
        hideRow.Children.Add(showAll);
        panel.Children.Add(hideRow);

        panel.Children.Add(new TextBlock
        {
            Text = "Ou trouver la zone d'après quelques mots (facultatif, en anglais : « blue ball ») :",
            TextWrapping = TextWrapping.Wrap, Foreground = Brush("TextMutedBrush"), FontSize = 11.5, Margin = new Thickness(0, 0, 0, 4),
        });
        panel.Children.Add(_maskText);
        _erase.ToolTip = "Coché : ce qui est dans la zone disparaît d'abord (pour remplacer un objet). Décoché : la zone dit seulement où placer le nouvel objet.";
        panel.Children.Add(_erase);

        // How many versions (they wait in the computing window, where one is kept), and which engine.
        foreach (var n in new[] { 1, 2, 4 }) _tries.Items.Add(new ComboBoxItem { Content = n.ToString(), Tag = n });
        _tries.SelectedIndex = 0;
        _tries.ToolTip = "Plusieurs essais : l'IA fait plusieurs versions (environ 1 min 20 chacune), tu gardes la meilleure dans la fenêtre des calculs.";
        _engine.Items.Add(new ComboBoxItem { Content = "FLUX Kontext", Tag = "kontext" });
        _engine.Items.Add(new ComboBoxItem { Content = "RealVisXL (zone seulement)", Tag = "sdxl" });
        _engine.SelectedIndex = 0;
        _engine.ToolTip = "FLUX Kontext comprend la phrase et la scène (conseillé). RealVisXL redessine seulement la zone, d'après la phrase comme description : un autre essai, au cas où.";
        var options = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };
        options.Children.Add(new TextBlock { Text = "Essais", VerticalAlignment = VerticalAlignment.Center, Foreground = Brush("TextMutedBrush") });
        options.Children.Add(_tries);
        options.Children.Add(new TextBlock { Text = "Moteur", VerticalAlignment = VerticalAlignment.Center, Foreground = Brush("TextMutedBrush") });
        options.Children.Add(_engine);
        panel.Children.Add(options);

        var send = new Button
        {
            Content = "Envoyer à l'IA", Padding = new Thickness(14, 6, 14, 6), Cursor = Cursors.Hand, HorizontalAlignment = HorizontalAlignment.Left,
            Style = Application.Current.TryFindResource("PrimaryButtonStyle") as Style,
        };
        send.Click += (_, _) => SendToAi();
        panel.Children.Add(send);

        var restoreHeader = Header("RESTAURER");
        restoreHeader.Margin = new Thickness(0, 18, 0, 6);
        panel.Children.Add(restoreHeader);
        panel.Children.Add(new TextBlock
        {
            Text = "Pour une petite photo floue ou abîmée : l'IA la nettoie puis l'agrandit ×4. Elle peut redessiner un peu les visages.",
            TextWrapping = TextWrapping.Wrap, Foreground = Brush("TextMutedBrush"), FontSize = 11.5, Margin = new Thickness(0, 0, 0, 8),
        });
        var restore = SideButton("Restaurer (IA)", () => { AiResult = new ImageAiRequest("restore", null, null, null, false, Tries(), "kontext"); DialogResult = true; });
        restore.HorizontalAlignment = HorizontalAlignment.Left;
        panel.Children.Add(restore);
        panel.Children.Add(new TextBlock
        {
            Text = "Fait sur un PC avec ComfyUI (une à quelques minutes). Le résultat remplace l'image ; « " + ImageEditing.RevertLabel + " » reste possible. " +
                   "La rotation, le recadrage et les couleurs réglés plus haut ne s'appliquent pas à l'IA.",
            TextWrapping = TextWrapping.Wrap, Foreground = Brush("TextFaintBrush"), FontSize = 11, Margin = new Thickness(0, 10, 0, 0),
        });
    }

    private void SendToAi()
    {
        var prompt = _prompt.Text.Trim();
        if (prompt.Length == 0)
        {
            MessageBox.Show(this, "Écris d'abord ce qu'il faut changer.", "Modifier avec l'IA");
            _prompt.Focus();
            return;
        }
        var maskText = string.IsNullOrWhiteSpace(_maskText.Text) ? null : _maskText.Text.Trim();
        var engine = (_engine.SelectedItem as ComboBoxItem)?.Tag as string ?? "kontext";
        var zone = _strokes.Any(s => !s.Hide);
        var hidden = _strokes.Any(s => s.Hide);
        if (engine == "sdxl" && !zone && maskText is null)
        {
            MessageBox.Show(this, "RealVisXL redessine seulement une zone : peins-la, ou nomme-la en quelques mots.", "Modifier avec l'IA");
            return;
        }
        AiResult = new ImageAiRequest("edit", prompt, zone ? RenderMask(hide: false) : null, maskText, _erase.IsChecked == true, Tries(), engine,
            hidden ? RenderMask(hide: true) : null);
        DialogResult = true;
    }

    private int Tries() => (_tries.SelectedItem as ComboBoxItem)?.Tag as int? ?? 1;

    private void ClearStrokes(bool hide)
    {
        _strokes.RemoveAll(s => s.Hide == hide);
        _currentStroke = null;
        DrawStrokes();
    }

    private void DrawStrokes()
    {
        _paint.Children.Clear();
        var zoneBrush = new SolidColorBrush(Color.FromArgb(0x70, 0xFF, 0x30, 0x30));
        var hideBrush = new SolidColorBrush(Color.FromArgb(0xD0, 0x20, 0x20, 0x28)); // hidden: nearly black
        foreach (var (points, size, hide) in _strokes)
        {
            _paint.Children.Add(new Polyline
            {
                Stroke = hide ? hideBrush : zoneBrush, StrokeThickness = size, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
                StrokeLineJoin = PenLineJoin.Round, Points = new PointCollection(points.Count == 1 ? [points[0], points[0]] : points),
            });
        }
    }

    // The zone (or the hidden parts) as a PNG at preview size: white where painted, black elsewhere (the worker scales
    // it to the picture).
    private byte[] RenderMask(bool hide)
    {
        var w = (int)PreviewWidth;
        var h = (int)PreviewHeight;
        using var bitmap = new SKBitmap(w, h, SKColorType.Gray8, SKAlphaType.Opaque);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.Black);
            foreach (var (points, size, _) in _strokes.Where(s => s.Hide == hide))
            {
                using var paint = new SKPaint
                {
                    Color = SKColors.White, StrokeWidth = (float)size, StrokeCap = SKStrokeCap.Round, StrokeJoin = SKStrokeJoin.Round,
                    Style = SKPaintStyle.Stroke, IsAntialias = true,
                };
                using var path = new SKPath();
                path.MoveTo((float)points[0].X, (float)points[0].Y);
                foreach (var p in points.Skip(1)) path.LineTo((float)p.X, (float)p.Y);
                if (points.Count == 1) path.LineTo((float)points[0].X + 0.01f, (float)points[0].Y);
                canvas.DrawPath(path, paint);
            }
        }
        using var data = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }
}

/// <summary>An AI request from the editor. Kind "edit" (a sentence; MaskPng = white where the change goes; MaskText =
/// words naming the zone; Erase = empty the zone first; HidePng = white where the picture is hidden from the AI, restored
/// as it was in the result) or "restore".</summary>
public sealed record ImageAiRequest(string Kind, string? Prompt, byte[]? MaskPng, string? MaskText, bool Erase, int Tries = 1, string Engine = "kontext",
    byte[]? HidePng = null);
