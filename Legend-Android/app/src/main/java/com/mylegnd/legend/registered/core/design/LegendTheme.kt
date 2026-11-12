package com.mylegnd.legend.registered.core.design

import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Shapes
import androidx.compose.material3.Typography
import androidx.compose.material3.darkColorScheme
import androidx.compose.material3.lightColorScheme
import androidx.compose.runtime.Composable

private fun legendMaterialTypography() = Typography(
    displayLarge = LegendTypography.Display,
    headlineLarge = LegendTypography.Hero,
    headlineMedium = LegendTypography.Title,
    titleLarge = LegendTypography.Section,
    titleMedium = LegendTypography.CardTitle,
    bodyLarge = LegendTypography.Body,
    bodyMedium = LegendTypography.Supporting,
    labelLarge = LegendTypography.Label,
    labelSmall = LegendTypography.Eyebrow,
)

@Composable fun LegendTheme(brandedSheet: Boolean = false, content: @Composable () -> Unit) = MaterialTheme(
    colorScheme = if (brandedSheet) darkColorScheme(
        primary = LegendColors.GoldBright, onPrimary = LegendColors.Midnight,
        secondary = LegendColors.Gold, onSecondary = LegendColors.Midnight,
        background = LegendColors.Midnight, onBackground = LegendColors.OnNavy,
        surface = LegendColors.Midnight, onSurface = LegendColors.OnNavy,
        surfaceVariant = LegendColors.NavyElevated, onSurfaceVariant = LegendColors.OnNavy.copy(alpha = 0.78f),
        primaryContainer = LegendColors.NavyElevated, onPrimaryContainer = LegendColors.OnNavy,
        secondaryContainer = LegendColors.GoldBright, onSecondaryContainer = LegendColors.Midnight,
        surfaceContainer = LegendColors.Navy, surfaceContainerHigh = LegendColors.NavyElevated,
        surfaceContainerHighest = LegendColors.NavyElevated,
        outline = LegendColors.Gold.copy(alpha = 0.45f), outlineVariant = LegendColors.Gold.copy(alpha = 0.22f),
        error = LegendColors.Error, scrim = LegendColors.Midnight,
    ) else lightColorScheme(
        primary = LegendColors.Navy,
        secondary = LegendColors.Gold,
        background = LegendColors.Canvas,
        surface = LegendColors.Surface,
        onPrimary = LegendColors.OnNavy,
        onSecondary = LegendColors.OnGold,
        onBackground = LegendColors.TextPrimary,
        onSurface = LegendColors.TextPrimary,
        error = LegendColors.Error,
        onError = LegendColors.OnNavy,
        primaryContainer = LegendColors.NavyElevated,
        onPrimaryContainer = LegendColors.OnNavy,
        inversePrimary = LegendColors.GoldBright,
        secondaryContainer = LegendColors.GoldSoft,
        onSecondaryContainer = LegendColors.Midnight,
        tertiary = LegendColors.Royal,
        onTertiary = LegendColors.OnNavy,
        tertiaryContainer = LegendColors.Navy,
        onTertiaryContainer = LegendColors.OnNavy,
        errorContainer = LegendColors.Error,
        onErrorContainer = LegendColors.OnNavy,
        surfaceVariant = LegendColors.SurfaceInset,
        onSurfaceVariant = LegendColors.TextSecondary,
        surfaceTint = LegendColors.Gold,
        inverseSurface = LegendColors.Navy,
        inverseOnSurface = LegendColors.OnNavy,
        outline = LegendColors.Divider,
        outlineVariant = LegendColors.Divider,
        scrim = LegendColors.Midnight,
        surfaceBright = LegendColors.SurfaceElevated,
        surfaceDim = LegendColors.SurfaceInset,
        surfaceContainer = LegendColors.Surface,
        surfaceContainerHigh = LegendColors.SurfaceElevated,
        surfaceContainerHighest = LegendColors.SurfaceElevated,
        surfaceContainerLow = LegendColors.Canvas,
        surfaceContainerLowest = LegendColors.Canvas,
    ),
    typography = legendMaterialTypography(),
    shapes = Shapes(
        small = LegendShapes.Compact,
        medium = LegendShapes.Control,
        large = LegendShapes.Card,
    ),
    content = content,
)
