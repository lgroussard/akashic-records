using AkashicRecords.Domain;
using AkashicRecords.Infrastructure.Budgeting;
using Microsoft.Data.Sqlite;

namespace AkashicRecords.Infrastructure.Persistence;

// Imported statement lines. Amounts are stored as text (exact decimals); ExternalId gives the
// idempotent re-import (INSERT OR IGNORE style via a pre-check, statements are small).
public sealed class BudgetTransactionRepository
{
    private readonly SqliteConnectionFactory _connectionFactory;

    public BudgetTransactionRepository(SqliteConnectionFactory connectionFactory) => _connectionFactory = connectionFactory;

    public int Add(BudgetTransaction tx)
    {
        using var connection = _connectionFactory.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO BudgetTransaction (Date, Label, Amount, CategoryId, Source, ExternalId, ImportedAt)
            VALUES ($date, $label, $amount, $category, $source, $external, $imported);
            SELECT last_insert_rowid();
            """;
        command.Parameters.AddWithValue("$date", tx.Date.ToString("yyyy-MM-dd"));
        command.Parameters.AddWithValue("$label", tx.Label);
        command.Parameters.AddWithValue("$amount", tx.Amount.ToString());
        command.Parameters.AddWithValue("$category", (object?)tx.CategoryId ?? DBNull.Value);
        command.Parameters.AddWithValue("$source", tx.Source);
        command.Parameters.AddWithValue("$external", tx.ExternalId);
        command.Parameters.AddWithValue("$imported", tx.ImportedAt.ToString("O"));
        return Convert.ToInt32((long)command.ExecuteScalar()!);
    }

    // True when a row with that dedup id already exists — the importer skips these, so importing
    // the same export twice adds nothing.
    public bool ExistsByExternalId(string externalId)
    {
        using var connection = _connectionFactory.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM BudgetTransaction WHERE ExternalId = $e LIMIT 1;";
        command.Parameters.AddWithValue("$e", externalId);
        return command.ExecuteScalar() is not null;
    }

    public IReadOnlyList<BudgetTransaction> GetAll()
    {
        using var connection = _connectionFactory.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Id, Date, Label, Amount, CategoryId, Source, ExternalId, ImportedAt FROM BudgetTransaction ORDER BY Date DESC, Id DESC;";
        var results = new List<BudgetTransaction>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            results.Add(new BudgetTransaction
            {
                Id = reader.GetInt32(0),
                Date = DateTime.Parse(reader.GetString(1)),
                Label = reader.GetString(2),
                Amount = decimal.Parse(reader.GetString(3)),
                CategoryId = reader.IsDBNull(4) ? null : reader.GetInt32(4),
                Source = reader.GetString(5),
                ExternalId = reader.IsDBNull(6) ? null : reader.GetString(6),
                ImportedAt = DateTime.Parse(reader.GetString(7))
            });
        }
        return results;
    }

    // Months present in the data, newest first — the period selector of the overview tab.
    public IReadOnlyList<string> GetDistinctMonths()
    {
        using var connection = _connectionFactory.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT DISTINCT substr(Date, 1, 7) FROM BudgetTransaction ORDER BY 1 DESC;";
        var results = new List<string>();
        using var reader = command.ExecuteReader();
        while (reader.Read()) results.Add(reader.GetString(0));
        return results;
    }

    public void SetCategory(int id, int? categoryId)
    {
        using var connection = _connectionFactory.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE BudgetTransaction SET CategoryId = $category WHERE Id = $id;";
        command.Parameters.AddWithValue("$category", (object?)categoryId ?? DBNull.Value);
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
    }

    // The whole classifier, run in managed code. SQLite can neither fold accents (its lower() is
    // ASCII-only, so a rule "noël" never matched a label "NOEL") nor test a word boundary (LIKE %free%
    // wrongly matched "FREIN"), so both jobs are done here via LabelMatcher. Rules never overwrite a
    // manual choice — only CategoryId IS NULL rows are considered.
    //
    // A rule carries an optional sign gate: null matches either sign (a user rule — the user picked the
    // sign-appropriate category themselves), true requires money-in, false requires money-out (the
    // curated built-in seeds, so an expense merchant can never tag a salary line and vice versa).
    //
    // Single-rule convenience used right after a user adds one rule, so the back-catalog fills too.
    public int CategorizeMatching(string keyword, int categoryId, bool? gate = null)
    {
        var fold = LabelMatcher.Fold(keyword);
        if (fold.Length == 0) return 0;
        var ids = new List<int>();
        foreach (var (id, label, amount) in GetUncategorizedForClassification())
            if (GatePasses(amount, gate) && LabelMatcher.Matches(LabelMatcher.Fold(label), fold))
                ids.Add(id);
        ApplyCategoryToIds(categoryId, ids);
        return ids.Count;
    }

    // Full pass over every rule at once so "most specific keyword wins" is decidable across rules (a
    // longer, more precise keyword beats a shorter one), not by call order. User rules form a higher
    // tier: any user match is taken before any system seed is even considered, so a curated map can
    // never silently override an explicit rule. Returns how many rows got a category.
    public int CategorizeAll(
        IReadOnlyList<(string Keyword, int CategoryId, bool? Gate)> userRules,
        IReadOnlyList<(string Keyword, int CategoryId, bool? Gate)> systemRules)
    {
        var user = Prepare(userRules);
        var system = Prepare(systemRules);
        if (user.Count == 0 && system.Count == 0) return 0;
        var assignments = new Dictionary<int, int>();
        foreach (var (id, label, amount) in GetUncategorizedForClassification())
        {
            var fold = LabelMatcher.Fold(label);
            var pick = BestMatch(fold, amount, user) ?? BestMatch(fold, amount, system);
            if (pick is { } cat) assignments[id] = cat;
        }
        ApplyAssignments(assignments);
        return assignments.Count;
    }

    // Drops empty-folded keywords and pre-folds the survivors once (matching runs per row).
    private static List<(string Fold, int CategoryId, bool? Gate)> Prepare(
        IReadOnlyList<(string Keyword, int CategoryId, bool? Gate)> rules)
    {
        var list = new List<(string, int, bool?)>();
        foreach (var r in rules)
        {
            var fold = LabelMatcher.Fold(r.Keyword);
            if (fold.Length > 0) list.Add((fold, r.CategoryId, r.Gate));
        }
        return list;
    }

    // Among the rules that pass the sign gate and match the folded label, the most specific wins = the
    // longest keyword ("montparnasse" beats "mont"); ties keep the first. null when nothing matched.
    private static int? BestMatch(string foldedLabel, decimal amount,
        IReadOnlyList<(string Fold, int CategoryId, bool? Gate)> rules)
    {
        int? best = null; var bestLen = -1;
        foreach (var r in rules)
        {
            if (!GatePasses(amount, r.Gate)) continue;
            if (!LabelMatcher.Matches(foldedLabel, r.Fold)) continue;
            if (r.Fold.Length > bestLen) { best = r.CategoryId; bestLen = r.Fold.Length; }
        }
        return best;
    }

    private static bool GatePasses(decimal amount, bool? gate) => gate switch
    {
        null => true,
        true => amount > 0,
        false => amount < 0,
    };

    // The classifier's working set: only rows still missing a category. Amount is parsed so the sign
    // gate can run in managed code.
    private IReadOnlyList<(int Id, string Label, decimal Amount)> GetUncategorizedForClassification()
    {
        using var connection = _connectionFactory.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Id, Label, Amount FROM BudgetTransaction WHERE CategoryId IS NULL;";
        var results = new List<(int, string, decimal)>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
            results.Add((reader.GetInt32(0), reader.GetString(1), decimal.Parse(reader.GetString(2))));
        return results;
    }

    private void ApplyCategoryToIds(int categoryId, IReadOnlyList<int> ids)
    {
        if (ids.Count == 0) return;
        using var connection = _connectionFactory.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE BudgetTransaction SET CategoryId = $cat WHERE Id = $id;";
        command.Parameters.AddWithValue("$cat", categoryId);
        var idParam = command.Parameters.AddWithValue("$id", 0);
        foreach (var id in ids) { idParam.Value = id; command.ExecuteNonQuery(); }
    }

    private void ApplyAssignments(IReadOnlyDictionary<int, int> assignments)
    {
        if (assignments.Count == 0) return;
        using var connection = _connectionFactory.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE BudgetTransaction SET CategoryId = $cat WHERE Id = $id;";
        var catParam = command.Parameters.AddWithValue("$cat", 0);
        var idParam = command.Parameters.AddWithValue("$id", 0);
        foreach (var entry in assignments) { catParam.Value = entry.Value; idParam.Value = entry.Key; command.ExecuteNonQuery(); }
    }

    public void Delete(int id)
    {
        using var connection = _connectionFactory.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM BudgetTransaction WHERE Id = $id;";
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
    }

    public void DeleteBySource(string source)
    {
        using var connection = _connectionFactory.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM BudgetTransaction WHERE Source = $source;";
        command.Parameters.AddWithValue("$source", source);
        command.ExecuteNonQuery();
    }
}
