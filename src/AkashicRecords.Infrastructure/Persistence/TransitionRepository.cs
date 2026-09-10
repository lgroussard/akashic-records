using AkashicRecords.Domain;
using Microsoft.Data.Sqlite;

namespace AkashicRecords.Infrastructure.Persistence;

public sealed class TransitionRepository
{
    private readonly SqliteConnectionFactory _connectionFactory;

    public TransitionRepository(SqliteConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    public int Add(Transition transition)
    {
        using var connection = _connectionFactory.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO Transition (Title, Description, CurrentState, DesiredState, Notes, Resources, ReminderText, ShowOnDesktop, CreatedAt)
            VALUES ($title, $description, $currentState, $desiredState, $notes, $resources, $reminderText, $showOnDesktop, $createdAt);
            SELECT last_insert_rowid();
            """;
        command.Parameters.AddWithValue("$title", transition.Title);
        command.Parameters.AddWithValue("$description", transition.Description);
        command.Parameters.AddWithValue("$currentState", transition.CurrentState);
        command.Parameters.AddWithValue("$desiredState", transition.DesiredState);
        command.Parameters.AddWithValue("$notes", transition.Notes);
        command.Parameters.AddWithValue("$resources", transition.Resources);
        command.Parameters.AddWithValue("$reminderText", transition.ReminderText);
        command.Parameters.AddWithValue("$showOnDesktop", transition.ShowOnDesktop ? 1 : 0);
        command.Parameters.AddWithValue("$createdAt", transition.CreatedAt.ToString("O"));
        return Convert.ToInt32((long)command.ExecuteScalar()!);
    }

    public IReadOnlyList<Transition> GetAll()
    {
        using var connection = _connectionFactory.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Id, Title, Description, CurrentState, DesiredState, Notes, Resources, ReminderText, ShowOnDesktop, CreatedAt FROM Transition ORDER BY ShowOnDesktop DESC, Title;";

        var results = new List<Transition>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            results.Add(ReadTransition(reader));
        }
        return results;
    }

    public Transition? GetById(int id)
    {
        using var connection = _connectionFactory.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Id, Title, Description, CurrentState, DesiredState, Notes, Resources, ReminderText, ShowOnDesktop, CreatedAt FROM Transition WHERE Id = $id;";
        command.Parameters.AddWithValue("$id", id);

        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadTransition(reader) : null;
    }

    public void Update(int id, string title, string description, string currentState, string desiredState, string notes, string resources, string reminderText, bool showOnDesktop)
    {
        using var connection = _connectionFactory.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE Transition SET Title = $title, Description = $description, CurrentState = $currentState,
                DesiredState = $desiredState, Notes = $notes, Resources = $resources,
                ReminderText = $reminderText, ShowOnDesktop = $showOnDesktop WHERE Id = $id;
            """;
        command.Parameters.AddWithValue("$title", title);
        command.Parameters.AddWithValue("$description", description);
        command.Parameters.AddWithValue("$currentState", currentState);
        command.Parameters.AddWithValue("$desiredState", desiredState);
        command.Parameters.AddWithValue("$notes", notes);
        command.Parameters.AddWithValue("$resources", resources);
        command.Parameters.AddWithValue("$reminderText", reminderText);
        command.Parameters.AddWithValue("$showOnDesktop", showOnDesktop ? 1 : 0);
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
    }

    // Transitions the user has opted to surface as ambient desktop reminder notes.
    public IReadOnlyList<Transition> GetDesktopReminders()
    {
        using var connection = _connectionFactory.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Id, Title, Description, CurrentState, DesiredState, Notes, Resources, ReminderText, ShowOnDesktop, CreatedAt FROM Transition WHERE ShowOnDesktop = 1 ORDER BY Title;";

        var results = new List<Transition>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            results.Add(ReadTransition(reader));
        }
        return results;
    }

    public void Delete(int id)
    {
        using var connection = _connectionFactory.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM Transition WHERE Id = $id;";
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
    }

    private static Transition ReadTransition(SqliteDataReader reader) => new()
    {
        Id = reader.GetInt32(0),
        Title = reader.GetString(1),
        Description = reader.GetString(2),
        CurrentState = reader.GetString(3),
        DesiredState = reader.GetString(4),
        Notes = reader.GetString(5),
        Resources = reader.GetString(6),
        ReminderText = reader.GetString(7),
        ShowOnDesktop = reader.GetInt32(8) != 0,
        CreatedAt = DateTime.Parse(reader.GetString(9))
    };
}
