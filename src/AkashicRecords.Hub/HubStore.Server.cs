namespace AkashicRecords.Hub;

// What the server screen (ServerStatus.cs) reads out of the catalogue: the files by kind, the devices' last sync.
public sealed partial class HubStore
{
    public sealed record FileKind(string Kind, long Files, long Bytes);
    public sealed record DeviceActivity(string DeviceId, string Name, string CreatedAt, string? LastSeenAt, string? LastChangeAt, long Changes, bool Revoked);

    /// <summary>The catalogued files (MediaFile rows), grouped by what they are, from their path.</summary>
    public List<FileKind> FilesByKind()
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            SELECT CASE
                     WHEN p LIKE 'music/%' THEN 'music'
                     WHEN p LIKE 'media/archive/%' THEN 'photos'
                     WHEN p LIKE 'books/%' THEN 'books'
                     WHEN p LIKE 'references/%' THEN 'references'
                     ELSE 'other' END AS kind,
                   COUNT(*), COALESCE(SUM(size), 0)
            FROM (SELECT lower(replace(json_extract(payload, '$.Path'), '\', '/')) AS p,
                         CAST(json_extract(payload, '$.Size') AS INTEGER) AS size
                  FROM entities WHERE entity_type = 'MediaFile' AND deleted = 0 AND payload IS NOT NULL AND json_valid(payload))
            GROUP BY kind
            """;
        using var r = cmd.ExecuteReader();
        var list = new List<FileKind>();
        while (r.Read()) list.Add(new FileKind(r.GetString(0), r.GetInt64(1), r.GetInt64(2)));
        return list;
    }

    /// <summary>Paired devices, with when each last wrote a change (sync_log) and how many in all.</summary>
    public List<DeviceActivity> DeviceActivities()
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            SELECT d.device_id, d.name, d.created_at, d.last_seen_at, l.last_at, COALESCE(l.n, 0), d.revoked
            FROM devices d
            LEFT JOIN (SELECT device_id, MAX(committed_at) AS last_at, COUNT(*) AS n FROM sync_log GROUP BY device_id) l
                   ON l.device_id = d.device_id
            ORDER BY d.revoked, COALESCE(d.last_seen_at, d.created_at) DESC
            """;
        using var r = cmd.ExecuteReader();
        var list = new List<DeviceActivity>();
        while (r.Read())
            list.Add(new DeviceActivity(r.GetString(0), r.GetString(1), r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3),
                r.IsDBNull(4) ? null : r.GetString(4), r.GetInt64(5), r.GetInt64(6) != 0));
        return list;
    }

    /// <summary>Live rows per entity type (the size of each part of the catalogue).</summary>
    public Dictionary<string, long> EntityCounts()
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT entity_type, COUNT(*) FROM entities WHERE deleted = 0 GROUP BY entity_type";
        using var r = cmd.ExecuteReader();
        var map = new Dictionary<string, long>();
        while (r.Read()) map[r.GetString(0)] = r.GetInt64(1);
        return map;
    }
}
