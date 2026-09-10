using AkashicRecords.Domain;
using Microsoft.Data.Sqlite;

namespace AkashicRecords.Infrastructure.Persistence;

public sealed class ObservationRepository
{
    // Flattened view used to pick an observation by its artwork title + subject (e.g. the canvas reference picker).
    // Carries ArtworkId/ArtworkCategory too so global search can deep-link back to the owning artwork's fiche.
    public sealed record ObservationSummary(int ObservationId, int ArtworkId, ArtworkCategory ArtworkCategory, string ArtworkTitle, string Subject, string Content)
    {
        public string Label => $"{ArtworkTitle} — {Subject}";
    }

    private readonly SqliteConnectionFactory _connectionFactory;

    public ObservationRepository(SqliteConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    public IReadOnlyList<ObservationSummary> GetAllWithArtwork()
    {
        using var connection = _connectionFactory.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT o.Id, o.ArtworkId, a.Category, a.Title, o.Subject, o.Content
            FROM Observation o
            JOIN Artwork a ON a.Id = o.ArtworkId
            ORDER BY a.Title, o.Subject;
            """;

        var results = new List<ObservationSummary>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            results.Add(new ObservationSummary(
                reader.GetInt32(0), reader.GetInt32(1), (ArtworkCategory)reader.GetInt32(2),
                reader.GetString(3), reader.GetString(4), reader.GetString(5)));
        }
        return results;
    }

    public int Add(Observation observation)
    {
        using var connection = _connectionFactory.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO Observation (ArtworkId, Subject, Content, CreatedAt)
            VALUES ($artworkId, $subject, $content, $createdAt);
            SELECT last_insert_rowid();
            """;
        command.Parameters.AddWithValue("$artworkId", observation.ArtworkId);
        command.Parameters.AddWithValue("$subject", observation.Subject);
        command.Parameters.AddWithValue("$content", observation.Content);
        command.Parameters.AddWithValue("$createdAt", observation.CreatedAt.ToString("O"));
        return Convert.ToInt32((long)command.ExecuteScalar()!);
    }

    // Observation count per artwork for a whole category, used for the tier list card badges (avoids one query per artwork).
    public IReadOnlyDictionary<int, int> GetCountsByCategory(ArtworkCategory category)
    {
        using var connection = _connectionFactory.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT o.ArtworkId, COUNT(*)
            FROM Observation o
            JOIN Artwork a ON a.Id = o.ArtworkId
            WHERE a.Category = $category
            GROUP BY o.ArtworkId;
            """;
        command.Parameters.AddWithValue("$category", (int)category);

        var results = new Dictionary<int, int>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            results[reader.GetInt32(0)] = reader.GetInt32(1);
        }
        return results;
    }

    public IReadOnlyList<Observation> GetByArtwork(int artworkId)
    {
        using var connection = _connectionFactory.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Id, ArtworkId, Subject, Content, CreatedAt FROM Observation WHERE ArtworkId = $artworkId ORDER BY CreatedAt;";
        command.Parameters.AddWithValue("$artworkId", artworkId);

        var results = new List<Observation>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            results.Add(ReadObservation(reader));
        }
        return results;
    }

    public Observation? GetById(int id)
    {
        using var connection = _connectionFactory.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Id, ArtworkId, Subject, Content, CreatedAt FROM Observation WHERE Id = $id;";
        command.Parameters.AddWithValue("$id", id);

        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadObservation(reader) : null;
    }

    public void Delete(int id)
    {
        using var connection = _connectionFactory.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM Observation WHERE Id = $id;";
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
    }

    private static Observation ReadObservation(SqliteDataReader reader) => new()
    {
        Id = reader.GetInt32(0),
        ArtworkId = reader.GetInt32(1),
        Subject = reader.GetString(2),
        Content = reader.GetString(3),
        CreatedAt = DateTime.Parse(reader.GetString(4))
    };
}
