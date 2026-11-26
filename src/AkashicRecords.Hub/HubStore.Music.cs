namespace AkashicRecords.Hub;

// The few things the hub reads out of the catalogue for its own downloads (Downloads.cs). Everywhere else the
// payloads stay opaque to it.
public sealed partial class HubStore
{
    /// <summary>The device id under which the hub writes what it adds itself (downloaded tracks).</summary>
    public const string HubDevice = "hub";

    /// <summary>The music folders already in the catalogue (the tracks' albums), for "put it in an existing folder".</summary>
    public List<string> MusicFolders()
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            SELECT DISTINCT json_extract(payload, '$.Album') AS album FROM entities
            WHERE entity_type = 'MusicTrack' AND deleted = 0 AND payload IS NOT NULL
              AND json_valid(payload) AND COALESCE(json_extract(payload, '$.Album'), '') <> ''
            ORDER BY album COLLATE NOCASE
            """;
        using var r = cmd.ExecuteReader();
        var list = new List<string>();
        while (r.Read()) if (!r.IsDBNull(0)) list.Add(r.GetString(0));
        return list;
    }

    /// <summary>Whether a live track of the catalogue already has this file path (compared without case or slash direction).</summary>
    public bool HasMusicPath(string filePath)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            SELECT COUNT(*) FROM entities
            WHERE entity_type = 'MusicTrack' AND deleted = 0 AND payload IS NOT NULL AND json_valid(payload)
              AND lower(replace(json_extract(payload, '$.FilePath'), '\', '/')) = $p
            """;
        cmd.Parameters.AddWithValue("$p", filePath.Replace('\\', '/').ToLowerInvariant());
        return (long)cmd.ExecuteScalar()! > 0;
    }
}
