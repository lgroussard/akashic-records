using AkashicRecords.Domain;
using Microsoft.Data.Sqlite;

namespace AkashicRecords.Infrastructure.Persistence;

public sealed class RecipeRepository
{
    private readonly SqliteConnectionFactory _connectionFactory;

    public RecipeRepository(SqliteConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    public int Add(Recipe recipe)
    {
        using var connection = _connectionFactory.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO Recipe (Title, Category, Ingredients, Instructions, Notes, Tags, CoverImagePath, CreatedAt)
            VALUES ($title, $category, $ingredients, $instructions, $notes, $tags, $coverImagePath, $createdAt);
            SELECT last_insert_rowid();
            """;
        AddParameters(command, recipe);
        return Convert.ToInt32((long)command.ExecuteScalar()!);
    }

    public IReadOnlyList<Recipe> GetAll()
    {
        using var connection = _connectionFactory.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Id, Title, Category, Ingredients, Instructions, Notes, Tags, CoverImagePath, CreatedAt FROM Recipe ORDER BY Title;";

        var results = new List<Recipe>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            results.Add(ReadRecipe(reader));
        }
        return results;
    }

    public Recipe? GetById(int id) => GetAll().FirstOrDefault(r => r.Id == id);

    public void Update(int id, string title, string category, string ingredients, string instructions, string notes, string tags)
    {
        using var connection = _connectionFactory.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE Recipe SET Title = $title, Category = $category, Ingredients = $ingredients, Instructions = $instructions,
                               Notes = $notes, Tags = $tags
            WHERE Id = $id;
            """;
        command.Parameters.AddWithValue("$title", title);
        command.Parameters.AddWithValue("$category", category);
        command.Parameters.AddWithValue("$ingredients", ingredients);
        command.Parameters.AddWithValue("$instructions", instructions);
        command.Parameters.AddWithValue("$notes", notes);
        command.Parameters.AddWithValue("$tags", tags);
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
    }

    public void UpdateCoverImage(int id, string? coverImagePath)
    {
        using var connection = _connectionFactory.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE Recipe SET CoverImagePath = $coverImagePath WHERE Id = $id;";
        command.Parameters.AddWithValue("$coverImagePath", (object?)coverImagePath ?? DBNull.Value);
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
    }

    public void Delete(int id)
    {
        using var connection = _connectionFactory.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM Recipe WHERE Id = $id;";
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
    }

    private static void AddParameters(SqliteCommand command, Recipe recipe)
    {
        command.Parameters.AddWithValue("$title", recipe.Title);
        command.Parameters.AddWithValue("$category", recipe.Category);
        command.Parameters.AddWithValue("$ingredients", recipe.Ingredients);
        command.Parameters.AddWithValue("$instructions", recipe.Instructions);
        command.Parameters.AddWithValue("$notes", recipe.Notes);
        command.Parameters.AddWithValue("$tags", recipe.Tags);
        command.Parameters.AddWithValue("$coverImagePath", (object?)recipe.CoverImagePath ?? DBNull.Value);
        command.Parameters.AddWithValue("$createdAt", recipe.CreatedAt.ToString("O"));
    }

    private static Recipe ReadRecipe(SqliteDataReader reader) => new()
    {
        Id = reader.GetInt32(0),
        Title = reader.GetString(1),
        Category = reader.GetString(2),
        Ingredients = reader.GetString(3),
        Instructions = reader.GetString(4),
        Notes = reader.GetString(5),
        Tags = reader.GetString(6),
        CoverImagePath = reader.IsDBNull(7) ? null : reader.GetString(7),
        CreatedAt = DateTime.Parse(reader.GetString(8))
    };
}
