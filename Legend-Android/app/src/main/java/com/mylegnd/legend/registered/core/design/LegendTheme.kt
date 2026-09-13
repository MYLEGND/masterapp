package com.mylegnd.legend.registered.core.design

import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Shapes
import androidx.compose.material3.Typography
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

@Composable fun LegendTheme(content: @Composable () -> Unit) = MaterialTheme(
    colorScheme = lightColorScheme(
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
