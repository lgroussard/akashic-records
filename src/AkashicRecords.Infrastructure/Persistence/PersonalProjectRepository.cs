using AkashicRecords.Domain;
using Microsoft.Data.Sqlite;

namespace AkashicRecords.Infrastructure.Persistence;

public sealed class PersonalProjectRepository
{
    private readonly SqliteConnectionFactory _connectionFactory;

    public PersonalProjectRepository(SqliteConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    public int Add(PersonalProject project)
    {
        using var connection = _connectionFactory.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO PersonalProject (Title, Description, Status, Deadline, CreatedAt)
            VALUES ($title, $description, $status, $deadline, $createdAt);
            SELECT last_insert_rowid();
            """;
        command.Parameters.AddWithValue("$title", project.Title);
        command.Parameters.AddWithValue("$description", project.Description);
        command.Parameters.AddWithValue("$status", (int)project.Status);
        command.Parameters.AddWithValue("$deadline", (object?)project.Deadline?.ToString("O") ?? DBNull.Value);
        command.Parameters.AddWithValue("$createdAt", project.CreatedAt.ToString("O"));
        return Convert.ToInt32((long)command.ExecuteScalar()!);
    }

    public IReadOnlyList<PersonalProject> GetAll()
    {
        using var connection = _connectionFactory.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Id, Title, Description, Status, Deadline, CreatedAt FROM PersonalProject ORDER BY Status, Title;";

        var results = new List<PersonalProject>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            results.Add(ReadProject(reader));
        }
        return results;
    }

    public PersonalProject? GetById(int id)
    {
        using var connection = _connectionFactory.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Id, Title, Description, Status, Deadline, CreatedAt FROM PersonalProject WHERE Id = $id;";
        command.Parameters.AddWithValue("$id", id);

        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadProject(reader) : null;
    }

    public void Update(int id, string title, string description, ProjectStatus status, DateTime? deadline)
    {
        using var connection = _connectionFactory.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE PersonalProject SET Title = $title, Description = $description, Status = $status, Deadline = $deadline WHERE Id = $id;";
        command.Parameters.AddWithValue("$title", title);
        command.Parameters.AddWithValue("$description", description);
        command.Parameters.AddWithValue("$status", (int)status);
        command.Parameters.AddWithValue("$deadline", (object?)deadline?.ToString("O") ?? DBNull.Value);
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
    }

    public void Delete(int id)
    {
        using var connection = _connectionFactory.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM PersonalProject WHERE Id = $id;";
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
    }

    private static PersonalProject ReadProject(SqliteDataReader reader) => new()
    {
        Id = reader.GetInt32(0),
        Title = reader.GetString(1),
        Description = reader.GetString(2),
        Status = (ProjectStatus)reader.GetInt32(3),
        Deadline = reader.IsDBNull(4) ? (DateTime?)null : DateTime.Parse(reader.GetString(4)),
        CreatedAt = DateTime.Parse(reader.GetString(5))
    };
}
