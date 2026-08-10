@file:OptIn(ExperimentalMaterial3Api::class)

package com.mylegnd.legend.registered.ui

import androidx.compose.foundation.background
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.material3.*
import androidx.compose.runtime.Composable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.unit.dp
import com.mylegnd.legend.registered.core.design.*

@Composable fun LegendScreen(title: String, actions: @Composable RowScope.() -> Unit = {}, content: @Composable ColumnScope.() -> Unit) = Scaffold(
    topBar = { TopAppBar(title = { Text(title, style = MaterialTheme.typography.titleLarge, color = LegendColors.TextPrimary) }, actions = actions, colors = TopAppBarDefaults.topAppBarColors(containerColor = LegendColors.Canvas)) },
    containerColor = LegendColors.Canvas
) { padding -> Column(Modifier.fillMaxSize().padding(padding).padding(horizontal = LegendSpacing.PageHorizontal), content = content) }
@Composable fun LegendCard(modifier: Modifier = Modifier, content: @Composable ColumnScope.() -> Unit) = Card(modifier, shape = LegendShapes.Card, colors = CardDefaults.cardColors(containerColor = LegendColors.Surface), elevation = CardDefaults.cardElevation(defaultElevation = 1.dp)) { Column(Modifier.padding(LegendSpacing.CardContent), verticalArrangement = Arrangement.spacedBy(LegendSpacing.Sm), content = content) }
@Composable fun LegendPrimaryButton(text: String, enabled: Boolean = true, onClick: () -> Unit) = Button(onClick = onClick, enabled = enabled, shape = LegendShapes.Control, colors = ButtonDefaults.buttonColors(containerColor = LegendColors.Navy), modifier = Modifier.fillMaxWidth().heightIn(min = LegendSize.ControlHeight)) { Text(text) }
@Composable fun LegendEmptyState(title: String, detail: String) = Column(Modifier.fillMaxSize().padding(LegendSpacing.Xxl), horizontalAlignment = Alignment.CenterHorizontally, verticalArrangement = Arrangement.Center) { Text(title, style = MaterialTheme.typography.titleLarge, color = LegendColors.TextPrimary, textAlign = TextAlign.Center); Spacer(Modifier.height(LegendSpacing.Sm)); Text(detail, style = MaterialTheme.typography.bodyMedium, color = LegendColors.TextSecondary, textAlign = TextAlign.Center) }
@Composable fun LegendLoadingState() = Box(Modifier.fillMaxSize(), contentAlignment = Alignment.Center) { CircularProgressIndicator(color = LegendColors.Navy) }
@Composable fun LegendErrorState(message: String, retry: () -> Unit) = Column(Modifier.fillMaxSize().padding(LegendSpacing.Xxl), horizontalAlignment = Alignment.CenterHorizontally, verticalArrangement = Arrangement.Center) { Text(message, textAlign = TextAlign.Center, color = LegendColors.TextSecondary); Spacer(Modifier.height(LegendSpacing.Md)); OutlinedButton(onClick = retry, shape = LegendShapes.Control) { Text("Try again") } }
@Composable fun LegendAvatar(name: String) = Box(Modifier.size(LegendSize.AvatarMedium).background(LegendColors.Navy, CircleShape), contentAlignment = Alignment.Center) { Text(name.take(1).uppercase(), color = LegendColors.GoldBright, style = MaterialTheme.typography.titleMedium) }
