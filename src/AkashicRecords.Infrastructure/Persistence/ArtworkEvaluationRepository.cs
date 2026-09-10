using AkashicRecords.Domain;
using Microsoft.Data.Sqlite;

namespace AkashicRecords.Infrastructure.Persistence;

public sealed class ArtworkEvaluationRepository
{
    private readonly SqliteConnectionFactory _connectionFactory;

    public ArtworkEvaluationRepository(SqliteConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    // Overwrites any existing score for this artwork/axis pair rather than accumulating duplicates.
    public void Set(int artworkId, int axisId, int score)
    {
        using var connection = _connectionFactory.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            DELETE FROM ArtworkEvaluation WHERE ArtworkId = $artworkId AND AxisId = $axisId;
            INSERT INTO ArtworkEvaluation (ArtworkId, AxisId, Score) VALUES ($artworkId, $axisId, $score);
            """;
        command.Parameters.AddWithValue("$artworkId", artworkId);
        command.Parameters.AddWithValue("$axisId", axisId);
        command.Parameters.AddWithValue("$score", score);
        command.ExecuteNonQuery();
    }

    // Average score per artwork for a whole category, used for the tier list card badges (avoids one query per artwork).
    public IReadOnlyDictionary<int, double> GetAverageScoresByCategory(ArtworkCategory category)
    {
        using var connection = _connectionFactory.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT e.ArtworkId, AVG(e.Score)
            FROM ArtworkEvaluation e
            JOIN Artwork a ON a.Id = e.ArtworkId
            WHERE a.Category = $category
            GROUP BY e.ArtworkId;
            """;
        command.Parameters.AddWithValue("$category", (int)category);

        var results = new Dictionary<int, double>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            results[reader.GetInt32(0)] = reader.GetDouble(1);
        }
        return results;
    }

    public IReadOnlyList<ArtworkEvaluation> GetByArtwork(int artworkId)
    {
        using var connection = _connectionFactory.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Id, ArtworkId, AxisId, Score FROM ArtworkEvaluation WHERE ArtworkId = $artworkId;";
        command.Parameters.AddWithValue("$artworkId", artworkId);

        var results = new List<ArtworkEvaluation>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            results.Add(new ArtworkEvaluation
            {
                Id = reader.GetInt32(0),
                ArtworkId = reader.GetInt32(1),
                AxisId = reader.GetInt32(2),
                Score = reader.GetInt32(3)
            });
        }
        return results;
    }

    public void Delete(int id)
    {
        using var connection = _connectionFactory.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM ArtworkEvaluation WHERE Id = $id;";
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
    }
}
