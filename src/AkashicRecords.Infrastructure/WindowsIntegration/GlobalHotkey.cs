using System.Windows.Forms;

namespace AkashicRecords.Infrastructure.WindowsIntegration;

/// <summary>
/// Registers a system-wide keyboard shortcut backed by a hidden message-only window.
/// Public surface has no WinForms/WPF types, so consumers only need a project reference.
/// </summary>
public sealed class GlobalHotkey : IDisposable
{
    // Public aliases for the Win32 modifier/virtual-key constants consumers need.
    public const uint ModAlt = NativeMethods.MOD_ALT;
    public const uint ModControl = NativeMethods.MOD_CONTROL;
    public const uint VkSpace = NativeMethods.VK_SPACE;

    private readonly MessageOnlyWindow _window;
    private readonly int _id;

    public event Action? Pressed;

    public GlobalHotkey(int id, uint modifiers, uint virtualKey)
    {
        _id = id;
        _window = new MessageOnlyWindow();
        _window.HotkeyPressed += hotkeyId =>
        {
            if (hotkeyId == _id) Pressed?.Invoke();
        };

        if (!NativeMethods.RegisterHotKey(_window.Handle, id, modifiers, virtualKey))
        {
            throw new InvalidOperationException(
                $"Failed to register global hotkey {id} - it may already be in use by another application.");
        }
    }

    public void Dispose()
    {
        NativeMethods.UnregisterHotKey(_window.Handle, _id);
        _window.Dispose();
    }

    private sealed class MessageOnlyWindow : NativeWindow, IDisposable
    {
        private static readonly IntPtr HwndMessage = new(-3);

        public event Action<int>? HotkeyPressed;

        public MessageOnlyWindow()
        {
            CreateHandle(new CreateParams { Parent = HwndMessage });
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == NativeMethods.WM_HOTKEY)
            {
                HotkeyPressed?.Invoke(m.WParam.ToInt32());
            }

            base.WndProc(ref m);
        }

        public void Dispose() => DestroyHandle();
    }
}
