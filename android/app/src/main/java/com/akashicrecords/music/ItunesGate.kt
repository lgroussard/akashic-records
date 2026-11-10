package com.akashicrecords.music

// The iTunes Search API allows about 20 requests a minute per Internet connection (the PC and the phone share it at
// home). Every iTunes request of the app goes through here:
//   * a sliding one-minute window counts this app's requests; the free search gets SEARCH_BUDGET of them, so typing
//     can never starve the Similaires list and the automatic chaining, which get the rest up to TOTAL_BUDGET;
//   * when iTunes answers 403/429 anyway (e.g. the other device used the quota), nothing is sent for a minute —
//     insisting would only extend the block.
// Requests that are not let through simply return nothing; callers treat that like "no network". Same rules on the
// PC (ItunesGate in MusicRecommendationService.cs).
object ItunesGate {
    private const val WINDOW_MS = 60_000L
    private const val TOTAL_BUDGET = 19
    private const val SEARCH_BUDGET = 12

    private val stamps = ArrayDeque<Long>()
    private var blockedUntil = 0L
    private var refusals = 0L

    fun isItunes(url: String) = url.startsWith("https://itunes.apple.com/")

    @Synchronized
    fun tryAcquire(search: Boolean): Boolean {
        val now = System.currentTimeMillis()
        prune(now)
        val allowed = now >= blockedUntil && stamps.size < (if (search) SEARCH_BUDGET else TOTAL_BUDGET)
        if (allowed) stamps.addLast(now) else refusals++
        return allowed
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
        val untilBudget = if (stamps.size >= SEARCH_BUDGET) stamps.elementAt(stamps.size - SEARCH_BUDGET) + WINDOW_MS - now else 0
        return maxOf(untilUnblocked, untilBudget, 0)
    }

    private fun prune(now: Long) {
        while (stamps.isNotEmpty() && now - stamps.first() >= WINDOW_MS) stamps.removeFirst()
    }
}

// Thrown by the search when the gate holds a request back; waitMs = when it may try again.
class ItunesRateLimited(val waitMs: Long) : Exception("iTunes rate limit")
