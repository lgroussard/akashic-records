package com.akashicrecords.music

// The iTunes Search API allows about 20 requests a minute per Internet connection (the PC and the phone share it at
// home). Every iTunes request of the app goes through here:
//   * a sliding one-minute window counts this app's requests. The background ones (Similaires list, automatic
//     chaining, clip lookups) get at most BACKGROUND_BUDGET of their own; the search may use up to TOTAL_BUDGET, so
//     playing songs can never use up the minute and lock the search out (it keeps at least 13);
//   * when iTunes answers 403/429 anyway (e.g. the other device used the quota), nothing is sent for a minute —
//     insisting would only extend the block.
// Requests that are not let through simply return nothing; callers treat that like "no network". Same rules on the
// PC (ItunesGate in MusicRecommendationService.cs).
object ItunesGate {
    private const val WINDOW_MS = 60_000L
    private const val TOTAL_BUDGET = 19
    private const val BACKGROUND_BUDGET = 6

    private val stamps = ArrayDeque<Long>()
    private val backgroundStamps = ArrayDeque<Long>()
    private var blockedUntil = 0L
    private var refusals = 0L

    fun isItunes(url: String) = url.startsWith("https://itunes.apple.com/")

    @Synchronized
    fun tryAcquire(search: Boolean): Boolean {
        val now = System.currentTimeMillis()
        prune(now)
        val allowed = now >= blockedUntil && stamps.size < TOTAL_BUDGET && (search || backgroundStamps.size < BACKGROUND_BUDGET)
        if (allowed) { stamps.addLast(now); if (!search) backgroundStamps.addLast(now) } else refusals++
        return allowed
    }

    // Whether a background request would be let through now (asks nothing, counts nothing): lets the suggestions skip
    // an optional lookup instead of being refused.
    @Synchronized
    fun backgroundAvailable(): Boolean {
        val now = System.currentTimeMillis()
        prune(now)
        return now >= blockedUntil && stamps.size < TOTAL_BUDGET && backgroundStamps.size < BACKGROUND_BUDGET
    }

    // iTunes said no: stop asking for a whole window.
    @Synchronized
    fun refused() {
        blockedUntil = System.currentTimeMillis() + WINDOW_MS
        refusals++
    }

    // Requests turned away so far (here or by iTunes): lets a caller tell an answer cut short by the limit.
    @Synchronized
    fun refusalCount() = refusals

    // How long until a search request would be let through again (0 = now).
    @Synchronized
    fun searchWaitMs(): Long {
        val now = System.currentTimeMillis()
        prune(now)
        val untilUnblocked = (blockedUntil - now).coerceAtLeast(0)
        val untilBudget = if (stamps.size >= TOTAL_BUDGET) stamps.elementAt(stamps.size - TOTAL_BUDGET) + WINDOW_MS - now else 0
        return maxOf(untilUnblocked, untilBudget, 0)
    }

    private fun prune(now: Long) {
        while (stamps.isNotEmpty() && now - stamps.first() >= WINDOW_MS) stamps.removeFirst()
        while (backgroundStamps.isNotEmpty() && now - backgroundStamps.first() >= WINDOW_MS) backgroundStamps.removeFirst()
    }
}

// Thrown by the search when the gate holds a request back; waitMs = when it may try again.
class ItunesRateLimited(val waitMs: Long) : Exception("iTunes rate limit")
