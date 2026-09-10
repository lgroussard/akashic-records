using AkashicRecords.Domain;
using Microsoft.Data.Sqlite;

namespace AkashicRecords.Infrastructure.Persistence;

public sealed class EvaluationAxisRepository
{
    private readonly SqliteConnectionFactory _connectionFactory;

    public EvaluationAxisRepository(SqliteConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    public int Add(EvaluationAxis axis)
    {
        using var connection = _connectionFactory.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO EvaluationAxis (Name) VALUES ($name);
            SELECT last_insert_rowid();
            """;
        command.Parameters.AddWithValue("$name", axis.Name);
        return Convert.ToInt32((long)command.ExecuteScalar()!);
    }

    public IReadOnlyList<EvaluationAxis> GetAll()
    {
        using var connection = _connectionFactory.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Id, Name FROM EvaluationAxis ORDER BY Name;";

        var results = new List<EvaluationAxis>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            results.Add(new EvaluationAxis { Id = reader.GetInt32(0), Name = reader.GetString(1) });
        }
        return results;
    }

    public void Delete(int id)
    {
        using var connection = _connectionFactory.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM EvaluationAxis WHERE Id = $id;";
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
    }
}
