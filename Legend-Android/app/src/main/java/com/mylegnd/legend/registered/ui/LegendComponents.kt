@file:OptIn(ExperimentalMaterial3Api::class)

package com.mylegnd.legend.registered.ui

import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.material3.*
import androidx.compose.runtime.Composable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.Dp
import com.mylegnd.legend.registered.core.design.*

@Composable fun LegendCard(modifier: Modifier = Modifier, content: @Composable ColumnScope.() -> Unit) = Card(modifier, shape = LegendShapes.Card, colors = CardDefaults.cardColors(containerColor = LegendColors.Surface), elevation = CardDefaults.cardElevation(defaultElevation = 1.dp)) { Column(Modifier.padding(LegendSpacing.CardContent), verticalArrangement = Arrangement.spacedBy(LegendSpacing.Sm), content = content) }
@Composable fun LegendPrimaryButton(text: String, modifier: Modifier = Modifier, enabled: Boolean = true, onClick: () -> Unit) = Button(onClick = onClick, enabled = enabled, shape = LegendShapes.Control, colors = ButtonDefaults.buttonColors(containerColor = LegendColors.Navy), modifier = modifier.fillMaxWidth().heightIn(min = LegendSize.ControlHeight)) { Text(text) }
@Composable fun LegendEmptyState(title: String, detail: String) = Column(Modifier.fillMaxSize().padding(LegendSpacing.Xxl), horizontalAlignment = Alignment.CenterHorizontally, verticalArrangement = Arrangement.Center) { Text(title, style = LegendTypography.Section, color = LegendColors.TextPrimary, textAlign = TextAlign.Center); Spacer(Modifier.height(LegendSpacing.Sm)); Text(detail, style = LegendTypography.Supporting, color = LegendColors.TextSecondary, textAlign = TextAlign.Center) }
@Composable fun LegendLoadingState() = Box(Modifier.fillMaxSize(), contentAlignment = Alignment.Center) { CircularProgressIndicator(color = LegendColors.Navy) }
@Composable fun LegendErrorState(message: String, retry: () -> Unit) = Column(Modifier.fillMaxSize().padding(LegendSpacing.Xxl), horizontalAlignment = Alignment.CenterHorizontally, verticalArrangement = Arrangement.Center) { Text(message, textAlign = TextAlign.Center, color = LegendColors.TextSecondary); Spacer(Modifier.height(LegendSpacing.Md)); OutlinedButton(onClick = retry, shape = LegendShapes.Control) { Text("Try again") } }
/**
 * The one fallback avatar renderer. Protected image avatars use the identical
 * gold ring in [LegendProtectedAvatar], so loading state never changes the
 * LEGEND profile-circle language.
 */
@Composable fun LegendAvatar(name: String, modifier: Modifier = Modifier, size: Dp = LegendSize.AvatarMedium) = Box(
    modifier
        .size(size)
        .background(LegendColors.Navy, CircleShape)
        .border(1.dp, LegendColors.Gold.copy(alpha = 0.7f), CircleShape),
    contentAlignment = Alignment.Center,
) { Text(name.take(1).uppercase(), color = LegendColors.GoldBright, style = LegendTypography.CardTitle) }
