package com.mylegnd.legend.registered.core.design

import androidx.compose.material3.MaterialTheme
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
        onSecondary = LegendColors.OnNavy,
        onBackground = LegendColors.TextPrimary,
        onSurface = LegendColors.TextPrimary,
        error = LegendColors.Error,
    ),
    typography = legendMaterialTypography(),
    shapes = androidx.compose.material3.Shapes(
        small = LegendShapes.Compact,
        medium = LegendShapes.Control,
        large = LegendShapes.Card,
    ),
    content = content,
)
