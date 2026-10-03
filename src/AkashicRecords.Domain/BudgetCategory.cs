namespace AkashicRecords.Domain;

// A budget category ("Alimentation", "Transport"…). MonthlyCap is the envelope for the default
// view; a color paints chips and bars. Null cap = tracked but not capped.
public sealed class BudgetCategory
{
    public int Id { get; set; }
    public required string Name { get; set; }
    public string Color { get; set; } = "#5B8CFF";
    public decimal? MonthlyCap { get; set; }
    public int SortOrder { get; set; }
}
