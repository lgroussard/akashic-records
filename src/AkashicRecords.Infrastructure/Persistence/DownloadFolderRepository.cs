using AkashicRecords.Domain;
using Microsoft.Data.Sqlite;

namespace AkashicRecords.Infrastructure.Persistence;

// CRUD for the downloader's placement zones. A zone is a named bucket whose "meaning" is the
// music/<Subfolder> its items download into; ordering is a manual SortOrder the UI rewrites on drag-reorder.
public sealed class DownloadFolderRepository
{
    private readonly SqliteConnectionFactory _connectionFactory;

    public DownloadFolderRepository(SqliteConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    // Returns the new row id; callers that need the id back must use it (Add does not set entity.Id).
    public int Add(DownloadFolder folder)
    {
        using var connection = _connectionFactory.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO DownloadFolder (Name, Subfolder, SortOrder)
            VALUES ($name, $subfolder, $sortOrder);
            SELECT last_insert_rowid();
            """;
        command.Parameters.AddWithValue("$name", folder.Name);
        command.Parameters.AddWithValue("$subfolder", folder.Subfolder);
        command.Parameters.AddWithValue("$sortOrder", folder.SortOrder);
        return Convert.ToInt32((long)command.ExecuteScalar()!);
    }

    public IReadOnlyList<DownloadFolder> GetAll()
    {
        using var connection = _connectionFactory.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Id, Name, Subfolder, SortOrder FROM DownloadFolder ORDER BY SortOrder, Id;";

        var results = new List<DownloadFolder>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var sub = reader.IsDBNull(2) ? null : reader.GetString(2);
            results.Add(new DownloadFolder
            {
                Id = reader.GetInt32(0),
                Name = reader.GetString(1),
                Subfolder = sub,
                SortOrder = reader.GetInt32(3)
            });
        }
        return results;
    }

    public void Update(int id, string name, string? subfolder)
    {
        using var connection = _connectionFactory.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE DownloadFolder SET Name = $name, Subfolder = $subfolder WHERE Id = $id;";
        command.Parameters.AddWithValue("$name", name);
        command.Parameters.AddWithValue("$subfolder", subfolder);
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
    }

    // Rewrites each zone's SortOrder to its index, so a drag-reorder persists.
    public void SetOrder(IReadOnlyList<int> orderedIds)
    {
        using var connection = _connectionFactory.CreateOpenConnection();
        for (var i = 0; i < orderedIds.Count; i++)
        {
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE DownloadFolder SET SortOrder = $order WHERE Id = $id;";
            command.Parameters.AddWithValue("$order", i);
            command.Parameters.AddWithValue("$id", orderedIds[i]);
            command.ExecuteNonQuery();
        }
    }

    // Deleting a zone un-files its items (FolderId back to null) rather than orphaning them.
    public void Delete(int id)
    {
        using var connection = _connectionFactory.CreateOpenConnection();
        using (var unfile = connection.CreateCommand())
        {
            unfile.CommandText = "UPDATE DownloadItem SET FolderId = NULL WHERE FolderId = $id;";
            unfile.Parameters.AddWithValue("$id", id);
            unfile.ExecuteNonQuery();
        }
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM DownloadFolder WHERE Id = $id;";
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
    }
}
