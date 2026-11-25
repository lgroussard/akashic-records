package com.akashicrecords.journaux

import kotlin.math.abs
import kotlin.math.roundToLong

// Reads the free-text ingredient list of a recipe into shopping lines. No Android here, so it runs in a
// plain unit test.
//
// One ingredient per line. A line that starts with a number is an amount ("200 g de lentilles", "3 gros
// oignons", "1/2 citron", "800 g à 1 kg de champignons" — a range keeps its upper bound) and can be scaled
// and added up; any other line ("Sel, poivre") is kept as it is. Remarks after a dash, a comma, a "+" or in
// brackets are dropped. Blank lines, the "Pour 4 personnes" line and section titles ("Crèmes", "Gelée") are
// not ingredients.
internal object ShoppingParser {

    // quantity is in the base unit: grams for "g", millilitres for "ml", otherwise the count as written.
    data class Line(val name: String, val quantity: Double?, val unit: String)

    private const val QTY = """(\d+(?:[.,]\d+)?(?:\s*/\s*\d+)?)"""
    private const val UNIT =
        """(kg|g|ml|cl|dl|l|c\.?\s*à\s*(?:soupe|café|s\.?|c\.?)|cuillères?\s+à\s+(?:soupe|café))(?=\s|$)"""
    private val AMOUNT = Regex(
        """^$QTY\s*(?:$UNIT)?(?:\s*(?:à|-|–)\s*$QTY\s*(?:$UNIT)?)?\s*(?:de\s+|d['’]\s*)?(.*)$""",
        RegexOption.IGNORE_CASE,
    )
    private val SERVINGS = Regex("""^pour\s+(\d+)\b""", RegexOption.IGNORE_CASE)
    private val REMARK = Regex("""\s+[—–-]\s+|\s\+\s|\(|,""")

    // The portions announced by a "Pour 4 personnes" / "Pour 3 repas" line, when there is one.
    fun servingsIn(ingredients: String): Int? =
        ingredients.lineSequence().map { it.trim() }
            .firstNotNullOfOrNull { SERVINGS.find(it)?.groupValues?.get(1)?.toIntOrNull() }
            ?.takeIf { it > 0 }

    fun parse(ingredients: String): List<Line> {
        val lines = ingredients.lines().map { it.trim().trimStart('-', '•', '*').trim() }
        val out = mutableListOf<Line>()
        // A lone title above a block of amounts ("Gelée") names a part of the recipe, not a product. Parts come
        // in pairs at least ("Crèmes" … "Gelée"): a single such line is a product without an amount that happens
        // to open the list ("Huile d'olive" above "2 oignons"), and is kept.
        fun startsBlock(i: Int) = !skipped(lines[i]) && AMOUNT.find(lines[i]) == null &&
            (i == 0 || lines[i - 1].isEmpty()) && lines.getOrNull(i + 1)?.firstOrNull()?.isDigit() == true
        val titled = lines.indices.count { startsBlock(it) } >= 2
        lines.forEachIndexed { i, line ->
            if (skipped(line)) return@forEachIndexed
            val amount = AMOUNT.find(line)
            if (amount == null) {
                val name = line.substringBefore('(').trim().trimEnd('.')
                if (!(titled && startsBlock(i)) && name.isNotEmpty()) out += Line(name, null, "")
                return@forEachIndexed
            }
            val (q1, u1, q2, u2, rest) = amount.destructured
            val name = REMARK.split(rest, 2)[0].trim().trimEnd('.')
            if (name.isEmpty()) return@forEachIndexed
            val qty = number(q2.ifEmpty { q1 })
            val (factor, unit) = baseUnit(u2.ifEmpty { u1 })
            out += Line(name, qty * factor, unit)
        }
        return out
    }

    // Not an ingredient at all: an empty line, « Pour 4 personnes », a title ending with a colon.
    private fun skipped(line: String) = line.isEmpty() || SERVINGS.containsMatchIn(line) || line.endsWith(":")

    private fun number(text: String): Double {
        val parts = text.replace(',', '.').split('/').map { it.trim().toDouble() }
        return if (parts.size == 2 && parts[1] != 0.0) parts[0] / parts[1] else parts[0]
    }

    // (how many base units one written unit is, base unit name)
    private fun baseUnit(written: String): Pair<Double, String> {
        val u = written.lowercase().replace(Regex("""\s+"""), " ")
        return when {
            u.isEmpty() -> 1.0 to ""
            u == "kg" -> 1000.0 to "g"
            u == "g" -> 1.0 to "g"
            u == "l" -> 1000.0 to "ml"
            u == "dl" -> 100.0 to "ml"
            u == "cl" -> 10.0 to "ml"
            u == "ml" -> 1.0 to "ml"
            u.endsWith("soupe") || u.endsWith("s") || u.endsWith("s.") -> 1.0 to "c. à s."
            else -> 1.0 to "c. à c."
        }
    }

    // Two lines are the same product when they have the same name (case and spacing aside) and unit.
    fun key(name: String, unit: String): String = name.lowercase().replace(Regex("""\s+"""), " ").trim() + "|" + unit

    // "400 g de lentilles", "1,5 l de bouillon", "6 gousses d'ail", "Sel, poivre".
    fun display(name: String, quantity: Double?, unit: String): String {
        if (quantity == null) return name
        // "d'huile" but "de haricots": the h of haricot is the one that takes no elision here.
        val first = name.firstOrNull()?.lowercaseChar() ?: ' '
        val of = if (first in "aeiouyhàâéèêîôûœ" && !name.startsWith("haricot", ignoreCase = true)) "d'" else "de "
        return when (unit) {
            "g" -> if (quantity >= 1000) "${decimal(quantity / 1000)} kg $of$name" else "${whole(quantity)} g $of$name"
            "ml" -> if (quantity >= 1000) "${decimal(quantity / 1000)} l $of$name" else "${whole(quantity)} ml $of$name"
            "" -> "${half(quantity)} $name"
            else -> "${half(quantity)} $unit $of$name"
        }
    }

    // Weights and volumes: to the unit, and to the nearest 5 once that no longer matters.
    private fun whole(v: Double): String = (if (v >= 50) (v / 5).roundToLong() * 5 else v.roundToLong().coerceAtLeast(1)).toString()

    // Things counted or spooned: to the nearest half, never down to nothing.
    private fun half(v: Double): String = decimal(((v * 2).roundToLong() / 2.0).coerceAtLeast(0.5))

    private fun decimal(v: Double): String {
        val r = (v * 100).roundToLong() / 100.0
        return if (abs(r - r.roundToLong()) < 0.005) r.roundToLong().toString() else r.toString().replace('.', ',')
    }
}
