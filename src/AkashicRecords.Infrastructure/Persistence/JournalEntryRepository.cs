using AkashicRecords.Domain;
using Microsoft.Data.Sqlite;

namespace AkashicRecords.Infrastructure.Persistence;

public sealed class JournalEntryRepository
{
    private readonly SqliteConnectionFactory _connectionFactory;

    public JournalEntryRepository(SqliteConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    public int Add(JournalEntry entry)
    {
        using var connection = _connectionFactory.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO JournalEntry (Title, Text, EntryDate, Tags)
            VALUES ($title, $text, $entryDate, $tags);
            SELECT last_insert_rowid();
            """;
        command.Parameters.AddWithValue("$title", entry.Title);
        command.Parameters.AddWithValue("$text", entry.Text);
        command.Parameters.AddWithValue("$entryDate", entry.EntryDate.ToString("O"));
        command.Parameters.AddWithValue("$tags", entry.Tags);
        return Convert.ToInt32((long)command.ExecuteScalar()!);
    }

    public IReadOnlyList<JournalEntry> GetAll()
    {
        using var connection = _connectionFactory.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Id, Title, Text, EntryDate, Tags FROM JournalEntry ORDER BY EntryDate DESC;";

        var results = new List<JournalEntry>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            results.Add(ReadEntry(reader));
        }
        return results;
    }

    public JournalEntry? GetById(int id)
    {
        using var connection = _connectionFactory.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Id, Title, Text, EntryDate, Tags FROM JournalEntry WHERE Id = $id;";
        command.Parameters.AddWithValue("$id", id);

        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadEntry(reader) : null;
    }

    public void Update(int id, string title, string text, DateTime entryDate, string tags)
    {
        using var connection = _connectionFactory.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE JournalEntry SET Title = $title, Text = $text, EntryDate = $entryDate, Tags = $tags WHERE Id = $id;";
        command.Parameters.AddWithValue("$title", title);
        command.Parameters.AddWithValue("$text", text);
        command.Parameters.AddWithValue("$entryDate", entryDate.ToString("O"));
        command.Parameters.AddWithValue("$tags", tags);
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
    }

    public void Delete(int id)
    {
        using var connection = _connectionFactory.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM JournalEntry WHERE Id = $id;";
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
    }

    private static JournalEntry ReadEntry(SqliteDataReader reader) => new()
    {
        Id = reader.GetInt32(0),
        Title = reader.GetString(1),
        Text = reader.GetString(2),
        EntryDate = DateTime.Parse(reader.GetString(3)),
        Tags = reader.GetString(4)
    };
}
