using AkashicRecords.Domain;
using Microsoft.Data.Sqlite;

namespace AkashicRecords.Infrastructure.Persistence;

public sealed class CalendarEventRepository
{
    private readonly SqliteConnectionFactory _connectionFactory;

    public CalendarEventRepository(SqliteConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    public int Add(CalendarEvent calendarEvent)
    {
        using var connection = _connectionFactory.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO CalendarEvent (Title, Kind, Date, EndDate, StartHour, StartMinute, RecurringYearly, Location, Notes, CreatedAt)
            VALUES ($title, $kind, $date, $endDate, $startHour, $startMinute, $recurring, $location, $notes, $createdAt);
            SELECT last_insert_rowid();
            """;
        command.Parameters.AddWithValue("$title", calendarEvent.Title);
        command.Parameters.AddWithValue("$kind", (int)calendarEvent.Kind);
        command.Parameters.AddWithValue("$date", calendarEvent.Date.Date.ToString("O"));
        command.Parameters.AddWithValue("$endDate", (object?)calendarEvent.EndDate?.Date.ToString("O") ?? DBNull.Value);
        command.Parameters.AddWithValue("$startHour", (object?)calendarEvent.StartHour ?? DBNull.Value);
        command.Parameters.AddWithValue("$startMinute", (object?)calendarEvent.StartMinute ?? DBNull.Value);
        command.Parameters.AddWithValue("$recurring", calendarEvent.RecurringYearly ? 1 : 0);
        command.Parameters.AddWithValue("$location", calendarEvent.Location);
        command.Parameters.AddWithValue("$notes", calendarEvent.Notes);
        command.Parameters.AddWithValue("$createdAt", calendarEvent.CreatedAt.ToString("O"));
        return Convert.ToInt32((long)command.ExecuteScalar()!);
    }

    public IReadOnlyList<CalendarEvent> GetAll()
    {
        using var connection = _connectionFactory.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT Id, Title, Kind, Date, EndDate, StartHour, StartMinute, RecurringYearly, Location, Notes, CreatedAt " +
            "FROM CalendarEvent ORDER BY Date, StartHour;";

        var results = new List<CalendarEvent>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            results.Add(ReadEvent(reader));
        }
        return results;
    }

    public CalendarEvent? GetById(int id)
    {
        using var connection = _connectionFactory.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT Id, Title, Kind, Date, EndDate, StartHour, StartMinute, RecurringYearly, Location, Notes, CreatedAt " +
            "FROM CalendarEvent WHERE Id = $id;";
        command.Parameters.AddWithValue("$id", id);

        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadEvent(reader) : null;
    }

    public void Update(int id, string title, CalendarEventKind kind, DateTime date, DateTime? endDate, int? startHour, int? startMinute,
        bool recurringYearly, string location, string notes)
    {
        using var connection = _connectionFactory.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE CalendarEvent
            SET Title = $title, Kind = $kind, Date = $date, EndDate = $endDate, StartHour = $startHour, StartMinute = $startMinute,
                RecurringYearly = $recurring, Location = $location, Notes = $notes
            WHERE Id = $id;
            """;
        command.Parameters.AddWithValue("$title", title);
        command.Parameters.AddWithValue("$kind", (int)kind);
        command.Parameters.AddWithValue("$date", date.Date.ToString("O"));
        command.Parameters.AddWithValue("$endDate", (object?)endDate?.Date.ToString("O") ?? DBNull.Value);
        command.Parameters.AddWithValue("$startHour", (object?)startHour ?? DBNull.Value);
        command.Parameters.AddWithValue("$startMinute", (object?)startMinute ?? DBNull.Value);
        command.Parameters.AddWithValue("$recurring", recurringYearly ? 1 : 0);
        command.Parameters.AddWithValue("$location", location);
        command.Parameters.AddWithValue("$notes", notes);
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
    }

    public void Delete(int id)
    {
        using var connection = _connectionFactory.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM CalendarEvent WHERE Id = $id;";
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
    }

    private static CalendarEvent ReadEvent(SqliteDataReader reader) => new()
    {
        Id = reader.GetInt32(0),
        Title = reader.GetString(1),
        Kind = (CalendarEventKind)reader.GetInt32(2),
        Date = DateTime.Parse(reader.GetString(3)).Date,
        EndDate = reader.IsDBNull(4) ? null : DateTime.Parse(reader.GetString(4)).Date,
        StartHour = reader.IsDBNull(5) ? (int?)null : reader.GetInt32(5),
        StartMinute = reader.IsDBNull(6) ? (int?)null : reader.GetInt32(6),
        RecurringYearly = reader.GetInt32(7) != 0,
        Location = reader.GetString(8),
        Notes = reader.GetString(9),
        CreatedAt = DateTime.Parse(reader.GetString(10))
    };
}
