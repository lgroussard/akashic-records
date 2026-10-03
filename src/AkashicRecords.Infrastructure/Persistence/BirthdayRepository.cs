using AkashicRecords.Domain;
using Microsoft.Data.Sqlite;

namespace AkashicRecords.Infrastructure.Persistence;

public sealed class BirthdayRepository
{
    private readonly SqliteConnectionFactory _connectionFactory;

    public BirthdayRepository(SqliteConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    public int Add(Birthday birthday)
    {
        using var connection = _connectionFactory.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO Birthday (Name, Month, Day, BirthYear, Notes)
            VALUES ($name, $month, $day, $birthYear, $notes);
            SELECT last_insert_rowid();
            """;
        command.Parameters.AddWithValue("$name", birthday.Name);
        command.Parameters.AddWithValue("$month", birthday.Month);
        command.Parameters.AddWithValue("$day", birthday.Day);
        command.Parameters.AddWithValue("$birthYear", (object?)birthday.BirthYear ?? DBNull.Value);
        command.Parameters.AddWithValue("$notes", birthday.Notes);
        return Convert.ToInt32((long)command.ExecuteScalar()!);
    }

    // Ordered by upcoming day-of-year so the list reads like a countdown, then by name.
    public IReadOnlyList<Birthday> GetAll()
    {
        using var connection = _connectionFactory.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Id, Name, Month, Day, BirthYear, Notes FROM Birthday ORDER BY Month, Day, Name;";

        var results = new List<Birthday>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            results.Add(ReadBirthday(reader));
        }
        return results;
    }

    public void Update(int id, string name, int month, int day, int? birthYear, string notes)
    {
        using var connection = _connectionFactory.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            "UPDATE Birthday SET Name = $name, Month = $month, Day = $day, BirthYear = $birthYear, Notes = $notes WHERE Id = $id;";
        command.Parameters.AddWithValue("$name", name);
        command.Parameters.AddWithValue("$month", month);
        command.Parameters.AddWithValue("$day", day);
        command.Parameters.AddWithValue("$birthYear", (object?)birthYear ?? DBNull.Value);
        command.Parameters.AddWithValue("$notes", notes);
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
    }

    public void Delete(int id)
    {
        using var connection = _connectionFactory.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM Birthday WHERE Id = $id;";
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
    }

    private static Birthday ReadBirthday(SqliteDataReader reader) => new()
    {
        Id = reader.GetInt32(0),
        Name = reader.GetString(1),
        Month = reader.GetInt32(2),
        Day = reader.GetInt32(3),
        BirthYear = reader.IsDBNull(4) ? (int?)null : reader.GetInt32(4),
        Notes = reader.GetString(5)
    };
}
