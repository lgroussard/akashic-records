using AkashicRecords.Domain;
using Microsoft.Data.Sqlite;

namespace AkashicRecords.Infrastructure.Persistence;

public sealed class PoemRepository
{
    private readonly SqliteConnectionFactory _connectionFactory;

    public PoemRepository(SqliteConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    public int Add(Poem poem)
    {
        using var connection = _connectionFactory.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO Poem (RecueilId, Title, Text, Tags, ImagePath, CreatedAt, TextAlignment, Margin, FontSize, FontFamily, Bold, Italic, RichContent)
            VALUES ($recueilId, $title, $text, $tags, $imagePath, $createdAt, $textAlignment, $margin, $fontSize, $fontFamily, $bold, $italic, $richContent);
            SELECT last_insert_rowid();
            """;
        AddParameters(command, poem);
        return Convert.ToInt32((long)command.ExecuteScalar()!);
    }

    public IReadOnlyList<Poem> GetAll()
    {
        using var connection = _connectionFactory.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Id, RecueilId, Title, Text, Tags, ImagePath, CreatedAt, TextAlignment, Margin, FontSize, FontFamily, Bold, Italic, RichContent FROM Poem ORDER BY Title;";

        var results = new List<Poem>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            results.Add(ReadPoem(reader));
        }
        return results;
    }

    public Poem? GetById(int id) => GetAll().FirstOrDefault(p => p.Id == id);

    public void Update(int id, int? recueilId, string title, string text, string tags)
    {
        using var connection = _connectionFactory.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            "UPDATE Poem SET RecueilId = $recueilId, Title = $title, Text = $text, Tags = $tags WHERE Id = $id;";
        command.Parameters.AddWithValue("$recueilId", (object?)recueilId ?? DBNull.Value);
        command.Parameters.AddWithValue("$title", title);
        command.Parameters.AddWithValue("$text", text);
        command.Parameters.AddWithValue("$tags", tags);
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
    }

    public void UpdateImage(int id, string? imagePath)
    {
        using var connection = _connectionFactory.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE Poem SET ImagePath = $imagePath WHERE Id = $id;";
        command.Parameters.AddWithValue("$imagePath", (object?)imagePath ?? DBNull.Value);
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
    }

    // Convenience overload used by the poem editor, which only changes alignment + margin.
    // Font size, family, bold, italic and line spacing are left untouched.
    public void UpdateFormatting(int id, string textAlignment, double margin)
    {
        using var connection = _connectionFactory.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE Poem
            SET TextAlignment = $textAlignment, Margin = $margin
            WHERE Id = $id;
            """;
        command.Parameters.AddWithValue("$textAlignment", textAlignment);
        command.Parameters.AddWithValue("$margin", margin);
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
    }

    public void UpdateFormatting(int id, string textAlignment, double margin, double fontSize, string fontFamily, bool bold, bool italic)
    {
        using var connection = _connectionFactory.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE Poem SET TextAlignment = $textAlignment, Margin = $margin, FontSize = $fontSize, FontFamily = $fontFamily, Bold = $bold, Italic = $italic WHERE Id = $id;";
        command.Parameters.AddWithValue("$textAlignment", textAlignment);
        command.Parameters.AddWithValue("$margin", margin);
        command.Parameters.AddWithValue("$fontSize", fontSize);
        command.Parameters.AddWithValue("$fontFamily", fontFamily);
        command.Parameters.AddWithValue("$bold", bold ? 1 : 0);
        command.Parameters.AddWithValue("$italic", italic ? 1 : 0);
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
    }

    // Persists the poem body: plain Text (for search) + the XAML-serialized rich content (per-letter styling).
    public void UpdateRichContent(int id, string text, string richContent)
    {
        using var connection = _connectionFactory.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE Poem SET Text = $text, RichContent = $richContent WHERE Id = $id;";
        command.Parameters.AddWithValue("$text", text);
        command.Parameters.AddWithValue("$richContent", richContent);
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
    }

    public void Delete(int id)
    {
        using var connection = _connectionFactory.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM Poem WHERE Id = $id;";
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
    }

    // Unassigns every poem that belongs to a deleted recueil, so no poem is orphaned.
    public void ClearRecueilForRecueilId(int recueilId)
    {
        using var connection = _connectionFactory.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE Poem SET RecueilId = NULL WHERE RecueilId = $recueilId;";
        command.Parameters.AddWithValue("$recueilId", recueilId);
        command.ExecuteNonQuery();
    }

    private static void AddParameters(SqliteCommand command, Poem poem)
    {
        command.Parameters.AddWithValue("$recueilId", (object?)poem.RecueilId ?? DBNull.Value);
        command.Parameters.AddWithValue("$title", poem.Title);
        command.Parameters.AddWithValue("$text", poem.Text);
        command.Parameters.AddWithValue("$tags", poem.Tags);
        command.Parameters.AddWithValue("$imagePath", (object?)poem.ImagePath ?? DBNull.Value);
        command.Parameters.AddWithValue("$createdAt", poem.CreatedAt.ToString("O"));
        command.Parameters.AddWithValue("$textAlignment", poem.TextAlignment);
        command.Parameters.AddWithValue("$margin", poem.Margin);
        command.Parameters.AddWithValue("$fontSize", poem.FontSize);
        command.Parameters.AddWithValue("$fontFamily", poem.FontFamily);
        command.Parameters.AddWithValue("$bold", poem.Bold ? 1 : 0);
        command.Parameters.AddWithValue("$italic", poem.Italic ? 1 : 0);
        command.Parameters.AddWithValue("$richContent", poem.RichContent);
    }

    private static Poem ReadPoem(SqliteDataReader reader) => new()
    {
        Id = reader.GetInt32(0),
        RecueilId = reader.IsDBNull(1) ? null : reader.GetInt32(1),
        Title = reader.GetString(2),
        Text = reader.GetString(3),
        Tags = reader.GetString(4),
        ImagePath = reader.IsDBNull(5) ? null : reader.GetString(5),
        CreatedAt = DateTime.Parse(reader.GetString(6)),
        TextAlignment = reader.GetString(7),
        Margin = reader.GetDouble(8),
        FontSize = reader.GetDouble(9),
        FontFamily = reader.GetString(10),
        Bold = reader.GetInt32(11) != 0,
        Italic = reader.GetInt32(12) != 0,
        RichContent = reader.GetString(13)
    };
}
