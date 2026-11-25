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

    // What is synchronised. A recipe's cover is a file path on this machine: it stays here.
    private static readonly SyncTable[] Tables =
    {
        new("Recipe", "Recipe", "CoverImagePath"),
        // One entry per day: two devices that each wrote "today" hold the same entry (joined on the date).
        new("JournalEntry", "JournalEntry") { MatchColumn = "EntryDate", MatchPrefix = 10 },
    };

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
            report = await Task.Run(async () =>
            {
                using var hub = new HubClient(settings.HubUrl, settings.Token);
                return await new SyncEngine(_dbPath, hub, Tables).RunAsync();
            });
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

        StateChanged?.Invoke();
        if (report is not null && report.Inserted + report.Updated + report.Deleted > 0) DataChanged?.Invoke(report);
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
