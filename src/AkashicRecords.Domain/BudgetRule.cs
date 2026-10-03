namespace AkashicRecords.Domain;

// Auto-categorization rule: a label keyword (case/accent-insensitive substring) that assigns
// transactions to a category at import time. This is what gives the numbers their "meaning".
public sealed class BudgetRule
{
    public int Id { get; set; }
    public required string Keyword { get; set; }
    public int CategoryId { get; set; }
}
