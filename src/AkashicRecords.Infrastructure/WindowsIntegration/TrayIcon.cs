using System.Windows.Forms;

namespace AkashicRecords.Infrastructure.WindowsIntegration;

/// <summary>
/// Wraps the tray icon and its context menu. Public surface exposes only events/methods,
/// so consumers don't need to reference System.Windows.Forms themselves.
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private readonly NotifyIcon _notifyIcon;
    private readonly ToolStripMenuItem _startWithWindowsItem;
    private readonly ToolStripMenuItem _filmWidgetItem;
    private readonly ToolStripMenuItem _remindersItem;

    public event Action? ToggleRequested;
    public event Action? ExitRequested;
    public event Action<bool>? StartWithWindowsChanged;
    public event Action<bool>? FilmWidgetToggled;
    public event Action<bool>? RemindersToggled;
    public event Action? DownloaderRequested;

    public TrayIcon(string tooltip, bool startWithWindowsInitiallyChecked)
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("Show / Hide", null, (_, _) => ToggleRequested?.Invoke());

        _filmWidgetItem = new ToolStripMenuItem("Film du jour") { CheckOnClick = true };
        _filmWidgetItem.CheckedChanged += FilmWidgetItem_OnCheckedChanged;
        menu.Items.Add(_filmWidgetItem);

        _remindersItem = new ToolStripMenuItem("Rappels bureau") { CheckOnClick = true };
        _remindersItem.CheckedChanged += RemindersItem_OnCheckedChanged;
        menu.Items.Add(_remindersItem);

        menu.Items.Add("Téléchargements", null, (_, _) => DownloaderRequested?.Invoke());

        _startWithWindowsItem = new ToolStripMenuItem("Start with Windows")
        {
            CheckOnClick = true,
            Checked = startWithWindowsInitiallyChecked
        };
        _startWithWindowsItem.CheckedChanged += (_, _) =>
            StartWithWindowsChanged?.Invoke(_startWithWindowsItem.Checked);
        menu.Items.Add(_startWithWindowsItem);

        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => ExitRequested?.Invoke());

        _notifyIcon = new NotifyIcon
        {
            Icon = System.Drawing.SystemIcons.Application,
            Visible = true,
            Text = tooltip,
            ContextMenuStrip = menu
        };
        _notifyIcon.DoubleClick += (_, _) => ToggleRequested?.Invoke();
    }

    // Set the initial checkbox state without re-raising the toggle event.
    public void SetFilmChecked(bool value)
    {
        if (_filmWidgetItem.Checked == value) return;
        _filmWidgetItem.CheckedChanged -= FilmWidgetItem_OnCheckedChanged;
        _filmWidgetItem.Checked = value;
        _filmWidgetItem.CheckedChanged += FilmWidgetItem_OnCheckedChanged;
    }

    public void SetRemindersChecked(bool value)
    {
        if (_remindersItem.Checked == value) return;
        _remindersItem.CheckedChanged -= RemindersItem_OnCheckedChanged;
        _remindersItem.Checked = value;
        _remindersItem.CheckedChanged += RemindersItem_OnCheckedChanged;
    }

    private void FilmWidgetItem_OnCheckedChanged(object? sender, EventArgs e) =>
        FilmWidgetToggled?.Invoke(_filmWidgetItem.Checked);

    private void RemindersItem_OnCheckedChanged(object? sender, EventArgs e) =>
        RemindersToggled?.Invoke(_remindersItem.Checked);

    public void Dispose() => _notifyIcon.Dispose();
}
