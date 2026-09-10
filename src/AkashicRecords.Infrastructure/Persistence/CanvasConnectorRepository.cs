using AkashicRecords.Domain;
using Microsoft.Data.Sqlite;

namespace AkashicRecords.Infrastructure.Persistence;

public sealed class CanvasConnectorRepository
{
    private readonly SqliteConnectionFactory _connectionFactory;

    public CanvasConnectorRepository(SqliteConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    public int Add(CanvasConnector connector)
    {
        using var connection = _connectionFactory.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO CanvasConnector (ProjectId, FromElementId, ToElementId)
            VALUES ($projectId, $fromElementId, $toElementId);
            SELECT last_insert_rowid();
            """;
        command.Parameters.AddWithValue("$projectId", connector.ProjectId);
        command.Parameters.AddWithValue("$fromElementId", connector.FromElementId);
        command.Parameters.AddWithValue("$toElementId", connector.ToElementId);
        return Convert.ToInt32((long)command.ExecuteScalar()!);
    }

    public IReadOnlyList<CanvasConnector> GetByProject(int projectId)
    {
        using var connection = _connectionFactory.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Id, ProjectId, FromElementId, ToElementId FROM CanvasConnector WHERE ProjectId = $projectId;";
        command.Parameters.AddWithValue("$projectId", projectId);

        var results = new List<CanvasConnector>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            results.Add(new CanvasConnector
            {
                Id = reader.GetInt32(0),
                ProjectId = reader.GetInt32(1),
                FromElementId = reader.GetInt32(2),
                ToElementId = reader.GetInt32(3)
            });
        }
        return results;
    }

    public void Delete(int id)
    {
        using var connection = _connectionFactory.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM CanvasConnector WHERE Id = $id;";
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
    }

    // Re-inserts a previously deleted connector with the same Id, used by undo.
    public void Restore(CanvasConnector connector)
    {
        using var connection = _connectionFactory.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO CanvasConnector (Id, ProjectId, FromElementId, ToElementId)
            VALUES ($id, $projectId, $fromElementId, $toElementId);
            """;
        command.Parameters.AddWithValue("$id", connector.Id);
        command.Parameters.AddWithValue("$projectId", connector.ProjectId);
        command.Parameters.AddWithValue("$fromElementId", connector.FromElementId);
        command.Parameters.AddWithValue("$toElementId", connector.ToElementId);
        command.ExecuteNonQuery();
    }
}
