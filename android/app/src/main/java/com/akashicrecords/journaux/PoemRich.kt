package com.akashicrecords.journaux

import android.util.Xml
import androidx.compose.ui.text.AnnotatedString
import androidx.compose.ui.text.SpanStyle
import androidx.compose.ui.text.font.FontStyle
import androidx.compose.ui.text.font.FontWeight
import org.xmlpull.v1.XmlPullParser

// Per-letter formatting of a poem, shared with the PC. The PC keeps it in Poem.RichContent as a WPF
// FlowDocument (one Paragraph per line, Runs carrying FontWeight / FontStyle); the phone reads that,
// edits it as plain text plus one override per character, and writes the same XAML back, so a word made
// bold on either device is bold on both. The poem's own B / I (whole poem) stay the baseline: an override
// of null means "as the poem".
data class CharStyle(val bold: Boolean? = null, val italic: Boolean? = null)

data class RichPoem(
    val text: String,
    val styles: List<CharStyle>, // one per character of text
    val lineAlign: Map<Int, String> = emptyMap(), // a line aligned apart from the poem (set on the PC)
) {
    val hasFormatting get() = lineAlign.isNotEmpty() || styles.any { it.bold != null || it.italic != null }

    // The text the user just typed, with the overrides moved along: kept before and after the change,
    // and the typed letters take the style of the one before them (as in any editor).
    fun edited(newText: String): RichPoem {
        if (newText == text) return this
        var p = 0
        val max = minOf(text.length, newText.length)
        while (p < max && text[p] == newText[p]) p++
        var q = 0
        while (q < max - p && text[text.length - 1 - q] == newText[newText.length - 1 - q]) q++
        val typed = newText.length - p - q
        val inherit = styles.getOrNull(p - 1)?.takeIf { text[p - 1] != '\n' } ?: CharStyle()
        val newStyles = styles.subList(0, p) + List(typed) { inherit } + styles.subList(text.length - q, text.length)
        // Lines after the change shift by the number of line breaks added or removed.
        val lineAtChange = text.substring(0, p).count { it == '\n' }
        val delta = newText.count { it == '\n' } - text.count { it == '\n' }
        val removedLines = text.substring(p, text.length - q).count { it == '\n' }
        val aligns = lineAlign.mapNotNull { (line, a) ->
            when {
                line <= lineAtChange -> line to a
                line <= lineAtChange + removedLines -> null
                else -> (line + delta) to a
            }
        }.toMap()
        return RichPoem(newText, newStyles, aligns)
    }

    // B / I on a selection: on everywhere unless it is already on across the whole selection.
    fun toggled(start: Int, end: Int, bold: Boolean, baseline: Boolean): RichPoem {
        val (a, b) = minOf(start, end).coerceIn(0, text.length) to maxOf(start, end).coerceIn(0, text.length)
        if (a == b) return this
        val on = (a until b).all { (if (bold) styles[it].bold else styles[it].italic) ?: baseline }
        val newStyles = styles.mapIndexed { i, s ->
            if (i !in a until b) s else if (bold) s.copy(bold = !on) else s.copy(italic = !on)
        }
        return copy(styles = newStyles)
    }

    fun isOn(start: Int, end: Int, bold: Boolean, baseline: Boolean): Boolean {
        val (a, b) = minOf(start, end).coerceIn(0, text.length) to maxOf(start, end).coerceIn(0, text.length)
        return a < b && (a until b).all { (if (bold) styles[it].bold else styles[it].italic) ?: baseline }
    }

    // What the editor draws: the overrides only, the baseline comes from the text field's own style.
    fun annotate(shown: String): AnnotatedString {
        val b = AnnotatedString.Builder(shown)
        if (shown.length != styles.size) return b.toAnnotatedString() // mid-edit; redrawn right after
        var i = 0
        while (i < styles.size) {
            val s = styles[i]
            var j = i + 1
            while (j < styles.size && styles[j] == s) j++
            if (s.bold != null || s.italic != null) {
                b.addStyle(
                    SpanStyle(
                        fontWeight = s.bold?.let { if (it) FontWeight.Bold else FontWeight.Normal },
                        fontStyle = s.italic?.let { if (it) FontStyle.Italic else FontStyle.Normal },
                    ),
                    i, j,
                )
            }
            i = j
        }
        return b.toAnnotatedString()
    }

    // Back to the PC's format. Without any formatting the PC rebuilds the same poem from Text alone, so
    // nothing is written then (an untouched plain poem stays plain).
    fun toXaml(): String {
        if (!hasFormatting) return ""
        val sb = StringBuilder("<FlowDocument xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\">")
        var offset = 0
        text.split('\n').forEachIndexed { line, content ->
            sb.append("<Paragraph Margin=\"0,0,0,0\"")
            lineAlign[line]?.let { sb.append(" TextAlignment=\"").append(esc(it)).append('"') }
            if (content.isEmpty()) sb.append(" />")
            else {
                sb.append('>')
                var i = 0
                while (i < content.length) {
                    val s = styles[offset + i]
                    var j = i + 1
                    while (j < content.length && styles[offset + j] == s) j++
                    sb.append("<Run xml:space=\"preserve\"")
                    s.bold?.let { sb.append(" FontWeight=\"").append(if (it) "Bold" else "Normal").append('"') }
                    s.italic?.let { sb.append(" FontStyle=\"").append(if (it) "Italic" else "Normal").append('"') }
                    sb.append('>').append(esc(content.substring(i, j))).append("</Run>")
                    i = j
                }
                sb.append("</Paragraph>")
            }
            offset += content.length + 1
        }
        return sb.append("</FlowDocument>").toString()
    }

    companion object {
        fun plain(text: String) = RichPoem(text, List(text.length) { CharStyle() })

        // The PC's XAML when there is some (and it reads back to the same words), else the plain text.
        fun of(text: String, xaml: String): RichPoem =
            if (xaml.isBlank()) plain(text)
            else runCatching { parse(xaml) }.getOrNull()?.takeIf { it.text.trimEnd('\n') == text.trimEnd('\n') }
                ?.let { if (it.text.length > text.length) it.edited(text) else it }
                ?: plain(text)

        private val BOLD = setOf("bold", "semibold", "demibold", "extrabold", "ultrabold", "black", "heavy", "extrablack", "ultrablack")
        private val INLINE = setOf("Run", "Span", "Bold", "Italic", "Underline", "Hyperlink")

        private fun parse(xaml: String): RichPoem {
            val parser = Xml.newPullParser()
            parser.setFeature(XmlPullParser.FEATURE_PROCESS_NAMESPACES, false)
            parser.setInput(xaml.reader())
            val text = StringBuilder()
            val styles = ArrayList<CharStyle>()
            val aligns = HashMap<Int, String>()
            val stack = ArrayList<CharStyle>().apply { add(CharStyle()) }
            var inline = 0
            var paragraphs = 0
            fun newLine() { text.append('\n'); styles.add(CharStyle()) }
            while (true) {
                when (parser.next()) {
                    XmlPullParser.END_DOCUMENT -> break
                    XmlPullParser.START_TAG -> {
                        val name = parser.name
                        val parent = stack.last()
                        when (name) {
                            "Paragraph" -> {
                                if (paragraphs++ > 0) newLine()
                                parser.getAttributeValue(null, "TextAlignment")?.let { aligns[paragraphs - 1] = it }
                            }
                            "LineBreak" -> newLine()
                        }
                        val weight = parser.getAttributeValue(null, "FontWeight")?.lowercase()
                        val style = parser.getAttributeValue(null, "FontStyle")?.lowercase()
                        stack.add(
                            CharStyle(
                                bold = when { name == "Bold" -> true; weight != null -> weight in BOLD; else -> parent.bold },
                                italic = when { name == "Italic" -> true; style != null -> style == "italic" || style == "oblique"; else -> parent.italic },
                            ),
                        )
                        if (name in INLINE) inline++
                    }
                    XmlPullParser.END_TAG -> {
                        if (parser.name in INLINE) inline--
                        stack.removeAt(stack.size - 1)
                    }
                    XmlPullParser.TEXT -> {
                        val t = parser.text
                        // Text straight inside a Paragraph is an implicit Run; whitespace between tags is not text.
                        if (inline > 0 || t.isNotBlank()) {
                            text.append(t)
                            repeat(t.length) { styles.add(stack.last()) }
                        }
                    }
                }
            }
            return RichPoem(text.toString(), styles, aligns)
        }

        private fun esc(s: String) = s.replace("&", "&amp;").replace("<", "&lt;").replace(">", "&gt;").replace("\"", "&quot;")
    }
}
