package com.akashicrecords.journaux

import com.akashicrecords.data.Poem

// Formatting the phone shows for a poem: the phone's own columns when set (schema v2), else the
// PC's. The phone only ever writes the Mobile* columns, so the PC layout is never touched.
data class PoemLook(val align: String, val fontSize: Double, val bold: Boolean, val italic: Boolean, val ownLayout: Boolean)

fun Poem.look() = PoemLook(
    align = MobileTextAlignment ?: TextAlignment,
    fontSize = MobileFontSize ?: FontSize,
    bold = MobileBold ?: Bold,
    italic = MobileItalic ?: Italic,
    ownLayout = MobileTextAlignment != null || MobileFontSize != null || MobileBold != null || MobileItalic != null,
)

// Copies the PC layout into the phone columns once, so the first phone tweak keeps the rest.
private fun Poem.seeded() = if (look().ownLayout) this else copy(
    MobileTextAlignment = TextAlignment, MobileFontSize = FontSize, MobileBold = Bold, MobileItalic = Italic,
)

fun Poem.withMobileAlign(a: String) = seeded().copy(MobileTextAlignment = a)
fun Poem.withMobileSize(s: Double) = seeded().copy(MobileFontSize = s)
fun Poem.withMobileBold(b: Boolean) = seeded().copy(MobileBold = b)
fun Poem.withMobileItalic(i: Boolean) = seeded().copy(MobileItalic = i)
fun Poem.withPcLayout() = copy(MobileTextAlignment = null, MobileFontSize = null, MobileBold = null, MobileItalic = null)
