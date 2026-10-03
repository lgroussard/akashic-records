using AkashicRecords.Domain;
using Microsoft.Data.Sqlite;

namespace AkashicRecords.Infrastructure.Persistence;

// Envelope categories: name + color + optional monthly cap (stored as text for decimal exactness).
public sealed class BudgetCategoryRepository
{
    private readonly SqliteConnectionFactory _connectionFactory;

    public BudgetCategoryRepository(SqliteConnectionFactory connectionFactory) => _connectionFactory = connectionFactory;

    public int Add(BudgetCategory category)
    {
        using var connection = _connectionFactory.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO BudgetCategory (Name, Color, MonthlyCap, SortOrder)
            VALUES ($name, $color, $cap, $sort);
            SELECT last_insert_rowid();
            """;
        command.Parameters.AddWithValue("$name", category.Name);
        command.Parameters.AddWithValue("$color", category.Color);
        command.Parameters.AddWithValue("$cap", (object?)Fmt(category.MonthlyCap) ?? DBNull.Value);
        command.Parameters.AddWithValue("$sort", category.SortOrder);
        return Convert.ToInt32((long)command.ExecuteScalar()!);
    }

    public IReadOnlyList<BudgetCategory> GetAll()
    {
        using var connection = _connectionFactory.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Id, Name, Color, MonthlyCap, SortOrder FROM BudgetCategory ORDER BY SortOrder, Name;";
        var results = new List<BudgetCategory>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            results.Add(new BudgetCategory
            {
                Id = reader.GetInt32(0),
                Name = reader.GetString(1),
                Color = reader.IsDBNull(2) ? "#5B8CFF" : reader.GetString(2),
                MonthlyCap = reader.IsDBNull(3) ? null : decimal.Parse(reader.GetString(3)),
                SortOrder = reader.GetInt32(4)
            });
        }
        return results;
    }

    public void Update(int id, string name, string color, decimal? monthlyCap)
    {
        using var connection = _connectionFactory.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE BudgetCategory SET Name = $name, Color = $color, MonthlyCap = $cap WHERE Id = $id;";
        command.Parameters.AddWithValue("$name", name);
        command.Parameters.AddWithValue("$color", color);
        command.Parameters.AddWithValue("$cap", (object?)Fmt(monthlyCap) ?? DBNull.Value);
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
    }

    // Deleting a category un-tags its transactions (they stay imported, just uncategorized).
    public void Delete(int id)
    {
        using var connection = _connectionFactory.CreateOpenConnection();
        using (var untag = connection.CreateCommand())
        {
            untag.CommandText = "UPDATE BudgetTransaction SET CategoryId = NULL WHERE CategoryId = $id;";
            untag.Parameters.AddWithValue("$id", id);
            untag.ExecuteNonQuery();
        }
        using (var dropRules = connection.CreateCommand())
        {
            dropRules.CommandText = "DELETE FROM BudgetRule WHERE CategoryId = $id;";
            dropRules.Parameters.AddWithValue("$id", id);
            dropRules.ExecuteNonQuery();
        }
        using (var dropLines = connection.CreateCommand())
        {
            dropLines.CommandText = "DELETE FROM BudgetPlanLine WHERE CategoryId = $id;";
            dropLines.Parameters.AddWithValue("$id", id);
            dropLines.ExecuteNonQuery();
        }
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM BudgetCategory WHERE Id = $id;";
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
    }

    // Primitive Parse/ToString are culture-free (invariant) under .NET Core, so decimals round-trip
    // as plain "1234.56" text regardless of the machine locale.
    private static string? Fmt(decimal? value) => value?.ToString();
}
