using AkashicRecords.Domain;
using Microsoft.Data.Sqlite;

namespace AkashicRecords.Infrastructure.Persistence;

public sealed class MusicTrackRepository
{
    private readonly SqliteConnectionFactory _connectionFactory;

    public MusicTrackRepository(SqliteConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    public int Add(MusicTrack track)
    {
        using var connection = _connectionFactory.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO MusicTrack (FilePath, Title, Artist, DurationSeconds, CoverImagePath, AddedAt, Album)
            VALUES ($filePath, $title, $artist, $durationSeconds, $coverImagePath, $addedAt, $album);
            SELECT last_insert_rowid();
            """;
        command.Parameters.AddWithValue("$filePath", track.FilePath);
        command.Parameters.AddWithValue("$title", track.Title);
        command.Parameters.AddWithValue("$artist", track.Artist);
        command.Parameters.AddWithValue("$durationSeconds", (object?)track.DurationSeconds ?? DBNull.Value);
        command.Parameters.AddWithValue("$coverImagePath", (object?)track.CoverImagePath ?? DBNull.Value);
        command.Parameters.AddWithValue("$addedAt", track.AddedAt.ToString("O"));
        command.Parameters.AddWithValue("$album", track.Album);
        return Convert.ToInt32((long)command.ExecuteScalar()!);
    }

    public IReadOnlyList<MusicTrack> GetAll()
    {
        using var connection = _connectionFactory.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Id, FilePath, Title, Artist, DurationSeconds, CoverImagePath, AddedAt, Album FROM MusicTrack ORDER BY Title;";
        return ReadAll(command);
    }

    public MusicTrack? GetById(int id)
    {
        using var connection = _connectionFactory.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Id, FilePath, Title, Artist, DurationSeconds, CoverImagePath, AddedAt, Album FROM MusicTrack WHERE Id = $id;";
        command.Parameters.AddWithValue("$id", id);
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadTrack(reader) : null;
    }

    // Used for scan dedupe - returns the existing row for a given relative FilePath, or null.
    public MusicTrack? GetByPath(string filePath)
    {
        using var connection = _connectionFactory.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Id, FilePath, Title, Artist, DurationSeconds, CoverImagePath, AddedAt, Album FROM MusicTrack WHERE FilePath = $filePath;";
        command.Parameters.AddWithValue("$filePath", filePath);
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadTrack(reader) : null;
    }

    public IReadOnlyList<MusicTrack> GetByMood(int moodId)
    {
        using var connection = _connectionFactory.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT t.Id, t.FilePath, t.Title, t.Artist, t.DurationSeconds, t.CoverImagePath, t.AddedAt, t.Album
            FROM MusicTrack t
            INNER JOIN TrackMood tm ON tm.TrackId = t.Id
            WHERE tm.MoodId = $moodId
            ORDER BY t.Title;
            """;
        command.Parameters.AddWithValue("$moodId", moodId);
        return ReadAll(command);
    }

    public void UpdateMetadata(int id, string title, string artist)
    {
        using var connection = _connectionFactory.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE MusicTrack SET Title = $title, Artist = $artist WHERE Id = $id;";
        command.Parameters.AddWithValue("$title", title);
        command.Parameters.AddWithValue("$artist", artist);
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
    }

    public void UpdateCover(int id, string? coverPath)
    {
        using var connection = _connectionFactory.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE MusicTrack SET CoverImagePath = $coverImagePath WHERE Id = $id;";
        command.Parameters.AddWithValue("$coverImagePath", (object?)coverPath ?? DBNull.Value);
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
    }

    public void UpdateDuration(int id, double seconds)
    {
        using var connection = _connectionFactory.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE MusicTrack SET DurationSeconds = $durationSeconds WHERE Id = $id;";
        command.Parameters.AddWithValue("$durationSeconds", seconds);
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
    }

    public void UpdateAlbum(int id, string album)
    {
        using var connection = _connectionFactory.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE MusicTrack SET Album = $album WHERE Id = $id;";
        command.Parameters.AddWithValue("$album", album);
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
    }

    // Cascade isn't enforced at the DB level in this app, so clean up the join rows manually.
    public void Delete(int id)
    {
        using var connection = _connectionFactory.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            DELETE FROM TrackMood WHERE TrackId = $id;
            DELETE FROM PlaylistTrack WHERE TrackId = $id;
            DELETE FROM MusicTrack WHERE Id = $id;
            """;
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
    }

    private static List<MusicTrack> ReadAll(SqliteCommand command)
    {
        var results = new List<MusicTrack>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            results.Add(ReadTrack(reader));
        }
        return results;
    }

    private static MusicTrack ReadTrack(SqliteDataReader reader) => new()
    {
        Id = reader.GetInt32(0),
        FilePath = reader.GetString(1),
        Title = reader.GetString(2),
        Artist = reader.GetString(3),
        DurationSeconds = reader.IsDBNull(4) ? null : reader.GetDouble(4),
        CoverImagePath = reader.IsDBNull(5) ? null : reader.GetString(5),
        AddedAt = DateTime.Parse(reader.GetString(6)),
        Album = reader.IsDBNull(7) ? string.Empty : reader.GetString(7)
    };
}
