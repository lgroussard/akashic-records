using System.Text.Json;

namespace AkashicRecords.Hub;

// What shared computing reads out of the catalogue to upscale an image of the app (ComputeAttach.cs): the row of
// the image, and the content of its file. Everywhere else the payloads stay opaque to the hub.
public sealed partial class HubStore
{
    /// <summary>The current payload of a live entity; null when unknown or deleted.</summary>
    public JsonElement? Current(string entityType, string entityId)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT payload FROM entities WHERE entity_type = $t AND entity_id = $id AND deleted = 0 AND payload IS NOT NULL";
        cmd.Parameters.AddWithValue("$t", entityType);
        cmd.Parameters.AddWithValue("$id", entityId);
        var json = cmd.ExecuteScalar() as string;
        if (json is null) return null;
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    /// <summary>The id of a live entity whose text field has this value (e.g. the PhotoAlbum named "Agrandies"); null if none.</summary>
    public string? FindLive(string entityType, string field, string value)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"""
            SELECT entity_id FROM entities
            WHERE entity_type = $t AND deleted = 0 AND payload IS NOT NULL AND json_valid(payload)
              AND json_extract(payload, '$.{field}') = $v
            ORDER BY updated_at LIMIT 1
            """;
        cmd.Parameters.AddWithValue("$t", entityType);
        cmd.Parameters.AddWithValue("$v", value);
        return cmd.ExecuteScalar() as string;
    }

    /// <summary>The content hash of a file listed by the devices (MediaFile, "media/x/y.jpg"), compared without case or slash direction.</summary>
    public string? FileHash(string path)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            SELECT json_extract(payload, '$.Hash') FROM entities
            WHERE entity_type = 'MediaFile' AND deleted = 0 AND payload IS NOT NULL AND json_valid(payload)
              AND lower(replace(json_extract(payload, '$.Path'), '\', '/')) = $p
            LIMIT 1
            """;
        cmd.Parameters.AddWithValue("$p", path.Replace('\\', '/').TrimStart('/').ToLowerInvariant());
        return cmd.ExecuteScalar() as string;
    }
}
