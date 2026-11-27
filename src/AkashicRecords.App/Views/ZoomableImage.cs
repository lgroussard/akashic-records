using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace AkashicRecords.App.Views;

// A picture shown large that zooms and pans, the same in every viewer:
// - the wheel zooms around the cursor (×1.2 a notch), from "fit" up to 8× the picture's native pixels;
// - a left drag pans once zoomed (hand cursor); a click without a drag goes on to whatever handled it before;
// - a double-click toggles fit / 100 % (one picture pixel per screen pixel), centred on the clicked point;
// - + / - / 0 on the keyboard (outside text fields) zoom in, out, back to fit;
// - a new Source goes back to fit, and so does a resize while at fit. ReplaceSource swaps the picture keeping the
//   view (the full-resolution decode after a fast one, or "Avant" / "Après" of the same picture).
// The picture never leaves this control's bounds, so nothing around it gets covered.
public sealed class ZoomableImage : Border
{
    public static readonly DependencyProperty SourceProperty = DependencyProperty.Register(
        nameof(Source), typeof(ImageSource), typeof(ZoomableImage),
        new PropertyMetadata(null, (d, _) => ((ZoomableImage)d).OnSourceChanged()));

    public ImageSource? Source
    {
        get => (ImageSource?)GetValue(SourceProperty);
        set => SetValue(SourceProperty, value);
    }

    private const double Step = 1.2;
    private const double MaxNativeZoom = 8;

    private readonly Image _image;
    private readonly MatrixTransform _transform = new();
    private double _scale = 1;   // relative to fit: 1 = the whole picture in view
    private Vector _offset;      // added after the scale, in this control's pixels
    private bool _keepView;
    private Point? _pressedAt;
    private Vector _pressedOffset;
    private bool _panning;
    private Window? _window;

    public ZoomableImage()
    {
        ClipToBounds = true;
        Background = Brushes.Transparent; // the whole area takes the wheel and the drag, not just the picture
        _image = new Image
        {
            Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            RenderTransformOrigin = new Point(0, 0),
            RenderTransform = _transform,
        };
        RenderOptions.SetBitmapScalingMode(_image, BitmapScalingMode.HighQuality);
        Child = _image;

        SizeChanged += (_, _) => { if (IsAtFit) ResetZoom(); else ClampAndApply(); };
        _image.SizeChanged += (_, _) => { if (IsAtFit) ResetZoom(); else ClampAndApply(); };
        Loaded += (_, _) => HookWindow();
        Unloaded += (_, _) => UnhookWindow();
    }

    /// <summary>Shows another picture without going back to fit (same framing, e.g. a sharper decode of it).</summary>
    public void ReplaceSource(ImageSource? source)
    {
        if (ReferenceEquals(Source, source)) return;
        _keepView = true;
        Source = source;
    }

    public void ResetZoom()
    {
        _scale = 1;
        _offset = default;
        Apply();
    }

    private bool IsAtFit => Math.Abs(_scale - 1) < 0.001;

    private void OnSourceChanged()
    {
        _image.Source = Source;
        if (_keepView) _keepView = false;
        else ResetZoom();
    }

    // ---- Geometry ----

    // The zoom (relative to fit) at which one picture pixel is one screen pixel.
    private double NativeScale()
    {
        var fitWidth = _image.ActualWidth;
        if (Source is null || fitWidth <= 0) return 1;
        var dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
        var pixels = Source is System.Windows.Media.Imaging.BitmapSource bitmap ? bitmap.PixelWidth : Source.Width * dpi;
        return pixels / dpi / fitWidth;
    }

    private double MinScale => Math.Min(1, NativeScale());
    private double MaxScale => Math.Max(1, NativeScale() * MaxNativeZoom);

    private Vector LayoutOffset => VisualTreeHelper.GetOffset(_image);

    // The picture point (in its unzoomed layout) under a point of this control.
    private Point PictureAt(Point p)
    {
        var o = LayoutOffset;
        return new Point((p.X - o.X - _offset.X) / _scale, (p.Y - o.Y - _offset.Y) / _scale);
    }

    private void ZoomAt(Point p, double scale)
    {
        scale = Math.Clamp(scale, MinScale, MaxScale);
        if (Math.Abs(scale - 1) < 0.02) scale = 1; // fit is a stop, not something to miss by a hair
        var q = PictureAt(p);
        var o = LayoutOffset;
        _scale = scale;
        _offset = new Vector(p.X - o.X - scale * q.X, p.Y - o.Y - scale * q.Y);
        ClampAndApply();
    }

    // The clicked picture point brought to the middle of the view at this zoom.
    private void CentreOn(Point p, double scale)
    {
        scale = Math.Clamp(scale, MinScale, MaxScale);
        var q = PictureAt(p);
        var o = LayoutOffset;
        _scale = scale;
        _offset = new Vector(ActualWidth / 2 - o.X - scale * q.X, ActualHeight / 2 - o.Y - scale * q.Y);
        ClampAndApply();
    }

    private bool CanPan =>
        Source is not null && (_image.ActualWidth * _scale > ActualWidth + 0.5 || _image.ActualHeight * _scale > ActualHeight + 0.5);

    // Smaller than the view: centred. Larger: no empty band on a side.
    private void ClampAndApply()
    {
        var o = LayoutOffset;
        _offset = new Vector(
            Clamp1(_offset.X, o.X, _image.ActualWidth * _scale, ActualWidth),
            Clamp1(_offset.Y, o.Y, _image.ActualHeight * _scale, ActualHeight));
        Apply();
    }

    private static double Clamp1(double offset, double layout, double size, double view) =>
        size <= view ? (view - size) / 2 - layout : Math.Clamp(offset, view - size - layout, -layout);

    private void Apply()
    {
        _transform.Matrix = new Matrix(_scale, 0, 0, _scale, _offset.X, _offset.Y);
        Cursor = _panning ? Cursors.SizeAll : CanPan ? Cursors.Hand : null;
    }

    // ---- Mouse ----

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        if (Source is null) return;
        ZoomAt(e.GetPosition(this), _scale * Math.Pow(Step, e.Delta / 120.0));
        e.Handled = true; // not also scrolling or stepping to another picture
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (Source is null) return;
        var p = e.GetPosition(this);
        if (e.ClickCount == 2)
        {
            var native = NativeScale();
            if (!IsAtFit) ResetZoom();
            else CentreOn(p, Math.Abs(native - 1) < 0.01 ? 2 : native); // already 100 % at fit: 200 %
            _pressedAt = null;
            e.Handled = true;
            return;
        }
        _pressedAt = p;
        _pressedOffset = _offset;
        _panning = false;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_pressedAt is not { } start || e.LeftButton != MouseButtonState.Pressed) return;
        var p = e.GetPosition(this);
        if (!_panning)
        {
            if (!CanPan) return;
            if (Math.Abs(p.X - start.X) < SystemParameters.MinimumHorizontalDragDistance
                && Math.Abs(p.Y - start.Y) < SystemParameters.MinimumVerticalDragDistance) return;
            _panning = true;
            CaptureMouse();
        }
        _offset = _pressedOffset + (p - start);
        ClampAndApply();
        e.Handled = true;
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        var wasPanning = _panning;
        _pressedAt = null;
        _panning = false;
        if (!wasPanning) return; // a plain click: whatever it did before still happens
        ReleaseMouseCapture();
        Apply();
        e.Handled = true;
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        if (!_panning) return;
        _panning = false;
        _pressedAt = null;
        Apply();
    }

    // ---- Keyboard: + / - / 0, wherever focus is in the window except a text field ----

    private void HookWindow()
    {
        UnhookWindow();
        _window = Window.GetWindow(this);
        if (_window is not null) _window.PreviewKeyDown += Window_OnPreviewKeyDown;
    }

    private void UnhookWindow()
    {
        if (_window is not null) _window.PreviewKeyDown -= Window_OnPreviewKeyDown;
        _window = null;
    }

    private void Window_OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!IsVisible || Source is null || ActualWidth <= 0) return;
        if ((Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Alt)) != 0) return;
        if (e.OriginalSource is TextBoxBase or PasswordBox || Keyboard.FocusedElement is TextBoxBase or PasswordBox) return;
        var centre = new Point(ActualWidth / 2, ActualHeight / 2);
        switch (e.Key)
        {
            case Key.Add:
            case Key.OemPlus:
                ZoomAt(centre, _scale * Step);
                break;
            case Key.Subtract:
            case Key.OemMinus:
                ZoomAt(centre, _scale / Step);
                break;
            case Key.D0:
            case Key.NumPad0:
                ResetZoom();
                break;
            default:
                return;
        }
        e.Handled = true;
    }
}
