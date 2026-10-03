namespace AkashicRecords.Domain;

// A person whose birthday is worth remembering. The year is optional — many people want the
// reminder without recording an exact age; when set, the widget can show the turning age.
// Month/Day drive the recurring annual reminder (a Date would break on Feb-29 years, and a
// leap-day birthday is nudged to Feb-28 or Mar-01 by the aggregator, never dropped).
public sealed class Birthday
{
    public int Id { get; set; }
    public required string Name { get; set; }
    public int Month { get; set; }
    public int Day { get; set; }
    // Null = age not tracked (only "anniversaire de X"); set = shown as "(N ans)".
    public int? BirthYear { get; set; }
    public string Notes { get; set; } = string.Empty;
}
