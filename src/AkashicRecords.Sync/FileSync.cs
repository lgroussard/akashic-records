using System.Security.Cryptography;
using Microsoft.Data.Sqlite;

namespace AkashicRecords.Sync;

public sealed class FileSyncReport
{
    public int Uploaded, Downloaded, Missing;
    public override string ToString() => $"fichiers : {Uploaded} envoye(s), {Downloaded} recu(s), {Missing} pas encore disponible(s)";
}

/// <summary>
/// The files the rows point at (covers, photos, tickets, books). The database stores them as paths
/// relative to the app folder, in the same form on the PC and the phone ("media\covers\x.jpg",
/// "books\x.epub", "references/x.jpg"), so a file is identified by its path:
///
///  - `sync_file` lists, for each referenced path, the SHA-256 and size of its content. It is an
///    ordinary synchronised table (entity type "MediaFile"), so the list itself travels through the
///    row engine like any other data;
///  - before the rows are synchronised, every referenced file present here and not listed yet is
///    hashed, its content sent to the hub (once: the hub stores a content under its hash), and
///    listed;
///  - after, every listed file missing here is fetched from the hub.
///
/// A file is never deleted and never overwritten by this: a path is created once (the name is a
/// random id) and its content does not change. Music is not handled here.
/// The phone does the same in android/.../sync/FileSync.kt.
/// </summary>
public sealed class FileSync
{
    /// <summary>The index of files, synchronised with the rest (add it to the engine's tables).</summary>
    public static readonly SyncTable IndexTable = new("MediaFile", "sync_file") { MatchColumns = ["Path"] };

    /// <summary>Only these top folders of the app folder hold synchronised files.</summary>
    private static readonly string[] Roots = ["media/", "books/", "references/"];

    private readonly string _connectionString;
    private readonly string _baseDir;
    private readonly HubClient _hub;
    private readonly IReadOnlyList<(string Table, string Column)> _columns;

    public FileSync(string dbPath, string baseDir, HubClient hub, IEnumerable<(string Table, string Column)> fileColumns)
    {
        _connectionString = new SqliteConnectionStringBuilder { DataSource = dbPath }.ToString();
        _baseDir = baseDir;
        _hub = hub;
        _columns = fileColumns.ToList();
    }

    /// <summary>Creates `sync_file` (and the local cache of hashes). Call before the row engine loads its tables.</summary>
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
            CREATE TABLE IF NOT EXISTS sync_file_local(      -- this device only: what was hashed and sent
                path     TEXT PRIMARY KEY,
                size     INTEGER NOT NULL,
                modified INTEGER NOT NULL,
                hash     TEXT NOT NULL,
                uploaded INTEGER NOT NULL DEFAULT 0
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

    private string FullPath(string normalized) => Path.Combine(_baseDir, normalized.Replace('/', Path.DirectorySeparatorChar));

    /// <summary>Hashes, sends and lists the referenced files that are here. Run BEFORE the row engine.</summary>
    public async Task PublishAsync(FileSyncReport report, CancellationToken ct = default)
    {
        using var db = new SqliteConnection(_connectionString);
        db.Open();

        foreach (var path in ReferencedPaths(db))
        {
            var file = new FileInfo(FullPath(path));
            if (!file.Exists) continue;

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
            // Listed once. A path whose listing says another content keeps the listing: file names
            // are random ids, a clash means the same file re-encoded somewhere, not a new one.
            Exec(db, "INSERT OR IGNORE INTO sync_file(Path, Hash, Size) VALUES ($p, $h, $s)", ("$p", path), ("$h", hash), ("$s", file.Length));
        }
    }

    /// <summary>Fetches the listed files that are missing here. Run AFTER the row engine.</summary>
    public async Task FetchAsync(FileSyncReport report, CancellationToken ct = default)
    {
        using var db = new SqliteConnection(_connectionString);
        db.Open();
        var listed = new List<(string Path, string Hash)>();
        using (var cmd = db.CreateCommand())
        {
            cmd.CommandText = "SELECT Path, Hash FROM sync_file";
            using var r = cmd.ExecuteReader();
            while (r.Read()) listed.Add((r.GetString(0), r.GetString(1)));
        }

        foreach (var (raw, hash) in listed)
        {
            var path = Normalize(raw);
            if (path is null || hash.Length != 64 || !hash.All(Uri.IsHexDigit)) continue; // not something to write to disk
            var target = FullPath(path);
            if (File.Exists(target)) continue;
            if (await _hub.DownloadFileAsync(hash.ToLowerInvariant(), target, ct))
            {
                var file = new FileInfo(target);
                Exec(db, "INSERT OR REPLACE INTO sync_file_local(path, size, modified, hash, uploaded) VALUES ($p, $s, $m, $h, 1)",
                    ("$p", path), ("$s", file.Length), ("$m", file.LastWriteTimeUtc.Ticks), ("$h", hash.ToLowerInvariant()));
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
