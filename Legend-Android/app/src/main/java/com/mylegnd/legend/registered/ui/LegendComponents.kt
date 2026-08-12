@file:OptIn(ExperimentalMaterial3Api::class)

package com.mylegnd.legend.registered.ui

import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.combinedClickable
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Verified
import androidx.compose.material3.*
import androidx.compose.runtime.Composable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.text.style.TextOverflow
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

/**
 * Android implementation of iOS's LegendContactCard.  Every compact person or
 * conversation presentation shares this surface, typography, and gold border
 * rather than creating an inbox-specific visual language.
 */
@Composable
fun LegendContactCard(
    displayName: String,
    nameStatus: String? = null,
    subtitle: String? = null,
    detail: String? = null,
    isVerified: Boolean = false,
    modifier: Modifier = Modifier,
    onClick: (() -> Unit)? = null,
    onLongClick: (() -> Unit)? = null,
    avatar: @Composable () -> Unit,
    action: @Composable () -> Unit,
) {
    val interactionModifier = when {
        onClick != null && onLongClick != null -> modifier.combinedClickable(
            onClick = onClick,
            onLongClick = onLongClick,
        )
        onClick != null -> modifier.clickable(onClick = onClick)
        else -> modifier
    }
    Surface(
        color = LegendColors.ContactNavy,
        shape = LegendShapes.Control,
        shadowElevation = 7.dp,
        modifier = interactionModifier
            .fillMaxWidth()
            .border(
                LegendSpacing.Hairline,
                LegendColors.Gold.copy(alpha = LegendOpacity.ContactBorder),
                LegendShapes.Control,
            ),
    ) {
        Row(
            modifier = Modifier
                .fillMaxWidth()
                .heightIn(min = 64.dp)
                .padding(horizontal = LegendSpacing.Sm, vertical = LegendSpacing.Xs),
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
                            contentDescription = "Verified",
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
