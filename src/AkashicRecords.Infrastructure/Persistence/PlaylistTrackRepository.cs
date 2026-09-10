using AkashicRecords.Domain;
using Microsoft.Data.Sqlite;

namespace AkashicRecords.Infrastructure.Persistence;

public sealed class PlaylistTrackRepository
{
    private readonly SqliteConnectionFactory _connectionFactory;

    public PlaylistTrackRepository(SqliteConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    public int Add(int playlistId, int trackId, int sortOrder)
    {
        using var connection = _connectionFactory.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO PlaylistTrack (PlaylistId, TrackId, SortOrder) VALUES ($playlistId, $trackId, $sortOrder);
            SELECT last_insert_rowid();
            """;
        command.Parameters.AddWithValue("$playlistId", playlistId);
        command.Parameters.AddWithValue("$trackId", trackId);
        command.Parameters.AddWithValue("$sortOrder", sortOrder);
        return Convert.ToInt32((long)command.ExecuteScalar()!);
    }

    public IReadOnlyList<MusicTrack> GetTracksForPlaylist(int playlistId)
    {
        using var connection = _connectionFactory.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT t.Id, t.FilePath, t.Title, t.Artist, t.DurationSeconds, t.CoverImagePath, t.AddedAt
            FROM MusicTrack t
            INNER JOIN PlaylistTrack pt ON pt.TrackId = t.Id
            WHERE pt.PlaylistId = $playlistId
            ORDER BY pt.SortOrder;
            """;
        command.Parameters.AddWithValue("$playlistId", playlistId);
        var results = new List<MusicTrack>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            results.Add(new MusicTrack
            {
                Id = reader.GetInt32(0),
                FilePath = reader.GetString(1),
                Title = reader.GetString(2),
                Artist = reader.GetString(3),
                DurationSeconds = reader.IsDBNull(4) ? null : reader.GetDouble(4),
                CoverImagePath = reader.IsDBNull(5) ? null : reader.GetString(5),
                AddedAt = DateTime.Parse(reader.GetString(6))
            });
        }
        return results;
    }

    public void Remove(int playlistId, int trackId)
    {
        using var connection = _connectionFactory.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM PlaylistTrack WHERE PlaylistId = $playlistId AND TrackId = $trackId;";
        command.Parameters.AddWithValue("$playlistId", playlistId);
        command.Parameters.AddWithValue("$trackId", trackId);
        command.ExecuteNonQuery();
    }
}
