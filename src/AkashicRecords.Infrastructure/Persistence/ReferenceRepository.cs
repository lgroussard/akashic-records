using AkashicRecords.Domain;

namespace AkashicRecords.Infrastructure.Persistence;

public sealed class ReferenceRepository
{
    private readonly SqliteConnectionFactory _connectionFactory;

    public ReferenceRepository(SqliteConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    public int Add(Reference reference)
    {
        using var connection = _connectionFactory.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO Reference (ObservationId, ProjectId) VALUES ($observationId, $projectId);
            SELECT last_insert_rowid();
            """;
        command.Parameters.AddWithValue("$observationId", reference.ObservationId);
        command.Parameters.AddWithValue("$projectId", reference.ProjectId);
        return Convert.ToInt32((long)command.ExecuteScalar()!);
    }

    // Every observation referenced by a given project - lets the canvas show "Princesse Kaguya -> Faisan".
    public IReadOnlyList<Reference> GetByProject(int projectId)
    {
        using var connection = _connectionFactory.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Id, ObservationId, ProjectId FROM Reference WHERE ProjectId = $projectId;";
        command.Parameters.AddWithValue("$projectId", projectId);
        return ReadAll(command);
    }

    // Every project that references a given observation - lets the artwork page show "used in these projects".
    public IReadOnlyList<Reference> GetByObservation(int observationId)
    {
        using var connection = _connectionFactory.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Id, ObservationId, ProjectId FROM Reference WHERE ObservationId = $observationId;";
        command.Parameters.AddWithValue("$observationId", observationId);
        return ReadAll(command);
    }

    public void Delete(int id)
    {
        using var connection = _connectionFactory.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM Reference WHERE Id = $id;";
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
    }

    private static List<Reference> ReadAll(Microsoft.Data.Sqlite.SqliteCommand command)
    {
        var results = new List<Reference>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            results.Add(new Reference
            {
                Id = reader.GetInt32(0),
                ObservationId = reader.GetInt32(1),
                ProjectId = reader.GetInt32(2)
            });
        }
        return results;
    }
}
