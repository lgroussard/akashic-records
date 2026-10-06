package com.akashicrecords

import androidx.compose.ui.graphics.Color
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.material3.ColorScheme
import androidx.compose.material3.Typography
import androidx.compose.material3.darkColorScheme
import androidx.compose.ui.unit.sp

// Design tokens translated 1:1 from the desktop App.xaml brushes so the mobile
// app matches the validated mockups. Dark surface ramp + accent + tier pills.
object Palette {
    val AppBg = Color(0xFF0E0E1A)
    val Surface1 = Color(0xFF16162A)
    val Surface2 = Color(0xFF1C1C34)
    val Surface3 = Color(0xFF232342)
    val SurfaceFaint = Color(0x08FFFFFF)
    val BorderStrong = Color(0x24FFFFFF)
    val Text = Color(0xFFECECF4)
    val TextMuted = Color(0xFFA0A0BD)
    val TextFaint = Color(0xFF6F6F8C)
    val Accent = Color(0xFF5B8CFF)
    val Accent2 = Color(0xFF9B7BFF)
    val AccentSoft = Color(0x295B8CFF)

    // Tier pills, keyed by the Artwork.Tier ordinal (0..4).
    val tierColors = listOf(
        Color(0xFF60A5FA), // S
        Color(0xFFA78BFA), // A
        Color(0xFF22D3EE), // B
        Color(0xFF94A3B8), // C
        Color(0xFF64748B), // D
    )
}

val AkashicColorScheme: ColorScheme = darkColorScheme(
    primary = Palette.Accent,
    onPrimary = Palette.AppBg,
    secondary = Palette.Accent2,
    background = Palette.AppBg,
    surface = Palette.Surface1,
    onBackground = Palette.Text,
    onSurface = Palette.Text,
)

val AkashicTypography = Typography(
    titleMedium = androidx.compose.ui.text.TextStyle(
        fontFamily = FontFamily.Default,
        fontWeight = FontWeight.Bold,
        fontSize = 18.sp,
        color = Palette.Text,
    ),
    bodyMedium = androidx.compose.ui.text.TextStyle(
        fontFamily = FontFamily.Default,
        fontSize = 13.sp,
        color = Palette.Text,
    ),
)
