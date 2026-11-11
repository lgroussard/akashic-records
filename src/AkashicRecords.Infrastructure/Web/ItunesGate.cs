namespace AkashicRecords.Infrastructure.Web;

// The iTunes Search API allows about 20 requests a minute per Internet connection (the PC and the phone share it at
// home). Every iTunes request of the app goes through here:
//   * a sliding one-minute window counts this app's requests. The background ones (SIMILAIRES, automatic chaining,
//     clip lookups) get at most BackgroundBudget of their own; the search may use up to TotalBudget, so playing
//     songs can never use up the minute and lock the search out (it keeps at least 13);
//   * when iTunes answers 403/429 anyway (e.g. the other device used the quota), nothing is sent for a minute —
//     insisting would only extend the block.
// Requests that are not let through simply return nothing; callers treat that like "no network". Port of the phone's
// ItunesGate.kt — same numbers.
public static class ItunesGate
{
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(1);
    private const int TotalBudget = 19;
    private const int BackgroundBudget = 6;

    private static readonly Queue<DateTime> Stamps = new();
    private static readonly Queue<DateTime> BackgroundStamps = new();
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
            var allowed = now >= _blockedUntil && Stamps.Count < TotalBudget && (search || BackgroundStamps.Count < BackgroundBudget);
            if (allowed) { Stamps.Enqueue(now); if (!search) BackgroundStamps.Enqueue(now); } else _refusals++;
            return allowed;
        }
    }

    // Whether a background request would be let through now (asks nothing, counts nothing): lets the suggestions skip
    // an optional lookup instead of being refused.
    public static bool BackgroundAvailable
    {
        get
        {
            lock (Sync)
            {
                var now = DateTime.UtcNow;
                Prune(now);
                return now >= _blockedUntil && Stamps.Count < TotalBudget && BackgroundStamps.Count < BackgroundBudget;
            }
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
    public static TimeSpan SearchWait => SearchWaitFor(1);

    // How long until `need` search requests in a row would be let through. A search step sends one or two requests:
    // waiting for a single free slot made it run, get held back on its next request, and show a new, random countdown
    // (1 s, then 12, then 1...) — so the step waits until all its requests fit.
    public static TimeSpan SearchWaitFor(int need)
    {
        lock (Sync)
        {
            var now = DateTime.UtcNow;
            Prune(now);
            var untilUnblocked = _blockedUntil > now ? _blockedUntil - now : TimeSpan.Zero;
            var over = Stamps.Count + need - TotalBudget; // requests that must leave the window first
            var untilBudget = over > 0
                ? Stamps.ElementAt(Math.Min(over, Stamps.Count) - 1) + Window - now
                : TimeSpan.Zero;
            var wait = untilUnblocked > untilBudget ? untilUnblocked : untilBudget;
            return wait > TimeSpan.Zero ? wait : TimeSpan.Zero;
        }
    }

    private static void Prune(DateTime now)
    {
        while (Stamps.Count > 0 && now - Stamps.Peek() >= Window) Stamps.Dequeue();
        while (BackgroundStamps.Count > 0 && now - BackgroundStamps.Peek() >= Window) BackgroundStamps.Dequeue();
    }
}

// Thrown by the search when the gate holds a request back; Wait = when it may try again.
public sealed class ItunesRateLimitedException : Exception
{
    public TimeSpan Wait { get; }
    public ItunesRateLimitedException(TimeSpan wait) : base("iTunes rate limit") => Wait = wait;
}
