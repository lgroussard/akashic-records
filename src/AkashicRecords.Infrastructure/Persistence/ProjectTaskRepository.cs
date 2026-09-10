using AkashicRecords.Domain;
using Microsoft.Data.Sqlite;

namespace AkashicRecords.Infrastructure.Persistence;

public sealed class ProjectTaskRepository
{
    private readonly SqliteConnectionFactory _connectionFactory;

    public ProjectTaskRepository(SqliteConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    public int Add(ProjectTask task)
    {
        using var connection = _connectionFactory.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO ProjectTask (ProjectId, Text, IsDone, SortOrder)
            VALUES ($projectId, $text, $isDone, $sortOrder);
            SELECT last_insert_rowid();
            """;
        command.Parameters.AddWithValue("$projectId", task.ProjectId);
        command.Parameters.AddWithValue("$text", task.Text);
        command.Parameters.AddWithValue("$isDone", task.IsDone ? 1 : 0);
        command.Parameters.AddWithValue("$sortOrder", task.SortOrder);
        return Convert.ToInt32((long)command.ExecuteScalar()!);
    }

    public IReadOnlyList<ProjectTask> GetByProject(int projectId)
    {
        using var connection = _connectionFactory.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Id, ProjectId, Text, IsDone, SortOrder FROM ProjectTask WHERE ProjectId = $projectId ORDER BY SortOrder;";
        command.Parameters.AddWithValue("$projectId", projectId);

        var results = new List<ProjectTask>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            results.Add(ReadTask(reader));
        }
        return results;
    }

    public void SetDone(int id, bool isDone)
    {
        using var connection = _connectionFactory.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE ProjectTask SET IsDone = $isDone WHERE Id = $id;";
        command.Parameters.AddWithValue("$isDone", isDone ? 1 : 0);
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
    }

    public void UpdateText(int id, string text)
    {
        using var connection = _connectionFactory.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE ProjectTask SET Text = $text WHERE Id = $id;";
        command.Parameters.AddWithValue("$text", text);
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
    }

    public void Delete(int id)
    {
        using var connection = _connectionFactory.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM ProjectTask WHERE Id = $id;";
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
    }

    private static ProjectTask ReadTask(SqliteDataReader reader) => new()
    {
        Id = reader.GetInt32(0),
        ProjectId = reader.GetInt32(1),
        Text = reader.GetString(2),
        IsDone = reader.GetInt32(3) != 0,
        SortOrder = reader.GetInt32(4)
    };
}
