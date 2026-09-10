using AkashicRecords.Domain;
using Microsoft.Data.Sqlite;

namespace AkashicRecords.Infrastructure.Persistence;

public sealed class WatchlistItemRepository
{
    private readonly SqliteConnectionFactory _connectionFactory;

    public WatchlistItemRepository(SqliteConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    public int Add(WatchlistItem item)
    {
        using var connection = _connectionFactory.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO WatchlistItem (Title, Category, Priority, CoverImagePath, AddedAt)
            VALUES ($title, $category, $priority, $coverImagePath, $addedAt);
            SELECT last_insert_rowid();
            """;
        command.Parameters.AddWithValue("$title", item.Title);
        command.Parameters.AddWithValue("$category", (int)item.Category);
        command.Parameters.AddWithValue("$priority", (int)item.Priority);
        command.Parameters.AddWithValue("$coverImagePath", (object?)item.CoverImagePath ?? DBNull.Value);
        command.Parameters.AddWithValue("$addedAt", item.AddedAt.ToString("O"));
        return Convert.ToInt32((long)command.ExecuteScalar()!);
    }

    public IReadOnlyList<WatchlistItem> GetAll()
    {
        using var connection = _connectionFactory.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Id, Title, Category, Priority, CoverImagePath, AddedAt FROM WatchlistItem ORDER BY Priority DESC, Title;";
        return ReadAll(command);
    }

    public IReadOnlyList<WatchlistItem> GetByCategory(WatchlistCategory category)
    {
        using var connection = _connectionFactory.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Id, Title, Category, Priority, CoverImagePath, AddedAt FROM WatchlistItem WHERE Category = $category ORDER BY Priority DESC, Title;";
        command.Parameters.AddWithValue("$category", (int)category);
        return ReadAll(command);
    }

    public void UpdateTitle(int id, string title)
    {
        using var connection = _connectionFactory.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE WatchlistItem SET Title = $title WHERE Id = $id;";
        command.Parameters.AddWithValue("$title", title);
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
    }

    public void UpdatePriority(int id, WatchPriority priority)
    {
        using var connection = _connectionFactory.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE WatchlistItem SET Priority = $priority WHERE Id = $id;";
        command.Parameters.AddWithValue("$priority", (int)priority);
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
    }

    public void UpdateCover(int id, string? coverPath)
    {
        using var connection = _connectionFactory.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE WatchlistItem SET CoverImagePath = $coverImagePath WHERE Id = $id;";
        command.Parameters.AddWithValue("$coverImagePath", (object?)coverPath ?? DBNull.Value);
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
    }

    public void Delete(int id)
    {
        using var connection = _connectionFactory.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM WatchlistItem WHERE Id = $id;";
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
    }

    private static List<WatchlistItem> ReadAll(SqliteCommand command)
    {
        var results = new List<WatchlistItem>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            results.Add(new WatchlistItem
            {
                Id = reader.GetInt32(0),
                Title = reader.GetString(1),
                Category = (WatchlistCategory)reader.GetInt32(2),
                Priority = (WatchPriority)reader.GetInt32(3),
                CoverImagePath = reader.IsDBNull(4) ? null : reader.GetString(4),
                AddedAt = DateTime.Parse(reader.GetString(5))
            });
        }
        return results;
    }
}
