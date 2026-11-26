using System.IO;
using System.Windows.Threading;
using AkashicRecords.Sync;

namespace AkashicRecords.App.Sync;

/// <summary>
/// The app's link to the sync hub (see MOBILITE-SYNC.md and AkashicRecords.Sync): holds the pairing,
/// runs the engine in the background at a steady pace, and tells the UI when rows arrived.
/// Not paired = does nothing at all, so the app behaves exactly as before.
/// </summary>
public sealed class SyncService
{
    public static SyncService Instance { get; } = new();

    // What is synchronised: every table of the catalog. File paths travel as they are (same relative form on
    // the PC and the phone); the files themselves are another matter.
    private static readonly IReadOnlyList<SyncTable> Tables = AkashicCatalog.AllTables;

    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);

    private readonly string _settingsPath = Path.Combine(AppContext.BaseDirectory, "config", "sync.json");
    private readonly string _dbPath = Path.Combine(AppContext.BaseDirectory, "data", "akashic.db");
    private readonly SemaphoreSlim _gate = new(1, 1);
    private DispatcherTimer? _timer;

    private SyncService() => Settings = SyncSettings.Load(_settingsPath);

    public SyncSettings Settings { get; private set; }
    public DateTime? LastSuccess { get; private set; }
    public string? LastError { get; private set; }
    public SyncReport? LastReport { get; private set; }
    public FileSyncReport? LastFiles { get; private set; }

    /// <summary>Pairing, a finished run or an error: the settings pane redraws its status line.</summary>
    public event Action? StateChanged;

    /// <summary>A sync is about to run (UI thread): open views save what is typed and not yet saved, so the
    /// engine merges it instead of the view later writing an outdated copy over what arrived.</summary>
    public event Action? BeforeSync;

    /// <summary>Rows were added, changed or removed here by a sync: open views should reload.</summary>
    public event Action<SyncReport>? DataChanged;

    /// <summary>One line for the settings pane.</summary>
    public string StatusText
    {
        get
        {
            if (!Settings.IsPaired) return "Non appairé : cet ordinateur ne synchronise rien.";
            var head = $"Appairé avec {Settings.HubUrl}.";
            if (LastError is not null) return $"{head} Dernier essai en échec : {LastError}";
            if (LastSuccess is null) return $"{head} Pas encore synchronisé.";
            var what = LastReport is null || LastReport.NothingToDo
                ? "rien à échanger"
                : $"{LastReport.Pushed} envoyé(s), {LastReport.Inserted + LastReport.Updated + LastReport.Deleted} reçu(s)";
            if (LastFiles is { } f && f.Uploaded + f.Downloaded > 0) what += $" ; fichiers : {f.Uploaded} envoyé(s), {f.Downloaded} reçu(s)";
            // A music library goes up in slices, one per cycle: say how much is left rather than look stuck.
            if (LastFiles is { ToSend: > 0 } or { ToFetch: > 0 })
                what += $" ; en cours : encore {LastFiles.ToSend} fichier(s) à envoyer, {LastFiles.ToFetch} à recevoir";
            var aside = SetAside().Count;
            var note = aside == 0 ? "" : $" {aside} version(s) mise(s) de côté à relire ci-dessous.";
            return $"{head} Dernière synchro à {LastSuccess:HH:mm:ss} : {what}.{note}";
        }
    }

    /// <summary>Starts the background pace. Called once the main window is up (never in screenshot mode).</summary>
    public void Start()
    {
        if (_timer is not null) return;
        _timer = new DispatcherTimer { Interval = Interval };
        _timer.Tick += async (_, _) => await RunAsync();
        _timer.Start();
        _ = RunAsync();
    }

    /// <summary>One sync now. Returns null when not paired or when a run is already going.</summary>
    public async Task<SyncReport?> RunAsync()
    {
        if (!Settings.IsPaired) return null;
        if (!await _gate.WaitAsync(0)) return null;
        SyncReport? report = null;
        try
        {
            try { BeforeSync?.Invoke(); } catch { /* a view that cannot save must not stop the sync */ }
            var settings = Settings;
            var files = new FileSyncReport();
            report = await Task.Run(async () =>
            {
                using var hub = new HubClient(settings.HubUrl, settings.Token);
                // The files the rows point at: sent before the rows, fetched after (see FileSync).
                FileSync.EnsureSchema(_dbPath);
                // This computer holds the whole music library: every track of the catalogue is wanted here.
                var fileSync = new FileSync(_dbPath, AppContext.BaseDirectory, hub, AkashicCatalog.FileColumns) { Music = MusicPolicy.All };
                await fileSync.PublishAsync(files);
                var rows = await new SyncEngine(_dbPath, hub, Tables).RunAsync();
                await fileSync.FetchAsync(files);
                return rows;
            });
            LastFiles = files;
            LastReport = report;
            LastSuccess = DateTime.Now;
            LastError = null;
        }
        catch (Exception ex) when (ex is HubException or SyncSafetyException)
        {
            LastError = ex.Message;
        }
        catch (Exception ex)
        {
            LastError = $"{ex.GetType().Name} : {ex.Message}";
        }
        finally
        {
            _gate.Release();
        }

        // Download requests made while the hub could not be reached: it can now.
        if (report is not null) _ = HubDownloadQueue.SendQueuedAsync();
        StateChanged?.Invoke();
        if (report is not null && report.Inserted + report.Updated + report.Deleted + (LastFiles?.Downloaded ?? 0) > 0) DataChanged?.Invoke(report);
        return report;
    }

    /// <summary>Versions that lost a field-level conflict (same field changed here and elsewhere): kept so the
    /// user can read them and copy back what matters.</summary>
    public List<SyncConflict> SetAside()
    {
        try { return File.Exists(_dbPath) ? SyncEngine.Conflicts(_dbPath) : new(); }
        catch (Microsoft.Data.Sqlite.SqliteException) { return new(); }
    }

    public void Dismiss(long id)
    {
        SyncEngine.DismissConflict(_dbPath, id);
        StateChanged?.Invoke();
    }

    /// <summary>Pairs this computer with a hub (one-time code shown on the hub), then syncs once.
    /// Throws HubException with a message fit for the user when the hub or the code is wrong.</summary>
    public async Task PairAsync(string hubUrl, string code)
    {
        var current = Settings;
        Settings = await Task.Run(() => SyncSettings.PairAsync(current, hubUrl, code, Environment.MachineName));
        Settings.Save(_settingsPath);
        LastError = null;
        LastSuccess = null;
        LastReport = null;
        StateChanged?.Invoke();
        await RunAsync();
    }

    /// <summary>Forgets the hub on this computer. Nothing is deleted, here or on the hub; the device
    /// id is kept so that pairing again resumes where it stopped.</summary>
    public void Unpair()
    {
        Settings = new SyncSettings { DeviceId = Settings.DeviceId, DeviceName = Settings.DeviceName };
        Settings.Save(_settingsPath);
        LastError = null;
        LastSuccess = null;
        LastReport = null;
        StateChanged?.Invoke();
    }
}
