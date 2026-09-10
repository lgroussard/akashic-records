using AkashicRecords.Domain;
using Microsoft.Data.Sqlite;

namespace AkashicRecords.Infrastructure.Persistence;

public sealed class ArtworkRepository
{
    private readonly SqliteConnectionFactory _connectionFactory;

    public ArtworkRepository(SqliteConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    public int Add(Artwork artwork)
    {
        using var connection = _connectionFactory.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO Artwork (Title, Category, Tier, CreatedAt, CoverImagePath, WatchPriority)
            VALUES ($title, $category, $tier, $createdAt, $coverImagePath, $watchPriority);
            SELECT last_insert_rowid();
            """;
        command.Parameters.AddWithValue("$title", artwork.Title);
        command.Parameters.AddWithValue("$category", (int)artwork.Category);
        command.Parameters.AddWithValue("$tier", (int)artwork.Tier);
        command.Parameters.AddWithValue("$createdAt", artwork.CreatedAt.ToString("O"));
        command.Parameters.AddWithValue("$coverImagePath", (object?)artwork.CoverImagePath ?? DBNull.Value);
        command.Parameters.AddWithValue("$watchPriority", (int)artwork.WatchPriority);
        return Convert.ToInt32((long)command.ExecuteScalar()!);
    }

    public IReadOnlyList<Artwork> GetByCategory(ArtworkCategory category)
    {
        using var connection = _connectionFactory.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Id, Title, Category, Tier, CreatedAt, CoverImagePath, WatchPriority FROM Artwork WHERE Category = $category ORDER BY Title;";
        command.Parameters.AddWithValue("$category", (int)category);

        var results = new List<Artwork>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            results.Add(ReadArtwork(reader));
        }
        return results;
    }

    public Artwork? GetById(int id)
    {
        using var connection = _connectionFactory.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Id, Title, Category, Tier, CreatedAt, CoverImagePath, WatchPriority FROM Artwork WHERE Id = $id;";
        command.Parameters.AddWithValue("$id", id);

        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadArtwork(reader) : null;
    }

    public void UpdateTier(int id, Tier tier)
    {
        using var connection = _connectionFactory.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE Artwork SET Tier = $tier WHERE Id = $id;";
        command.Parameters.AddWithValue("$tier", (int)tier);
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
    }

    public void UpdateCoverImage(int id, string? coverImagePath)
    {
        using var connection = _connectionFactory.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE Artwork SET CoverImagePath = $coverImagePath WHERE Id = $id;";
        command.Parameters.AddWithValue("$coverImagePath", (object?)coverImagePath ?? DBNull.Value);
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
    }

    public void UpdateTitle(int id, string title)
    {
        using var connection = _connectionFactory.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE Artwork SET Title = $title WHERE Id = $id;";
        command.Parameters.AddWithValue("$title", title);
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
    }

    public void UpdateWatchPriority(int id, WatchPriority priority)
    {
        using var connection = _connectionFactory.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE Artwork SET WatchPriority = $watchPriority WHERE Id = $id;";
        command.Parameters.AddWithValue("$watchPriority", (int)priority);
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
    }

    // Candidates for the "Film du jour" widget: flagged with a watch priority and among the
    // watchable categories (Film=0, FilmAnimation=1, Anime=2) - books are excluded.
    public IReadOnlyList<Artwork> GetWatchlistCandidates()
    {
        using var connection = _connectionFactory.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Id, Title, Category, Tier, CreatedAt, CoverImagePath, WatchPriority FROM Artwork WHERE WatchPriority <> 0 AND Category IN (0, 1, 2) ORDER BY Title;";

        var results = new List<Artwork>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            results.Add(ReadArtwork(reader));
        }
        return results;
    }

    public void Delete(int id)
    {
        using var connection = _connectionFactory.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM Artwork WHERE Id = $id;";
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
    }

    private static Artwork ReadArtwork(SqliteDataReader reader) => new()
    {
        Id = reader.GetInt32(0),
        Title = reader.GetString(1),
        Category = (ArtworkCategory)reader.GetInt32(2),
        Tier = (Tier)reader.GetInt32(3),
        CreatedAt = DateTime.Parse(reader.GetString(4)),
        CoverImagePath = reader.IsDBNull(5) ? null : reader.GetString(5),
        WatchPriority = (WatchPriority)reader.GetInt32(6)
    };
}
