namespace AkashicRecords.Domain;

// One bank-statement line, imported from the user's own exported file (CSV/OFX) or typed by hand.
// Amount is signed: negative = money leaving the account. ExternalId is a stable dedup hash
// (date+label+amount) so re-importing the same statement never doubles rows.
public sealed class BudgetTransaction
{
    public int Id { get; set; }
    public DateTime Date { get; set; }
    public required string Label { get; set; }
    public decimal Amount { get; set; }
    public int? CategoryId { get; set; }
    // Where the row came from: import file name, or "manuel".
    public string Source { get; set; } = "manuel";
    public string? ExternalId { get; set; }
    public DateTime ImportedAt { get; set; }
}
