using System.Diagnostics;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;

namespace AkashicRecords.Sync;

public sealed class FileSyncReport
{
    public int Uploaded, Downloaded;
    /// <summary>Listed files not here that the hub could not give yet (their device has not sent them).</summary>
    public int Missing;
    /// <summary>Files here still to send: the run stopped at its time budget and goes on at the next one.</summary>
    public int ToSend;
    /// <summary>Files to fetch left for the next run, for the same reason.</summary>
    public int ToFetch;
    public override string ToString() =>
        $"fichiers : {Uploaded} envoye(s), {Downloaded} recu(s), {Missing} pas encore disponible(s), reste {ToSend} a envoyer et {ToFetch} a recevoir";
}

/// <summary>Which music files a device wants.</summary>
public enum MusicPolicy
{
    /// <summary>Every track of the catalogue, except those removed from this device by hand (the PC).</summary>
    All,
    /// <summary>Only the tracks explicitly kept on this device (the phone).</summary>
    Chosen,
}

/// <summary>
/// The files the rows point at (covers, photos, tickets, books, music). The database stores them as
/// paths relative to the app folder, in the same form on the PC and the phone ("media\covers\x.jpg",
/// "books\x.epub", "references/x.jpg", "music\Album\x.mp3"), so a file is identified by its path:
///
///  - `sync_file` lists, for each referenced path, the SHA-256 and size of its content. It is an
///    ordinary synchronised table (entity type "MediaFile"), so the list itself travels through the
///    row engine like any other data;
///  - before the rows are synchronised, every referenced file present here and not listed yet is
///    hashed, its content sent to the hub (once: the hub stores a content under its hash), and
///    listed;
///  - after, the listed files missing here are fetched from the hub.
///
/// Music is the one kind of file a device may choose not to hold (`sync_keep`, MusicPolicy): the
/// catalogue of tracks is everywhere, the audio only where it is wanted. A track whose file was here
/// and has been removed by hand is remembered as "not kept here" instead of being fetched again.
///
/// A library is gigabytes: each call works for a limited time and picks up where it stopped at the
/// next one, small files first, so the rows keep synchronising while the music goes up.
/// A file is never deleted and never overwritten by this: a path is created once and its content
/// does not change.
/// The phone does the same in android/.../sync/FileSync.kt.
/// </summary>
public sealed class FileSync
{
    /// <summary>The index of files, synchronised with the rest (add it to the engine's tables).</summary>
    public static readonly SyncTable IndexTable = new("MediaFile", "sync_file") { MatchColumns = ["Path"] };

    /// <summary>Only these top folders of the app folder hold synchronised files.</summary>
    private static readonly string[] Roots = ["media/", "books/", "references/", "music/"];

    private readonly string _connectionString;
    private readonly string _baseDir;
    private readonly HubClient _hub;
    private readonly IReadOnlyList<(string Table, string Column)> _columns;

    public MusicPolicy Music { get; init; } = MusicPolicy.All;

    /// <summary>How long one call may work before leaving the rest for the next one.</summary>
    public TimeSpan Budget { get; init; } = TimeSpan.FromSeconds(25);

    public FileSync(string dbPath, string baseDir, HubClient hub, IEnumerable<(string Table, string Column)> fileColumns)
    {
        _connectionString = new SqliteConnectionStringBuilder { DataSource = dbPath }.ToString();
        _baseDir = baseDir;
        _hub = hub;
        _columns = fileColumns.ToList();
    }

    /// <summary>Creates `sync_file` and this device's own tables. Call before the row engine loads its tables.</summary>
    public static void EnsureSchema(string dbPath)
    {
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = dbPath }.ToString());
        db.Open();
        Exec(db, """
            CREATE TABLE IF NOT EXISTS sync_file(
                Id   INTEGER PRIMARY KEY AUTOINCREMENT,
                Path TEXT    NOT NULL UNIQUE,   -- relative to the app folder, with forward slashes
                Hash TEXT    NOT NULL,          -- SHA-256 of the content
                Size INTEGER NOT NULL
            );
            CREATE TABLE IF NOT EXISTS sync_file_local(      -- this device only: what was hashed and sent, or fetched
                path     TEXT PRIMARY KEY,
                size     INTEGER NOT NULL,
                modified INTEGER NOT NULL,
                hash     TEXT NOT NULL,
                uploaded INTEGER NOT NULL DEFAULT 0
            );
            CREATE TABLE IF NOT EXISTS sync_keep(            -- this device only: music wanted here (1) or not (0)
                path TEXT PRIMARY KEY,
                keep INTEGER NOT NULL
            );
            """);
    }

    /// <summary>"media\covers\x.jpg" -> "media/covers/x.jpg"; null for anything that is not a plain
    /// path under one of the synchronised folders.</summary>
    public static string? Normalize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var clean = path.Replace('\\', '/').TrimStart('/');
        if (clean.Split('/').Any(part => part is "" or "." or "..")) return null;
        return Roots.Any(r => clean.StartsWith(r, StringComparison.OrdinalIgnoreCase)) ? clean : null;
    }

    public static bool IsMusic(string normalized) => normalized.StartsWith("music/", StringComparison.OrdinalIgnoreCase);

    private string FullPath(string normalized) => Path.Combine(_baseDir, normalized.Replace('/', Path.DirectorySeparatorChar));

    /// <summary>Marks a track as wanted on this device or not (the choice a device makes for its music).</summary>
    public static void SetKeep(string dbPath, string path, bool keep)
    {
        if (Normalize(path) is not { } normalized) return;
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = dbPath }.ToString());
        db.Open();
        Exec(db, "INSERT INTO sync_keep(path, keep) VALUES ($p, $k) ON CONFLICT(path) DO UPDATE SET keep = excluded.keep", ("$p", normalized), ("$k", keep ? 1 : 0));
    }

    /// <summary>Hashes, sends and lists the referenced files that are here. Run BEFORE the row engine.</summary>
    public async Task PublishAsync(FileSyncReport report, CancellationToken ct = default)
    {
        using var db = new SqliteConnection(_connectionString);
        db.Open();
        var clock = Stopwatch.StartNew();

        // Everything already done is skipped by one query; the rest, small things before the music.
        var done = new HashSet<string>(StringComparer.Ordinal);
        using (var cmd = db.CreateCommand())
        {
            cmd.CommandText = "SELECT l.path FROM sync_file_local l JOIN sync_file f ON f.Path = l.path WHERE l.uploaded = 1";
            using var r = cmd.ExecuteReader();
            while (r.Read()) done.Add(r.GetString(0));
        }
        var todo = ReferencedPaths(db).Where(p => !done.Contains(p)).OrderBy(IsMusic).ThenBy(p => p, StringComparer.Ordinal).ToList();

        var handled = 0;
        for (var i = 0; i < todo.Count; i++)
        {
            var path = todo[i];
            var file = new FileInfo(FullPath(path));
            if (!file.Exists) continue;
            // At least one file per call, whatever the budget: a run always moves forward.
            if (handled++ > 0 && clock.Elapsed > Budget)
            {
                report.ToSend = todo.Skip(i).Count(p => File.Exists(FullPath(p)));
                return;
            }

            var (hash, uploaded) = Cached(db, path, file);
            if (hash is null)
            {
                hash = await HashAsync(file.FullName, ct);
                Exec(db, "INSERT OR REPLACE INTO sync_file_local(path, size, modified, hash, uploaded) VALUES ($p, $s, $m, $h, 0)",
                    ("$p", path), ("$s", file.Length), ("$m", file.LastWriteTimeUtc.Ticks), ("$h", hash));
                uploaded = false;
            }
            if (!uploaded)
            {
                // The content first, the listing after: a device that sees the listing can fetch at once.
                if (!await _hub.HasFileAsync(hash, ct))
                {
                    await _hub.UploadFileAsync(file.FullName, hash, ct);
                    report.Uploaded++;
                }
                Exec(db, "UPDATE sync_file_local SET uploaded = 1 WHERE path = $p", ("$p", path));
            }
            // Listed once. A path whose listing says another content keeps the listing: a clash means
            // the same file re-encoded somewhere, not a new one.
            Exec(db, "INSERT OR IGNORE INTO sync_file(Path, Hash, Size) VALUES ($p, $h, $s)", ("$p", path), ("$h", hash), ("$s", file.Length));
        }
    }

    /// <summary>Fetches the listed files that are missing here and wanted here. Run AFTER the row engine.</summary>
    public async Task FetchAsync(FileSyncReport report, CancellationToken ct = default)
    {
        using var db = new SqliteConnection(_connectionString);
        db.Open();
        var clock = Stopwatch.StartNew();

        var listed = new List<(string Path, string Hash, bool WasHere, long? Keep)>();
        using (var cmd = db.CreateCommand())
        {
            cmd.CommandText = """
                SELECT f.Path, f.Hash, l.path IS NOT NULL, k.keep
                FROM sync_file f
                LEFT JOIN sync_file_local l ON l.path = f.Path
                LEFT JOIN sync_keep k ON k.path = f.Path
                ORDER BY f.Path
                """;
            using var r = cmd.ExecuteReader();
            while (r.Read()) listed.Add((r.GetString(0), r.GetString(1), r.GetInt64(2) != 0, r.IsDBNull(3) ? null : r.GetInt64(3)));
        }

        var wanted = new List<(string Path, string Hash, string Target)>();
        foreach (var (raw, hash, wasHere, keep) in listed)
        {
            var path = Normalize(raw);
            if (path is null || hash.Length != 64 || !hash.All(Uri.IsHexDigit)) continue; // not something to write to disk
            var target = FullPath(path);
            if (File.Exists(target)) continue;
            if (IsMusic(path))
            {
                if (keep == 0) continue;
                if (wasHere && keep != 1)
                {
                    // It was here and is gone: removed from this device on purpose. Remembered, not undone
                    // (unless it is explicitly asked for again: keep = 1).
                    Exec(db, "INSERT INTO sync_keep(path, keep) VALUES ($p, 0) ON CONFLICT(path) DO UPDATE SET keep = 0", ("$p", path));
                    continue;
                }
                if (Music == MusicPolicy.Chosen && keep != 1) continue;
            }
            wanted.Add((path, hash.ToLowerInvariant(), target));
        }

        var order = wanted.OrderBy(w => IsMusic(w.Path)).ToList();
        for (var i = 0; i < order.Count; i++)
        {
            if (i > 0 && clock.Elapsed > Budget) { report.ToFetch = order.Count - i; return; }
            var (path, hash, target) = order[i];
            if (await _hub.DownloadFileAsync(hash, target, ct))
            {
                var file = new FileInfo(target);
                Exec(db, "INSERT OR REPLACE INTO sync_file_local(path, size, modified, hash, uploaded) VALUES ($p, $s, $m, $h, 1)",
                    ("$p", path), ("$s", file.Length), ("$m", file.LastWriteTimeUtc.Ticks), ("$h", hash));
                report.Downloaded++;
            }
            else
            {
                report.Missing++; // listed by a device that has not sent the content yet
            }
        }
    }

    private List<string> ReferencedPaths(SqliteConnection db)
    {
        var paths = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var (table, column) in _columns)
        {
            using var exists = db.CreateCommand();
            exists.CommandText = "SELECT COUNT(*) FROM pragma_table_info($t) WHERE name = $c";
            exists.Parameters.AddWithValue("$t", table);
            exists.Parameters.AddWithValue("$c", column);
            if ((long)exists.ExecuteScalar()! == 0) continue;

            using var cmd = db.CreateCommand();
            cmd.CommandText = $"SELECT DISTINCT \"{column}\" FROM \"{table}\" WHERE \"{column}\" IS NOT NULL AND \"{column}\" <> ''";
            using var r = cmd.ExecuteReader();
            while (r.Read())
                if (Normalize(r.GetString(0)) is { } p) paths.Add(p);
        }
        return paths.ToList();
    }

    private static (string? Hash, bool Uploaded) Cached(SqliteConnection db, string path, FileInfo file)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT hash, uploaded FROM sync_file_local WHERE path = $p AND size = $s AND modified = $m";
        cmd.Parameters.AddWithValue("$p", path);
        cmd.Parameters.AddWithValue("$s", file.Length);
        cmd.Parameters.AddWithValue("$m", file.LastWriteTimeUtc.Ticks);
        using var r = cmd.ExecuteReader();
        return r.Read() ? (r.GetString(0), r.GetInt64(1) != 0) : (null, false);
    }

    private static async Task<string> HashAsync(string fullPath, CancellationToken ct)
    {
        await using var stream = File.OpenRead(fullPath);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, ct)).ToLowerInvariant();
    }

    private static void Exec(SqliteConnection db, string sql, params (string Name, object Value)[] args)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in args) cmd.Parameters.AddWithValue(name, value);
        cmd.ExecuteNonQuery();
    }
}
