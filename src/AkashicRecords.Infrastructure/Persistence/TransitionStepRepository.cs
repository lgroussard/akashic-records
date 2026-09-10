using AkashicRecords.Domain;
using Microsoft.Data.Sqlite;

namespace AkashicRecords.Infrastructure.Persistence;

public sealed class TransitionStepRepository
{
    private readonly SqliteConnectionFactory _connectionFactory;

    public TransitionStepRepository(SqliteConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    public int Add(TransitionStep step)
    {
        using var connection = _connectionFactory.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO TransitionStep (TransitionId, Text, IsDone, SortOrder)
            VALUES ($transitionId, $text, $isDone, $sortOrder);
            SELECT last_insert_rowid();
            """;
        command.Parameters.AddWithValue("$transitionId", step.TransitionId);
        command.Parameters.AddWithValue("$text", step.Text);
        command.Parameters.AddWithValue("$isDone", step.IsDone ? 1 : 0);
        command.Parameters.AddWithValue("$sortOrder", step.SortOrder);
        return Convert.ToInt32((long)command.ExecuteScalar()!);
    }

    public IReadOnlyList<TransitionStep> GetByTransition(int transitionId)
    {
        using var connection = _connectionFactory.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Id, TransitionId, Text, IsDone, SortOrder FROM TransitionStep WHERE TransitionId = $transitionId ORDER BY SortOrder;";
        command.Parameters.AddWithValue("$transitionId", transitionId);

        var results = new List<TransitionStep>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            results.Add(ReadStep(reader));
        }
        return results;
    }

    public void SetDone(int id, bool isDone)
    {
        using var connection = _connectionFactory.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE TransitionStep SET IsDone = $isDone WHERE Id = $id;";
        command.Parameters.AddWithValue("$isDone", isDone ? 1 : 0);
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
    }

    public void UpdateText(int id, string text)
    {
        using var connection = _connectionFactory.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE TransitionStep SET Text = $text WHERE Id = $id;";
        command.Parameters.AddWithValue("$text", text);
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
    }

    public void Delete(int id)
    {
        using var connection = _connectionFactory.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM TransitionStep WHERE Id = $id;";
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
    }

    private static TransitionStep ReadStep(SqliteDataReader reader) => new()
    {
        Id = reader.GetInt32(0),
        TransitionId = reader.GetInt32(1),
        Text = reader.GetString(2),
        IsDone = reader.GetInt32(3) != 0,
        SortOrder = reader.GetInt32(4)
    };
}
