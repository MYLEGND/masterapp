@file:OptIn(ExperimentalMaterial3Api::class)

package com.mylegnd.legend.registered.ui

import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.combinedClickable
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.ui.draw.shadow
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Verified
import androidx.compose.material3.*
import androidx.compose.runtime.Composable
import androidx.compose.runtime.remember
import androidx.compose.runtime.getValue
import androidx.compose.foundation.interaction.MutableInteractionSource
import androidx.compose.foundation.interaction.collectIsPressedAsState
import androidx.compose.ui.composed
import androidx.compose.ui.graphics.graphicsLayer
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.Dp
import com.mylegnd.legend.registered.core.design.*

@Composable fun LegendCard(modifier: Modifier = Modifier, content: @Composable ColumnScope.() -> Unit) = Card(modifier, shape = LegendShapes.Card, colors = CardDefaults.cardColors(containerColor = LegendColors.Surface), elevation = CardDefaults.cardElevation(defaultElevation = 1.dp)) { Column(Modifier.padding(LegendSpacing.CardContent), verticalArrangement = Arrangement.spacedBy(LegendSpacing.Sm), content = content) }
@Composable fun LegendPrimaryButton(text: String, modifier: Modifier = Modifier, enabled: Boolean = true, onClick: () -> Unit) = Button(onClick = onClick, enabled = enabled, shape = LegendShapes.Control, colors = ButtonDefaults.buttonColors(containerColor = LegendColors.Navy), modifier = modifier.fillMaxWidth().heightIn(min = LegendSize.ControlHeight)) { Text(legendLocalized(text)) }
@Composable fun LegendEmptyState(title: String, detail: String) = Column(Modifier.fillMaxSize().padding(LegendSpacing.Xxl), horizontalAlignment = Alignment.CenterHorizontally, verticalArrangement = Arrangement.Center) { Text(legendLocalized(title), style = LegendTypography.Section, color = LegendColors.TextPrimary, textAlign = TextAlign.Center); Spacer(Modifier.height(LegendSpacing.Sm)); Text(legendLocalized(detail), style = LegendTypography.Supporting, color = LegendColors.TextSecondary, textAlign = TextAlign.Center) }
@Composable fun LegendLoadingState() = Box(Modifier.fillMaxSize(), contentAlignment = Alignment.Center) { CircularProgressIndicator(color = LegendColors.Navy) }
@Composable fun LegendErrorState(message: String, retry: () -> Unit) = Column(Modifier.fillMaxSize().padding(LegendSpacing.Xxl), horizontalAlignment = Alignment.CenterHorizontally, verticalArrangement = Arrangement.Center) { Text(legendLocalized(message), textAlign = TextAlign.Center, color = LegendColors.TextSecondary); Spacer(Modifier.height(LegendSpacing.Md)); OutlinedButton(onClick = retry, shape = LegendShapes.Control) { Text(legendLocalized("Try again")) } }
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

/**
 * Native Compose rendering of the shared contact-card tokens. Every compact person or
 * conversation presentation shares this surface, typography, and gold border
 * rather than creating an inbox-specific visual language.
 */
@Composable
fun LegendContactCard(
    displayName: String,
    modifier: Modifier = Modifier,
    nameStatus: String? = null,
    subtitle: String? = null,
    detail: String? = null,
    statusContent: (@Composable () -> Unit)? = null,
    isVerified: Boolean = false,
    onClick: (() -> Unit)? = null,
    onLongClick: (() -> Unit)? = null,
    avatar: @Composable () -> Unit,
    action: @Composable () -> Unit,
) {
    val metrics = LegendDesignAuthority.contactCard()
    val shape = RoundedCornerShape(metrics.cornerRadius.dp)
    val interactionModifier = if (onClick != null) modifier.legendPressClickable(onClick, onLongClick) else modifier
    Surface(
        color = LegendColors.ContactNavy,
        shape = shape,
        modifier = interactionModifier
            .fillMaxWidth()
            .shadow(metrics.shadowRadius.dp, shape, ambientColor = LegendColors.Midnight.copy(alpha = metrics.shadowOpacity), spotColor = LegendColors.Midnight.copy(alpha = metrics.shadowOpacity))
            .border(
                metrics.borderWidth.dp,
                LegendColors.Gold.copy(alpha = metrics.borderOpacity),
                shape,
            ),
    ) {
        Row(
            modifier = Modifier
                .fillMaxWidth()
                .heightIn(min = metrics.minimumHeight.dp)
                .padding(horizontal = metrics.horizontalPadding.dp, vertical = metrics.verticalPadding.dp),
            verticalAlignment = Alignment.CenterVertically,
        ) {
            avatar()
            Spacer(Modifier.width(LegendSpacing.Sm))
            Column(
                modifier = Modifier.weight(1f),
                verticalArrangement = Arrangement.spacedBy(2.dp),
            ) {
                Row(verticalAlignment = Alignment.CenterVertically) {
                    Text(
                        displayName,
                        style = LegendTypography.BodyEmphasis,
                        color = LegendColors.OnNavy,
                        maxLines = 1,
                        overflow = TextOverflow.Ellipsis,
                    )
                    if (isVerified) {
                        Spacer(Modifier.width(4.dp))
                        Icon(
                            imageVector = Icons.Default.Verified,
                            contentDescription = legendLocalized("Verified", "accessibility copy"),
                            tint = LegendColors.Verified,
                            modifier = Modifier.size(16.dp),
                        )
                    }
                    nameStatus?.trim()?.takeIf(String::isNotEmpty)?.let {
                        Spacer(Modifier.width(4.dp))
                        Text(
                            it,
                            style = LegendTypography.Caption,
                            color = LegendColors.ContactConnected,
                            maxLines = 1,
                            overflow = TextOverflow.Ellipsis,
                        )
                    }
                }
                statusContent?.invoke()
                subtitle?.trim()?.takeIf(String::isNotEmpty)?.let {
                    Text(
                        it,
                        style = LegendTypography.Supporting,
                        color = LegendColors.OnNavy.copy(alpha = LegendOpacity.ContactSupporting),
                        maxLines = 1,
                        overflow = TextOverflow.Ellipsis,
                    )
                }
                detail?.trim()?.takeIf(String::isNotEmpty)?.let {
                    Text(
                        it,
                        style = LegendTypography.Caption,
                        color = LegendColors.OnNavy.copy(alpha = LegendOpacity.ContactDetail),
                        maxLines = 1,
                        overflow = TextOverflow.Ellipsis,
                    )
                }
            }
            Spacer(Modifier.width(LegendSpacing.Xs))
            action()
        }
    }
}

/** Shared branded heading for page and workflow sections. */
@Composable
fun LegendSectionPill(title: String, detail: String? = null, eyebrow: String? = null) {
    Surface(modifier = Modifier.fillMaxWidth().border(1.dp, LegendColors.Gold.copy(alpha = 0.35f), LegendShapes.Card),
        color = LegendColors.Navy, shape = LegendShapes.Card) {
        Column(Modifier.padding(horizontal = LegendSpacing.Md, vertical = LegendSpacing.Sm), verticalArrangement = Arrangement.spacedBy(LegendSpacing.Micro)) {
            if (!eyebrow.isNullOrBlank() && !title.contains(eyebrow, ignoreCase = true))
                Text(legendLocalized(eyebrow).uppercase(), style = LegendTypography.Eyebrow, color = LegendColors.GoldBright)
            Text(legendLocalized(title), style = LegendTypography.Section, color = LegendColors.OnNavy)
            if (!detail.isNullOrBlank()) Text(legendLocalized(detail), style = LegendTypography.Supporting, color = LegendColors.OnNavy.copy(alpha = 0.72f))
        }
    }
}

/** Press feedback reads the same token resource as SwiftUI. */
fun Modifier.legendPressClickable(onClick: () -> Unit, onLongClick: (() -> Unit)? = null): Modifier = composed {
    val interactions = remember { MutableInteractionSource() }
    val pressed by interactions.collectIsPressedAsState()
    graphicsLayer {
        scaleX = if (pressed) LegendDesignAuthority.opacity("pressedControlScale") else 1f
        scaleY = scaleX
        alpha = if (pressed) LegendDesignAuthority.opacity("pressedSurface") else 1f
    }.combinedClickable(interactionSource = interactions, indication = null, onClick = onClick, onLongClick = onLongClick)
}
