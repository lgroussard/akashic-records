using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace AkashicRecords.App;

// ShowInTaskbar="False" still leaves a window in Alt+Tab; the tool-window style is what removes it.
// Registered once for every Window of the app (named windows and ad-hoc dialogs alike).
internal static class AltTabHider
{
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_APPWINDOW = 0x00040000;
    private const uint SWP_NOSIZE = 0x0001, SWP_NOMOVE = 0x0002, SWP_NOZORDER = 0x0004,
                       SWP_NOACTIVATE = 0x0010, SWP_FRAMECHANGED = 0x0020;

    public static void RegisterForAllWindows() =>
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent,
            new RoutedEventHandler((sender, _) => { if (sender is Window w) Apply(w); }));

    private static void Apply(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;
        var style = GetWindowLong(hwnd, GWL_EXSTYLE);
        var hidden = (style | WS_EX_TOOLWINDOW) & ~WS_EX_APPWINDOW;
        if (hidden == style) return;
        SetWindowLong(hwnd, GWL_EXSTYLE, hidden);
        // The window is already shown at Loaded: commit the style change without moving/activating it.
        SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0, SWP_NOSIZE | SWP_NOMOVE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);
    }

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);
}
