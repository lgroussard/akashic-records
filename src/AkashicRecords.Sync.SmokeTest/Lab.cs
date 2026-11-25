using System.Net;
using AkashicRecords.Hub;
using AkashicRecords.Sync;
using Microsoft.AspNetCore.Builder;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace AkashicRecords.Sync.SmokeTest;

/// <summary>A throw-away hub plus the devices created against it.</summary>
public sealed class Lab : IAsyncDisposable
{
    public static readonly SyncTable[] Tables =
    {
        // Same title = the same recipe, when two devices each already had it in slightly different versions.
        new("Recipe", "Recipe", "CoverImagePath") { MatchColumn = "Title" },
        // One entry per day: two devices that each wrote "today" hold the same entry.
        new("JournalEntry", "JournalEntry") { MatchColumn = "EntryDate", MatchPrefix = 10 },
        // A table whose rows point at a file, and the index of those files.
        new("Photo", "Photo") { MatchColumns = ["ImagePath"] },
        FileSync.IndexTable,
    };

    private WebApplication? _app;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "akashic-sync-smoke-" + Guid.NewGuid().ToString("N"));
    private readonly List<Device> _devices = new();

    public Uri Address { get; private set; } = null!;
    private HubStore Store => _app!.Services.GetRequiredService<HubStore>();

    public async Task StartAsync()
    {
        var options = new HubOptions { DataDir = Path.Combine(_dir, "hub"), Url = "http://127.0.0.1:0", EntityTypes = new() { "*" } };
        _app = HubApp.Build(new[] { "--Logging:LogLevel:Default=Warning" }, options);
        await _app.StartAsync();
        Address = new Uri(_app.Urls.First());
    }

    public long HubRows() => Store.HeadSeq();

    /// <summary>A device with its own database, paired with the hub through the real flow.</summary>
    public async Task<Device> Device(string name, bool withoutServings = false, string? existingDb = null, bool chosenMusic = false, int budgetMs = 25_000)
    {
        Directory.CreateDirectory(_dir);
        var db = Path.Combine(_dir, $"{name}-{Guid.NewGuid():N}.db");
        if (existingDb is not null)
        {
            SqliteConnection.ClearAllPools();
            File.Copy(existingDb, db);
        }
        using var anonymous = new HubClient(Address.ToString());
        var paired = await anonymous.PairAsync(Store.CreatePairCode(10), name, name);
        var device = new Device(name, db, Address.ToString(), paired.Token, create: existingDb is null, withoutServings, chosenMusic, budgetMs);
        _devices.Add(device);
        return device;
    }

    /// <summary>PC and phone holding the same recipes and already in sync.</summary>
    public async Task<(Device Pc, Device Phone)> SeededPair(params string[] titles)
    {
        var id = Guid.NewGuid().ToString("N")[..6];
        var pc = await Device("pc-" + id);
        var phone = await Device("phone-" + id);
        foreach (var t in titles) { pc.Add(t); phone.Add(t); }
        await pc.Sync();
        await phone.Sync();
        return (pc, phone);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var d in _devices) d.Dispose();
        if (_app is not null) { await _app.StopAsync(); await _app.DisposeAsync(); }
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }
}

public sealed record RecipeRow(string Title, string Notes, long Servings, string? Cover);

/// <summary>One replica: a SQLite file with the app's Recipe table, and the sync engine on it.</summary>
public sealed class Device : IDisposable
{
    private readonly string _cs;
    private readonly HubClient _hub;
    private readonly SyncEngine _engine;
    private readonly FlakyHandler _handler = new();
    private readonly bool _hasServings;

    public string Name { get; }
    public string DbPath { get; }

    public Device(string name, string dbPath, string hubUrl, string token, bool create, bool withoutServings, bool chosenMusic = false, int budgetMs = 25_000)
    {
        Name = name;
        DbPath = dbPath;
        _hasServings = !withoutServings;
        _cs = new SqliteConnectionStringBuilder { DataSource = dbPath }.ToString();
        if (create)
        {
            // The app's own table (SchemaInitializer), plus the columns it adds afterwards.
            Exec("""
                CREATE TABLE Recipe (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    Title TEXT NOT NULL,
                    Category TEXT NOT NULL DEFAULT '',
                    Ingredients TEXT NOT NULL,
                    Instructions TEXT NOT NULL,
                    Notes TEXT NOT NULL,
                    Tags TEXT NOT NULL,
                    CoverImagePath TEXT,
                    CreatedAt TEXT NOT NULL
                );
                """);
            if (_hasServings) Exec("ALTER TABLE Recipe ADD COLUMN Servings INTEGER NOT NULL DEFAULT 0");
            Exec("""
                CREATE TABLE JournalEntry (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    Title TEXT NOT NULL,
                    Text TEXT NOT NULL,
                    EntryDate TEXT NOT NULL,
                    Tags TEXT NOT NULL
                );
                """);
        }
        if (create) Exec("CREATE TABLE Photo (Id INTEGER PRIMARY KEY AUTOINCREMENT, ImagePath TEXT NOT NULL, Title TEXT NOT NULL)");
        BaseDir = dbPath + ".files";
        Directory.CreateDirectory(BaseDir);
        FileSync.EnsureSchema(dbPath);
        _hub = new HubClient(hubUrl, token, _handler);
        _engine = new SyncEngine(dbPath, _hub, Lab.Tables);
        _files = new FileSync(dbPath, BaseDir, _hub, new[] { ("Photo", "ImagePath") })
        {
            Music = chosenMusic ? MusicPolicy.Chosen : MusicPolicy.All,
            Budget = TimeSpan.FromMilliseconds(budgetMs),
        };
    }

    /// <summary>What the app does at each cycle: send the files, synchronise the rows, fetch the files.</summary>
    public async Task<SyncReport> Sync()
    {
        LastFiles = new FileSyncReport();
        await _files.PublishAsync(LastFiles);
        var report = await _engine.RunAsync();
        await _files.FetchAsync(LastFiles);
        return report;
    }

    public string BaseDir { get; }
    public FileSyncReport LastFiles { get; private set; } = new();
    private readonly FileSync _files;

    /// <summary>A photo as the app adds one: the file in the app folder, its path in the row.</summary>
    public void AddPhoto(string relativePath, byte[] content, string title = "photo")
    {
        var full = Path.Combine(BaseDir, relativePath.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, content);
        AddPhotoRow(relativePath, title);
    }

    public void AddPhotoRow(string relativePath, string title = "photo") =>
        Exec("INSERT INTO Photo (ImagePath, Title) VALUES ($p, $t)", ("$p", relativePath), ("$t", title));

    public byte[]? FileBytes(string relativePath)
    {
        var full = Path.Combine(BaseDir, relativePath.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar));
        return File.Exists(full) ? File.ReadAllBytes(full) : null;
    }

    /// <summary>The device's own choice for a track: keep its file here, or not.</summary>
    public void Keep(string relativePath, bool keep) => FileSync.SetKeep(DbPath, relativePath, keep);

    public void RemoveFileByHand(string relativePath) =>
        File.Delete(Path.Combine(BaseDir, relativePath.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar)));

    public long PhotoRows() => (long)Scalar("SELECT COUNT(*) FROM Photo")!;

    public void DropNextPushAnswer() => _handler.DropNextPushAnswer = true;

    public void Add(string title, long servings = 0)
    {
        Exec("INSERT INTO Recipe (Title, Category, Ingredients, Instructions, Notes, Tags, CreatedAt) VALUES ($t, 'Plat', 'ingredients de ' || $t, 'etapes', '', 'test', '2026-11-25T12:00:00.0000000')", ("$t", title));
        if (_hasServings && servings != 0) Exec("UPDATE Recipe SET Servings = $s WHERE Title = $t", ("$s", servings), ("$t", title));
    }

    public void Edit(string title, string notes) => Exec("UPDATE Recipe SET Notes = $n WHERE Title = $t", ("$n", notes), ("$t", title));

    /// <summary>Sets one column of a recipe (column name comes from the test itself).</summary>
    public void Set(string title, string column, string value) => Exec($"UPDATE Recipe SET {column} = $v WHERE Title = $t", ("$v", value), ("$t", title));
    public string? Field(string title, string column) => Query($"SELECT {column} FROM Recipe WHERE Title = $t", r => r.IsDBNull(0) ? null : r.GetString(0), ("$t", title)).FirstOrDefault();

    // The journal: one entry per day. `time` differs between devices, as it does for real.
    public void WriteDay(string day, string text, string title = "", string time = "T00:00:00.0000000") =>
        Exec("INSERT INTO JournalEntry (Title, Text, EntryDate, Tags) VALUES ($ti, $tx, $d, '')", ("$ti", title), ("$tx", text), ("$d", day + time));
    public void RewriteDay(string day, string text) => Exec("UPDATE JournalEntry SET Text = $tx WHERE substr(EntryDate, 1, 10) = $d", ("$tx", text), ("$d", day));
    public List<string> Day(string day) => Query("SELECT Text FROM JournalEntry WHERE substr(EntryDate, 1, 10) = $d ORDER BY Id", r => r.GetString(0), ("$d", day));

    /// <summary>Versions set aside by field-level conflicts on this device.</summary>
    public List<SyncConflict> SetAside() => SyncEngine.Conflicts(DbPath);

    /// <summary>Puts the sync bookkeeping back to what the first version of the engine wrote (no ancestor kept).</summary>
    public void ForgetAncestors() => Exec("UPDATE sync_map SET synced_values = NULL");
    public void SetCover(string title, string path) => Exec("UPDATE Recipe SET CoverImagePath = $p WHERE Title = $t", ("$p", path), ("$t", title));
    public void DeleteByTitle(string title) => Exec("DELETE FROM Recipe WHERE Title = $t", ("$t", title));

    public long Count() => (long)Scalar("SELECT COUNT(*) FROM Recipe")!;
    public List<string> Titles() => Query("SELECT Title FROM Recipe ORDER BY Title", r => r.GetString(0));

    public RecipeRow? Get(string title) => Query(
        $"SELECT Title, Notes, {(_hasServings ? "Servings" : "0")}, CoverImagePath FROM Recipe WHERE Title = $t",
        r => new RecipeRow(r.GetString(0), r.GetString(1), r.GetInt64(2), r.IsDBNull(3) ? null : r.GetString(3)), ("$t", title)).FirstOrDefault();

    /// <summary>The synchronised content of the table, without ids or local-only columns.</summary>
    public string Dump() => string.Join("\n", Query(
        $"SELECT Title || '|' || Category || '|' || Ingredients || '|' || Instructions || '|' || Notes || '|' || Tags || '|' || CreatedAt || '|' || {(_hasServings ? "Servings" : "0")} FROM Recipe ORDER BY 1",
        r => r.GetString(0)));

    private void Exec(string sql, params (string, object)[] args)
    {
        using var c = Open();
        using var cmd = Command(c, sql, args);
        cmd.ExecuteNonQuery();
    }

    private object? Scalar(string sql)
    {
        using var c = Open();
        using var cmd = Command(c, sql);
        return cmd.ExecuteScalar();
    }

    private List<T> Query<T>(string sql, Func<SqliteDataReader, T> read, params (string, object)[] args)
    {
        using var c = Open();
        using var cmd = Command(c, sql, args);
        using var r = cmd.ExecuteReader();
        var list = new List<T>();
        while (r.Read()) list.Add(read(r));
        return list;
    }

    private SqliteConnection Open()
    {
        var c = new SqliteConnection(_cs);
        c.Open();
        return c;
    }

    private static SqliteCommand Command(SqliteConnection c, string sql, params (string Name, object Value)[] args)
    {
        var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (n, v) in args) cmd.Parameters.AddWithValue(n, v);
        return cmd;
    }

    public void Dispose() => _hub.Dispose();

    /// <summary>Lets a test lose one answer on the way back, after the hub has done its work.</summary>
    private sealed class FlakyHandler : HttpClientHandler
    {
        public bool DropNextPushAnswer;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var response = await base.SendAsync(request, ct);
            if (DropNextPushAnswer && request.RequestUri!.AbsolutePath.EndsWith("/sync/push"))
            {
                DropNextPushAnswer = false;
                response.Dispose();
                throw new HttpRequestException("connexion coupee (simulee)");
            }
            return response;
        }
    }
}
