package com.mylegnd.legend.registered.core.design

import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.lightColorScheme
import androidx.compose.runtime.Composable
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.text.TextStyle
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp

/** Exact values extracted from Legend-ios/Legend/DesignSystem/NextGen/LegendNextTheme.swift. */
object LegendColors {
    val Midnight = Color(0xFF0A162E); val Navy = Color(0xFF10254C); val NavyElevated = Color(0xFF3159BF)
    val Royal = Color(0xFF234284); val Gold = Color(0xFFA68023); val GoldBright = Color(0xFFE0B853); val GoldSoft = Color(0xFFF6E8BF)
    val Canvas = Color.White; val CanvasSecondary = Color(0xFFF6F9FF); val SurfaceInset = Color(0xFFEDF3FD)
    val TextPrimary = Color(0xFF0A162E); val TextSecondary = Color(0xFF475569); val TextTertiary = Color(0xFF64748B)
    // iOS source uses UIColor.systemGreen/systemOrange/systemRed/systemBlue; these are their light-mode canonical values.
    val Divider = Color(0x1A10254C); val Success = Color(0xFF34C759); val Warning = Color(0xFFFF9500); val Error = Color(0xFFFF3B30); val Info = Color(0xFF007AFF)
}
object LegendSpacing { val Micro = 3.dp; val Xs = 7.dp; val Sm = 10.dp; val Md = 14.dp; val Lg = 20.dp; val Xl = 26.dp; val Xxl = 32.dp; val PageHorizontal = 16.dp }
object LegendShapes { val Compact = RoundedCornerShape(10.dp); val Control = RoundedCornerShape(16.dp); val Card = RoundedCornerShape(20.dp); val Hero = RoundedCornerShape(28.dp) }

private val LegendTypography = androidx.compose.material3.Typography(
    displayLarge = TextStyle(fontSize = 32.sp, fontWeight = FontWeight.Bold),
    headlineLarge = TextStyle(fontSize = 27.sp, fontWeight = FontWeight.Bold),
    headlineMedium = TextStyle(fontSize = 22.sp, fontWeight = FontWeight.Bold),
    titleLarge = TextStyle(fontSize = 18.sp, fontWeight = FontWeight.SemiBold),
    titleMedium = TextStyle(fontSize = 16.sp, fontWeight = FontWeight.SemiBold),
    bodyLarge = TextStyle(fontSize = 16.sp), bodyMedium = TextStyle(fontSize = 14.sp),
    labelLarge = TextStyle(fontSize = 13.sp, fontWeight = FontWeight.SemiBold), labelSmall = TextStyle(fontSize = 11.sp, fontWeight = FontWeight.Bold)
)
@Composable fun LegendTheme(content: @Composable () -> Unit) = MaterialTheme(
    colorScheme = lightColorScheme(primary = LegendColors.Navy, secondary = LegendColors.Gold, background = LegendColors.Canvas, surface = Color.White, onPrimary = Color.White, onSecondary = Color.White, onBackground = LegendColors.TextPrimary, onSurface = LegendColors.TextPrimary, error = LegendColors.Error),
    typography = LegendTypography, shapes = androidx.compose.material3.Shapes(small = LegendShapes.Compact, medium = LegendShapes.Control, large = LegendShapes.Card), content = content
)
