using AkashicRecords.Domain;
using Microsoft.Data.Sqlite;

namespace AkashicRecords.Infrastructure.Persistence;

public sealed class TrackMoodRepository
{
    private readonly SqliteConnectionFactory _connectionFactory;

    public TrackMoodRepository(SqliteConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    // No-op if the pair already exists so callers can toggle freely without duplicate rows.
    public void Add(int trackId, int moodId)
    {
        using var connection = _connectionFactory.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO TrackMood (TrackId, MoodId)
            SELECT $trackId, $moodId
            WHERE NOT EXISTS (SELECT 1 FROM TrackMood WHERE TrackId = $trackId AND MoodId = $moodId);
            """;
        command.Parameters.AddWithValue("$trackId", trackId);
        command.Parameters.AddWithValue("$moodId", moodId);
        command.ExecuteNonQuery();
    }

    public void Delete(int trackId, int moodId)
    {
        using var connection = _connectionFactory.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM TrackMood WHERE TrackId = $trackId AND MoodId = $moodId;";
        command.Parameters.AddWithValue("$trackId", trackId);
        command.Parameters.AddWithValue("$moodId", moodId);
        command.ExecuteNonQuery();
    }

    public IReadOnlyList<int> GetMoodIdsForTrack(int trackId)
    {
        using var connection = _connectionFactory.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT MoodId FROM TrackMood WHERE TrackId = $trackId;";
        command.Parameters.AddWithValue("$trackId", trackId);
        var results = new List<int>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            results.Add(reader.GetInt32(0));
        }
        return results;
    }

    public IReadOnlyList<Mood> GetMoodsForTrack(int trackId)
    {
        using var connection = _connectionFactory.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT m.Id, m.Name
            FROM Mood m
            INNER JOIN TrackMood tm ON tm.MoodId = m.Id
            WHERE tm.TrackId = $trackId
            ORDER BY m.Name;
            """;
        command.Parameters.AddWithValue("$trackId", trackId);
        var results = new List<Mood>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            results.Add(new Mood { Id = reader.GetInt32(0), Name = reader.GetString(1) });
        }
        return results;
    }
}
