using AkashicRecords.Domain;
using Microsoft.Data.Sqlite;

namespace AkashicRecords.Infrastructure.Persistence;

// Keyword -> category auto-tagging rules, applied by the importer at paste time and by
// CategorizeMatching against the already-imported catalog.
public sealed class BudgetRuleRepository
{
    private readonly SqliteConnectionFactory _connectionFactory;

    public BudgetRuleRepository(SqliteConnectionFactory connectionFactory) => _connectionFactory = connectionFactory;

    public int Add(BudgetRule rule)
    {
        using var connection = _connectionFactory.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO BudgetRule (Keyword, CategoryId)
            VALUES ($keyword, $category);
            SELECT last_insert_rowid();
            """;
        command.Parameters.AddWithValue("$keyword", rule.Keyword);
        command.Parameters.AddWithValue("$category", rule.CategoryId);
        return Convert.ToInt32((long)command.ExecuteScalar()!);
    }

    public IReadOnlyList<BudgetRule> GetAll()
    {
        using var connection = _connectionFactory.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Id, Keyword, CategoryId FROM BudgetRule ORDER BY Keyword;";
        var results = new List<BudgetRule>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            results.Add(new BudgetRule
            {
                Id = reader.GetInt32(0),
                Keyword = reader.GetString(1),
                CategoryId = reader.GetInt32(2)
            });
        }
        return results;
    }

    public void Delete(int id)
    {
        using var connection = _connectionFactory.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM BudgetRule WHERE Id = $id;";
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
    }
}
