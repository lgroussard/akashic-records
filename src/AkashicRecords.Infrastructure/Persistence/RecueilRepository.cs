using AkashicRecords.Domain;
using Microsoft.Data.Sqlite;

namespace AkashicRecords.Infrastructure.Persistence;

public sealed class RecueilRepository
{
    private readonly SqliteConnectionFactory _connectionFactory;

    public RecueilRepository(SqliteConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    public int Add(Recueil recueil)
    {
        using var connection = _connectionFactory.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO Recueil (Title, CreatedAt) VALUES ($title, $createdAt);
            SELECT last_insert_rowid();
            """;
        command.Parameters.AddWithValue("$title", recueil.Title);
        command.Parameters.AddWithValue("$createdAt", recueil.CreatedAt.ToString("O"));
        return Convert.ToInt32((long)command.ExecuteScalar()!);
    }

    public IReadOnlyList<Recueil> GetAll()
    {
        using var connection = _connectionFactory.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Id, Title, CreatedAt, Summary FROM Recueil ORDER BY Title;";

        var results = new List<Recueil>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            results.Add(new Recueil
            {
                Id = reader.GetInt32(0),
                Title = reader.GetString(1),
                CreatedAt = DateTime.Parse(reader.GetString(2)),
                Summary = reader.IsDBNull(3) ? string.Empty : reader.GetString(3)
            });
        }
        return results;
    }

    public void Update(int id, string title)
    {
        using var connection = _connectionFactory.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE Recueil SET Title = $title WHERE Id = $id;";
        command.Parameters.AddWithValue("$title", title);
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
    }

    public void UpdateSummary(int id, string summary)
    {
        using var connection = _connectionFactory.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE Recueil SET Summary = $summary WHERE Id = $id;";
        command.Parameters.AddWithValue("$summary", summary);
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
    }

    public void Delete(int id)
    {
        using var connection = _connectionFactory.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM Recueil WHERE Id = $id;";
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
    }
}
