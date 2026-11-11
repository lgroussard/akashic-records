package com.akashicrecords.crossword

import android.content.Context
import org.json.JSONArray
import org.json.JSONObject
import java.text.Normalizer
import kotlin.random.Random

// A clue of the grid: its number, direction, first cell, length, text and answer.
data class Clue(val number: Int, val across: Boolean, val row: Int, val col: Int, val length: Int, val text: String, val answer: String) {
    fun cells(): List<Pair<Int, Int>> = (0 until length).map { if (across) row to col + it else row + it to col }
}

// A crossword: the solution row by row ('#' = black square, letters A–Z without accents) and its clues.
data class Puzzle(val title: String, val rows: Int, val cols: Int, val solution: List<String>, val clues: List<Clue>) {
    fun isBlack(r: Int, c: Int) = solution[r][c] == '#'
    fun letter(r: Int, c: Int) = solution[r][c]

    // The number printed in a cell, if a clue starts there.
    val numbers: Map<Pair<Int, Int>, Int> by lazy { clues.associate { (it.row to it.col) to it.number } }

    fun clueAt(r: Int, c: Int, across: Boolean) = clues.firstOrNull { it.across == across && (r to c) in it.cells() }

    fun toJson(): String = JSONObject()
        .put("title", title).put("rows", rows).put("cols", cols)
        .put("solution", JSONArray(solution))
        .put("clues", JSONArray(clues.map {
            JSONObject().put("n", it.number).put("a", it.across).put("r", it.row).put("c", it.col)
                .put("l", it.length).put("t", it.text).put("s", it.answer)
        }))
        .toString()

    companion object {
        fun fromJson(json: String): Puzzle {
            val o = JSONObject(json)
            val sol = o.getJSONArray("solution").let { a -> (0 until a.length()).map { a.getString(it) } }
            val cl = o.getJSONArray("clues").let { a ->
                (0 until a.length()).map { i ->
                    val c = a.getJSONObject(i)
                    Clue(c.getInt("n"), c.getBoolean("a"), c.getInt("r"), c.getInt("c"), c.getInt("l"), c.getString("t"), c.getString("s"))
                }
            }
            return Puzzle(o.optString("title"), o.getInt("rows"), o.getInt("cols"), sol, cl)
        }

        // Numbers the clue starts in reading order from a solution and the texts per (number, across).
        fun build(title: String, solution: List<String>, texts: (Int, Boolean, String) -> String?): Puzzle {
            val rows = solution.size
            val cols = solution.maxOf { it.length }
            val grid = solution.map { it.padEnd(cols, '#') }
            fun open(r: Int, c: Int) = r in 0 until rows && c in 0 until cols && grid[r][c] != '#'
            val clues = ArrayList<Clue>()
            var n = 0
            for (r in 0 until rows) for (c in 0 until cols) {
                if (!open(r, c)) continue
                val startsAcross = !open(r, c - 1) && open(r, c + 1)
                val startsDown = !open(r - 1, c) && open(r + 1, c)
                if (!startsAcross && !startsDown) continue
                n++
                if (startsAcross) {
                    var len = 0
                    while (open(r, c + len)) len++
                    val answer = (0 until len).map { grid[r][c + it] }.joinToString("")
                    clues += Clue(n, true, r, c, len, texts(n, true, answer) ?: "", answer)
                }
                if (startsDown) {
                    var len = 0
                    while (open(r + len, c)) len++
                    val answer = (0 until len).map { grid[r + it][c] }.joinToString("")
                    clues += Clue(n, false, r, c, len, texts(n, false, answer) ?: "", answer)
                }
            }
            return Puzzle(title, rows, cols, grid, clues)
        }
    }
}

// Letters as crosswords write them: upper case, no accents, Œ/Æ spelled out.
fun normalizeLetters(s: String): String =
    Normalizer.normalize(s.uppercase().replace("Œ", "OE").replace("Æ", "AE"), Normalizer.Form.NFD)
        .replace(Regex("\\p{Mn}+"), "")

// ---- Generation ----

// The bundled word lists (WORD <TAB> clue): fr.tsv, clues written for Akashic, then fr-wiktionnaire.tsv, ~19 000
// common words with a definition from the Wiktionnaire (CC BY-SA; rebuilt by build-crossword-words.ps1).
// A word in both keeps the hand-written clue.
object WordList {
    @Volatile private var cache: Map<String, String>? = null
    private val FILES = listOf("crossword/fr.tsv", "crossword/fr-wiktionnaire.tsv")

    fun load(context: Context): Map<String, String> = cache ?: synchronized(this) {
        cache ?: LinkedHashMap<String, String>().also { map ->
            for (file in FILES) {
                runCatching {
                    context.assets.open(file).bufferedReader().useLines { lines ->
                        lines.filter { it.isNotBlank() && !it.startsWith("#") }.forEach { line ->
                            val tab = line.indexOf('\t')
                            if (tab <= 0) return@forEach
                            val word = normalizeLetters(line.substring(0, tab).trim())
                            if (word.length >= 3 && word.all { it in 'A'..'Z' }) map.putIfAbsent(word, line.substring(tab + 1).trim())
                        }
                    }
                }
            }
        }.also { cache = it }
    }
}

// Builds a size×size grid by laying crossing words one after another (each new word must cross at least one
// letter already placed, and never touch another word side by side), then keeps the fullest of many attempts.
// Unfilled squares become black ones. Varied by [seed].
object Generator {
    private data class Placed(val word: String, val row: Int, val col: Int, val across: Boolean)

    // [avoid]: answers of the recent grids, left out so a new grid does not repeat them.
    fun generate(
        words: Map<String, String>, size: Int, avoid: Set<String> = emptySet(),
        seed: Long = System.nanoTime(), attempts: Int = 60, budgetMs: Long = 1500,
    ): Puzzle {
        val random = Random(seed)
        val pool = words.keys.filter { it.length in 3..size && it !in avoid }
        var best: List<Placed> = emptyList()
        var bestScore = -1
        val deadline = System.currentTimeMillis() + budgetMs
        for (n in 0 until attempts) {
            if (n > 0 && System.currentTimeMillis() > deadline) break
            val placed = attempt(pool, size, random)
            val letters = placed.flatMap { p -> p.word.indices.map { i -> if (p.across) p.row to p.col + i else p.row + i to p.col } }.toSet().size
            val score = letters * 4 + placed.size
            if (score > bestScore) { bestScore = score; best = placed }
        }
        val grid = Array(size) { CharArray(size) { '#' } }
        best.forEach { p -> p.word.forEachIndexed { i, ch -> if (p.across) grid[p.row][p.col + i] = ch else grid[p.row + i][p.col] = ch } }
        val byStart = best.associateBy { Triple(it.row, it.col, it.across) }
        return Puzzle.build("Grille ${size}×$size", grid.map { String(it) }) { _, across, answer ->
            words[answer] ?: byStart.values.firstOrNull { it.word == answer && it.across == across }?.let { words[it.word] }
        }
    }

    private fun attempt(pool: List<String>, size: Int, random: Random): List<Placed> {
        val grid = Array(size) { CharArray(size) { ' ' } }
        val placed = ArrayList<Placed>()
        val used = HashSet<String>()

        // First word: long, across, near the middle.
        val first = pool.filter { it.length >= size - 2 }.ifEmpty { pool }.random(random)
        val row = (size / 2 + random.nextInt(-1, 2)).coerceIn(0, size - 1)
        val col = random.nextInt(0, size - first.length + 1)
        put(grid, Placed(first, row, col, true)); placed += Placed(first, row, col, true); used += first

        while (true) {
            var bestPlace: Placed? = null
            var bestCross = 0
            // Only the placements anchored on a letter already in the grid can cross it.
            val filled = (0 until size).flatMap { r -> (0 until size).filter { c -> grid[r][c] != ' ' }.map { c -> r to c } }
            for (word in pool.shuffled(random)) {
                if (word in used) continue
                for ((r, c) in filled) {
                    val letter = grid[r][c]
                    for (i in word.indices) {
                        if (word[i] != letter) continue
                        for (across in listOf(true, false)) {
                            val p = if (across) Placed(word, r, c - i, true) else Placed(word, r - i, c, false)
                            if (p.row < 0 || p.col < 0) continue
                            val cross = crossings(grid, p, size)
                            if (cross > bestCross || (cross == bestCross && cross > 0 && random.nextInt(4) == 0)) {
                                bestCross = cross; bestPlace = p
                            }
                        }
                    }
                }
                // Good enough: stop scanning once a placement with 2+ crossings is found among the first words.
                if (bestCross >= 2) break
            }
            val p = bestPlace ?: break
            put(grid, p); placed += p; used += p.word
        }
        return placed
    }

    // Number of letters [p] shares with the grid, or 0 when it cannot go there.
    private fun crossings(grid: Array<CharArray>, p: Placed, size: Int): Int {
        val dr = if (p.across) 0 else 1
        val dc = if (p.across) 1 else 0
        val endR = p.row + dr * (p.word.length - 1)
        val endC = p.col + dc * (p.word.length - 1)
        if (endR >= size || endC >= size) return 0
        fun at(r: Int, c: Int) = if (r in 0 until size && c in 0 until size) grid[r][c] else ' '
        // The squares just before and after the word stay empty.
        if (at(p.row - dr, p.col - dc) != ' ' || at(endR + dr, endC + dc) != ' ') return 0
        var cross = 0
        for (i in p.word.indices) {
            val r = p.row + dr * i
            val c = p.col + dc * i
            val existing = grid[r][c]
            if (existing == ' ') {
                // A new letter must not touch another word on its sides.
                if (at(r - dc, c - dr) != ' ' || at(r + dc, c + dr) != ' ') return 0
            } else if (existing == p.word[i]) cross++ else return 0
        }
        return if (cross in 1 until p.word.length) cross else 0
    }

    private fun put(grid: Array<CharArray>, p: Placed) {
        p.word.forEachIndexed { i, ch -> if (p.across) grid[p.row][p.col + i] = ch else grid[p.row + i][p.col] = ch }
    }
}

// ---- .ipuz import (the open crossword format: JSON with dimensions, solution and clues) ----

object Ipuz {
    fun parse(text: String, fallbackTitle: String): Puzzle {
        val o = JSONObject(text.trim().removePrefix("ipuz(").removeSuffix(")"))
        val solution = o.getJSONArray("solution")
        val rows = solution.length()
        val grid = (0 until rows).map { r ->
            val line = solution.getJSONArray(r)
            (0 until line.length()).map { c ->
                when (val v = line.opt(c)) {
                    null, JSONObject.NULL -> '#'
                    is JSONObject -> v.optString("value").let { normalizeLetters(it).firstOrNull() ?: '#' }
                    else -> v.toString().let { s -> if (s == "#" || s.isBlank()) '#' else normalizeLetters(s).firstOrNull() ?: '#' }
                }.let { if (it in 'A'..'Z') it else '#' }
            }.joinToString("")
        }
        val clues = o.optJSONObject("clues")
        fun texts(key: String): Map<Int, String> {
            val arr = clues?.optJSONArray(clues.keys().asSequence().firstOrNull { it.startsWith(key, ignoreCase = true) } ?: return emptyMap())
                ?: return emptyMap()
            return (0 until arr.length()).mapNotNull { i ->
                when (val e = arr.get(i)) {
                    is JSONArray -> e.optString(0).toIntOrNull()?.let { it to e.optString(1) }
                    is JSONObject -> e.optString("number").toIntOrNull()?.let { it to e.optString("clue") }
                    else -> null
                }
            }.toMap()
        }
        val across = texts("Across")
        val down = texts("Down")
        val title = o.optString("title").ifBlank { fallbackTitle }
        return Puzzle.build(title, grid) { n, isAcross, _ -> if (isAcross) across[n] else down[n] }
    }
}
