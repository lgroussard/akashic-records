using System.Windows;
using System.Windows.Threading;

namespace AkashicRecords.App;

/// <summary>
/// Keeps every small window of the app (music player, downloads, widgets, Serveur…) within reach.
///
/// They have no Windows title bar (WindowStyle None), so two things used to strand them: dragged against the top of
/// the screen, Windows "snaps" them to full screen, and with no maximise/restore button there was no way back but to
/// restart the app; dragged too far off an edge, their own title bar was out of reach. Now a full-screen snap is undone
/// at once, and a window let go too far out is brought back so that its top strip stays on screen.
///
/// Attached to every window when it loads (App.OnStartup); the main window, which places itself, is left alone.
/// </summary>
internal static class WindowKeeper
{
    private const double KeepVisible = 80; // pixels of the window that must stay on screen, sideways and below

    public static void Register() =>
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent, new RoutedEventHandler((s, _) =>
        {
            if (s is Window w and not MainWindow) Attach(w);
        }));

    private static readonly DependencyProperty AttachedProperty =
        DependencyProperty.RegisterAttached("WindowKeeperAttached", typeof(bool), typeof(WindowKeeper));

    public static void Attach(Window w)
    {
        if ((bool)w.GetValue(AttachedProperty)) return;
        w.SetValue(AttachedProperty, true);

        w.StateChanged += (_, _) =>
        {
            // No way out of full screen without a title bar: never stay there.
            if (w.WindowState == WindowState.Maximized)
                w.Dispatcher.BeginInvoke(() => w.WindowState = WindowState.Normal, DispatcherPriority.Background);
        };

        // Checked once the window has stopped moving (a drag fires this many times).
        var settle = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        settle.Tick += (_, _) => { settle.Stop(); BringBack(w); };
        w.LocationChanged += (_, _) => { settle.Stop(); settle.Start(); };
        w.Closed += (_, _) => settle.Stop();
        BringBack(w);
    }

    private static void BringBack(Window w)
    {
        if (w.WindowState != WindowState.Normal || double.IsNaN(w.Left) || double.IsNaN(w.Top)) return;
        double left = SystemParameters.VirtualScreenLeft, top = SystemParameters.VirtualScreenTop;
        double right = left + SystemParameters.VirtualScreenWidth, bottom = top + SystemParameters.VirtualScreenHeight;
        var width = double.IsNaN(w.ActualWidth) || w.ActualWidth <= 0 ? w.Width : w.ActualWidth;

        var x = Math.Clamp(w.Left, left - width + KeepVisible, right - KeepVisible);
        var y = Math.Clamp(w.Top, top, bottom - KeepVisible); // the top strip (where it is dragged from) stays on screen
        if (Math.Abs(x - w.Left) > 0.5) w.Left = x;
        if (Math.Abs(y - w.Top) > 0.5) w.Top = y;
    }
}
