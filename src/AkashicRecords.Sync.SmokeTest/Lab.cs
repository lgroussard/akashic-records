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
    public static readonly SyncTable[] Tables = { new("Recipe", "Recipe", "CoverImagePath") };

    private WebApplication? _app;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "akashic-sync-smoke-" + Guid.NewGuid().ToString("N"));
    private readonly List<Device> _devices = new();

    public Uri Address { get; private set; } = null!;
    private HubStore Store => _app!.Services.GetRequiredService<HubStore>();

    public async Task StartAsync()
    {
        var options = new HubOptions { DataDir = Path.Combine(_dir, "hub"), Url = "http://127.0.0.1:0", EntityTypes = new() { "Recipe" } };
        _app = HubApp.Build(new[] { "--Logging:LogLevel:Default=Warning" }, options);
        await _app.StartAsync();
        Address = new Uri(_app.Urls.First());
    }

    public long HubRows() => Store.HeadSeq();

    /// <summary>A device with its own database, paired with the hub through the real flow.</summary>
    public async Task<Device> Device(string name, bool withoutServings = false, string? existingDb = null)
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
        var device = new Device(name, db, Address.ToString(), paired.Token, create: existingDb is null, withoutServings);
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

    public Device(string name, string dbPath, string hubUrl, string token, bool create, bool withoutServings)
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
        }
        _hub = new HubClient(hubUrl, token, _handler);
        _engine = new SyncEngine(dbPath, _hub, Lab.Tables);
    }

    public Task<SyncReport> Sync() => _engine.RunAsync();

    public void DropNextPushAnswer() => _handler.DropNextPushAnswer = true;

    public void Add(string title, long servings = 0)
    {
        Exec("INSERT INTO Recipe (Title, Category, Ingredients, Instructions, Notes, Tags, CreatedAt) VALUES ($t, 'Plat', 'ingredients de ' || $t, 'etapes', '', 'test', '2026-11-25T12:00:00.0000000')", ("$t", title));
        if (_hasServings && servings != 0) Exec("UPDATE Recipe SET Servings = $s WHERE Title = $t", ("$s", servings), ("$t", title));
    }

    public void Edit(string title, string notes) => Exec("UPDATE Recipe SET Notes = $n WHERE Title = $t", ("$n", notes), ("$t", title));
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
