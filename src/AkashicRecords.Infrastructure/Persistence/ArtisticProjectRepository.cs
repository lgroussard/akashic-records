using AkashicRecords.Domain;
using Microsoft.Data.Sqlite;

namespace AkashicRecords.Infrastructure.Persistence;

public sealed class ArtisticProjectRepository
{
    private readonly SqliteConnectionFactory _connectionFactory;

    public ArtisticProjectRepository(SqliteConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    public int Add(ArtisticProject project)
    {
        using var connection = _connectionFactory.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO ArtisticProject (Title, CreatedAt) VALUES ($title, $createdAt);
            SELECT last_insert_rowid();
            """;
        command.Parameters.AddWithValue("$title", project.Title);
        command.Parameters.AddWithValue("$createdAt", project.CreatedAt.ToString("O"));
        return Convert.ToInt32((long)command.ExecuteScalar()!);
    }

    public IReadOnlyList<ArtisticProject> GetAll()
    {
        using var connection = _connectionFactory.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Id, Title, CreatedAt FROM ArtisticProject ORDER BY CreatedAt;";

        var results = new List<ArtisticProject>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            results.Add(new ArtisticProject
            {
                Id = reader.GetInt32(0),
                Title = reader.GetString(1),
                CreatedAt = DateTime.Parse(reader.GetString(2))
            });
        }
        return results;
    }

    public ArtisticProject? GetById(int id)
    {
        using var connection = _connectionFactory.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Id, Title, CreatedAt FROM ArtisticProject WHERE Id = $id;";
        command.Parameters.AddWithValue("$id", id);

        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        return new ArtisticProject
        {
            Id = reader.GetInt32(0),
            Title = reader.GetString(1),
            CreatedAt = DateTime.Parse(reader.GetString(2))
        };
    }
}
