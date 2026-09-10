using AkashicRecords.Domain;
using Microsoft.Data.Sqlite;

namespace AkashicRecords.Infrastructure.Persistence;

public sealed class CanvasElementRepository
{
    private readonly SqliteConnectionFactory _connectionFactory;

    public CanvasElementRepository(SqliteConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    public int Add(CanvasElement element)
    {
        using var connection = _connectionFactory.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO CanvasElement (ProjectId, Type, X, Y, TextContent, ImagePath, ObservationId, Width, Height)
            VALUES ($projectId, $type, $x, $y, $textContent, $imagePath, $observationId, $width, $height);
            SELECT last_insert_rowid();
            """;
        AddParameters(command, element);
        return Convert.ToInt32((long)command.ExecuteScalar()!);
    }

    public IReadOnlyList<CanvasElement> GetByProject(int projectId)
    {
        using var connection = _connectionFactory.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT Id, ProjectId, Type, X, Y, TextContent, ImagePath, ObservationId, Width, Height FROM CanvasElement WHERE ProjectId = $projectId;";
        command.Parameters.AddWithValue("$projectId", projectId);

        var results = new List<CanvasElement>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            results.Add(ReadElement(reader));
        }
        return results;
    }

    public CanvasElement? GetById(int id)
    {
        using var connection = _connectionFactory.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Id, ProjectId, Type, X, Y, TextContent, ImagePath, ObservationId, Width, Height FROM CanvasElement WHERE Id = $id;";
        command.Parameters.AddWithValue("$id", id);

        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadElement(reader) : null;
    }

    public void UpdatePosition(int id, double x, double y)
    {
        using var connection = _connectionFactory.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE CanvasElement SET X = $x, Y = $y WHERE Id = $id;";
        command.Parameters.AddWithValue("$x", x);
        command.Parameters.AddWithValue("$y", y);
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
    }

    public void UpdateSize(int id, double width, double height)
    {
        using var connection = _connectionFactory.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE CanvasElement SET Width = $width, Height = $height WHERE Id = $id;";
        command.Parameters.AddWithValue("$width", width);
        command.Parameters.AddWithValue("$height", height);
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
    }

    public void UpdateText(int id, string text)
    {
        using var connection = _connectionFactory.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE CanvasElement SET TextContent = $text WHERE Id = $id;";
        command.Parameters.AddWithValue("$text", text);
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
    }

    public void Delete(int id)
    {
        using var connection = _connectionFactory.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM CanvasElement WHERE Id = $id;";
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
    }

    // Re-inserts a previously deleted element with the same Id, used by undo.
    public void Restore(CanvasElement element)
    {
        using var connection = _connectionFactory.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO CanvasElement (Id, ProjectId, Type, X, Y, TextContent, ImagePath, ObservationId, Width, Height)
            VALUES ($id, $projectId, $type, $x, $y, $textContent, $imagePath, $observationId, $width, $height);
            """;
        command.Parameters.AddWithValue("$id", element.Id);
        AddParameters(command, element);
        command.ExecuteNonQuery();
    }

    private static void AddParameters(SqliteCommand command, CanvasElement element)
    {
        command.Parameters.AddWithValue("$projectId", element.ProjectId);
        command.Parameters.AddWithValue("$type", (int)element.Type);
        command.Parameters.AddWithValue("$x", element.X);
        command.Parameters.AddWithValue("$y", element.Y);
        command.Parameters.AddWithValue("$textContent", (object?)element.TextContent ?? DBNull.Value);
        command.Parameters.AddWithValue("$imagePath", (object?)element.ImagePath ?? DBNull.Value);
        command.Parameters.AddWithValue("$observationId", (object?)element.ObservationId ?? DBNull.Value);
        command.Parameters.AddWithValue("$width", (object?)element.Width ?? DBNull.Value);
        command.Parameters.AddWithValue("$height", (object?)element.Height ?? DBNull.Value);
    }

    private static CanvasElement ReadElement(SqliteDataReader reader) => new()
    {
        Id = reader.GetInt32(0),
        ProjectId = reader.GetInt32(1),
        Type = (CanvasElementType)reader.GetInt32(2),
        X = reader.GetDouble(3),
        Y = reader.GetDouble(4),
        TextContent = reader.IsDBNull(5) ? null : reader.GetString(5),
        ImagePath = reader.IsDBNull(6) ? null : reader.GetString(6),
        ObservationId = reader.IsDBNull(7) ? null : reader.GetInt32(7),
        Width = reader.IsDBNull(8) ? null : reader.GetDouble(8),
        Height = reader.IsDBNull(9) ? null : reader.GetDouble(9)
    };
}
