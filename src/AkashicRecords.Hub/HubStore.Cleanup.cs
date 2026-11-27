namespace AkashicRecords.Hub;

// What the clean-up (BlobCleaner.cs) reads out of the catalogue: which file paths the live rows still name, and the
// file listing itself.
public sealed partial class HubStore
{
    /// <summary>The columns holding the path of a file, per entity type. Same list as the apps' AkashicCatalog
    /// (src/AkashicRecords.Sync/AkashicCatalog.cs, android/…/sync/AkashicCatalog.kt): kept in step by hand. A column
    /// missing here would let files still in use be removed, after the grace period.</summary>
    public static readonly IReadOnlyList<(string Type, string Column)> FileColumns =
    [
        ("Recipe", "CoverImagePath"), ("Artwork", "CoverImagePath"), ("WatchlistItem", "CoverImagePath"),
        ("Poem", "ImagePath"), ("JournalPhoto", "ImagePath"), ("Photo", "ImagePath"), ("CanvasElement", "ImagePath"),
        ("RefImage", "ImagePath"), ("Book", "FilePath"), ("Book", "CoverPath"), ("Ticket", "FilePath"),
        ("MusicTrack", "FilePath"), ("MusicTrack", "CoverImagePath"),
    ];

    public static string PathKey(string path) => path.Replace('\\', '/').Trim('/').ToLowerInvariant();

    /// <summary>Every file path a live row names (as <see cref="PathKey"/>).</summary>
    public HashSet<string> ReferencedPaths()
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        using var c = Open();
        foreach (var (type, column) in FileColumns)
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = $"""
                SELECT json_extract(payload, '$.{column}') FROM entities
                WHERE entity_type = $t AND deleted = 0 AND payload IS NOT NULL AND json_valid(payload)
                """;
            cmd.Parameters.AddWithValue("$t", type);
            using var r = cmd.ExecuteReader();
            while (r.Read())
                if (!r.IsDBNull(0) && r.GetValue(0) is string p && p.Length > 0) set.Add(PathKey(p));
        }
        return set;
    }

    public sealed record MediaFileRow(string EntityId, string Path, string Hash, long Size);

    public List<MediaFileRow> MediaFileRows()
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            SELECT entity_id, json_extract(payload, '$.Path'), lower(json_extract(payload, '$.Hash')), CAST(json_extract(payload, '$.Size') AS INTEGER)
            FROM entities WHERE entity_type = 'MediaFile' AND deleted = 0 AND payload IS NOT NULL AND json_valid(payload)
            """;
        using var r = cmd.ExecuteReader();
        var list = new List<MediaFileRow>();
        while (r.Read())
            if (!r.IsDBNull(1) && !r.IsDBNull(2))
                list.Add(new MediaFileRow(r.GetString(0), r.GetString(1), r.GetString(2), r.IsDBNull(3) ? 0 : r.GetInt64(3)));
        return list;
    }
}
