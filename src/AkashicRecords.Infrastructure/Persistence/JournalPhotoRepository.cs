using AkashicRecords.Domain;
using Microsoft.Data.Sqlite;

namespace AkashicRecords.Infrastructure.Persistence;

public sealed class JournalPhotoRepository
{
    private readonly SqliteConnectionFactory _connectionFactory;

    public JournalPhotoRepository(SqliteConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    public int Add(JournalPhoto photo)
    {
        using var connection = _connectionFactory.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO JournalPhoto (EntryId, ImagePath) VALUES ($entryId, $imagePath);
            SELECT last_insert_rowid();
            """;
        command.Parameters.AddWithValue("$entryId", photo.EntryId);
        command.Parameters.AddWithValue("$imagePath", photo.ImagePath);
        return Convert.ToInt32((long)command.ExecuteScalar()!);
    }

    public IReadOnlyList<JournalPhoto> GetByEntry(int entryId)
    {
        using var connection = _connectionFactory.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Id, EntryId, ImagePath FROM JournalPhoto WHERE EntryId = $entryId;";
        command.Parameters.AddWithValue("$entryId", entryId);

        var results = new List<JournalPhoto>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            results.Add(new JournalPhoto { Id = reader.GetInt32(0), EntryId = reader.GetInt32(1), ImagePath = reader.GetString(2) });
        }
        return results;
    }

    public void Delete(int id)
    {
        using var connection = _connectionFactory.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM JournalPhoto WHERE Id = $id;";
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
    }
}
