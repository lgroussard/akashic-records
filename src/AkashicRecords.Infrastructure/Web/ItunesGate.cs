namespace AkashicRecords.Infrastructure.Web;

// The iTunes Search API allows about 20 requests a minute per Internet connection (the PC and the phone share it at
// home). Every iTunes request of the app goes through here:
//   * a sliding one-minute window counts this app's requests; the free search gets SearchBudget of them, so typing
//     can never starve SIMILAIRES and the automatic chaining, which get the rest up to TotalBudget;
//   * when iTunes answers 403/429 anyway (e.g. the other device used the quota), nothing is sent for a minute —
//     insisting would only extend the block.
// Requests that are not let through simply return nothing; callers treat that like "no network". Port of the phone's
// ItunesGate.kt — same numbers.
public static class ItunesGate
{
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(1);
    private const int TotalBudget = 19;
    private const int SearchBudget = 12;

    private static readonly Queue<DateTime> Stamps = new();
    private static readonly object Sync = new();
    private static DateTime _blockedUntil = DateTime.MinValue;
    private static long _refusals;

    public static bool IsItunes(string url) => url.StartsWith("https://itunes.apple.com/", StringComparison.Ordinal);

    public static bool TryAcquire(bool search)
    {
        lock (Sync)
        {
            var now = DateTime.UtcNow;
            Prune(now);
            var allowed = now >= _blockedUntil && Stamps.Count < (search ? SearchBudget : TotalBudget);
            if (allowed) Stamps.Enqueue(now); else _refusals++;
            return allowed;
        }
    }

    // iTunes said no: stop asking for a whole window.
    public static void Refused()
    {
        lock (Sync)
        {
            _blockedUntil = DateTime.UtcNow + Window;
            _refusals++;
        }
    }

    // Requests turned away so far (here or by iTunes): lets a caller tell an answer cut short by the limit.
    public static long RefusalCount { get { lock (Sync) return _refusals; } }

    // How long until a search request would be let through again (zero = now).
    public static TimeSpan SearchWait
    {
        get
        {
            lock (Sync)
            {
                var now = DateTime.UtcNow;
                Prune(now);
                var untilUnblocked = _blockedUntil > now ? _blockedUntil - now : TimeSpan.Zero;
                var untilBudget = Stamps.Count >= SearchBudget
                    ? Stamps.ElementAt(Stamps.Count - SearchBudget) + Window - now
                    : TimeSpan.Zero;
                var wait = untilUnblocked > untilBudget ? untilUnblocked : untilBudget;
                return wait > TimeSpan.Zero ? wait : TimeSpan.Zero;
            }
        }
    }

    private static void Prune(DateTime now)
    {
        while (Stamps.Count > 0 && now - Stamps.Peek() >= Window) Stamps.Dequeue();
    }
}

// Thrown by the search when the gate holds a request back; Wait = when it may try again.
public sealed class ItunesRateLimitedException : Exception
{
    public TimeSpan Wait { get; }
    public ItunesRateLimitedException(TimeSpan wait) : base("iTunes rate limit") => Wait = wait;
}
