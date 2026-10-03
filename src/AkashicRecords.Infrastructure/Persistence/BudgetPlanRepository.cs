using AkashicRecords.Domain;
using Microsoft.Data.Sqlite;

namespace AkashicRecords.Infrastructure.Persistence;

// Budget plans = saved scenarios of monthly envelopes, one plan holding many category lines.
public sealed class BudgetPlanRepository
{
    private readonly SqliteConnectionFactory _connectionFactory;

    public BudgetPlanRepository(SqliteConnectionFactory connectionFactory) => _connectionFactory = connectionFactory;

    public int Add(BudgetPlan plan)
    {
        using var connection = _connectionFactory.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO BudgetPlan (Name, Notes, CreatedAt)
            VALUES ($name, $notes, $created);
            SELECT last_insert_rowid();
            """;
        command.Parameters.AddWithValue("$name", plan.Name);
        command.Parameters.AddWithValue("$notes", plan.Notes);
        command.Parameters.AddWithValue("$created", plan.CreatedAt.ToString("O"));
        return Convert.ToInt32((long)command.ExecuteScalar()!);
    }

    public IReadOnlyList<BudgetPlan> GetAll()
    {
        using var connection = _connectionFactory.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Id, Name, Notes, CreatedAt FROM BudgetPlan ORDER BY CreatedAt;";
        var results = new List<BudgetPlan>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            results.Add(new BudgetPlan
            {
                Id = reader.GetInt32(0),
                Name = reader.GetString(1),
                Notes = reader.GetString(2),
                CreatedAt = DateTime.Parse(reader.GetString(3))
            });
        }
        return results;
    }

    public IReadOnlyList<BudgetPlanLine> GetLines(int planId)
    {
        using var connection = _connectionFactory.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Id, PlanId, CategoryId, MonthlyAmount FROM BudgetPlanLine WHERE PlanId = $plan;";
        command.Parameters.AddWithValue("$plan", planId);
        var results = new List<BudgetPlanLine>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            results.Add(new BudgetPlanLine
            {
                Id = reader.GetInt32(0),
                PlanId = reader.GetInt32(1),
                CategoryId = reader.GetInt32(2),
                MonthlyAmount = decimal.Parse(reader.GetString(3))
            });
        }
        return results;
    }

    // Upsert of one envelope inside a plan: same category updates in place, else a line is added.
    public void SetLine(int planId, int categoryId, decimal monthlyAmount)
    {
        using var connection = _connectionFactory.CreateOpenConnection();
        using (var find = connection.CreateCommand())
        {
            find.CommandText = "SELECT Id FROM BudgetPlanLine WHERE PlanId = $plan AND CategoryId = $cat LIMIT 1;";
            find.Parameters.AddWithValue("$plan", planId);
            find.Parameters.AddWithValue("$cat", categoryId);
            var existing = find.ExecuteScalar() as long?;
            if (existing is not null)
            {
                using var upd = connection.CreateCommand();
                upd.CommandText = "UPDATE BudgetPlanLine SET MonthlyAmount = $amount WHERE Id = $id;";
                upd.Parameters.AddWithValue("$amount", monthlyAmount.ToString());
                upd.Parameters.AddWithValue("$id", existing.Value);
                upd.ExecuteNonQuery();
                return;
            }
        }
        using var insert = connection.CreateCommand();
        insert.CommandText = "INSERT INTO BudgetPlanLine (PlanId, CategoryId, MonthlyAmount) VALUES ($plan, $cat, $amount);";
        insert.Parameters.AddWithValue("$plan", planId);
        insert.Parameters.AddWithValue("$cat", categoryId);
        insert.Parameters.AddWithValue("$amount", monthlyAmount.ToString());
        insert.ExecuteNonQuery();
    }

    public void Update(int id, string name, string notes)
    {
        using var connection = _connectionFactory.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE BudgetPlan SET Name = $name, Notes = $notes WHERE Id = $id;";
        command.Parameters.AddWithValue("$name", name);
        command.Parameters.AddWithValue("$notes", notes);
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
    }

    public void Delete(int id)
    {
        using var connection = _connectionFactory.CreateOpenConnection();
        using (var drop = connection.CreateCommand())
        {
            drop.CommandText = "DELETE FROM BudgetPlanLine WHERE PlanId = $id;";
            drop.Parameters.AddWithValue("$id", id);
            drop.ExecuteNonQuery();
        }
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM BudgetPlan WHERE Id = $id;";
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
    }
}
