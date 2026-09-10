using AkashicRecords.Domain;
using Microsoft.Data.Sqlite;

namespace AkashicRecords.Infrastructure.Persistence;

public sealed class PlaylistRepository
{
    private readonly SqliteConnectionFactory _connectionFactory;

    public PlaylistRepository(SqliteConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    public int Add(Playlist playlist)
    {
        using var connection = _connectionFactory.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO Playlist (Name, CreatedAt) VALUES ($name, $createdAt);
            SELECT last_insert_rowid();
            """;
        command.Parameters.AddWithValue("$name", playlist.Name);
        command.Parameters.AddWithValue("$createdAt", playlist.CreatedAt.ToString("O"));
        return Convert.ToInt32((long)command.ExecuteScalar()!);
    }

    public IReadOnlyList<Playlist> GetAll()
    {
        using var connection = _connectionFactory.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Id, Name, CreatedAt FROM Playlist ORDER BY Name;";
        var results = new List<Playlist>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            results.Add(ReadPlaylist(reader));
        }
        return results;
    }

    // Cascade isn't enforced at the DB level in this app, so clean up the join rows manually.
    public void Delete(int id)
    {
        using var connection = _connectionFactory.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            DELETE FROM PlaylistTrack WHERE PlaylistId = $id;
            DELETE FROM Playlist WHERE Id = $id;
            """;
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
    }

    private static Playlist ReadPlaylist(SqliteDataReader reader) => new()
    {
        Id = reader.GetInt32(0),
        Name = reader.GetString(1),
        CreatedAt = DateTime.Parse(reader.GetString(2))
    };
}
