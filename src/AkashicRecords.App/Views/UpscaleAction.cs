using System.Windows;
using AkashicRecords.App.Sync;
using AkashicRecords.Sync;

namespace AkashicRecords.App.Views;

// "Agrandir ×4" on one picture already in the app (a row of Photo, JournalPhoto, RefImage or CanvasElement): the hub
// upscales it and adds the result as a new row next to the original, which arrives at the next sync.
public static class UpscaleAction
{
    public const string Label = "Agrandir ×4";
    public const string Confirmation = "Envoyée à l'agrandissement : la version ×4 apparaîtra à côté de l'original dans quelques instants.";

    /// <summary>Only offered when this computer is paired with the hub.</summary>
    public static bool Available => SyncService.Instance.Settings.IsPaired;

    /// <summary>
    /// Sends the request with `control` disabled meanwhile, then says how it went: through `report` when the view has
    /// its own feedback line, otherwise in a message box.
    /// </summary>
    public static async Task RequestAsync(UIElement? control, string table, long localId, Action<string>? report = null)
    {
        if (control is not null) control.IsEnabled = false;
        string message;
        try
        {
            await ComputeService.Instance.RequestFromAsync(table, localId);
            message = Confirmation;
        }
        catch (HubException ex)
        {
            message = ex.Message;
        }
        catch (Exception ex)
        {
            // Called from click handlers: anything unexpected is said, never left to crash the app.
            message = "L'agrandissement n'a pas pu être demandé : " + ex.Message;
        }
        finally
        {
            if (control is not null) control.IsEnabled = true;
        }
        if (report is not null) report(message);
        else
        {
            var owner = control is DependencyObject d ? Window.GetWindow(d) : null;
            if (owner is not null) MessageBox.Show(owner, message, Label);
            else MessageBox.Show(message, Label);
        }
    }
}
