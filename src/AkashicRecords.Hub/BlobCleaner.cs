using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace AkashicRecords.Hub;

/// <summary>
/// Removes, for good, the files nothing uses any more. Until now the hub only ever added: a photo deleted in the app
/// lost its row, but its file stayed on the server forever.
///
/// Once a day:
///   1. a listed file (MediaFile row) that no live row names any more — and that is not a version of a picture whose
///      edit can still be undone — is marked; still unused after <see cref="HubOptions.DeleteAfterDays"/> days (30),
///      its listing is removed (a deletion every device receives);
///   2. a blob that no listed file and no shared-computing job still needs is marked the same way, then its file and
///      its preview are deleted.
/// Used again within the delay (the photo came back, another device still has it…), the mark is dropped.
///
/// Brakes: nothing is removed when no live row names any file at all, nor when more than half of the files would go
/// at once (a catalogue that looks emptied by accident rather than cleaned by hand). The night's copy on the
/// external disk mirrors the blobs, so a file removed here leaves that copy the next night.
/// What is pending and what was removed is written to cleanup-status.json (the apps' Serveur screen).
/// </summary>
public sealed class BlobCleaner(HubOptions options, HubStore store, ComputeStore compute, BlobStore blobs, RequestLog log) : BackgroundService
{
    public const string StatusFile = "cleanup-status.json";

    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        if (options.DeleteAfterDays <= 0) return;
        try { await Task.Delay(TimeSpan.FromMinutes(15), stop); } catch (OperationCanceledException) { return; }
        while (!stop.IsCancellationRequested)
        {
            try
            {
                var report = Run(options, store, compute, blobs, DateTime.UtcNow);
                if (report.DeletedFiles + report.DeletedBlobs > 0 || report.Brake is not null)
                    log.Info($"menage : {report.DeletedFiles} fichiers retires du catalogue, {report.DeletedBlobs} fichiers effaces ({report.DeletedBytes / 1048576} Mo)" + (report.Brake is null ? "" : $" ; frein : {report.Brake}"));
            }
            catch (Exception e) { log.Info($"menage en echec : {e.Message}"); }
            try { await Task.Delay(TimeSpan.FromHours(24), stop); } catch (OperationCanceledException) { return; }
        }
    }

    public sealed record Report(
        string RanAt, int GraceDays, int PendingFiles, long PendingBytes, string? NextAt,
        int DeletedFiles, int DeletedBlobs, long DeletedBytes, string? Brake);

    /// <summary>One clean-up pass; <paramref name="now"/> is a parameter so the smoke test can jump ahead in time.</summary>
    public static Report Run(HubOptions options, HubStore store, ComputeStore compute, BlobStore blobs, DateTime now)
    {
        var grace = TimeSpan.FromDays(Math.Max(1, options.DeleteAfterDays));
        using var marks = OpenMarks(options);
        string? brake = null;

        // ---- 1. Listed files nothing names any more.
        var referenced = store.ReferencedPaths();
        var rows = store.MediaFileRows();
        // An edited picture keeps every version of itself (« Revenir à l'original », « Annuler la dernière étape »).
        var editRoots = referenced.Where(k => k.Contains(".edit-")).Select(RootOf).ToHashSet(StringComparer.Ordinal);
        bool InUse(HubStore.MediaFileRow r)
        {
            var key = HubStore.PathKey(r.Path);
            return referenced.Contains(key) || editRoots.Contains(RootOf(key));
        }
        var unusedRows = rows.Where(r => !InUse(r)).ToList();
        if (referenced.Count == 0 && rows.Count > 0) { brake = "aucune fiche ne nomme de fichier"; unusedRows.Clear(); }
        else if (unusedRows.Count > 50 && unusedRows.Count * 2 > rows.Count) { brake = $"{unusedRows.Count} fichiers sur {rows.Count} d'un coup"; unusedRows.Clear(); }

        var fileMarks = Marks(marks, "file");
        var unusedIds = unusedRows.Select(r => r.EntityId).ToHashSet();
        foreach (var id in fileMarks.Keys.Where(id => !unusedIds.Contains(id)).ToList()) { Unmark(marks, "file", id); fileMarks.Remove(id); }
        var expiredRows = new List<HubStore.MediaFileRow>();
        foreach (var r in unusedRows)
        {
            if (!fileMarks.TryGetValue(r.EntityId, out var since)) { Mark(marks, "file", r.EntityId, now); fileMarks[r.EntityId] = now; }
            else if (now - since >= grace) expiredRows.Add(r);
        }
        foreach (var batch in expiredRows.Chunk(500))
        {
            store.Push(HubStore.HubDevice, batch.Select(r =>
                new Mutation(Guid.NewGuid().ToString(), "MediaFile", r.EntityId, store.Rev("MediaFile", r.EntityId), null, true)).ToList());
            foreach (var r in batch) { Unmark(marks, "file", r.EntityId); fileMarks.Remove(r.EntityId); }
        }

        var expiredHashes = expiredRows.Select(r => r.Hash).ToHashSet(StringComparer.Ordinal);

        // ---- 2. Blobs nothing needs any more (listings just removed included).
        var liveRows = store.MediaFileRows();
        var needed = liveRows.Select(r => r.Hash).ToHashSet(StringComparer.Ordinal);
        needed.UnionWith(compute.ReferencedHashes());
        var onDisk = BlobFiles(options.BlobDir);
        var unusedBlobs = onDisk.Where(b => !needed.Contains(b.Hash)).ToList();
        if (brake is null && unusedBlobs.Count > 50 && unusedBlobs.Count * 2 > onDisk.Count) { brake = $"{unusedBlobs.Count} fichiers sur {onDisk.Count} sur le disque"; unusedBlobs.Clear(); }

        var blobMarks = Marks(marks, "blob");
        var unusedHashes = unusedBlobs.Select(b => b.Hash).ToHashSet();
        foreach (var h in blobMarks.Keys.Where(h => !unusedHashes.Contains(h)).ToList()) { Unmark(marks, "blob", h); blobMarks.Remove(h); }
        int deletedBlobs = 0;
        long deletedBytes = 0;
        foreach (var b in unusedBlobs)
        {
            // A file whose listing was just removed has already waited its delay: it goes now.
            var waited = expiredHashes.Contains(b.Hash);
            if (!waited && !blobMarks.TryGetValue(b.Hash, out var since)) { Mark(marks, "blob", b.Hash, now); blobMarks[b.Hash] = now; continue; }
            if (!waited && now - blobMarks[b.Hash] < grace) continue;
            try
            {
                File.Delete(b.Path);
                var thumb = Path.Combine(options.DataDir, "thumbs", b.Hash[..2], b.Hash + ".jpg");
                if (File.Exists(thumb)) File.Delete(thumb);
                deletedBlobs++;
                deletedBytes += b.Size;
                Unmark(marks, "blob", b.Hash);
                blobMarks.Remove(b.Hash);
            }
            catch (IOException) { }
        }

        // ---- What is waiting: the space that will be freed, and when the first of it goes.
        var size = onDisk.ToDictionary(b => b.Hash, b => b.Size);
        var stillNeeded = liveRows.Where(r => !fileMarks.ContainsKey(r.EntityId)).Select(r => r.Hash).ToHashSet();
        stillNeeded.UnionWith(compute.ReferencedHashes());
        var pendingHashes = liveRows.Where(r => fileMarks.ContainsKey(r.EntityId)).Select(r => r.Hash).Where(h => !stillNeeded.Contains(h))
            .Concat(blobMarks.Keys).Distinct().ToList();
        var pendingBytes = pendingHashes.Sum(h => size.GetValueOrDefault(h));
        var first = fileMarks.Values.Concat(blobMarks.Values).DefaultIfEmpty(DateTime.MaxValue).Min();

        var report = new Report(now.ToString("O"), (int)grace.TotalDays, pendingHashes.Count, pendingBytes,
            first == DateTime.MaxValue ? null : (first + grace).ToString("O"), expiredRows.Count, deletedBlobs, deletedBytes, brake);
        File.WriteAllText(Path.Combine(options.DataDir, StatusFile), JsonSerializer.Serialize(report, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower }));
        return report;
    }

    /// <summary>"media/archive/x.jpg.edit-1a2b3c4d.from-…jpg" → "media/archive/x.jpg" (the original's key).</summary>
    private static string RootOf(string key)
    {
        var at = key.IndexOf(".edit-", StringComparison.Ordinal);
        return at < 0 ? key : key[..at];
    }

    private sealed record BlobFile(string Hash, string Path, long Size);

    private static List<BlobFile> BlobFiles(string dir)
    {
        var list = new List<BlobFile>();
        if (!Directory.Exists(dir)) return list;
        foreach (var sub in Directory.EnumerateDirectories(dir))
        {
            if (System.IO.Path.GetFileName(sub).Length != 2) continue; // tmp/ and anything else is not a blob folder
            foreach (var f in new DirectoryInfo(sub).EnumerateFiles())
                if (BlobStore.IsHash(f.Name)) list.Add(new BlobFile(f.Name, f.FullName, f.Length));
        }
        return list;
    }

    // ---- The marks: when each unused file was first seen unused (its own small database).

    private static SqliteConnection OpenMarks(HubOptions options)
    {
        var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(options.DataDir, "cleanup.db") }.ToString());
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "CREATE TABLE IF NOT EXISTS unused(kind TEXT NOT NULL, key TEXT NOT NULL, since TEXT NOT NULL, PRIMARY KEY(kind, key))";
        cmd.ExecuteNonQuery();
        return c;
    }

    private static Dictionary<string, DateTime> Marks(SqliteConnection c, string kind)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT key, since FROM unused WHERE kind = $k";
        cmd.Parameters.AddWithValue("$k", kind);
        using var r = cmd.ExecuteReader();
        var map = new Dictionary<string, DateTime>();
        while (r.Read()) map[r.GetString(0)] = DateTime.Parse(r.GetString(1), null, System.Globalization.DateTimeStyles.RoundtripKind);
        return map;
    }

    private static void Mark(SqliteConnection c, string kind, string key, DateTime since)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT OR IGNORE INTO unused(kind, key, since) VALUES ($k, $key, $s)";
        cmd.Parameters.AddWithValue("$k", kind);
        cmd.Parameters.AddWithValue("$key", key);
        cmd.Parameters.AddWithValue("$s", since.ToString("O"));
        cmd.ExecuteNonQuery();
    }

    private static void Unmark(SqliteConnection c, string kind, string key)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = "DELETE FROM unused WHERE kind = $k AND key = $key";
        cmd.Parameters.AddWithValue("$k", kind);
        cmd.Parameters.AddWithValue("$key", key);
        cmd.ExecuteNonQuery();
    }
}
