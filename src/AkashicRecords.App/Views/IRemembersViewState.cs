namespace AkashicRecords.App.Views;

/// <summary>
/// The common "reopen where I left off" rule, shared by every section view:
/// <list type="number">
///   <item>The view keeps its pointer in <c>AppConfig</c> (sub-tab + open item) and rewrites it on every
///         navigation step — tab switch, item opened, item closed — so the pointer always matches the screen.</item>
///   <item>MainWindow calls <see cref="CaptureViewState"/> on the outgoing view whenever the section is left,
///         the window is hidden or the app exits, then writes the config to disk. A step the view forgot to
///         record is therefore still caught, and a crash loses at most the current visit.</item>
///   <item>On Loaded the view restores tab first, then item; an id that no longer exists falls back to the
///         tab's default screen, and an item pointer belonging to another tab is ignored.</item>
///   <item>Explicit navigation (global search, headless screenshot) wins over the restore.</item>
/// </list>
/// </summary>
public interface IRemembersViewState
{
    /// <summary>Writes the currently displayed sub-tab and item into the shared AppConfig (no disk write).</summary>
    void CaptureViewState();
}
