using AkashicRecords.Domain;
using Microsoft.Data.Sqlite;

namespace AkashicRecords.Infrastructure.Persistence;

public sealed class PhotoRepository
{
    private const string SelectColumns =
        "SELECT Id, ImagePath, Title, Description, TakenDate, Tags, IsFavorite, AlbumId, ImportedAt FROM Photo";

    private readonly SqliteConnectionFactory _connectionFactory;

    public PhotoRepository(SqliteConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    public int Add(Photo photo)
    {
        using var connection = _connectionFactory.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO Photo (ImagePath, Title, Description, TakenDate, Tags, IsFavorite, AlbumId, ImportedAt)
            VALUES ($imagePath, $title, $description, $takenDate, $tags, $isFavorite, $albumId, $importedAt);
            SELECT last_insert_rowid();
            """;
        command.Parameters.AddWithValue("$imagePath", photo.ImagePath);
        command.Parameters.AddWithValue("$title", photo.Title);
        command.Parameters.AddWithValue("$description", photo.Description);
        command.Parameters.AddWithValue("$takenDate", (object?)photo.TakenDate?.ToString("O") ?? DBNull.Value);
        command.Parameters.AddWithValue("$tags", photo.Tags);
        command.Parameters.AddWithValue("$isFavorite", photo.IsFavorite ? 1 : 0);
        command.Parameters.AddWithValue("$albumId", (object?)photo.AlbumId ?? DBNull.Value);
        command.Parameters.AddWithValue("$importedAt", photo.ImportedAt.ToString("O"));
        return Convert.ToInt32((long)command.ExecuteScalar()!);
    }

    // Chronological, newest first: prefer the date the photo was taken, fall back to import time.
    public IReadOnlyList<Photo> GetAll()
    {
        using var connection = _connectionFactory.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = $"{SelectColumns} ORDER BY COALESCE(TakenDate, ImportedAt) DESC;";
        return ReadAll(command);
    }

    public IReadOnlyList<Photo> GetByAlbum(int albumId)
    {
        using var connection = _connectionFactory.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = $"{SelectColumns} WHERE AlbumId = $albumId ORDER BY COALESCE(TakenDate, ImportedAt) DESC;";
        command.Parameters.AddWithValue("$albumId", albumId);
        return ReadAll(command);
    }

    public IReadOnlyList<Photo> GetFavorites()
    {
        using var connection = _connectionFactory.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = $"{SelectColumns} WHERE IsFavorite = 1 ORDER BY COALESCE(TakenDate, ImportedAt) DESC;";
        return ReadAll(command);
    }

    public IReadOnlyList<Photo> Search(string query)
    {
        using var connection = _connectionFactory.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            $"{SelectColumns} WHERE Title LIKE $q OR Tags LIKE $q OR Description LIKE $q " +
            "ORDER BY COALESCE(TakenDate, ImportedAt) DESC;";
        command.Parameters.AddWithValue("$q", $"%{query}%");
        return ReadAll(command);
    }

    public Photo? GetById(int id)
    {
        using var connection = _connectionFactory.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = $"{SelectColumns} WHERE Id = $id;";
        command.Parameters.AddWithValue("$id", id);

        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadPhoto(reader) : null;
    }

    public void UpdateMetadata(int id, string title, string description, DateTime? takenDate, string tags)
    {
        using var connection = _connectionFactory.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            "UPDATE Photo SET Title = $title, Description = $description, TakenDate = $takenDate, Tags = $tags WHERE Id = $id;";
        command.Parameters.AddWithValue("$title", title);
        command.Parameters.AddWithValue("$description", description);
        command.Parameters.AddWithValue("$takenDate", (object?)takenDate?.ToString("O") ?? DBNull.Value);
        command.Parameters.AddWithValue("$tags", tags);
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
    }

    public void SetFavorite(int id, bool isFavorite)
    {
        using var connection = _connectionFactory.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE Photo SET IsFavorite = $isFavorite WHERE Id = $id;";
        command.Parameters.AddWithValue("$isFavorite", isFavorite ? 1 : 0);
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
    }

    public void SetAlbum(int id, int? albumId)
    {
        using var connection = _connectionFactory.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE Photo SET AlbumId = $albumId WHERE Id = $id;";
        command.Parameters.AddWithValue("$albumId", (object?)albumId ?? DBNull.Value);
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
    }

    public void Delete(int id)
    {
        using var connection = _connectionFactory.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM Photo WHERE Id = $id;";
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
    }

    private static IReadOnlyList<Photo> ReadAll(SqliteCommand command)
    {
        var results = new List<Photo>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            results.Add(ReadPhoto(reader));
        }
        return results;
    }

    private static Photo ReadPhoto(SqliteDataReader reader) => new()
    {
        Id = reader.GetInt32(0),
        ImagePath = reader.GetString(1),
        Title = reader.GetString(2),
        Description = reader.GetString(3),
        TakenDate = reader.IsDBNull(4) ? null : DateTime.Parse(reader.GetString(4)),
        Tags = reader.GetString(5),
        IsFavorite = reader.GetInt32(6) != 0,
        AlbumId = reader.IsDBNull(7) ? null : reader.GetInt32(7),
        ImportedAt = DateTime.Parse(reader.GetString(8))
    };
}
