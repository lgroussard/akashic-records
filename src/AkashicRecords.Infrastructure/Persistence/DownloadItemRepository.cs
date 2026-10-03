using AkashicRecords.Domain;
using Microsoft.Data.Sqlite;

namespace AkashicRecords.Infrastructure.Persistence;

public sealed class DownloadItemRepository
{
    private readonly SqliteConnectionFactory _connectionFactory;

    public DownloadItemRepository(SqliteConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    public int Add(DownloadItem item)
    {
        using var connection = _connectionFactory.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO DownloadItem (Url, Title, Status, AddedAt, FolderId)
            VALUES ($url, $title, $status, $addedAt, $folderId);
            SELECT last_insert_rowid();
            """;
        command.Parameters.AddWithValue("$url", item.Url);
        command.Parameters.AddWithValue("$title", item.Title);
        command.Parameters.AddWithValue("$status", (int)item.Status);
        command.Parameters.AddWithValue("$addedAt", item.AddedAt.ToString("O"));
        command.Parameters.AddWithValue("$folderId", (object?)item.FolderId ?? DBNull.Value);
        return Convert.ToInt32((long)command.ExecuteScalar()!);
    }

    public IReadOnlyList<DownloadItem> GetAll()
    {
        using var connection = _connectionFactory.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Id, Url, Title, Status, AddedAt, FolderId FROM DownloadItem ORDER BY AddedAt;";

        var results = new List<DownloadItem>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            results.Add(ReadItem(reader));
        }
        return results;
    }

    public bool ExistsByUrl(string url)
    {
        using var connection = _connectionFactory.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM DownloadItem WHERE Url = $url LIMIT 1;";
        command.Parameters.AddWithValue("$url", url);
        return command.ExecuteScalar() is not null;
    }

    public void UpdateStatus(int id, DownloadStatus status)
    {
        using var connection = _connectionFactory.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE DownloadItem SET Status = $status WHERE Id = $id;";
        command.Parameters.AddWithValue("$status", (int)status);
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
    }

    // Moves a card into a placement zone; null un-files it back to the implicit bucket.
    public void UpdateFolder(int id, int? folderId)
    {
        using var connection = _connectionFactory.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE DownloadItem SET FolderId = $folderId WHERE Id = $id;";
        command.Parameters.AddWithValue("$folderId", (object?)folderId ?? DBNull.Value);
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
    }

    public void UpdateTitle(int id, string title)
    {
        using var connection = _connectionFactory.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE DownloadItem SET Title = $title WHERE Id = $id;";
        command.Parameters.AddWithValue("$title", title);
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
    }

    public void Delete(int id)
    {
        using var connection = _connectionFactory.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM DownloadItem WHERE Id = $id;";
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
    }

    public void DeleteByStatus(DownloadStatus status)
    {
        using var connection = _connectionFactory.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM DownloadItem WHERE Status = $status;";
        command.Parameters.AddWithValue("$status", (int)status);
        command.ExecuteNonQuery();
    }

    private static DownloadItem ReadItem(SqliteDataReader reader) => new()
    {
        Id = reader.GetInt32(0),
        Url = reader.GetString(1),
        Title = reader.GetString(2),
        Status = (DownloadStatus)reader.GetInt32(3),
        AddedAt = DateTime.Parse(reader.GetString(4)),
        FolderId = reader.IsDBNull(5) ? null : reader.GetInt32(5)
    };
}
