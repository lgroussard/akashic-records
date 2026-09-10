using AkashicRecords.Domain;
using Microsoft.Data.Sqlite;

namespace AkashicRecords.Infrastructure.Persistence;

public sealed class MoodRepository
{
    private readonly SqliteConnectionFactory _connectionFactory;

    public MoodRepository(SqliteConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    public int Add(Mood mood)
    {
        using var connection = _connectionFactory.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO Mood (Name) VALUES ($name);
            SELECT last_insert_rowid();
            """;
        command.Parameters.AddWithValue("$name", mood.Name);
        return Convert.ToInt32((long)command.ExecuteScalar()!);
    }

    public IReadOnlyList<Mood> GetAll()
    {
        using var connection = _connectionFactory.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Id, Name FROM Mood ORDER BY Name;";
        var results = new List<Mood>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            results.Add(ReadMood(reader));
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
            DELETE FROM TrackMood WHERE MoodId = $id;
            DELETE FROM Mood WHERE Id = $id;
            """;
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
    }

    private static Mood ReadMood(SqliteDataReader reader) => new()
    {
        Id = reader.GetInt32(0),
        Name = reader.GetString(1)
    };
}
