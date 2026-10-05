namespace AkashicRecords.Domain;

// One numbered drawing drill of the Atelier bank: a theme, a duration, the 2-3 things to
// produce, and the references that illustrate them. Pure data — the bank itself lives in
// Infrastructure (Atelier/ExerciseBank) so the Domain layer stays dependency-free.
public sealed class DrawingExercise
{
    public int N { get; set; }
    public required string Theme { get; set; }
    public int Seconds { get; set; }
    public required string Title { get; set; }
    public List<string> Steps { get; set; } = new();
    public List<DrawingReference> Refs { get; set; } = new();
}

// A study reference for an exercise: the work, what it teaches, and the term to look it up
// with on Wikimedia Commons (null = text-only reference, no thumbnail requested).
public sealed class DrawingReference
{
    public required string Artist { get; set; }
    public required string Work { get; set; }
    public required string Takeaway { get; set; }
    public string? Query { get; set; }
}

// One 30-second gesture subject inside a chrono series.
public sealed class ChronoSubject
{
    public int N { get; set; }
    public int Seconds { get; set; } = 30;
    public required string Subject { get; set; }
}
