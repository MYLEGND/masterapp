@file:OptIn(ExperimentalMaterial3Api::class)

package com.mylegnd.legend.registered.ui

import androidx.activity.compose.BackHandler
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.layout.widthIn
import androidx.compose.foundation.layout.navigationBarsPadding
import androidx.compose.foundation.layout.statusBarsPadding
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.Send
import androidx.compose.material.icons.filled.Add
import androidx.compose.material.icons.filled.Close
import androidx.compose.material.icons.filled.Menu
import androidx.compose.material.icons.filled.StopCircle
import androidx.compose.material3.Button
import androidx.compose.material3.ButtonDefaults
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.ModalDrawerSheet
import androidx.compose.material3.ModalNavigationDrawer
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.material3.Switch
import androidx.compose.material3.rememberDrawerState
import androidx.compose.material3.DrawerValue
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.draw.shadow
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.layout.ContentScale
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.Dp
import androidx.compose.ui.window.Dialog
import androidx.compose.ui.window.DialogProperties
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import coil3.compose.AsyncImage
import com.mylegnd.legend.registered.core.design.LegendColors
import com.mylegnd.legend.registered.core.design.LegendGradients
import com.mylegnd.legend.registered.core.design.LegendShapes
import com.mylegnd.legend.registered.core.design.LegendSize
import com.mylegnd.legend.registered.core.design.LegendSpacing
import com.mylegnd.legend.registered.core.design.LegendTypography
import com.mylegnd.legend.registered.core.design.legendLocalized
import com.mylegnd.legend.registered.data.LoadState
import com.mylegnd.legend.registered.feature.FounderAiConversationState
import com.mylegnd.legend.registered.feature.FounderAiTranscriptMessage
import com.mylegnd.legend.registered.feature.FounderAiViewModel
import kotlinx.coroutines.launch

private const val FOUNDER_AI_ARTWORK = "file:///android_asset/legendai.png"

/** Compact entry point in the existing authenticated mobile chrome. */
@Composable
fun LegendFounderAiLauncherButton(onClick: () -> Unit, modifier: Modifier = Modifier) {
    IconButton(
        onClick = onClick,
        modifier = modifier
            .size(LegendSize.MinimumTapTarget)
            .clip(CircleShape),
    ) {
        FounderAiMark(LegendSize.MinimumTapTarget)
    }
}

/**
 * A platform-native surface over the server-owned Founder conversation
 * contract. Android keeps no response, mode, or provider authority of its own.
 */
@Composable
fun FounderAiConversationDialog(
    viewModel: FounderAiViewModel,
    onDismiss: () -> Unit,
) {
    val state by viewModel.state.collectAsStateWithLifecycle()
    val drawerState = rememberDrawerState(DrawerValue.Closed)
    val scope = rememberCoroutineScope()
    var mode by remember { mutableStateOf("legend") }
    var nativeOnly by remember { mutableStateOf(false) }
    var draft by remember { mutableStateOf("") }

    LaunchedEffect(Unit) { viewModel.resolveAvailability() }
    BackHandler(enabled = drawerState.isOpen) { scope.launch { drawerState.close() } }

    Dialog(
        onDismissRequest = {
            if (state.isSending) viewModel.cancel()
            onDismiss()
        },
        properties = DialogProperties(usePlatformDefaultWidth = false, decorFitsSystemWindows = false),
    ) {
        ModalNavigationDrawer(
            drawerState = drawerState,
            scrimColor = LegendColors.Midnight.copy(alpha = 0.24f),
            drawerContent = {
                ModalDrawerSheet(
                    modifier = Modifier
                        .widthIn(max = 304.dp)
                        .statusBarsPadding()
                        .navigationBarsPadding()
                        .padding(vertical = 20.dp),
                    drawerContainerColor = LegendColors.Surface,
                    drawerContentColor = LegendColors.TextPrimary,
                ) {
                    FounderAiDrawer(
                        mode = mode,
                        nativeOnly = nativeOnly,
                        canChangeMode = !state.isSending,
                        startNewConversation = {
                            viewModel.startNewConversation()
                            scope.launch { drawerState.close() }
                        },
                        selectMode = { selected ->
                            mode = selected
                            if (selected == "teacher") nativeOnly = false
                            viewModel.startNewConversation()
                        },
                        setNativeOnly = { nativeOnly = it },
                        close = { scope.launch { drawerState.close() } },
                        clear = viewModel::startNewConversation,
                    )
                }
            },
        ) {
            FounderAiConversationContent(
                state = state,
                mode = mode,
                nativeOnly = nativeOnly,
                draft = draft,
                openDrawer = { scope.launch { drawerState.open() } },
                close = {
                    if (state.isSending) viewModel.cancel()
                    onDismiss()
                },
                updateDraft = { draft = it },
                submit = {
                    if (state.isSending) {
                        viewModel.cancel()
                    } else {
                        viewModel.send(draft, mode, nativeOnly)
                        draft = ""
                    }
                },
            )
        }
    }
}

@Composable
private fun FounderAiConversationContent(
    state: FounderAiConversationState,
    mode: String,
    nativeOnly: Boolean,
    draft: String,
    openDrawer: () -> Unit,
    close: () -> Unit,
    updateDraft: (String) -> Unit,
    submit: () -> Unit,
) {
    val available = (state.availability as? LoadState.Data)?.value == true
    Box(
        modifier = Modifier
            .fillMaxSize()
            .background(LegendColors.Canvas),
    ) {
        Column(
            Modifier
                .fillMaxSize()
                .statusBarsPadding()
                .navigationBarsPadding(),
        ) {
            Row(
                modifier = Modifier
                    .fillMaxWidth()
                    .height(84.dp)
                    .background(LegendGradients.Hero)
                    .padding(horizontal = LegendSpacing.Md, vertical = 12.dp),
                verticalAlignment = Alignment.CenterVertically,
            ) {
                FounderAiMark(52.dp)
                Spacer(Modifier.width(LegendSpacing.Sm))
                Text(
                    legendLocalized("Legend® Ai"),
                    style = LegendTypography.Wordmark,
                    color = LegendColors.OnNavy,
                    modifier = Modifier.weight(1f),
                    maxLines = 1,
                    overflow = TextOverflow.Ellipsis,
                )
                FounderAiHeaderAction(Icons.Default.Menu, "Open conversation menu", openDrawer)
                Spacer(Modifier.width(LegendSpacing.Xs))
                FounderAiHeaderAction(Icons.Default.Close, "Close Founder AI", close)
            }

            LazyColumn(
                modifier = Modifier
                    .weight(1f)
                    .fillMaxWidth(),
                contentPadding = PaddingValues(
                    horizontal = LegendSpacing.Md,
                    vertical = LegendSpacing.Lg,
                ),
                verticalArrangement = Arrangement.spacedBy(LegendSpacing.Sm),
            ) {
                when (val access = state.availability) {
                    is LoadState.Loading, LoadState.Idle -> item { FounderAiStatusCard("Checking Founder AI access…") }
                    is LoadState.Error -> item { FounderAiStatusCard(access.message, isError = true) }
                    is LoadState.Data -> if (!access.value) item { FounderAiStatusCard("Founder AI is unavailable for this account.", isError = true) }
                }
                if (state.messages.isEmpty()) {
                    item {
                        Column(
                            modifier = Modifier
                                .fillMaxWidth()
                                .padding(top = LegendSpacing.Xl)
                                .shadow(18.dp, LegendShapes.ProminentCard)
                                .clip(LegendShapes.ProminentCard)
                                .background(LegendColors.Surface)
                                .border(1.dp, LegendColors.Divider, LegendShapes.ProminentCard)
                                .padding(horizontal = LegendSpacing.Lg, vertical = LegendSpacing.Xl),
                            horizontalAlignment = Alignment.CenterHorizontally,
                            verticalArrangement = Arrangement.spacedBy(LegendSpacing.Sm),
                        ) {
                            FounderAiMark(LegendSize.AvatarLarge)
                            Text(
                                legendLocalized("FOUNDER INTELLIGENCE"),
                                style = LegendTypography.Eyebrow,
                                color = LegendColors.Gold,
                            )
                            Text(
                                legendLocalized(
                                    "Ask {provider}",
                                    mapOf("provider" to if (mode == "teacher") "OpenAI" else "Legend® Ai"),
                                ),
                                style = LegendTypography.Display,
                                color = LegendColors.Navy,
                            )
                            Text(
                                if (mode == "teacher") legendLocalized("A direct Founder-to-OpenAI conversation through the governed application authority.")
                                else legendLocalized("A governed conversation for inspecting knowledge, evidence, readiness, and the next legitimate learning step."),
                                style = LegendTypography.Body,
                                color = LegendColors.TextSecondary,
                                textAlign = TextAlign.Center,
                            )
                        }
                    }
                } else {
                    items(state.messages) { message -> FounderAiMessageBubble(message) }
                }
                state.progress?.let { progress -> item { FounderAiStatusCard(progress) } }
                state.failure?.let { failure -> item { FounderAiStatusCard(failure, isError = true) } }
            }

            HorizontalDivider(color = LegendColors.Divider)
            Row(
                modifier = Modifier
                    .fillMaxWidth()
                    .padding(horizontal = LegendSpacing.Md, vertical = LegendSpacing.Sm),
                verticalAlignment = Alignment.Bottom,
                horizontalArrangement = Arrangement.spacedBy(LegendSpacing.Sm),
            ) {
                OutlinedTextField(
                    value = draft,
                    onValueChange = updateDraft,
                    modifier = Modifier.weight(1f),
                    enabled = available,
                    minLines = 1,
                    maxLines = 4,
                    placeholder = {
                        Text(
                            if (mode == "teacher") legendLocalized("Message OpenAI…")
                            else legendLocalized("Message Legend® Ai…"),
                        )
                    },
                    shape = LegendShapes.Control,
                )
                Button(
                    onClick = submit,
                    enabled = if (state.isSending) true else available && draft.isNotBlank(),
                    modifier = Modifier.size(LegendSize.ControlHeight),
                    shape = CircleShape,
                    colors = ButtonDefaults.buttonColors(
                        containerColor = if (state.isSending) LegendColors.Error else LegendColors.Gold,
                        contentColor = if (state.isSending) LegendColors.OnNavy else LegendColors.OnGold,
                    ),
                    contentPadding = PaddingValues(0.dp),
                ) {
                    Icon(
                        if (state.isSending) Icons.Default.StopCircle else Icons.AutoMirrored.Filled.Send,
                        if (state.isSending) legendLocalized("Stop response", "accessibility copy")
                        else legendLocalized("Send message", "accessibility copy"),
                    )
                }
            }
        }
    }
}

@Composable
private fun FounderAiHeaderAction(
    icon: ImageVector,
    contentDescription: String,
    action: () -> Unit,
) {
    IconButton(
        onClick = action,
        modifier = Modifier
            .size(LegendSize.CompactControlHeight)
            .clip(CircleShape)
            .background(LegendColors.OnNavy.copy(alpha = 0.09f))
            .border(1.dp, LegendColors.GoldBright.copy(alpha = 0.44f), CircleShape),
    ) {
        Icon(icon, contentDescription, tint = LegendColors.GoldBright)
    }
}

@Composable
private fun FounderAiDrawer(
    mode: String,
    nativeOnly: Boolean,
    canChangeMode: Boolean,
    startNewConversation: () -> Unit,
    selectMode: (String) -> Unit,
    setNativeOnly: (Boolean) -> Unit,
    close: () -> Unit,
    clear: () -> Unit,
) {
    Column(
        modifier = Modifier
            .fillMaxSize()
            .padding(horizontal = 14.dp, vertical = 10.dp),
        verticalArrangement = Arrangement.spacedBy(10.dp),
    ) {
        Row(verticalAlignment = Alignment.CenterVertically) {
            Column(Modifier.weight(1f)) {
                Text(legendLocalized("Founder space"), style = LegendTypography.Label, color = LegendColors.Gold)
                Text(legendLocalized("Conversations"), style = LegendTypography.Section, color = LegendColors.Navy)
            }
            IconButton(
                onClick = close,
                modifier = Modifier
                    .size(LegendSize.CompactControlHeight)
                    .border(1.dp, LegendColors.Divider, CircleShape),
            ) { Icon(Icons.Default.Close, legendLocalized("Close conversations", "accessibility copy"), tint = LegendColors.Navy) }
        }
        Button(
            onClick = startNewConversation,
            modifier = Modifier.fillMaxWidth().heightIn(min = 40.dp),
            shape = LegendShapes.Compact,
            colors = ButtonDefaults.buttonColors(containerColor = LegendColors.GoldBright, contentColor = LegendColors.OnGold),
        ) {
            Icon(Icons.Default.Add, null)
            Spacer(Modifier.width(LegendSpacing.Xs))
            Text(legendLocalized("New conversation"), style = LegendTypography.Label, fontWeight = FontWeight.Bold)
        }
        Text(legendLocalized("Responder"), style = LegendTypography.Label, color = LegendColors.TextTertiary)
        Row(horizontalArrangement = Arrangement.spacedBy(LegendSpacing.Xs)) {
            FounderAiModeButton(
                label = "Legend® Ai",
                selected = mode == "legend",
                enabled = canChangeMode,
                color = LegendColors.Success,
                modifier = Modifier.weight(1f),
                select = { selectMode("legend") },
            )
            FounderAiModeButton(
                label = "OpenAI",
                selected = mode == "teacher",
                enabled = canChangeMode,
                color = LegendColors.Royal,
                modifier = Modifier.weight(1f),
                select = { selectMode("teacher") },
            )
        }
        if (mode == "legend") {
            Row(
                modifier = Modifier
                    .fillMaxWidth()
                    .clip(LegendShapes.Compact)
                    .background(LegendColors.SurfaceInset)
                    .border(1.dp, LegendColors.Divider, LegendShapes.Compact)
                    .padding(horizontal = LegendSpacing.Sm, vertical = 6.dp),
                verticalAlignment = Alignment.CenterVertically,
            ) {
                Column(Modifier.weight(1f)) {
                    Text(legendLocalized("Native-only"), style = LegendTypography.Label, color = LegendColors.TextPrimary, fontWeight = FontWeight.Bold)
                    Text(legendLocalized("Block OpenAI escalation for this LEGEND test."), style = LegendTypography.Label, color = LegendColors.TextSecondary)
                }
                Switch(
                    checked = nativeOnly,
                    enabled = canChangeMode,
                    onCheckedChange = setNativeOnly,
                )
            }
        }
        HorizontalDivider(color = LegendColors.Divider, modifier = Modifier.padding(vertical = LegendSpacing.Xs))
        Text(legendLocalized("Recent"), style = LegendTypography.Label, color = LegendColors.TextTertiary)
        Text(legendLocalized("The active conversation is retained in this session."), style = LegendTypography.Label, color = LegendColors.TextSecondary)
        Spacer(Modifier.weight(1f))
        OutlinedButton(
            onClick = clear,
            enabled = canChangeMode,
            modifier = Modifier.fillMaxWidth().heightIn(min = 38.dp),
            shape = LegendShapes.Compact,
        ) { Text(legendLocalized("Clear conversation"), style = LegendTypography.Body) }
    }
}

@Composable
private fun FounderAiModeButton(
    label: String,
    selected: Boolean,
    enabled: Boolean,
    color: Color,
    modifier: Modifier,
    select: () -> Unit,
) {
    if (selected) {
        Button(
            onClick = select,
            modifier = modifier.heightIn(min = 34.dp),
            enabled = enabled,
            shape = LegendShapes.Compact,
            colors = ButtonDefaults.buttonColors(containerColor = color, contentColor = LegendColors.OnNavy),
            contentPadding = PaddingValues(horizontal = LegendSpacing.Xs),
        ) { Text(label, maxLines = 1, overflow = TextOverflow.Ellipsis) }
    } else {
        OutlinedButton(
            onClick = select,
            modifier = modifier.heightIn(min = 34.dp),
            enabled = enabled,
            shape = LegendShapes.Compact,
            contentPadding = PaddingValues(horizontal = LegendSpacing.Xs),
        ) { Text(label, maxLines = 1, overflow = TextOverflow.Ellipsis) }
    }
}

@Composable
private fun FounderAiMessageBubble(message: FounderAiTranscriptMessage) {
    val isUser = message.role == "user"
    val authority = message.responseAuthority?.trim()
    if (isUser) {
        Row(
            modifier = Modifier.fillMaxWidth(),
            horizontalArrangement = Arrangement.End,
        ) {
            Text(
                message.content,
                style = LegendTypography.Body,
                color = LegendColors.OnGold,
                fontWeight = FontWeight.SemiBold,
                modifier = Modifier
                    .widthIn(max = 320.dp)
                    .shadow(8.dp, LegendShapes.Card)
                    .clip(LegendShapes.Card)
                    .background(LegendGradients.Gold)
                    .border(1.dp, LegendColors.Gold.copy(alpha = 0.62f), LegendShapes.Card)
                    .padding(horizontal = LegendSpacing.Sm, vertical = LegendSpacing.Xs),
            )
        }
    } else {
        Row(
            modifier = Modifier.fillMaxWidth(),
            horizontalArrangement = Arrangement.spacedBy(LegendSpacing.Xs),
            verticalAlignment = Alignment.Top,
        ) {
            FounderAiMark(28.dp)
            Column(
                modifier = Modifier
                    .weight(1f, fill = false)
                    .widthIn(max = 520.dp)
                    .shadow(8.dp, LegendShapes.Card)
                    .clip(LegendShapes.Card)
                    .background(LegendColors.AiResponseRoyal)
                    .border(1.dp, LegendColors.OnNavy.copy(alpha = 0.24f), LegendShapes.Card)
                    .padding(horizontal = LegendSpacing.Sm, vertical = LegendSpacing.Sm),
                verticalArrangement = Arrangement.spacedBy(LegendSpacing.Xs),
            ) {
                Text(message.content, style = LegendTypography.Body, color = LegendColors.OnNavy)
                val authorityLabel = when (authority) {
                    "LegendAi" -> legendLocalized("Legend® Ai")
                    "GovernedResearch" -> legendLocalized("LEGEND governed research")
                    "OpenAITeacher" -> legendLocalized("OpenAI")
                    "SystemDiagnostic" -> legendLocalized("System diagnostic")
                    else -> null
                }
                if (authorityLabel != null) {
                    Text(
                        authorityLabel,
                        style = LegendTypography.Label,
                        color = LegendColors.OnNavy,
                        fontWeight = FontWeight.Bold,
                        modifier = Modifier
                            .background(LegendColors.OnNavy.copy(alpha = 0.14f), CircleShape)
                            .border(1.dp, LegendColors.OnNavy.copy(alpha = 0.26f), CircleShape)
                            .padding(horizontal = LegendSpacing.Xs, vertical = LegendSpacing.Micro),
                    )
                }
            }
        }
    }
}

@Composable
private fun FounderAiStatusCard(message: String, isError: Boolean = false) {
    Text(
        legendLocalized(message),
        style = LegendTypography.Body,
        color = if (isError) LegendColors.Error else LegendColors.TextSecondary,
        modifier = Modifier
            .fillMaxWidth()
            .clip(LegendShapes.Compact)
            .background(if (isError) LegendColors.Error.copy(alpha = 0.08f) else LegendColors.SurfaceInset)
            .padding(LegendSpacing.Sm),
    )
}

@Composable
private fun FounderAiMark(size: Dp) {
    AsyncImage(
        model = FOUNDER_AI_ARTWORK,
        contentDescription = legendLocalized("Legend® Ai", "accessibility copy"),
        contentScale = ContentScale.Crop,
        modifier = Modifier
            .size(size)
            .clip(CircleShape),
    )
}
