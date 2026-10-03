namespace AkashicRecords.Domain;

// A named budget plan — a scenario of envelopes ("Standard", "Économies projet", "Rentrée").
// Lines are per-category monthly amounts; the view compares planned vs actual.
public sealed class BudgetPlan
{
    public int Id { get; set; }
    public required string Name { get; set; }
    public string Notes { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
}

// One category's envelope inside a plan. Amount 0 means "listed but uncapped".
public sealed class BudgetPlanLine
{
    public int Id { get; set; }
    public int PlanId { get; set; }
    public int CategoryId { get; set; }
    public decimal MonthlyAmount { get; set; }
}
