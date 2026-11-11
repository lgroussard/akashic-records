using AkashicRecords.Domain;
using Microsoft.Data.Sqlite;

namespace AkashicRecords.Infrastructure.Persistence;

// Boards of the Références tab.
public sealed class RefBoardRepository
{
    private readonly SqliteConnectionFactory _connectionFactory;

    public RefBoardRepository(SqliteConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    public int Add(string title)
    {
        using var connection = _connectionFactory.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO RefBoard (Title, CreatedAt) VALUES ($title, $createdAt);
            SELECT last_insert_rowid();
            """;
        command.Parameters.AddWithValue("$title", title);
        command.Parameters.AddWithValue("$createdAt", DateTime.Now.ToString("O"));
        return Convert.ToInt32((long)command.ExecuteScalar()!);
    }

    public IReadOnlyList<RefBoard> GetAll()
    {
        using var connection = _connectionFactory.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Id, Title, CreatedAt FROM RefBoard ORDER BY Title COLLATE NOCASE;";
        var results = new List<RefBoard>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            results.Add(new RefBoard
            {
                Id = reader.GetInt32(0),
                Title = reader.GetString(1),
                CreatedAt = DateTime.TryParse(reader.GetString(2), out var d) ? d : DateTime.MinValue,
            });
        }
        return results;
    }

    public void Rename(int id, string title)
    {
        using var connection = _connectionFactory.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE RefBoard SET Title = $title WHERE Id = $id;";
        command.Parameters.AddWithValue("$title", title);
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
    }

    // The board's images go back to « À trier »; nothing is deleted with it.
    public void Delete(int id)
    {
        using var connection = _connectionFactory.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE RefImage SET BoardId = NULL WHERE BoardId = $id;
            DELETE FROM RefBoard WHERE Id = $id;
            """;
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
    }
}

// Images of the Références tab.
public sealed class RefImageRepository
{
    private readonly SqliteConnectionFactory _connectionFactory;

    public RefImageRepository(SqliteConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    public int Add(RefImage image)
    {
        using var connection = _connectionFactory.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO RefImage (BoardId, ImagePath, Title, Author, Source, SourceUrl, PageUrl, Width, Height, Note, CreatedAt)
            VALUES ($boardId, $imagePath, $title, $author, $source, $sourceUrl, $pageUrl, $width, $height, $note, $createdAt);
            SELECT last_insert_rowid();
            """;
        command.Parameters.AddWithValue("$boardId", (object?)image.BoardId ?? DBNull.Value);
        command.Parameters.AddWithValue("$imagePath", image.ImagePath);
        command.Parameters.AddWithValue("$title", image.Title);
        command.Parameters.AddWithValue("$author", image.Author);
        command.Parameters.AddWithValue("$source", image.Source);
        command.Parameters.AddWithValue("$sourceUrl", (object?)image.SourceUrl ?? DBNull.Value);
        command.Parameters.AddWithValue("$pageUrl", (object?)image.PageUrl ?? DBNull.Value);
        command.Parameters.AddWithValue("$width", image.Width);
        command.Parameters.AddWithValue("$height", image.Height);
        command.Parameters.AddWithValue("$note", image.Note);
        command.Parameters.AddWithValue("$createdAt", image.CreatedAt.ToString("O"));
        return Convert.ToInt32((long)command.ExecuteScalar()!);
    }

    public IReadOnlyList<RefImage> GetAll()
    {
        using var connection = _connectionFactory.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT Id, BoardId, ImagePath, Title, Author, Source, SourceUrl, PageUrl, Width, Height, Note, CreatedAt
            FROM RefImage ORDER BY CreatedAt DESC, Id DESC;
            """;
        var results = new List<RefImage>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            results.Add(new RefImage
            {
                Id = reader.GetInt32(0),
                BoardId = reader.IsDBNull(1) ? null : reader.GetInt32(1),
                ImagePath = reader.GetString(2),
                Title = reader.GetString(3),
                Author = reader.GetString(4),
                Source = reader.GetString(5),
                SourceUrl = reader.IsDBNull(6) ? null : reader.GetString(6),
                PageUrl = reader.IsDBNull(7) ? null : reader.GetString(7),
                Width = reader.GetInt32(8),
                Height = reader.GetInt32(9),
                Note = reader.GetString(10),
                CreatedAt = DateTime.TryParse(reader.GetString(11), out var d) ? d : DateTime.MinValue,
            });
        }
        return results;
    }

    public void Move(int id, int? boardId)
    {
        using var connection = _connectionFactory.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE RefImage SET BoardId = $boardId WHERE Id = $id;";
        command.Parameters.AddWithValue("$boardId", (object?)boardId ?? DBNull.Value);
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
    }

    public void Delete(int id)
    {
        using var connection = _connectionFactory.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM RefImage WHERE Id = $id;";
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
    }
}
