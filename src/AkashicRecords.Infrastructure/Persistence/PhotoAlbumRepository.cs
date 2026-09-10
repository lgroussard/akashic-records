using AkashicRecords.Domain;
using Microsoft.Data.Sqlite;

namespace AkashicRecords.Infrastructure.Persistence;

public sealed class PhotoAlbumRepository
{
    private readonly SqliteConnectionFactory _connectionFactory;

    public PhotoAlbumRepository(SqliteConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    public int Add(PhotoAlbum album)
    {
        using var connection = _connectionFactory.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO PhotoAlbum (Name, CreatedAt)
            VALUES ($name, $createdAt);
            SELECT last_insert_rowid();
            """;
        command.Parameters.AddWithValue("$name", album.Name);
        command.Parameters.AddWithValue("$createdAt", album.CreatedAt.ToString("O"));
        return Convert.ToInt32((long)command.ExecuteScalar()!);
    }

    public IReadOnlyList<PhotoAlbum> GetAll()
    {
        using var connection = _connectionFactory.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Id, Name, CreatedAt FROM PhotoAlbum ORDER BY Name;";

        var results = new List<PhotoAlbum>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            results.Add(ReadAlbum(reader));
        }
        return results;
    }

    public void Delete(int id)
    {
        using var connection = _connectionFactory.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM PhotoAlbum WHERE Id = $id;";
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
    }

    private static PhotoAlbum ReadAlbum(SqliteDataReader reader) => new()
    {
        Id = reader.GetInt32(0),
        Name = reader.GetString(1),
        CreatedAt = DateTime.Parse(reader.GetString(2))
    };
}
