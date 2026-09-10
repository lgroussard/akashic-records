using System.Runtime.InteropServices;

namespace AkashicRecords.Infrastructure.WindowsIntegration;

/// <summary>
/// Reserves a strip of screen space along the top edge (the same mechanism the
/// taskbar itself uses), so maximized windows and Aero Snap leave that room free
/// instead of being covered by this app's window.
/// </summary>
public sealed class ScreenEdgeDock
{
    private readonly IntPtr _hwnd;
    private bool _isRegistered;

    public ScreenEdgeDock(IntPtr hwnd)
    {
        _hwnd = hwnd;
    }

    public void DockTop(int left, int top, int right, int bottom)
    {
        EnsureRegistered();
        SetPos(left, top, right, bottom);
    }

    // Reserves zero height instead of fully unregistering - repeatedly removing and
    // re-adding the same hwnd confuses the shell's internal appbar ordering and makes
    // the reserved strip grow on each cycle.
    public void ReleaseSpace(int left, int top, int right)
    {
        EnsureRegistered();
        SetPos(left, top, right, top);
    }

    public void Remove()
    {
        if (!_isRegistered) return;

        var data = NewData();
        NativeMethods.SHAppBarMessage(NativeMethods.ABM_REMOVE, ref data);
        _isRegistered = false;
    }

    private void EnsureRegistered()
    {
        if (_isRegistered) return;

        var data = NewData();
        NativeMethods.SHAppBarMessage(NativeMethods.ABM_NEW, ref data);
        _isRegistered = true;
    }

    private void SetPos(int left, int top, int right, int bottom)
    {
        var data = NewData();
        data.uEdge = NativeMethods.ABE_TOP;
        data.rc = new NativeMethods.RECT { Left = left, Top = top, Right = right, Bottom = bottom };

        // Deliberately skips ABM_QUERYPOS: querying while our own bar is still
        // registered makes the shell treat our existing reservation as an obstacle
        // to itself, pushing the position further down on every call.
        NativeMethods.SHAppBarMessage(NativeMethods.ABM_SETPOS, ref data);
    }

    private NativeMethods.APPBARDATA NewData() => new()
    {
        cbSize = Marshal.SizeOf<NativeMethods.APPBARDATA>(),
        hWnd = _hwnd
    };
}
