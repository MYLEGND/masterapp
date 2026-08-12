@file:OptIn(ExperimentalMaterial3Api::class)

package com.mylegnd.legend.registered.ui

import android.app.Activity
import android.Manifest
import android.content.Intent
import android.content.pm.PackageManager
import android.os.Build
import android.net.Uri
import android.webkit.WebResourceRequest
import android.webkit.WebResourceResponse
import android.webkit.WebView
import android.webkit.WebViewClient
import androidx.activity.compose.LocalActivity
import androidx.activity.compose.BackHandler
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.PickVisualMediaRequest
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.LazyRow
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.pager.VerticalPager
import androidx.compose.foundation.pager.HorizontalPager
import androidx.compose.foundation.pager.rememberPagerState
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.Logout
import androidx.compose.material.icons.automirrored.filled.Send
import androidx.compose.material.icons.automirrored.filled.ArrowBack
import androidx.compose.material.icons.automirrored.filled.MenuBook
import androidx.compose.material.icons.automirrored.filled.Reply
import androidx.compose.material.icons.automirrored.filled.TrendingUp
import androidx.compose.material.icons.filled.*
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Brush
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.compose.ui.viewinterop.AndroidView
import androidx.compose.ui.window.Dialog
import androidx.compose.ui.window.DialogProperties
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import androidx.lifecycle.viewmodel.compose.viewModel
import androidx.core.content.ContextCompat
import com.mylegnd.legend.registered.LegendContainer
import com.mylegnd.legend.registered.LegendViewModelFactory
import com.mylegnd.legend.registered.core.design.LegendColors
import com.mylegnd.legend.registered.core.design.LegendCopy
import com.mylegnd.legend.registered.core.design.LegendGradients
import com.mylegnd.legend.registered.core.design.LegendOpacity
import com.mylegnd.legend.registered.core.design.LegendShapes
import com.mylegnd.legend.registered.core.design.LegendSize
import com.mylegnd.legend.registered.core.design.LegendSpacing
import com.mylegnd.legend.registered.core.design.LegendSocialFormats
import com.mylegnd.legend.registered.core.design.LegendTypography
import com.mylegnd.legend.registered.core.model.*
import com.mylegnd.legend.registered.core.media.AuthenticatedMediaRepository
import com.mylegnd.legend.registered.core.media.LegendProtectedAvatar
import com.mylegnd.legend.registered.core.media.LegendProtectedSocialMedia
import com.mylegnd.legend.registered.core.media.LegendLocalVideoPreview
import com.mylegnd.legend.registered.core.media.legendDisplayName
import com.mylegnd.legend.registered.core.network.DiscoveryPage
import com.mylegnd.legend.registered.core.network.DiscoveryResult
import com.mylegnd.legend.registered.core.network.JourneyDashboard
import com.mylegnd.legend.registered.core.network.JourneyConnection
import com.mylegnd.legend.registered.core.network.JourneyProfileInput
import com.mylegnd.legend.registered.core.network.NotificationItem
import com.mylegnd.legend.registered.core.network.NotificationSnapshot
import com.mylegnd.legend.registered.core.realtime.LegendRealtimeEvents
import com.mylegnd.legend.registered.core.session.ActiveLegendSession
import com.mylegnd.legend.registered.core.session.SessionState
import com.mylegnd.legend.registered.core.session.SessionViewModel
import com.mylegnd.legend.registered.data.FinancialRepository
import com.mylegnd.legend.registered.data.LoadState
import com.mylegnd.legend.registered.feature.*
import kotlinx.coroutines.flow.collectLatest
import kotlinx.coroutines.launch
import coil3.compose.AsyncImage

@Composable
fun LegendRoot(sessionViewModel: SessionViewModel, container: LegendContainer) {
    val state by sessionViewModel.state.collectAsStateWithLifecycle()
    val context = LocalContext.current
    val notificationPermission = rememberLauncherForActivityResult(
        ActivityResultContracts.RequestPermission(),
    ) { }
    when (state) {
        SessionState.Loading,
        SessionState.Authenticating -> LegendLoadingState()

        SessionState.ConfigurationRequired -> LegendEmptyState(
            "Native mobile configuration required",
            "This build is waiting for LEGEND® environment configuration before secure sign-in can begin.",
        )

        SessionState.SignedOut -> SignInScreen(sessionViewModel::signIn)
        is SessionState.RoleSelection -> RoleSelectionScreen(
            roles = (state as SessionState.RoleSelection).roles,
            select = sessionViewModel::selectRole,
        )

        is SessionState.Failure -> LegendErrorState(
            (state as SessionState.Failure).message,
            sessionViewModel::restore,
        )

        is SessionState.Authenticated -> {
            val session = (state as SessionState.Authenticated).session
            LaunchedEffect(session.actor.identity.userId) {
                if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU &&
                    ContextCompat.checkSelfPermission(context, Manifest.permission.POST_NOTIFICATIONS) != PackageManager.PERMISSION_GRANTED
                ) {
                    notificationPermission.launch(Manifest.permission.POST_NOTIFICATIONS)
                }
            }
            AuthenticatedShell(
                session = session,
                container = container,
                signOut = sessionViewModel::signOut,
                switchRole = sessionViewModel::selectRole,
            )
        }
    }
}

@Composable
private fun SignInScreen(onSignIn: (Activity) -> Unit) {
    val activity = LocalActivity.current
    Column(
        Modifier.fillMaxSize().background(LegendColors.Canvas).padding(horizontal = LegendSpacing.PageHorizontal, vertical = LegendSpacing.Xxl),
        horizontalAlignment = Alignment.CenterHorizontally,
        verticalArrangement = Arrangement.Center,
    ) {
        LegendBrandArtwork()
        Spacer(Modifier.height(LegendSpacing.Xl))
        Text("Secure sign in", style = LegendTypography.Hero, color = LegendColors.TextPrimary)
        Spacer(Modifier.height(LegendSpacing.Xs))
        Text(
            "Verify your LEGEND account to continue.",
            style = LegendTypography.Supporting,
            color = LegendColors.TextSecondary,
        )
        Spacer(Modifier.height(LegendSpacing.Xl))
        LegendPrimaryButton("Sign in securely", modifier = Modifier.fillMaxWidth(), enabled = activity != null) {
            activity?.let(onSignIn)
        }
    }
}

/** The same iOS-owned artwork bundled by Gradle; Android keeps no forked logo file. */
@Composable
private fun LegendBrandArtwork(modifier: Modifier = Modifier) {
    AsyncImage(
        model = "file:///android_asset/legend-logo.png",
        contentDescription = "LEGEND®",
        contentScale = androidx.compose.ui.layout.ContentScale.Fit,
        modifier = modifier.size(96.dp),
    )
}

@Composable
private fun RoleSelectionScreen(roles: List<String>, select: (String) -> Unit) {
    Column(
        Modifier.fillMaxSize().background(LegendColors.Canvas).padding(horizontal = LegendSpacing.PageHorizontal, vertical = LegendSpacing.Xxl),
        verticalArrangement = Arrangement.Center,
    ) {
        Text("LEGEND ACCOUNT", style = LegendTypography.Eyebrow, color = LegendColors.Gold)
        Text("Choose your experience", style = LegendTypography.Hero, color = LegendColors.TextPrimary)
        Spacer(Modifier.height(LegendSpacing.Xs))
        Text("Choose the account you want to use. LEGEND will reopen this account next time.", style = LegendTypography.Supporting, color = LegendColors.TextSecondary)
        Spacer(Modifier.height(LegendSpacing.Lg))
        Text("Available workspaces", style = LegendTypography.Section, color = LegendColors.TextPrimary)
        Spacer(Modifier.height(LegendSpacing.Sm))
        roles.forEach { role ->
            Surface(
                color = LegendColors.SurfaceInset,
                shape = LegendShapes.Control,
                modifier = Modifier.fillMaxWidth().padding(bottom = LegendSpacing.Xs).clickable { select(role) },
            ) {
                Row(Modifier.padding(LegendSpacing.Md), verticalAlignment = Alignment.CenterVertically) {
                    Icon(if (role.equals("Agent", ignoreCase = true)) Icons.Default.BusinessCenter else Icons.Default.Person, null, tint = LegendColors.Gold)
                    Spacer(Modifier.width(LegendSpacing.Sm))
                    Text("Continue as $role", style = LegendTypography.CardTitle, color = LegendColors.TextPrimary, modifier = Modifier.weight(1f))
                    Icon(Icons.Default.ChevronRight, null, tint = LegendColors.Gold)
                }
            }
        }
    }
}

private enum class LegendTab(private val copyKey: String) {
    HOME("tab.home"),
    DISCOVER("tab.discover"),
    SOCIAL("tab.forYou"),
    MESSAGES("tab.messages"),
    ACCOUNT("tab.account");

    val label get() = LegendCopy.value(copyKey)
}

/**
 * The one presentation route for social sharing.  It deliberately carries no
 * recipient, conversation, or message cache: those remain in MessagingViewModel
 * and its existing repository, just as the iOS global share control does.
 */
private val LocalLegendSocialShare = staticCompositionLocalOf<(SocialPost) -> Unit> { {} }

/** A shell event, not a second home controller. Home remains the owner of both sheets. */
private enum class LegendHomeChromeAction { CREATE, NOTIFICATIONS }

@Composable
private fun LegendPillNavigation(
    selection: LegendTab,
    unreadMessageCount: Int,
    accountName: String,
    accountAvatar: MobileAvatar?,
    mediaRepository: AuthenticatedMediaRepository,
    participantType: String,
    select: (LegendTab) -> Unit,
) {
    Box(
        modifier = Modifier
            .fillMaxWidth()
            .background(LegendColors.Canvas)
            .padding(
                start = LegendSpacing.Sm,
                end = LegendSpacing.Sm,
                bottom = 4.dp,
            ),
    ) {
        Row(
            modifier = Modifier
                .fillMaxWidth()
                .heightIn(min = LegendSize.ProminentControlHeight)
                .clip(CircleShape)
                .background(LegendGradients.Hero)
                .padding(horizontal = LegendSpacing.Xs, vertical = LegendSpacing.Micro),
            verticalAlignment = Alignment.CenterVertically,
        ) {
            LegendTab.entries.forEach { tab ->
                val selected = selection == tab
                Box(
                    modifier = Modifier.weight(1f),
                    contentAlignment = Alignment.Center,
                ) {
                    IconButton(onClick = { select(tab) }, modifier = Modifier.size(LegendSize.MinimumTapTarget)) {
                        if (tab == LegendTab.ACCOUNT) {
                            LegendProtectedAvatar(
                                avatar = accountAvatar,
                                displayName = accountName,
                                participantType = participantType,
                                repository = mediaRepository,
                                size = LegendSize.AvatarMedium,
                            )
                        } else {
                            Icon(
                                imageVector = legendTabIcon(tab, selected),
                                contentDescription = tab.label,
                                tint = if (selected) LegendColors.GoldBright else LegendColors.OnNavy.copy(alpha = 0.88f),
                            )
                        }
                    }
                    if (tab == LegendTab.MESSAGES && unreadMessageCount > 0) {
                        Text(
                            text = unreadMessageCount.coerceAtMost(99).toString(),
                            style = LegendTypography.Eyebrow,
                            color = LegendColors.OnNavy,
                            modifier = Modifier
                                .align(Alignment.TopEnd)
                                .offset(x = -LegendSpacing.Xs, y = LegendSpacing.Micro)
                                .background(LegendColors.Error, CircleShape)
                                .padding(horizontal = LegendSpacing.Xs, vertical = LegendSpacing.Micro),
                        )
                    }
                }
            }
        }
    }
}

private fun legendTabIcon(tab: LegendTab, selected: Boolean) = when (tab) {
    LegendTab.HOME -> Icons.Default.Home
    LegendTab.DISCOVER -> Icons.Default.Search
    LegendTab.SOCIAL -> Icons.Default.VideoLibrary
    LegendTab.MESSAGES -> if (selected) Icons.Default.ChatBubble else Icons.Default.ChatBubbleOutline
    LegendTab.ACCOUNT -> Icons.Default.Person
}

@Composable
private fun AuthenticatedShell(
    session: ActiveLegendSession,
    container: LegendContainer,
    signOut: () -> Unit,
    switchRole: (String) -> Unit,
) {
    var tab by remember { mutableStateOf(LegendTab.HOME) }
    var homeChromeAction by remember { mutableStateOf<LegendHomeChromeAction?>(null) }
    var requestedConversationId by remember { mutableStateOf<String?>(null) }
    var isMessageThreadOpen by remember { mutableStateOf(false) }
    var sharingPost by remember { mutableStateOf<SocialPost?>(null) }
    val notificationDestination by container.notificationNavigation.destination.collectAsStateWithLifecycle()
    val participantType = session.actor.identity.participantType
    val home: HomeViewModel = viewModel(
        factory = LegendViewModelFactory { HomeViewModel(container.homeRepository, participantType) },
    )
    val agentWorkspace: AgentWorkspaceViewModel = viewModel(
        factory = LegendViewModelFactory { AgentWorkspaceViewModel(container.agentWorkspaceRepository, participantType) },
    )
    val social: SocialViewModel = viewModel(
        factory = LegendViewModelFactory { SocialViewModel(container.socialRepository, participantType) },
    )
    val discovery: DiscoveryViewModel = viewModel(
        factory = LegendViewModelFactory {
            DiscoveryViewModel(container.discoveryRepository, container.journeyRepository, container.communityRepository, participantType)
        },
    )
    val messages: MessagingViewModel = viewModel(
        factory = LegendViewModelFactory { MessagingViewModel(container.messagingRepository, participantType) },
    )
    val account: AccountViewModel = viewModel(
        factory = LegendViewModelFactory { AccountViewModel(container.accountRepository, participantType) },
    )
    val founderAccounts: FounderAccountsViewModel = viewModel(
        factory = LegendViewModelFactory { FounderAccountsViewModel(container.founderAccountRepository, participantType) },
    )
    val controlledResources: ControlledResourceViewModel = viewModel(
        factory = LegendViewModelFactory { ControlledResourceViewModel(container.messagingRepository, participantType) },
    )
    val dailyScriptureManagement: DailyScriptureManagementViewModel = viewModel(
        factory = LegendViewModelFactory { DailyScriptureManagementViewModel(container.dailyScriptureManagementRepository, participantType) },
    )
    val communitySafety: CommunitySafetyReviewViewModel = viewModel(
        factory = LegendViewModelFactory { CommunitySafetyReviewViewModel(container.communityRepository, participantType) },
    )
    val notifications: NotificationsViewModel = viewModel(
        factory = LegendViewModelFactory { NotificationsViewModel(container.notificationRepository, participantType) },
    )
    val messagingRealtime = remember(participantType) { container.messagingRealtime(participantType) }
    DisposableEffect(messagingRealtime) {
        // Match iOS bootstrap behavior: keep the sanctioned server stream
        // available for the authenticated account, not only while its
        // Messages tab happens to be visible.
        messagingRealtime.start()
        onDispose { messagingRealtime.close() }
    }
    val homeState by home.state.collectAsStateWithLifecycle()
    val notificationState by notifications.state.collectAsStateWithLifecycle()
    val notificationCount = (notificationState as? LoadState.Data<NotificationSnapshot>)?.value?.badge?.unreadCount ?: 0
    LaunchedEffect(participantType) { container.fcmPushRegistration.registerForAuthenticatedActor(participantType) }
    LaunchedEffect(messages, notifications, home) {
        LegendRealtimeEvents.events.collectLatest { event ->
            notifications.applyRealtime(event)
            messages.reconcileRealtime(event)
            if (event.conversationId != null) home.refreshForRealtime()
        }
    }
    LaunchedEffect(notificationDestination) {
        val destination = notificationDestination ?: return@LaunchedEffect
        destination.conversationId?.let {
            tab = LegendTab.MESSAGES
            requestedConversationId = it
        }
        container.notificationNavigation.markHandled(destination)
    }

    CompositionLocalProvider(LocalLegendSocialShare provides { post -> sharingPost = post }) {
    Scaffold(
        topBar = {
            // A thread alone hides chrome. A stale detail callback must never
            // hide another tab's stationary iOS-equivalent wordmark.
            if (tab != LegendTab.MESSAGES || !isMessageThreadOpen) {
                LegendHomeBrandBar(
                    notificationCount = notificationCount,
                    create = if (tab == LegendTab.HOME) {
                        { homeChromeAction = LegendHomeChromeAction.CREATE }
                    } else {
                        null
                    },
                    notifications = if (tab == LegendTab.HOME) {
                        { homeChromeAction = LegendHomeChromeAction.NOTIFICATIONS }
                    } else {
                        null
                    },
                    showsHomeActions = tab == LegendTab.HOME,
                    usesDarkSurface = tab == LegendTab.DISCOVER,
                )
            }
        },
        bottomBar = {
            if (tab != LegendTab.MESSAGES || !isMessageThreadOpen) {
                LegendPillNavigation(
                    selection = tab,
                    unreadMessageCount = (homeState as? LoadState.Data<MobileHomeResponse>)?.value?.messaging?.unreadCount ?: 0,
                    accountName = session.actor.displayName,
                    accountAvatar = session.actor.avatar,
                    mediaRepository = container.authenticatedMediaRepository,
                    participantType = participantType,
                    select = { tab = it },
                )
            }
        },
        containerColor = LegendColors.Canvas,
    ) { padding ->
        Box(Modifier.padding(padding)) {
            when (tab) {
                LegendTab.HOME -> HomeScreen(
                    homeViewModel = home,
                    agentWorkspaceViewModel = agentWorkspace,
                    socialViewModel = social,
                    notificationsViewModel = notifications,
                    mediaRepository = container.authenticatedMediaRepository,
                    participantType = participantType,
                    currentActor = session.actor,
                    chromeAction = homeChromeAction,
                    onChromeActionHandled = { homeChromeAction = null },
                    openSocial = { tab = LegendTab.SOCIAL },
                    openConversation = { conversationId ->
                        messages.load()
                        requestedConversationId = conversationId
                        tab = LegendTab.MESSAGES
                    },
                )
                LegendTab.DISCOVER -> DiscoverScreen(discovery, social, container.authenticatedMediaRepository, participantType)
                LegendTab.SOCIAL -> SocialScreen(social, container.authenticatedMediaRepository, participantType)
                LegendTab.MESSAGES -> MessagesScreen(
                    viewModel = messages,
                    mediaRepository = container.authenticatedMediaRepository,
                    participantType = participantType,
                    requestedConversationId = requestedConversationId,
                    onRequestedConversationOpened = { requestedConversationId = null },
                    onThreadOpenChanged = { isMessageThreadOpen = it },
                )
                LegendTab.ACCOUNT -> AccountScreen(
                    viewModel = account,
                    socialViewModel = social,
                    founderAccountsViewModel = founderAccounts,
                    controlledResourceViewModel = controlledResources,
                    dailyScriptureManagementViewModel = dailyScriptureManagement,
                    communitySafetyViewModel = communitySafety,
                    isFounder = session.capabilities.isFounder,
                    canManageScripture = session.capabilities.canManageScripture,
                    canManageCommunity = session.capabilities.canManageCommunity,
                    financialRepository = container.financialRepository,
                    mediaRepository = container.authenticatedMediaRepository,
                    participantType = participantType,
                    alternateParticipantTypes = session.permittedParticipantTypes
                        .filterNot { it.equals(participantType, ignoreCase = true) },
                    switchRole = switchRole,
                    signOut = signOut,
                )
            }
        }
    }
    }
    sharingPost?.let { post ->
        LegendGlobalSocialShareSheet(
            post = post,
            messaging = messages,
            social = social,
            mediaRepository = container.authenticatedMediaRepository,
            participantType = participantType,
            dismiss = { sharingPost = null },
        )
    }
}

@Composable
private fun DiscoverScreen(viewModel: DiscoveryViewModel, socialViewModel: SocialViewModel, mediaRepository: AuthenticatedMediaRepository, participantType: String) {
    val page by viewModel.page.collectAsStateWithLifecycle()
    val journey by viewModel.journeyState.collectAsStateWithLifecycle()
    val profile by viewModel.profile.collectAsStateWithLifecycle()
    var query by remember { mutableStateOf("") }
    var safetyTarget by remember { mutableStateOf<DiscoveryResult?>(null) }
    var selectedProfile by remember { mutableStateOf<DiscoveryResult?>(null) }
    var editingJourney by remember { mutableStateOf(false) }
    LaunchedEffect(Unit) { viewModel.load() }
    LaunchedEffect(query) {
        kotlinx.coroutines.delay(120)
        viewModel.search(query)
    }
    when (page) {
        LoadState.Idle, LoadState.Loading -> LegendLoadingState()
        is LoadState.Error -> LegendErrorState((page as LoadState.Error).message, viewModel::load)
        is LoadState.Data -> {
            val snapshot = (page as LoadState.Data<DiscoveryPage>).value
            LazyColumn(
                modifier = Modifier.fillMaxSize().background(LegendColors.Midnight),
                verticalArrangement = Arrangement.spacedBy(LegendSpacing.Xs),
                contentPadding = PaddingValues(horizontal = LegendSpacing.PageHorizontal, vertical = LegendSpacing.Md),
            ) {
                item {
                    Row(verticalAlignment = Alignment.CenterVertically) {
                        OutlinedTextField(
                            value = query,
                            onValueChange = { query = it },
                            modifier = Modifier.weight(1f),
                            placeholder = { Text(if (snapshot.scope == "OwnedClients") "Search clients and agents" else "Search people, goals, interests", color = LegendColors.OnNavy.copy(alpha = 0.66f)) },
                            leadingIcon = { Icon(Icons.Default.Search, null, tint = LegendColors.GoldBright) },
                            trailingIcon = { if (query.isNotBlank()) IconButton(onClick = { query = "" }) { Icon(Icons.Default.Close, "Clear search", tint = LegendColors.OnNavy) } },
                            singleLine = true,
                            shape = CircleShape,
                            colors = OutlinedTextFieldDefaults.colors(focusedContainerColor = LegendColors.Navy, unfocusedContainerColor = LegendColors.Navy, focusedTextColor = LegendColors.OnNavy, unfocusedTextColor = LegendColors.OnNavy, focusedBorderColor = LegendColors.NavyElevated, unfocusedBorderColor = LegendColors.NavyElevated),
                        )
                    }
                }
                if (participantType.equals("Client", ignoreCase = true)) item {
                    JourneyCirclesSection(
                        state = journey,
                        mediaRepository = mediaRepository,
                        participantType = participantType,
                        edit = { editingJourney = true },
                        requestConnection = viewModel::requestConnection,
                        respond = viewModel::respondToJourneyConnection,
                        disconnect = viewModel::disconnectJourneyConnection,
                    )
                }
                item { Text("${snapshot.totalCount} ${if (snapshot.totalCount == 1) "member" else "members"}${if (query.isBlank()) " in your LEGEND community" else " matching your search"}", style = LegendTypography.Supporting, color = LegendColors.OnNavy.copy(alpha = 0.72f)) }
                if (snapshot.results.isEmpty()) {
                    item { LegendDiscoverEmptyState(query) }
                } else {
                    items(snapshot.results, key = { it.clientProfileId }) { result ->
                        LegendDiscoverResultCard(
                            result = result,
                            mediaRepository = mediaRepository,
                            participantType = participantType,
                            open = { selectedProfile = result; viewModel.openProfile(result.clientProfileId) },
                            safety = { safetyTarget = result },
                        )
                    }
                    if (snapshot.hasMore) item { TextButton(onClick = viewModel::loadMore, modifier = Modifier.fillMaxWidth()) { Text("Load more members", color = LegendColors.GoldBright) } }
                }
            }
        }
    }
    safetyTarget?.let { target ->
        CommunitySafetyDialog(
            target = target,
            onDismiss = { safetyTarget = null },
            block = {
                viewModel.block(target)
                safetyTarget = null
            },
            report = { category, detail ->
                viewModel.report(target, category, detail)
                safetyTarget = null
            },
        )
    }
    selectedProfile?.let { target ->
        LegendDiscoveryProfileSheet(
            target = target,
            state = profile,
            socialViewModel = socialViewModel,
            mediaRepository = mediaRepository,
            participantType = participantType,
            dismiss = { selectedProfile = null },
            requestConnection = { viewModel.requestConnection(target.clientProfileId) },
            safety = { safetyTarget = target },
            disconnectJourney = target.relationship.connectionId?.let { id -> { viewModel.disconnectJourneyConnection(id); selectedProfile = null } },
            blockJourney = if (participantType.equals("Client", ignoreCase = true) && target.identity.participantType.equals("Client", ignoreCase = true)) {
                { viewModel.blockJourneyProfile(target.clientProfileId); selectedProfile = null }
            } else null,
            reportJourney = if (participantType.equals("Client", ignoreCase = true) && target.identity.participantType.equals("Client", ignoreCase = true)) {
                { category -> viewModel.reportJourneyProfile(target.clientProfileId, category); selectedProfile = null }
            } else null,
        )
    }
    if (editingJourney && journey is LoadState.Data) {
        JourneyProfileEditorSheet(
            dashboard = (journey as LoadState.Data<JourneyDashboard>).value,
            dismiss = { editingJourney = false },
            save = { input -> viewModel.saveJourneyProfile(input) { editingJourney = false } },
        )
    }
}

@Composable
private fun LegendDiscoverEmptyState(query: String) {
    Surface(color = LegendColors.Navy, shape = LegendShapes.Card, modifier = Modifier.fillMaxWidth()) {
        Column(Modifier.padding(LegendSpacing.Lg), horizontalAlignment = Alignment.CenterHorizontally, verticalArrangement = Arrangement.spacedBy(LegendSpacing.Sm)) {
            Icon(Icons.Default.PersonSearch, null, tint = LegendColors.GoldBright, modifier = Modifier.size(32.dp))
            Text(if (query.isBlank()) "No members yet" else "No members found", style = LegendTypography.Section, color = LegendColors.OnNavy)
            Text(if (query.isBlank()) "Active LEGEND members and agents will appear here." else "Try another name, goal, interest, or location.", style = LegendTypography.Supporting, color = LegendColors.GoldSoft, textAlign = androidx.compose.ui.text.style.TextAlign.Center)
        }
    }
}

@Composable
private fun LegendDiscoverResultCard(
    result: DiscoveryResult,
    mediaRepository: AuthenticatedMediaRepository,
    participantType: String,
    open: () -> Unit,
    safety: () -> Unit,
) {
    val isAgent = result.identity.participantType.equals("Agent", ignoreCase = true)
    val detail = when {
        result.relationship.followsCurrentActor && result.relationship.followedByCurrentActor -> "You follow each other"
        result.relationship.followsCurrentActor -> "Follows you"
        isAgent -> null
        else -> result.matchExplanation ?: result.headline ?: result.location
    }
    LegendContactCard(
        displayName = result.displayName,
        nameStatus = result.relationship.connectionStatus
            .takeIf { it.equals("Accepted", ignoreCase = true) }
            ?.let { "Connected" },
        subtitle = if (isAgent) result.roleLabel else result.username?.let { "@$it" },
        detail = detail,
        isVerified = result.isVerified,
        onClick = open,
        onLongClick = safety,
        avatar = {
            LegendProtectedAvatar(
                result.avatar,
                result.displayName,
                participantType,
                mediaRepository,
                size = 46.dp,
            )
        },
        action = {
            Icon(
                Icons.Default.ChevronRight,
                "Open ${result.displayName}'s profile",
                modifier = Modifier.size(20.dp),
                tint = LegendColors.OnNavy.copy(alpha = LegendOpacity.ContactAction),
            )
        },
    )
}

@Composable
private fun LegendDiscoveryProfileSheet(
    target: DiscoveryResult,
    state: LoadState<com.mylegnd.legend.registered.core.network.DiscoveryProfile>,
    socialViewModel: SocialViewModel,
    mediaRepository: AuthenticatedMediaRepository,
    participantType: String,
    dismiss: () -> Unit,
    requestConnection: () -> Unit,
    safety: () -> Unit,
    disconnectJourney: (() -> Unit)? = null,
    blockJourney: (() -> Unit)? = null,
    reportJourney: ((String) -> Unit)? = null,
) {
    val socialPosts by socialViewModel.publicProfilePosts.collectAsStateWithLifecycle()
    val socialMetrics by socialViewModel.publicProfileMetrics.collectAsStateWithLifecycle()
    var commentingPost by remember { mutableStateOf<SocialPost?>(null) }
    val author = remember(target.clientProfileId) {
        SocialAuthor(
            identity = target.identity,
            profileId = target.clientProfileId,
            displayName = target.displayName,
            avatar = target.avatar,
            username = target.username,
            bio = target.bio,
            website = target.website,
            location = target.location,
            publicEmail = target.publicEmail,
            publicPhone = target.publicPhone,
            isPrivate = target.isPrivate,
            isVerified = target.isVerified,
            roleLabel = target.roleLabel,
        )
    }
    LaunchedEffect(target.clientProfileId) { socialViewModel.loadPublicProfile(author) }
    ModalBottomSheet(onDismissRequest = dismiss, containerColor = LegendColors.Canvas) {
        LazyColumn(
            modifier = Modifier.fillMaxHeight(0.92f),
            contentPadding = PaddingValues(horizontal = LegendSpacing.PageHorizontal, vertical = LegendSpacing.Md),
            verticalArrangement = Arrangement.spacedBy(LegendSpacing.Sm),
        ) {
            item {
                Row(verticalAlignment = Alignment.CenterVertically) {
                    LegendProtectedAvatar(author.avatar, author.displayName, participantType, mediaRepository, size = 72.dp)
                    Spacer(Modifier.width(LegendSpacing.Md))
                    Column(Modifier.weight(1f)) {
                        Row(verticalAlignment = Alignment.CenterVertically) {
                            Text(author.displayName, style = LegendTypography.Section, color = LegendColors.TextPrimary)
                            if (author.isVerified) Icon(Icons.Default.Verified, "Verified", modifier = Modifier.padding(start = LegendSpacing.Xs).size(18.dp), tint = LegendColors.Verified)
                        }
                        Text(author.roleLabel ?: author.username?.let { "@$it" } ?: "LEGEND member", style = LegendTypography.Label, color = LegendColors.TextSecondary)
                    }
                    TextButton(onClick = dismiss) { Text("Done", color = LegendColors.Gold) }
                }
            }
            when (state) {
                is LoadState.Data -> {
                    val detail = state.value
                    detail.introduction?.takeIf(String::isNotBlank)?.let { introduction -> item { Text(introduction, style = LegendTypography.Body, color = LegendColors.TextPrimary) } }
                    item {
                        Surface(color = LegendColors.Navy, shape = LegendShapes.Control, modifier = Modifier.fillMaxWidth()) {
                            Row(Modifier.padding(LegendSpacing.Sm), horizontalArrangement = Arrangement.SpaceEvenly) {
                                LegendMetric("Posts", detail.postCount.toString())
                                LegendMetric("Followers", detail.followerCount.toString())
                                LegendMetric("Following", detail.followingCount.toString())
                            }
                        }
                    }
                }
                is LoadState.Error -> item { Text(state.message, style = LegendTypography.Supporting, color = LegendColors.Error) }
                else -> item { LinearProgressIndicator(modifier = Modifier.fillMaxWidth(), color = LegendColors.Gold) }
            }
            item {
                Row(horizontalArrangement = Arrangement.spacedBy(LegendSpacing.Xs), modifier = Modifier.fillMaxWidth()) {
                    if (target.relationship.canFollow) OutlinedButton(onClick = { socialViewModel.toggleFollow(author) }, modifier = Modifier.weight(1f), shape = LegendShapes.Control) { Text(if (target.relationship.followedByCurrentActor) "Following" else if (target.relationship.followRequestPending) "Requested" else "Follow") }
                    if (target.relationship.canRequestConnection && participantType.equals("Client", ignoreCase = true)) LegendPrimaryButton("Connect", modifier = Modifier.weight(1f), onClick = requestConnection)
                    IconButton(onClick = safety, modifier = Modifier.background(LegendColors.SurfaceInset, CircleShape)) { Icon(Icons.Default.MoreHoriz, "Community safety", tint = LegendColors.TextSecondary) }
                }
            }
            if (disconnectJourney != null || blockJourney != null || reportJourney != null) item {
                LegendJourneySafetyActions(disconnectJourney, blockJourney, reportJourney)
            }
            item { Text("Posts", style = LegendTypography.Section, color = LegendColors.TextPrimary) }
            when (socialPosts) {
                LoadState.Idle, LoadState.Loading -> item { LegendLoadingState() }
                is LoadState.Error -> item { Text((socialPosts as LoadState.Error).message, color = LegendColors.Error) }
                is LoadState.Data -> {
                    val posts = (socialPosts as LoadState.Data<List<SocialPost>>).value
                    if (posts.isEmpty()) item { Text("No server-visible posts.", style = LegendTypography.Supporting, color = LegendColors.TextSecondary) }
                    else items(posts, key = { it.id }) { post ->
                        LegendSocialPostCard(post, mediaRepository, participantType, false, null, { socialViewModel.react(post.id) }, { commentingPost = post }, { socialViewModel.toggleFollow(post) }, { socialViewModel.toggleSave(post.id) }, { socialViewModel.toggleRepost(post.id) })
                    }
                }
            }
            if (socialMetrics is LoadState.Data) item { Text("Server-authorized public profile", style = LegendTypography.Label, color = LegendColors.TextTertiary) }
        }
    }
    commentingPost?.let { post ->
        LegendCommentsSheet(post, mediaRepository, participantType, { commentingPost = null }) { body, parentCommentId -> socialViewModel.comment(post.id, body, parentCommentId) }
    }
}

@Composable
private fun CommunitySafetyDialog(
    target: DiscoveryResult,
    onDismiss: () -> Unit,
    block: () -> Unit,
    report: (String, String) -> Unit,
) {
    var reporting by remember { mutableStateOf(false) }
    var category by remember { mutableStateOf("") }
    var detail by remember { mutableStateOf("") }
    AlertDialog(
        onDismissRequest = onDismiss,
        title = { Text(if (reporting) "Report ${target.displayName}" else "Safety actions") },
        text = {
            if (reporting) {
                Column(verticalArrangement = Arrangement.spacedBy(LegendSpacing.Xs)) {
                    OutlinedTextField(category, { category = it }, label = { Text("Reason") }, modifier = Modifier.fillMaxWidth())
                    OutlinedTextField(detail, { detail = it }, label = { Text("Details (optional)") }, modifier = Modifier.fillMaxWidth())
                }
            } else {
                Text("LEGEND sends block and report decisions to the community-safety service for server enforcement.")
            }
        },
        confirmButton = {
            if (reporting) {
                TextButton(onClick = { report(category, detail) }, enabled = category.isNotBlank()) { Text("Submit report") }
            } else {
                TextButton(onClick = block) { Text("Block", color = LegendColors.Error) }
            }
        },
        dismissButton = {
            if (reporting) {
                TextButton(onClick = { reporting = false }) { Text("Back") }
            } else {
                Row {
                    TextButton(onClick = { reporting = true }) { Text("Report") }
                    TextButton(onClick = onDismiss) { Text("Cancel") }
                }
            }
        },
    )
}

@Composable
private fun JourneyCirclesSection(
    state: LoadState<JourneyDashboard>,
    mediaRepository: AuthenticatedMediaRepository,
    participantType: String,
    edit: () -> Unit,
    requestConnection: (String) -> Unit,
    respond: (String, Boolean) -> Unit,
    disconnect: (String) -> Unit,
) {
    when (state) {
        LoadState.Idle,
        LoadState.Loading -> Surface(color = LegendColors.Navy, shape = LegendShapes.Card, modifier = Modifier.fillMaxWidth()) {
            Row(Modifier.padding(LegendSpacing.Md), verticalAlignment = Alignment.CenterVertically) {
                CircularProgressIndicator(modifier = Modifier.size(18.dp), color = LegendColors.GoldBright, strokeWidth = 2.dp)
                Spacer(Modifier.width(LegendSpacing.Sm))
                Text("Loading Journey Circles", style = LegendTypography.Label, color = LegendColors.OnNavy)
            }
        }

        is LoadState.Error -> Surface(color = LegendColors.Navy, shape = LegendShapes.Card, modifier = Modifier.fillMaxWidth()) {
            Row(Modifier.padding(LegendSpacing.Md), verticalAlignment = Alignment.CenterVertically) {
                Icon(Icons.Default.Group, null, tint = LegendColors.GoldBright)
                Spacer(Modifier.width(LegendSpacing.Sm))
                Text("Journey Circles is currently unavailable.", style = LegendTypography.Supporting, color = LegendColors.GoldSoft)
            }
        }
        is LoadState.Data -> {
            val dashboard = state.value
            Surface(color = LegendColors.Navy, shape = LegendShapes.Card, modifier = Modifier.fillMaxWidth()) {
                Column(Modifier.padding(LegendSpacing.Md), verticalArrangement = Arrangement.spacedBy(LegendSpacing.Sm)) {
                    Row(verticalAlignment = Alignment.CenterVertically) {
                        Icon(Icons.Default.Groups, null, tint = LegendColors.GoldBright, modifier = Modifier.size(22.dp))
                        Spacer(Modifier.width(LegendSpacing.Xs))
                        Column(Modifier.weight(1f)) {
                            Text("JOURNEY CIRCLES", style = LegendTypography.Eyebrow, color = LegendColors.GoldBright)
                            Text(if (dashboard.preferences?.consentAffirmed == true) "Your matching circle" else "Build your matching circle", style = LegendTypography.CardTitle, color = LegendColors.OnNavy)
                        }
                        IconButton(onClick = edit, modifier = Modifier.background(LegendColors.NavyElevated.copy(alpha = .45f), CircleShape)) {
                            Icon(if (dashboard.profile == null) Icons.Default.PersonAdd else Icons.Default.Tune, if (dashboard.profile == null) "Set up Journey Circles" else "Manage Journey Circles", tint = LegendColors.GoldBright)
                        }
                    }
                    Text(
                        if (dashboard.preferences?.consentAffirmed == true) "Your recommendations and connection choices remain server-authorized and under your control."
                        else "Confirm participation to activate private, respectful matching in LEGEND.",
                        style = LegendTypography.Supporting,
                        color = LegendColors.GoldSoft,
                    )
                    if (dashboard.requests.isNotEmpty()) {
                        LegendJourneySectionLabel("Connection requests")
                        dashboard.requests.forEach { request ->
                            LegendJourneyConnectionRow(
                                connection = request,
                                mediaRepository = mediaRepository,
                                participantType = participantType,
                                accept = { respond(request.id, true) },
                                decline = { respond(request.id, false) },
                                disconnect = null,
                            )
                        }
                    }
                    if (dashboard.recommendations.isNotEmpty()) {
                        LegendJourneySectionLabel("Recommended for you")
                        dashboard.recommendations.take(3).forEach { recommendation ->
                            Row(verticalAlignment = Alignment.CenterVertically, modifier = Modifier.fillMaxWidth()) {
                                LegendProtectedAvatar(recommendation.profile.avatar, recommendation.profile.displayName, participantType, mediaRepository, size = 38.dp)
                                Spacer(Modifier.width(LegendSpacing.Xs))
                                Column(Modifier.weight(1f)) {
                                    Text(recommendation.profile.displayName, style = LegendTypography.Label, color = LegendColors.OnNavy)
                                    Text(recommendation.explanation, style = LegendTypography.Supporting, color = LegendColors.GoldSoft, maxLines = 2, overflow = TextOverflow.Ellipsis)
                                }
                                TextButton(onClick = { requestConnection(recommendation.profile.clientProfileId) }) { Text("Connect", color = LegendColors.GoldBright, style = LegendTypography.Label) }
                            }
                        }
                    }
                    if (dashboard.connections.isNotEmpty()) {
                        LegendJourneySectionLabel("Your connections")
                        dashboard.connections.take(3).forEach { connection ->
                            LegendJourneyConnectionRow(connection, mediaRepository, participantType, null, null, { disconnect(connection.id) })
                        }
                    }
                    if (dashboard.recommendations.isEmpty() && dashboard.connections.isEmpty() && dashboard.requests.isEmpty()) {
                        Text("Complete your profile to receive server-authorized recommendations.", style = LegendTypography.Supporting, color = LegendColors.TextSecondary)
                    }
                }
            }
        }
    }
}

@Composable
private fun LegendJourneySectionLabel(label: String) {
    Text(label.uppercase(), style = LegendTypography.Eyebrow, color = LegendColors.GoldBright, modifier = Modifier.padding(top = LegendSpacing.Xs))
}

@Composable
private fun LegendJourneyConnectionRow(
    connection: JourneyConnection,
    mediaRepository: AuthenticatedMediaRepository,
    participantType: String,
    accept: (() -> Unit)?,
    decline: (() -> Unit)?,
    disconnect: (() -> Unit)?,
) {
    Surface(color = LegendColors.SurfaceInset.copy(alpha = .7f), shape = LegendShapes.Control, modifier = Modifier.fillMaxWidth()) {
        Row(Modifier.padding(LegendSpacing.Xs), verticalAlignment = Alignment.CenterVertically) {
            LegendProtectedAvatar(connection.profile.avatar, connection.profile.displayName, participantType, mediaRepository, size = 36.dp)
            Spacer(Modifier.width(LegendSpacing.Xs))
            Column(Modifier.weight(1f)) {
                Text(connection.profile.displayName, style = LegendTypography.Label, color = LegendColors.OnNavy)
                Text(connection.introduction ?: connection.connectionReason ?: connection.status, style = LegendTypography.Supporting, color = LegendColors.GoldSoft, maxLines = 1, overflow = TextOverflow.Ellipsis)
            }
            if (disconnect == null) {
                decline?.let { action -> IconButton(onClick = action) { Icon(Icons.Default.Close, "Decline connection", tint = LegendColors.TextSecondary) } }
                accept?.let { action -> IconButton(onClick = action) { Icon(Icons.Default.Check, "Accept connection", tint = LegendColors.GoldBright) } }
            } else {
                TextButton(onClick = disconnect) { Text("Connected", style = LegendTypography.Supporting, color = LegendColors.GoldBright) }
            }
        }
    }
}

@Composable
private fun JourneyProfileEditorSheet(
    dashboard: JourneyDashboard,
    dismiss: () -> Unit,
    save: (JourneyProfileInput) -> Unit,
) {
    val profile = dashboard.profile
    val preferences = dashboard.preferences
    var consent by remember(profile?.clientProfileId) { mutableStateOf(preferences?.consentAffirmed ?: false) }
    var optedIn by remember(profile?.clientProfileId) { mutableStateOf(preferences?.isOptedIn ?: false) }
    var discoverable by remember(profile?.clientProfileId) { mutableStateOf(preferences?.isDiscoverable ?: true) }
    var suggestions by remember(profile?.clientProfileId) { mutableStateOf(preferences?.allowSuggestions ?: true) }
    var requests by remember(profile?.clientProfileId) { mutableStateOf(preferences?.allowConnectionRequests ?: true) }
    var introduction by remember(profile?.clientProfileId) { mutableStateOf(profile?.introduction.orEmpty()) }
    var lifeStages by remember(profile?.clientProfileId) { mutableStateOf(profile?.lifeStages.orEmpty()) }
    var locations by remember(profile?.clientProfileId) { mutableStateOf(profile?.locations.orEmpty()) }
    var goals by remember(profile?.clientProfileId) { mutableStateOf(profile?.goals.orEmpty()) }
    var interests by remember(profile?.clientProfileId) { mutableStateOf(profile?.interests.orEmpty()) }
    var circles by remember(profile?.clientProfileId) { mutableStateOf(profile?.circleCodes.orEmpty()) }
    var connectionTypes by remember(profile?.clientProfileId) { mutableStateOf(profile?.connectionTypes.orEmpty()) }
    var communicationStyles by remember(profile?.clientProfileId) { mutableStateOf(profile?.communicationStyles.orEmpty()) }
    var accountability by remember(profile?.clientProfileId) { mutableStateOf(profile?.accountabilityFrequencies.orEmpty()) }
    fun toggle(current: List<String>, value: String): List<String> = if (value in current) current - value else current + value

    ModalBottomSheet(onDismissRequest = dismiss, containerColor = LegendColors.Canvas) {
        LazyColumn(
            modifier = Modifier.fillMaxHeight(.94f),
            contentPadding = PaddingValues(horizontal = LegendSpacing.PageHorizontal, vertical = LegendSpacing.Md),
            verticalArrangement = Arrangement.spacedBy(LegendSpacing.Md),
        ) {
            item {
                Text("JOURNEY CIRCLES", style = LegendTypography.Eyebrow, color = LegendColors.Gold)
                Text("Build your circle", style = LegendTypography.Hero, color = LegendColors.TextPrimary)
                Text("Confirm participation once to begin. Every additional detail makes your recommendations more precise.", style = LegendTypography.Supporting, color = LegendColors.TextSecondary)
            }
            item {
                Surface(color = LegendColors.Navy, shape = LegendShapes.Card, modifier = Modifier.fillMaxWidth()) {
                    Column(Modifier.padding(LegendSpacing.Md)) {
                        LegendJourneyToggle("Confirm community participation", "Start a private, respectful matching profile.", consent) { consent = it }
                        HorizontalDivider(color = LegendColors.Divider)
                        LegendJourneyToggle("Join Journey Circles", "Take part in the LEGEND matching community.", optedIn) { optedIn = it }
                        HorizontalDivider(color = LegendColors.Divider)
                        LegendJourneyToggle("Show my profile in Discover", "Let compatible members find your profile.", discoverable) { discoverable = it }
                        HorizontalDivider(color = LegendColors.Divider)
                        LegendJourneyToggle("Allow recommendations", "Receive tailored connection suggestions.", suggestions) { suggestions = it }
                        HorizontalDivider(color = LegendColors.Divider)
                        LegendJourneyToggle("Allow connection requests", "Let compatible members request a connection.", requests) { requests = it }
                    }
                }
            }
            item {
                Text("A LITTLE ABOUT YOUR SEASON", style = LegendTypography.Eyebrow, color = LegendColors.Gold)
                OutlinedTextField(
                    introduction,
                    { introduction = it },
                    modifier = Modifier.fillMaxWidth(),
                    label = { Text("What are you building, learning, or looking for?") },
                    minLines = 3,
                    maxLines = 6,
                    shape = LegendShapes.Control,
                    colors = OutlinedTextFieldDefaults.colors(
                        focusedContainerColor = LegendColors.SurfaceInset,
                        unfocusedContainerColor = LegendColors.SurfaceInset,
                        focusedTextColor = LegendColors.TextPrimary,
                        unfocusedTextColor = LegendColors.TextPrimary,
                        focusedBorderColor = LegendColors.Gold,
                        unfocusedBorderColor = LegendColors.Divider,
                        focusedLabelColor = LegendColors.Gold,
                        unfocusedLabelColor = LegendColors.TextSecondary,
                    ),
                )
            }
            item { JourneyChoiceSection("Goals", "The strongest starting signal for recommendations.", dashboard.taxonomy.goals, goals) { goals = toggle(goals, it) } }
            item { JourneyChoiceSection("Circles", "Choose communities that fit your current season.", dashboard.taxonomy.circles, circles) { circles = toggle(circles, it) } }
            item { JourneyChoiceSection("Life stage", "Optional context that refines your matches.", dashboard.taxonomy.lifeStages, lifeStages) { lifeStages = toggle(lifeStages, it) } }
            item { JourneyChoiceSection("Location", "Optional regional relevance.", dashboard.taxonomy.locations, locations) { locations = toggle(locations, it) } }
            item { JourneyChoiceSection("Interests", "Add the subjects you want to explore together.", dashboard.taxonomy.interests, interests) { interests = toggle(interests, it) } }
            item { JourneyChoiceSection("Connection types", "Set the kind of connection you value.", dashboard.taxonomy.connectionTypes, connectionTypes) { connectionTypes = toggle(connectionTypes, it) } }
            item { JourneyChoiceSection("Communication style", "Help recommendations feel natural from the start.", dashboard.taxonomy.communicationStyles, communicationStyles) { communicationStyles = toggle(communicationStyles, it) } }
            item { JourneyChoiceSection("Accountability", "Optional cadence preferences for stronger fit.", dashboard.taxonomy.accountabilityFrequencies, accountability) { accountability = toggle(accountability, it) } }
            item {
                LegendPrimaryButton("Save Journey Circles", modifier = Modifier.fillMaxWidth(), enabled = consent) {
                    save(JourneyProfileInput(consent, optedIn, discoverable, suggestions, requests, introduction.trim().takeIf(String::isNotBlank), lifeStages.sorted(), locations.sorted(), goals.sorted(), interests.sorted(), circles.sorted(), connectionTypes.sorted(), communicationStyles.sorted(), accountability.sorted()))
                }
                TextButton(onClick = dismiss, modifier = Modifier.fillMaxWidth()) { Text("Close", color = LegendColors.TextSecondary) }
            }
        }
    }
}

@Composable
private fun LegendJourneyToggle(title: String, detail: String, checked: Boolean, change: (Boolean) -> Unit) {
    Row(Modifier.fillMaxWidth().padding(vertical = LegendSpacing.Xs), verticalAlignment = Alignment.CenterVertically) {
        Column(Modifier.weight(1f)) {
            Text(title, style = LegendTypography.Label, color = LegendColors.OnNavy)
            Text(detail, style = LegendTypography.Supporting, color = LegendColors.GoldSoft)
        }
        Switch(checked = checked, onCheckedChange = change, colors = SwitchDefaults.colors(checkedThumbColor = LegendColors.Navy, checkedTrackColor = LegendColors.Gold))
    }
}

@Composable
private fun JourneyChoiceSection(title: String, detail: String, options: List<String>, selected: List<String>, toggle: (String) -> Unit) {
    if (options.isEmpty()) return
    Surface(color = LegendColors.Surface, shape = LegendShapes.Card, modifier = Modifier.fillMaxWidth()) {
        Column(Modifier.padding(LegendSpacing.Md), verticalArrangement = Arrangement.spacedBy(LegendSpacing.Xs)) {
            Text(title.uppercase(), style = LegendTypography.Eyebrow, color = LegendColors.Gold)
            Text(detail, style = LegendTypography.Supporting, color = LegendColors.TextSecondary)
            LazyRow(horizontalArrangement = Arrangement.spacedBy(LegendSpacing.Xs)) {
                items(options, key = { it }) { option ->
                    FilterChip(
                        selected = option in selected,
                        onClick = { toggle(option) },
                        label = { Text(option, style = LegendTypography.Supporting) },
                        colors = FilterChipDefaults.filterChipColors(selectedContainerColor = LegendColors.Navy, selectedLabelColor = LegendColors.GoldBright, containerColor = LegendColors.SurfaceInset, labelColor = LegendColors.TextSecondary),
                        border = FilterChipDefaults.filterChipBorder(enabled = true, selected = option in selected, borderColor = LegendColors.Divider, selectedBorderColor = LegendColors.Gold.copy(alpha = .7f)),
                    )
                }
            }
        }
    }
}

@Composable
private fun LegendJourneySafetyActions(disconnect: (() -> Unit)?, block: (() -> Unit)?, report: ((String) -> Unit)?) {
    var expanded by remember { mutableStateOf(false) }
    var confirmDisconnect by remember { mutableStateOf(false) }
    var confirmBlock by remember { mutableStateOf(false) }
    Row(horizontalArrangement = Arrangement.spacedBy(LegendSpacing.Xs), modifier = Modifier.fillMaxWidth()) {
        disconnect?.let { TextButton(onClick = { confirmDisconnect = true }, modifier = Modifier.weight(1f)) { Text("Remove connection", color = LegendColors.TextSecondary) } }
        if (block != null || report != null) Box {
            OutlinedButton(onClick = { expanded = true }, shape = LegendShapes.Control) { Text("Safety", color = LegendColors.TextSecondary) }
            DropdownMenu(expanded = expanded, onDismissRequest = { expanded = false }) {
                report?.let { submit -> listOf(
                    "Harassment or hate" to "HarassmentOrHate",
                    "Threat or self-harm" to "ThreatOrSelfHarm",
                    "Sexual content" to "SexualContent",
                    "Spam or scam" to "SpamOrScam",
                ).forEach { (label, category) -> DropdownMenuItem(text = { Text("Report: $label") }, onClick = { expanded = false; submit(category) }) } }
                block?.let { DropdownMenuItem(text = { Text("Block profile", color = LegendColors.Error) }, onClick = { expanded = false; confirmBlock = true }) }
            }
        }
    }
    if (confirmDisconnect) AlertDialog(onDismissRequest = { confirmDisconnect = false }, title = { Text("Remove this connection?") }, text = { Text("This disconnects this Journey Circles connection. It does not change any server rules outside the existing account relationship.") }, confirmButton = { TextButton(onClick = { confirmDisconnect = false; disconnect?.invoke() }) { Text("Remove", color = LegendColors.Error) } }, dismissButton = { TextButton(onClick = { confirmDisconnect = false }) { Text("Cancel") } })
    if (confirmBlock) AlertDialog(onDismissRequest = { confirmBlock = false }, title = { Text("Block this profile?") }, text = { Text("This removes the Journey Circles connection and prevents client-to-client messaging with this profile.") }, confirmButton = { TextButton(onClick = { confirmBlock = false; block?.invoke() }) { Text("Block", color = LegendColors.Error) } }, dismissButton = { TextButton(onClick = { confirmBlock = false }) { Text("Cancel") } })
}

@Composable
private fun HomeScreen(
    homeViewModel: HomeViewModel,
    agentWorkspaceViewModel: AgentWorkspaceViewModel,
    socialViewModel: SocialViewModel,
    notificationsViewModel: NotificationsViewModel,
    mediaRepository: AuthenticatedMediaRepository,
    participantType: String,
    currentActor: MobileActor,
    chromeAction: LegendHomeChromeAction?,
    onChromeActionHandled: () -> Unit,
    openSocial: () -> Unit,
    openConversation: (String) -> Unit,
) {
    val homeState by homeViewModel.state.collectAsStateWithLifecycle()
    val agentClients by agentWorkspaceViewModel.clients.collectAsStateWithLifecycle()
    val agentLeads by agentWorkspaceViewModel.leads.collectAsStateWithLifecycle()
    val clientCreationPortal by agentWorkspaceViewModel.clientCreationPortal.collectAsStateWithLifecycle()
    val socialState by socialViewModel.state.collectAsStateWithLifecycle()
    val notificationState by notificationsViewModel.state.collectAsStateWithLifecycle()
    val context = LocalContext.current
    var creating by remember { mutableStateOf(false) }
    var scriptureOpen by remember { mutableStateOf(false) }
    var activityOpen by remember { mutableStateOf(false) }
    var notificationsOpen by remember { mutableStateOf(false) }

    LaunchedEffect(chromeAction) {
        when (chromeAction) {
            LegendHomeChromeAction.CREATE -> creating = true
            LegendHomeChromeAction.NOTIFICATIONS -> {
                notificationsViewModel.load()
                notificationsOpen = true
            }
            null -> Unit
        }
        if (chromeAction != null) onChromeActionHandled()
    }

    LaunchedEffect(Unit) {
        homeViewModel.load()
        agentWorkspaceViewModel.load()
        socialViewModel.load()
        notificationsViewModel.load()
    }

    when (homeState) {
        LoadState.Idle,
        LoadState.Loading -> LegendLoadingState()

        is LoadState.Error -> LegendErrorState((homeState as LoadState.Error).message, homeViewModel::load)
        is LoadState.Data -> {
            val home = (homeState as LoadState.Data<MobileHomeResponse>).value
            val notificationCount = (notificationState as? LoadState.Data<NotificationSnapshot>)?.value?.badge?.unreadCount ?: 0
            val activityCount = home.actions.size
            LazyColumn(
                modifier = Modifier.fillMaxSize().background(LegendColors.Canvas),
                verticalArrangement = Arrangement.spacedBy(LegendSpacing.Xs),
                contentPadding = PaddingValues(
                    start = LegendSpacing.PageHorizontal,
                    end = LegendSpacing.PageHorizontal,
                    top = LegendSpacing.PageTop,
                    bottom = LegendSpacing.Xl,
                ),
            ) {
                item {
                    LegendHomeHero(
                        home = home,
                        openScripture = { scriptureOpen = true },
                    )
                }
                item {
                    LegendHomeActivityPill(
                        count = activityCount,
                        hasActivity = home.actions.isNotEmpty() || activityCount > 0,
                        openActivity = { activityOpen = true },
                    )
                }
                if (participantType.equals("Agent", ignoreCase = true)) item {
                    LegendAgentWorkspaceCard(
                        clients = agentClients,
                        leads = agentLeads,
                        clientCreationPortal = clientCreationPortal,
                        mediaRepository = mediaRepository,
                        participantType = participantType,
                        createClient = agentWorkspaceViewModel::launchClientCreationPortal,
                    )
                }
                when (socialState) {
                    LoadState.Idle,
                    LoadState.Loading -> item { LegendHomeSocialLoading() }

                    is LoadState.Error -> item {
                        LegendInlineRetry(
                            message = (socialState as LoadState.Error).message,
                            retry = socialViewModel::load,
                        )
                    }

                    is LoadState.Data -> {
                        val snapshot = (socialState as LoadState.Data<SocialSnapshot>).value
                        item {
                            LegendStoryRail(
                                currentActor = currentActor,
                                stories = snapshot.stories,
                                mediaRepository = mediaRepository,
                                participantType = participantType,
                                create = { creating = true },
                            )
                        }
                        items(snapshot.promotedGroups, key = { it.conversationId }) { group ->
                            LegendPromotedGroupCard(
                                group = group,
                                mediaRepository = mediaRepository,
                                participantType = participantType,
                                join = {
                                    socialViewModel.joinPromotedGroup(group.conversationId) {
                                        openConversation(group.conversationId)
                                    }
                                },
                            )
                        }
                        if (snapshot.posts.isEmpty() && snapshot.promotedGroups.isEmpty()) {
                            item {
                                LegendHomeEmptyFeed(create = { creating = true })
                            }
                        } else {
                            items(snapshot.posts.take(2), key = { it.id }) { post ->
                                LegendSocialPostCard(
                                    post = post,
                                    mediaRepository = mediaRepository,
                                    participantType = participantType,
                                    isCurrentActor = post.author.identity.userId == currentActor.identity.userId && post.author.identity.participantType == currentActor.identity.participantType,
                                    onProfile = null,
                                    onReact = { socialViewModel.react(post.id) },
                                    onComment = openSocial,
                                    onFollow = { socialViewModel.toggleFollow(post) },
                                    onSave = { socialViewModel.toggleSave(post.id) },
                                    onRepost = { socialViewModel.toggleRepost(post.id) },
                                )
                            }
                        }
                    }
                }
            }
            if (scriptureOpen) {
                DailyScriptureSheet(
                    scripture = home.dailyScripture,
                    dismiss = { scriptureOpen = false },
                )
            }
            if (activityOpen) {
                HomeActivitySheet(
                    actions = home.actions,
                    dismiss = { activityOpen = false },
                )
            }
        }
    }

    if (creating) {
        CreatePostSheet(
            onDismiss = { creating = false },
            createText = { request ->
                socialViewModel.create(request)
                creating = false
            },
            createMedia = { uris, options, previewUri ->
                socialViewModel.createMedia(context, uris, options, previewUri)
                creating = false
            },
        )
    }
    if (notificationsOpen) {
        NotificationInboxSheet(
            state = notificationState,
            dismiss = { notificationsOpen = false },
            retry = notificationsViewModel::load,
            clearBadges = notificationsViewModel::clearBadges,
            open = { item ->
                notificationsViewModel.markRead(item.id) { opened ->
                    notificationsOpen = false
                    opened.conversationId?.let(openConversation)
                }
            },
        )
    }
    (clientCreationPortal as? LoadState.Data<MobileClientCreationPortalLaunch>)?.value?.let { launch ->
        LegendAgentClientCreationPortal(
            launchPath = launch.launchPath,
            dismiss = {
                agentWorkspaceViewModel.clearClientCreationPortal()
                agentWorkspaceViewModel.load()
            },
            recoverExpiredTicket = {
                agentWorkspaceViewModel.launchClientCreationPortal()
            },
        )
    }
}

@Composable
private fun LegendAgentWorkspaceCard(
    clients: LoadState<List<MobileAgentClient>>,
    leads: LoadState<List<MobileAgentLead>>,
    clientCreationPortal: LoadState<MobileClientCreationPortalLaunch>,
    mediaRepository: AuthenticatedMediaRepository,
    participantType: String,
    createClient: () -> Unit,
) {
    Surface(color = LegendColors.Navy, shape = LegendShapes.Card, modifier = Modifier.fillMaxWidth()) {
        Column(Modifier.padding(LegendSpacing.Md), verticalArrangement = Arrangement.spacedBy(LegendSpacing.Sm)) {
            Row(verticalAlignment = Alignment.CenterVertically) {
                Icon(Icons.Default.BusinessCenter, null, tint = LegendColors.GoldBright)
                Spacer(Modifier.width(LegendSpacing.Xs))
                Column(Modifier.weight(1f)) {
                    Text("AGENT WORKSPACE", style = LegendTypography.Eyebrow, color = LegendColors.GoldBright)
                    Text("Your client relationship desk", style = LegendTypography.CardTitle, color = LegendColors.OnNavy)
                }
            }
            when (clients) {
                is LoadState.Data -> {
                    Text("${clients.value.size} active clients", style = LegendTypography.Label, color = LegendColors.GoldSoft)
                    clients.value.take(3).forEach { client ->
                        Row(verticalAlignment = Alignment.CenterVertically) {
                            LegendProtectedAvatar(client.avatar, client.displayName, participantType, mediaRepository, size = 34.dp)
                            Spacer(Modifier.width(LegendSpacing.Xs))
                            Column(Modifier.weight(1f)) {
                                Text(client.displayName, style = LegendTypography.Label, color = LegendColors.OnNavy)
                                Text(client.crmStatus, style = LegendTypography.Supporting, color = LegendColors.GoldSoft)
                            }
                        }
                    }
                    if (clients.value.isEmpty()) Text("No active clients are currently available from the CRM authority.", style = LegendTypography.Supporting, color = LegendColors.GoldSoft)
                }
                is LoadState.Error -> Text(clients.message, style = LegendTypography.Supporting, color = LegendColors.GoldSoft)
                else -> Row(verticalAlignment = Alignment.CenterVertically) { CircularProgressIndicator(modifier = Modifier.size(16.dp), color = LegendColors.GoldBright, strokeWidth = 2.dp); Spacer(Modifier.width(LegendSpacing.Xs)); Text("Loading client CRM", style = LegendTypography.Supporting, color = LegendColors.GoldSoft) }
            }
            when (leads) {
                is LoadState.Data -> if (leads.value.isNotEmpty()) {
                    Text("LEADS", style = LegendTypography.Eyebrow, color = LegendColors.GoldBright)
                    leads.value.take(3).forEach { lead ->
                        Row(Modifier.fillMaxWidth(), verticalAlignment = Alignment.CenterVertically) {
                            Column(Modifier.weight(1f)) {
                                Text(lead.displayName, style = LegendTypography.Label, color = LegendColors.OnNavy)
                                Text(lead.crmStage, style = LegendTypography.Supporting, color = LegendColors.GoldSoft)
                            }
                            Text(legendCompactTime(lead.updatedUtc), style = LegendTypography.Label, color = LegendColors.TextTertiary)
                        }
                    }
                }
                else -> Unit
            }
            when (clientCreationPortal) {
                LoadState.Loading -> Row(verticalAlignment = Alignment.CenterVertically) {
                    CircularProgressIndicator(modifier = Modifier.size(16.dp), color = LegendColors.GoldBright, strokeWidth = 2.dp)
                    Spacer(Modifier.width(LegendSpacing.Xs))
                    Text("Opening client intake", style = LegendTypography.Supporting, color = LegendColors.GoldSoft)
                }
                is LoadState.Error -> Column(verticalArrangement = Arrangement.spacedBy(LegendSpacing.Xs)) {
                    Text(clientCreationPortal.message, style = LegendTypography.Supporting, color = LegendColors.Warning)
                    OutlinedButton(onClick = createClient, modifier = Modifier.fillMaxWidth(), shape = LegendShapes.Control) {
                        Text("Retry client intake", color = LegendColors.GoldBright)
                    }
                }
                else -> LegendPrimaryButton("Create client", modifier = Modifier.fillMaxWidth(), onClick = createClient)
            }
        }
    }
}

/**
 * Android host for the exact same short-lived AgentPortal client-intake page
 * used by iOS. There are intentionally no Kotlin fields or CRM mutations
 * here: the Razor page remains the only implementation of that workflow.
 */
@Composable
private fun LegendAgentClientCreationPortal(
    launchPath: String,
    dismiss: () -> Unit,
    recoverExpiredTicket: () -> Unit,
) {
    var failure by remember(launchPath) { mutableStateOf<String?>(null) }
    Dialog(
        onDismissRequest = dismiss,
        properties = DialogProperties(
            usePlatformDefaultWidth = false,
            decorFitsSystemWindows = false,
        ),
    ) {
        Surface(color = LegendColors.Canvas, modifier = Modifier.fillMaxSize()) {
            Column(Modifier.fillMaxSize()) {
                Row(
                    modifier = Modifier.fillMaxWidth().statusBarsPadding().padding(horizontal = LegendSpacing.Md, vertical = LegendSpacing.Xs),
                    verticalAlignment = Alignment.CenterVertically,
                ) {
                    Column(Modifier.weight(1f)) {
                        Text("CLIENT CRM", style = LegendTypography.Eyebrow, color = LegendColors.Gold)
                        Text("Create client", style = LegendTypography.Section, color = LegendColors.TextPrimary)
                    }
                    IconButton(
                        onClick = dismiss,
                        modifier = Modifier.size(LegendSize.MinimumTapTarget).background(LegendColors.SurfaceInset, CircleShape),
                    ) { Icon(Icons.Default.Close, "Close client intake", tint = LegendColors.TextPrimary) }
                }
                HorizontalDivider(color = LegendColors.Divider)
                if (failure == null) {
                    key(launchPath) {
                        AndroidView(
                            factory = { context ->
                                WebView(context).apply {
                                    settings.javaScriptEnabled = true
                                    settings.domStorageEnabled = true
                                    settings.allowFileAccess = false
                                    settings.allowContentAccess = false
                                    webViewClient = LegendClientCreationPortalWebViewClient(
                                        launchPath = launchPath,
                                        onCreated = dismiss,
                                        onSessionExpired = recoverExpiredTicket,
                                        onFailure = { failure = it },
                                    )
                                    loadUrl(launchPath)
                                }
                            },
                            modifier = Modifier.fillMaxSize(),
                        )
                    }
                } else {
                    Column(
                        modifier = Modifier.fillMaxSize().padding(LegendSpacing.Xl),
                        verticalArrangement = Arrangement.Center,
                    ) {
                        Text("Client intake unavailable", style = LegendTypography.Section, color = LegendColors.TextPrimary)
                        Spacer(Modifier.height(LegendSpacing.Xs))
                        Text(failure.orEmpty(), style = LegendTypography.Body, color = LegendColors.TextSecondary)
                        Spacer(Modifier.height(LegendSpacing.Md))
                        LegendPrimaryButton("Retry", modifier = Modifier.fillMaxWidth()) {
                            failure = null
                            recoverExpiredTicket()
                        }
                    }
                }
            }
        }
    }
}

private class LegendClientCreationPortalWebViewClient(
    launchPath: String,
    private val onCreated: () -> Unit,
    private val onSessionExpired: () -> Unit,
    private val onFailure: (String) -> Unit,
) : WebViewClient() {
    private val origin = Uri.parse(launchPath)
    private var completed = false

    override fun shouldOverrideUrlLoading(view: WebView, request: WebResourceRequest): Boolean {
        if (!request.isForMainFrame) return false
        val destination = request.url
        if (!isApprovedPortalLocation(destination)) return true
        if (destination.path == "/mobile/agent/clients/create-complete") {
            complete()
            return true
        }
        return false
    }

    override fun onReceivedHttpError(view: WebView, request: WebResourceRequest, response: WebResourceResponse) {
        if (!request.isForMainFrame) return
        when (response.statusCode) {
            401, 403 -> onSessionExpired()
            in 500..599 -> onFailure("The client intake could not be opened. Please try again.")
        }
    }

    override fun onReceivedError(view: WebView, request: WebResourceRequest, error: android.webkit.WebResourceError) {
        if (request.isForMainFrame && !completed) {
            onFailure(error.description?.toString() ?: "The client intake could not be opened. Please try again.")
        }
    }

    private fun isApprovedPortalLocation(destination: Uri): Boolean =
        destination.scheme.equals("https", ignoreCase = true) &&
            destination.scheme.equals(origin.scheme, ignoreCase = true) &&
            destination.host.equals(origin.host, ignoreCase = true) &&
            normalizedPort(destination) == normalizedPort(origin)

    private fun normalizedPort(uri: Uri): Int = when {
        uri.port != -1 -> uri.port
        uri.scheme.equals("https", ignoreCase = true) -> 443
        else -> -1
    }

    private fun complete() {
        if (completed) return
        completed = true
        onCreated()
    }
}

@Composable
private fun LegendHomeBrandBar(
    notificationCount: Int,
    create: (() -> Unit)?,
    notifications: (() -> Unit)?,
    showsHomeActions: Boolean = true,
    usesDarkSurface: Boolean = false,
) {
    Row(
        modifier = Modifier
            .fillMaxWidth()
            .heightIn(min = LegendSize.MinimumTapTarget)
            .background(if (usesDarkSurface) LegendColors.Midnight else LegendColors.Canvas)
            .statusBarsPadding()
            .padding(horizontal = LegendSpacing.Sm, vertical = LegendSpacing.Micro),
        verticalAlignment = Alignment.CenterVertically,
        horizontalArrangement = Arrangement.SpaceBetween,
    ) {
        if (showsHomeActions && create != null) {
            LegendHomeChromeButton(
                icon = Icons.Default.Add,
                description = "Create a LEGEND update",
                onClick = create,
            )
        } else {
            Spacer(Modifier.size(LegendSize.MinimumTapTarget))
        }
        Text(
            text = "LEGEND®",
            style = LegendTypography.Wordmark,
            color = if (usesDarkSurface) LegendColors.OnNavy else LegendColors.Navy,
        )
        if (showsHomeActions && notifications != null) {
            Box {
                LegendHomeChromeButton(
                    icon = Icons.Default.FavoriteBorder,
                    description = "Open notifications",
                    onClick = notifications,
                )
                if (notificationCount > 0) {
                    Text(
                        text = notificationCount.coerceAtMost(99).toString(),
                        style = LegendTypography.Label,
                        color = LegendColors.OnNavy,
                        modifier = Modifier
                            .align(Alignment.TopEnd)
                            .offset(x = LegendSpacing.Xs, y = -LegendSpacing.Micro)
                            .background(LegendColors.Error, CircleShape)
                            .padding(horizontal = LegendSpacing.Xs, vertical = LegendSpacing.Micro),
                    )
                }
            }
        } else {
            Spacer(Modifier.size(LegendSize.MinimumTapTarget))
        }
    }
}

@Composable
private fun LegendHomeChromeButton(
    icon: androidx.compose.ui.graphics.vector.ImageVector,
    description: String,
    onClick: () -> Unit,
) = IconButton(
    onClick = onClick,
    modifier = Modifier
        .size(LegendSize.MinimumTapTarget)
        .clip(CircleShape)
        .background(LegendColors.Navy),
) {
    Icon(icon, contentDescription = description, tint = LegendColors.OnNavy)
}

@Composable
private fun LegendHomeHero(home: MobileHomeResponse, openScripture: () -> Unit) {
    val firstName = home.identity.displayName.substringBefore(' ').ifBlank { home.identity.displayName }
    Card(
        onClick = openScripture,
        modifier = Modifier
            .fillMaxWidth()
            .border(
                LegendSpacing.Hairline,
                LegendColors.Gold.copy(alpha = 0.62f),
                LegendShapes.ProminentCard,
            ),
        shape = LegendShapes.ProminentCard,
        colors = CardDefaults.cardColors(containerColor = LegendColors.Navy),
        elevation = CardDefaults.cardElevation(defaultElevation = 8.dp),
    ) {
        Column(
            modifier = Modifier
                .fillMaxWidth()
                .background(LegendGradients.Hero)
                .padding(LegendSpacing.Sm),
            verticalArrangement = Arrangement.spacedBy(LegendSpacing.Sm),
        ) {
            Row(verticalAlignment = Alignment.CenterVertically) {
                Text("Welcome back,", style = LegendTypography.Supporting.copy(fontWeight = FontWeight.SemiBold), color = LegendColors.GoldBright)
                Spacer(Modifier.width(LegendSpacing.Xs))
                Text(firstName, style = LegendTypography.Section, color = LegendColors.OnNavy, maxLines = 1)
                Spacer(Modifier.weight(1f))
                Icon(Icons.Default.AutoAwesome, contentDescription = null, tint = LegendColors.GoldBright)
            }
            Column(verticalArrangement = Arrangement.spacedBy(LegendSpacing.Micro)) {
                Row(verticalAlignment = Alignment.CenterVertically) {
                    Text("DAILY SCRIPTURE", style = LegendTypography.Eyebrow.copy(letterSpacing = 1.sp), color = LegendColors.GoldBright)
                    Spacer(Modifier.weight(1f))
                    Icon(Icons.Default.NorthEast, contentDescription = null, tint = LegendColors.OnNavy.copy(alpha = 0.72f))
                }
                Text(home.dailyScripture.reference, style = LegendTypography.CardTitle, color = LegendColors.OnNavy)
                Text(
                    home.dailyScripture.text.ifBlank { home.dailyScripture.passageText },
                    style = LegendTypography.Caption,
                    color = LegendColors.OnNavy.copy(alpha = 0.78f),
                    maxLines = 2,
                    overflow = TextOverflow.Ellipsis,
                )
            }
        }
    }
}

@Composable
private fun LegendHomeActivityPill(count: Int, hasActivity: Boolean, openActivity: () -> Unit) = Card(
    onClick = openActivity,
    modifier = Modifier
        .fillMaxWidth()
        .border(
            LegendSpacing.Hairline,
            LegendColors.Gold.copy(alpha = 0.58f),
            CircleShape,
        ),
    shape = CircleShape,
    colors = CardDefaults.cardColors(containerColor = LegendColors.Navy),
    elevation = CardDefaults.cardElevation(defaultElevation = 5.dp),
) {
    Row(
        modifier = Modifier
            .fillMaxWidth()
            .background(LegendGradients.Hero)
            .padding(horizontal = LegendSpacing.CardContent, vertical = LegendSpacing.Xs),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        Icon(
            Icons.Default.Checklist,
            contentDescription = null,
            tint = LegendColors.GoldBright,
            modifier = Modifier
                .size(LegendSize.AvatarMedium)
                .border(LegendSpacing.Hairline, LegendColors.GoldBright.copy(alpha = 0.22f), CircleShape)
                .padding(LegendSpacing.Xs),
        )
        Spacer(Modifier.width(LegendSpacing.Sm))
        Column(Modifier.weight(1f)) {
            Text("TODAY'S ACTIVITY", style = LegendTypography.Eyebrow.copy(letterSpacing = 1.sp), color = LegendColors.GoldBright)
            Text(if (hasActivity) "Your live LEGEND activity" else "Your day is clear", style = LegendTypography.Body, color = LegendColors.OnNavy.copy(alpha = 0.84f))
        }
        Text(count.toString(), style = LegendTypography.Title, color = if (count > 0) LegendColors.Error else LegendColors.OnNavy)
        Spacer(Modifier.width(LegendSpacing.Xs))
        Icon(Icons.Default.ChevronRight, contentDescription = "Open today's activity", tint = LegendColors.OnNavy.copy(alpha = 0.70f))
    }
}

@Composable
private fun LegendStoryRail(
    currentActor: MobileActor,
    stories: List<SocialPost>,
    mediaRepository: AuthenticatedMediaRepository,
    participantType: String,
    create: () -> Unit,
) {
    val orderedAuthors = stories.map { it.author }.distinctBy { "${it.identity.participantType}:${it.identity.userId}" }
    LazyRow(
        horizontalArrangement = Arrangement.spacedBy(LegendSpacing.Sm),
        contentPadding = PaddingValues(horizontal = LegendSpacing.Xs, vertical = LegendSpacing.Tiny),
    ) {
        item {
            Column(
                horizontalAlignment = Alignment.CenterHorizontally,
                modifier = Modifier
                    .width(LegendSize.AvatarHero)
                    .clickable(onClick = create),
            ) {
                Box(contentAlignment = Alignment.BottomEnd) {
                    LegendProtectedAvatar(
                        avatar = currentActor.avatar,
                        displayName = currentActor.displayName,
                        participantType = participantType,
                        repository = mediaRepository,
                        modifier = Modifier
                            .padding(3.dp)
                            .border(2.dp, LegendColors.Gold, CircleShape),
                        size = 58.dp,
                    )
                    Box(
                        modifier = Modifier
                            .size(22.dp)
                            .clip(CircleShape)
                            .background(LegendColors.Navy)
                            .border(LegendSpacing.Hairline, LegendColors.OnNavy, CircleShape),
                        contentAlignment = Alignment.Center,
                    ) {
                        Icon(Icons.Default.Add, "Create your story", tint = LegendColors.OnNavy, modifier = Modifier.size(15.dp))
                    }
                }
                Spacer(Modifier.height(LegendSpacing.Xs))
                Text("Your story", style = LegendTypography.Label, color = LegendColors.TextPrimary, maxLines = 1)
            }
        }
        items(orderedAuthors, key = { "${it.identity.participantType}:${it.identity.userId}" }) { author ->
            Column(
                horizontalAlignment = Alignment.CenterHorizontally,
                modifier = Modifier.width(LegendSize.AvatarHero),
            ) {
                LegendProtectedAvatar(
                    avatar = author.avatar,
                    displayName = author.displayName,
                    participantType = participantType,
                    repository = mediaRepository,
                    modifier = Modifier
                        .padding(3.dp)
                        .border(2.dp, LegendColors.Gold, CircleShape),
                    size = 58.dp,
                )
                Spacer(Modifier.height(LegendSpacing.Xs))
                Text(author.displayName, style = LegendTypography.Label, color = LegendColors.TextPrimary, maxLines = 1, overflow = TextOverflow.Ellipsis)
            }
        }
    }
}

@Composable
private fun LegendPromotedGroupCard(
    group: SocialPromotedGroup,
    mediaRepository: AuthenticatedMediaRepository,
    participantType: String,
    join: () -> Unit,
) = Card(
    modifier = Modifier.fillMaxWidth(),
    shape = LegendShapes.ProminentCard,
    colors = CardDefaults.cardColors(containerColor = LegendColors.Navy),
    elevation = CardDefaults.cardElevation(defaultElevation = 7.dp),
) {
    Row(
        modifier = Modifier
            .fillMaxWidth()
            .background(Brush.linearGradient(listOf(LegendColors.Navy, LegendColors.Midnight)))
            .padding(LegendSpacing.CardContent),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        if (group.groupAvatar != null) {
            LegendProtectedAvatar(
                avatar = group.groupAvatar,
                displayName = group.subject,
                participantType = participantType,
                repository = mediaRepository,
                modifier = Modifier.clip(LegendShapes.Compact),
                size = LegendSize.AvatarLarge,
            )
        } else {
            Box(
                modifier = Modifier.size(LegendSize.AvatarLarge).clip(LegendShapes.Compact).background(LegendColors.Gold),
                contentAlignment = Alignment.Center,
            ) { Icon(Icons.Default.Groups, contentDescription = null, tint = LegendColors.Midnight) }
        }
        Spacer(Modifier.width(LegendSpacing.Sm))
        Column(Modifier.weight(1f)) {
            Text("FEATURED GROUP", style = LegendTypography.Eyebrow.copy(letterSpacing = 1.sp), color = LegendColors.GoldBright)
            Text(group.subject, style = LegendTypography.Section, color = LegendColors.OnNavy, maxLines = 1, overflow = TextOverflow.Ellipsis)
            Text("Hosted by ${group.owner.displayName} · ${group.activeMemberCount} members", style = LegendTypography.Supporting, color = LegendColors.OnNavy.copy(alpha = 0.72f), maxLines = 1, overflow = TextOverflow.Ellipsis)
        }
        Spacer(Modifier.width(LegendSpacing.Xs))
        Button(
            onClick = join,
            enabled = !group.isJoinedByCurrentActor,
            colors = ButtonDefaults.buttonColors(containerColor = LegendColors.GoldBright, contentColor = LegendColors.Midnight, disabledContainerColor = LegendColors.OnNavy.copy(alpha = 0.18f), disabledContentColor = LegendColors.OnNavy),
            contentPadding = PaddingValues(horizontal = LegendSpacing.Sm),
        ) { Text(if (group.isJoinedByCurrentActor) "Joined" else "Join", style = LegendTypography.Label) }
    }
}

@Composable
private fun LegendHomeSocialLoading() = Card(
    modifier = Modifier.fillMaxWidth(),
    shape = LegendShapes.Card,
    colors = CardDefaults.cardColors(containerColor = LegendColors.SurfaceElevated),
) {
    Row(Modifier.padding(LegendSpacing.CardContent), verticalAlignment = Alignment.CenterVertically) {
        CircularProgressIndicator(modifier = Modifier.size(LegendSize.AvatarSmall), color = LegendColors.Navy, strokeWidth = LegendSpacing.Hairline)
        Spacer(Modifier.width(LegendSpacing.Sm))
        Text("Loading your secure community feed…", style = LegendTypography.Supporting, color = LegendColors.TextSecondary)
    }
}

@Composable
private fun LegendInlineRetry(message: String, retry: () -> Unit) = Card(
    modifier = Modifier.fillMaxWidth(),
    shape = LegendShapes.Card,
    colors = CardDefaults.cardColors(containerColor = LegendColors.SurfaceElevated),
) {
    Row(Modifier.padding(LegendSpacing.CardContent), verticalAlignment = Alignment.CenterVertically) {
        Text(message, modifier = Modifier.weight(1f), style = LegendTypography.Supporting, color = LegendColors.TextSecondary)
        TextButton(onClick = retry) { Text("Retry", color = LegendColors.Navy) }
    }
}

@Composable
private fun LegendHomeEmptyFeed(create: () -> Unit) = Card(
    onClick = create,
    modifier = Modifier.fillMaxWidth(),
    shape = LegendShapes.Card,
    colors = CardDefaults.cardColors(containerColor = LegendColors.SurfaceElevated),
) {
    Column(Modifier.padding(LegendSpacing.CardContent), verticalArrangement = Arrangement.spacedBy(LegendSpacing.Xs)) {
        Text("Your community is ready", style = LegendTypography.CardTitle, color = LegendColors.TextPrimary)
        Text("Create the first server-authorized LEGEND update for your feed.", style = LegendTypography.Supporting, color = LegendColors.TextSecondary)
    }
}

@Composable
private fun DailyScriptureSheet(scripture: MobileDailyScripture, dismiss: () -> Unit) = ModalBottomSheet(
    onDismissRequest = dismiss,
    containerColor = LegendColors.Midnight,
) {
    Column(
        modifier = Modifier.fillMaxWidth().padding(LegendSpacing.Lg),
        verticalArrangement = Arrangement.spacedBy(LegendSpacing.Sm),
    ) {
        Text("DAILY SCRIPTURE", style = LegendTypography.Eyebrow.copy(letterSpacing = 1.sp), color = LegendColors.GoldBright)
        Text(scripture.reference, style = LegendTypography.Hero, color = LegendColors.OnNavy)
        Text(scripture.passageText.ifBlank { scripture.text }, style = LegendTypography.Body, color = LegendColors.OnNavy.copy(alpha = 0.86f))
        Text(scripture.translation, style = LegendTypography.Label, color = LegendColors.GoldBright)
        LegendPrimaryButton("Close", onClick = dismiss)
    }
}

@Composable
private fun HomeActivitySheet(actions: List<MobileActionItem>, dismiss: () -> Unit) = ModalBottomSheet(
    onDismissRequest = dismiss,
    containerColor = LegendColors.Surface,
) {
    LazyColumn(
        modifier = Modifier.fillMaxWidth(),
        contentPadding = PaddingValues(LegendSpacing.Lg),
        verticalArrangement = Arrangement.spacedBy(LegendSpacing.Sm),
    ) {
        item {
            Text("Today's activity", style = LegendTypography.Title, color = LegendColors.TextPrimary)
            Text("Your server-authorized LEGEND action projection.", style = LegendTypography.Supporting, color = LegendColors.TextSecondary)
        }
        items(actions, key = { "action-${it.id}" }) { action ->
            LegendActivityRow(action.title, "${action.priority} · ${action.status}")
        }
        if (actions.isEmpty()) item { Text("Your day is clear.", style = LegendTypography.Body, color = LegendColors.TextSecondary) }
        item { LegendPrimaryButton("Close", onClick = dismiss) }
    }
}

@Composable
private fun LegendActivityRow(title: String, detail: String) = Card(
    modifier = Modifier.fillMaxWidth(),
    shape = LegendShapes.Control,
    colors = CardDefaults.cardColors(containerColor = LegendColors.SurfaceElevated),
) {
    Column(Modifier.padding(LegendSpacing.Md), verticalArrangement = Arrangement.spacedBy(LegendSpacing.Micro)) {
        Text(title, style = LegendTypography.CardTitle, color = LegendColors.TextPrimary)
        Text(detail, style = LegendTypography.Supporting, color = LegendColors.TextSecondary)
    }
}

@Composable
private fun NotificationInboxSheet(
    state: LoadState<NotificationSnapshot>,
    dismiss: () -> Unit,
    retry: () -> Unit,
    clearBadges: () -> Unit,
    open: (NotificationItem) -> Unit,
) = ModalBottomSheet(onDismissRequest = dismiss, containerColor = LegendColors.Surface) {
    when (state) {
        LoadState.Idle,
        LoadState.Loading -> Box(Modifier.fillMaxWidth().height(LegendSize.AvatarHero), contentAlignment = Alignment.Center) { CircularProgressIndicator(color = LegendColors.Navy) }
        is LoadState.Error -> Column(Modifier.padding(LegendSpacing.Lg)) { Text(state.message, color = LegendColors.TextSecondary); LegendPrimaryButton("Retry", onClick = retry) }
        is LoadState.Data -> {
            val snapshot = state.value
            LazyColumn(
                modifier = Modifier.fillMaxWidth(),
                contentPadding = PaddingValues(LegendSpacing.Lg),
                verticalArrangement = Arrangement.spacedBy(LegendSpacing.Sm),
            ) {
                item {
                    Row(verticalAlignment = Alignment.CenterVertically) {
                        Column(Modifier.weight(1f)) {
                            Text("Notifications", style = LegendTypography.Title, color = LegendColors.TextPrimary)
                            Text("${snapshot.badge.unreadCount} unread", style = LegendTypography.Supporting, color = LegendColors.TextSecondary)
                        }
                        TextButton(onClick = clearBadges) { Text("Clear badges", color = LegendColors.Navy) }
                    }
                }
                if (snapshot.notifications.isEmpty()) item { Text("No notifications yet.", style = LegendTypography.Body, color = LegendColors.TextSecondary) }
                items(snapshot.notifications, key = { it.id }) { item ->
                    Card(
                        onClick = { open(item) },
                        modifier = Modifier.fillMaxWidth(),
                        shape = LegendShapes.Control,
                        colors = CardDefaults.cardColors(containerColor = if (item.isRead) LegendColors.SurfaceElevated else LegendColors.GoldSoft),
                    ) {
                        Column(Modifier.padding(LegendSpacing.Md), verticalArrangement = Arrangement.spacedBy(LegendSpacing.Micro)) {
                            Text(item.title, style = LegendTypography.CardTitle, color = LegendColors.TextPrimary)
                            Text(item.detail, style = LegendTypography.Supporting, color = LegendColors.TextSecondary)
                        }
                    }
                }
            }
        }
    }
}

@Composable
private fun MessagesScreen(
    viewModel: MessagingViewModel,
    mediaRepository: AuthenticatedMediaRepository,
    participantType: String,
    requestedConversationId: String?,
    onRequestedConversationOpened: () -> Unit,
    onThreadOpenChanged: (Boolean) -> Unit,
) {
    val context = LocalContext.current
    val conversations by viewModel.conversations.collectAsStateWithLifecycle()
    val detail by viewModel.detail.collectAsStateWithLifecycle()
    var selectedConversationId by remember { mutableStateOf<String?>(null) }
    var creatingConversation by remember { mutableStateOf(false) }
    var conversationMenu by remember { mutableStateOf<ConversationSummary?>(null) }
    var callDirectoryOpen by remember { mutableStateOf(false) }
    var callTarget by remember { mutableStateOf<ConversationSummary?>(null) }
    var managingGroup by remember { mutableStateOf<ConversationDetail?>(null) }
    var addingGroupMember by remember { mutableStateOf<ConversationDetail?>(null) }
    LaunchedEffect(Unit) { viewModel.load() }
    LaunchedEffect(requestedConversationId, conversations) {
        val requestedId = requestedConversationId ?: return@LaunchedEffect
        val rows = (conversations as? LoadState.Data<List<ConversationSummary>>)?.value ?: return@LaunchedEffect
        rows.firstOrNull { it.id == requestedId }?.let { conversation ->
            selectedConversationId = conversation.id
            viewModel.open(conversation.id)
            onRequestedConversationOpened()
        }
    }
    DisposableEffect(selectedConversationId) {
        onThreadOpenChanged(selectedConversationId != null)
        onDispose { onThreadOpenChanged(false) }
    }

    if (selectedConversationId == null) {
        Box(Modifier.fillMaxSize().background(LegendColors.Canvas)) {
            when (conversations) {
                LoadState.Idle,
                LoadState.Loading -> LegendLoadingState()

                is LoadState.Error -> LegendErrorState((conversations as LoadState.Error).message, viewModel::load)
                is LoadState.Data -> {
                    val rows = (conversations as LoadState.Data<List<ConversationSummary>>).value
                    LazyColumn(
                        verticalArrangement = Arrangement.spacedBy(LegendSpacing.Sm),
                        contentPadding = PaddingValues(vertical = LegendSpacing.Md, horizontal = LegendSpacing.PageHorizontal),
                    ) {
                        item {
                            LegendMessagesInboxHeader(
                                onNewMessage = {
                                    creatingConversation = true
                                    viewModel.loadRecipients()
                                },
                                onCallDirectory = { callDirectoryOpen = true },
                            )
                        }
                        if (rows.isEmpty()) {
                            item {
                                LegendMessagingEmptyCard(
                                    title = "Start a private conversation",
                                    detail = "Choose someone in your LEGEND network to begin.",
                                    action = {
                                        creatingConversation = true
                                        viewModel.loadRecipients()
                                    },
                                )
                            }
                        } else {
                            items(rows, key = { it.id }) { row ->
                                LegendConversationRow(
                                    conversation = row,
                                    mediaRepository = mediaRepository,
                                    participantType = participantType,
                                    open = {
                                        selectedConversationId = row.id
                                        viewModel.open(row.id)
                                    },
                                    more = { conversationMenu = row },
                                )
                            }
                        }
                    }
                }
            }
        }
    } else {
        when (detail) {
                LoadState.Idle,
                LoadState.Loading -> LegendLoadingState()

                is LoadState.Error -> LegendErrorState((detail as LoadState.Error).message) {
                    selectedConversationId?.let(viewModel::open)
                }
                is LoadState.Data -> MessageThread(
                    conversation = (detail as LoadState.Data<ConversationDetail>).value,
                    mediaRepository = mediaRepository,
                    participantType = participantType,
                    isSending = viewModel.isSending.collectAsStateWithLifecycle().value,
                    back = { selectedConversationId = null },
                    send = viewModel::send,
                    loadOlder = viewModel::loadOlder,
                    delete = viewModel::deleteMessage,
                    manageGroup = { managingGroup = it },
                    resolveVerification = viewModel::resolveVerification,
                )
        }
    }

    if (creatingConversation) {
        LegendRecipientPicker(
            state = viewModel.recipients.collectAsStateWithLifecycle().value,
            mediaRepository = mediaRepository,
            participantType = participantType,
            load = viewModel::loadRecipients,
            choose = { recipient ->
                viewModel.startConversation(recipient) { id ->
                    creatingConversation = false
                    selectedConversationId = id
                }
            },
            createGroup = { subject, recipients, image ->
                viewModel.createGroup(context, subject, recipients, image) { id ->
                    creatingConversation = false
                    selectedConversationId = id
                }
            },
            isSending = viewModel.isSending.collectAsStateWithLifecycle().value,
            dismiss = { creatingConversation = false },
        )
    }
    if (callDirectoryOpen) {
        val rows = (conversations as? LoadState.Data<List<ConversationSummary>>)?.value.orEmpty()
        LegendMessagingCallDirectorySheet(
            conversations = rows,
            mediaRepository = mediaRepository,
            participantType = participantType,
            dismiss = { callDirectoryOpen = false },
            select = { conversation ->
                callTarget = conversation
                viewModel.loadCallOptions(conversation.id)
            },
        )
    }
    callTarget?.let { conversation ->
        LegendConversationCallSheet(
            state = viewModel.callOptions.collectAsStateWithLifecycle().value,
            fallbackName = conversation.title,
            dismiss = { callTarget = null },
        )
    }
    conversationMenu?.let { conversation ->
        LegendConversationActions(
            conversation = conversation,
            dismiss = { conversationMenu = null },
            pin = { viewModel.setPinned(conversation, !conversation.isPinned) },
            mute = { viewModel.setMuted(conversation, !conversation.isMuted) },
            remove = {
                viewModel.remove(conversation.id) {
                    if (selectedConversationId == conversation.id) selectedConversationId = null
                }
            },
        )
    }
    managingGroup?.let { group ->
        LegendGroupManagementSheet(
            conversation = group,
            mediaRepository = mediaRepository,
            participantType = participantType,
            dismiss = { managingGroup = null },
            addMember = { addingGroupMember = group; viewModel.loadRecipients() },
            updateSubject = { viewModel.updateGroup(group.id, it) },
            updateImage = { image -> viewModel.updateGroupImage(context, group.id, group.title, image) },
            updateMeeting = { meeting -> viewModel.updateGroup(group.id, group.title, meeting) },
            setManager = { member, isManager -> viewModel.setGroupManager(group.id, member, isManager) },
            setPromotion = { enabled -> viewModel.setGroupPromotion(group.id, enabled) },
            deleteGroup = { viewModel.deleteGroup(group.id) { managingGroup = null; selectedConversationId = null } },
        )
    }
    addingGroupMember?.let { group ->
        LegendGroupMemberPicker(
            state = viewModel.recipients.collectAsStateWithLifecycle().value,
            mediaRepository = mediaRepository,
            participantType = participantType,
            load = viewModel::loadRecipients,
            dismiss = { addingGroupMember = null },
            select = { recipient -> viewModel.addGroupParticipant(group.id, recipient); addingGroupMember = null },
        )
    }
}

@Composable
private fun LegendMessagesInboxHeader(
    onNewMessage: () -> Unit,
    onCallDirectory: () -> Unit,
) {
    Surface(
        color = LegendColors.Navy,
        shape = LegendShapes.Card,
        modifier = Modifier.fillMaxWidth(),
    ) {
        Row(
            Modifier.padding(LegendSpacing.Md),
            verticalAlignment = Alignment.CenterVertically,
        ) {
            Text("Messages", style = LegendTypography.Section, color = LegendColors.OnNavy, modifier = Modifier.weight(1f))
            IconButton(
                onClick = onNewMessage,
                modifier = Modifier
                    .size(48.dp)
                    .background(LegendGradients.Gold, CircleShape)
                    .border(LegendSpacing.Hairline, LegendColors.OnNavy.copy(alpha = 0.30f), CircleShape),
            ) { Icon(Icons.Default.Edit, "Start a new conversation", tint = LegendColors.Midnight) }
            Spacer(Modifier.width(LegendSpacing.Sm))
            IconButton(
                onClick = onCallDirectory,
                modifier = Modifier
                    .size(48.dp)
                    .background(LegendColors.OnNavy.copy(alpha = 0.12f), CircleShape)
                    .border(LegendSpacing.Hairline, LegendColors.Gold.copy(alpha = 0.66f), CircleShape),
            ) { Icon(Icons.Default.Phone, "Call a connection", tint = LegendColors.OnNavy) }
        }
    }
}

@Composable
private fun LegendConversationRow(
    conversation: ConversationSummary,
    mediaRepository: AuthenticatedMediaRepository,
    participantType: String,
    open: () -> Unit,
    more: (() -> Unit)?,
) {
    val isGroup = conversation.conversationType.equals("Group", ignoreCase = true)
    val relationship = when {
        isGroup -> "Group chat"
        conversation.counterparty.identity.participantType.equals("Agent", ignoreCase = true) ->
            conversation.counterparty.roleLabel?.takeIf(String::isNotBlank) ?: "LEGEND guide"
        else -> "Connection"
    }
    LegendContactCard(
        displayName = conversation.title,
        subtitle = conversation.lastMessagePreview ?: "Start your conversation",
        detail = relationship,
        isVerified = !isGroup && conversation.counterparty.isVerified,
        onClick = open,
        onLongClick = more,
        avatar = {
            LegendProtectedAvatar(
                avatar = conversation.groupAvatar ?: conversation.counterparty.avatar,
                displayName = conversation.title,
                participantType = participantType,
                repository = mediaRepository,
                size = 46.dp,
            )
        },
        action = {
            Column(
                horizontalAlignment = Alignment.End,
                verticalArrangement = Arrangement.spacedBy(LegendSpacing.Tiny),
            ) {
                conversation.lastMessageUtc?.let {
                    Text(
                        legendCompactTime(it),
                        style = LegendTypography.Caption,
                        color = if (conversation.unreadCount > 0) LegendColors.Error else LegendColors.OnNavy.copy(alpha = LegendOpacity.ContactAction),
                        maxLines = 1,
                    )
                }
                when {
                    conversation.isPinned -> Icon(Icons.Default.PushPin, "Pinned conversation", modifier = Modifier.size(15.dp), tint = LegendColors.GoldBright)
                    conversation.unreadCount > 0 -> Text(
                        conversation.unreadCount.coerceAtMost(99).toString(),
                        style = LegendTypography.Label,
                        color = LegendColors.OnNavy,
                        modifier = Modifier
                            .background(LegendColors.Error, CircleShape)
                            .padding(horizontal = LegendSpacing.Xs, vertical = LegendSpacing.Micro),
                    )
                    conversation.isMuted -> Icon(Icons.Default.NotificationsOff, "Muted conversation", modifier = Modifier.size(15.dp), tint = LegendColors.OnNavy.copy(alpha = LegendOpacity.ContactAction))
                    else -> Icon(Icons.Default.ChevronRight, "Open conversation", modifier = Modifier.size(20.dp), tint = LegendColors.OnNavy.copy(alpha = LegendOpacity.ContactAction))
                }
            }
        },
    )
}

/** Android's platform-native counterpart to the iOS Messages call directory. */
@Composable
private fun LegendMessagingCallDirectorySheet(
    conversations: List<ConversationSummary>,
    mediaRepository: AuthenticatedMediaRepository,
    participantType: String,
    dismiss: () -> Unit,
    select: (ConversationSummary) -> Unit,
) {
    val directConversations = conversations.filterNot {
        it.conversationType.equals("Group", ignoreCase = true)
    }
    ModalBottomSheet(
        onDismissRequest = dismiss,
        containerColor = LegendColors.Canvas,
    ) {
        LazyColumn(
            modifier = Modifier.fillMaxHeight(.88f),
            contentPadding = PaddingValues(
                horizontal = LegendSpacing.PageHorizontal,
                vertical = LegendSpacing.Md,
            ),
            verticalArrangement = Arrangement.spacedBy(LegendSpacing.Sm),
        ) {
            item {
                Row(verticalAlignment = Alignment.CenterVertically) {
                    Column(Modifier.weight(1f)) {
                        Text("CALL", style = LegendTypography.Eyebrow, color = LegendColors.Gold)
                        Text("Call a connection", style = LegendTypography.Section, color = LegendColors.TextPrimary)
                        Text(
                            "Calls open through your device’s secure Phone experience.",
                            style = LegendTypography.Supporting,
                            color = LegendColors.TextSecondary,
                        )
                    }
                    IconButton(onClick = dismiss, modifier = Modifier.size(LegendSize.MinimumTapTarget)) {
                        Icon(Icons.Default.Close, "Close call directory", tint = LegendColors.TextPrimary)
                    }
                }
            }
            if (directConversations.isEmpty()) {
                item {
                    LegendMessagingEmptyCard(
                        title = "No direct conversations",
                        detail = "Start a private conversation to call a connection.",
                        action = dismiss,
                    )
                }
            } else {
                items(directConversations, key = { it.id }) { conversation ->
                    LegendConversationRow(
                        conversation = conversation,
                        mediaRepository = mediaRepository,
                        participantType = participantType,
                        open = { select(conversation) },
                        more = null,
                    )
                }
            }
        }
    }
}

/** Uses the existing server-issued call addresses and Android's safe dial intent. */
@Composable
private fun LegendConversationCallSheet(
    state: LoadState<ConversationCallOptions>,
    fallbackName: String,
    dismiss: () -> Unit,
) {
    val context = LocalContext.current
    ModalBottomSheet(
        onDismissRequest = dismiss,
        containerColor = LegendColors.Canvas,
    ) {
        Column(
            modifier = Modifier
                .fillMaxWidth()
                .padding(
                    horizontal = LegendSpacing.PageHorizontal,
                    vertical = LegendSpacing.Xl,
                ),
            horizontalAlignment = Alignment.CenterHorizontally,
            verticalArrangement = Arrangement.spacedBy(LegendSpacing.Md),
        ) {
            when (state) {
                LoadState.Idle,
                LoadState.Loading -> {
                    CircularProgressIndicator(color = LegendColors.Gold)
                    Text("Preparing secure call options", style = LegendTypography.Supporting, color = LegendColors.TextSecondary)
                }
                is LoadState.Error -> {
                    Icon(Icons.Default.PhoneDisabled, null, tint = LegendColors.Warning, modifier = Modifier.size(30.dp))
                    Text("Calling unavailable", style = LegendTypography.Section, color = LegendColors.TextPrimary)
                    Text(state.message, style = LegendTypography.Supporting, color = LegendColors.TextSecondary)
                }
                is LoadState.Data -> {
                    val options = state.value
                    Icon(
                        Icons.Default.PhoneInTalk,
                        null,
                        tint = LegendColors.Midnight,
                        modifier = Modifier
                            .size(68.dp)
                            .background(LegendGradients.Gold, CircleShape)
                            .padding(LegendSpacing.Sm),
                    )
                    Text(options.displayName, style = LegendTypography.Section, color = LegendColors.TextPrimary)
                    val phoneNumber = options.phoneNumber?.trim().orEmpty()
                    if (phoneNumber.isNotEmpty()) {
                        Button(
                            onClick = {
                                context.startActivity(
                                    Intent(
                                        Intent.ACTION_DIAL,
                                        Uri.parse("tel:${Uri.encode(phoneNumber)}"),
                                    ),
                                )
                                dismiss()
                            },
                            modifier = Modifier.fillMaxWidth(),
                            shape = LegendShapes.Control,
                            colors = ButtonDefaults.buttonColors(
                                containerColor = LegendColors.Navy,
                                contentColor = LegendColors.OnNavy,
                            ),
                        ) {
                            Icon(Icons.Default.Phone, null)
                            Spacer(Modifier.width(LegendSpacing.Xs))
                            Text("Phone call", style = LegendTypography.BodyEmphasis)
                        }
                    } else {
                        Text("$fallbackName has not shared a call address for this private conversation.", style = LegendTypography.Supporting, color = LegendColors.TextSecondary)
                    }
                }
            }
            TextButton(onClick = dismiss) { Text("Done", color = LegendColors.Gold) }
        }
    }
}

@Composable
private fun LegendMessagingEmptyCard(title: String, detail: String, action: () -> Unit) {
    Surface(color = LegendColors.Surface, shape = LegendShapes.Card, modifier = Modifier.fillMaxWidth()) {
        Column(Modifier.padding(LegendSpacing.Lg), horizontalAlignment = Alignment.CenterHorizontally, verticalArrangement = Arrangement.spacedBy(LegendSpacing.Sm)) {
            Icon(Icons.Default.ChatBubbleOutline, null, tint = LegendColors.Gold, modifier = Modifier.size(32.dp))
            Text(title, style = LegendTypography.Section, color = LegendColors.TextPrimary)
            Text(detail, style = LegendTypography.Supporting, color = LegendColors.TextSecondary, textAlign = androidx.compose.ui.text.style.TextAlign.Center)
            LegendPrimaryButton("New message", onClick = action)
        }
    }
}

@Composable
private fun LegendRecipientPicker(
    state: LoadState<List<MessagingRecipient>>,
    mediaRepository: AuthenticatedMediaRepository,
    participantType: String,
    load: (String?, String?) -> Unit,
    choose: (MessagingRecipient) -> Unit,
    createGroup: (String, List<MessagingRecipient>, Uri?) -> Unit,
    isSending: Boolean,
    dismiss: () -> Unit,
) {
    val context = LocalContext.current
    var search by remember { mutableStateOf("") }
    var scope by remember { mutableStateOf<String?>(null) }
    var isCreatingGroup by remember { mutableStateOf(false) }
    var groupSubject by remember { mutableStateOf("") }
    var groupRecipients by remember { mutableStateOf<List<MessagingRecipient>>(emptyList()) }
    var groupImage by remember { mutableStateOf<Uri?>(null) }
    val groupPhotoPicker = rememberLauncherForActivityResult(ActivityResultContracts.PickVisualMedia()) { groupImage = it }
    LaunchedEffect(search, scope) { load(search.trim().takeIf { it.isNotEmpty() }, scope) }
    ModalBottomSheet(onDismissRequest = dismiss, containerColor = LegendColors.Canvas) {
        Column(Modifier.fillMaxHeight(0.9f).padding(horizontal = LegendSpacing.PageHorizontal)) {
            Row(verticalAlignment = Alignment.CenterVertically) {
                Column(Modifier.weight(1f)) {
                    Text(if (isCreatingGroup) "New group" else "New message", style = LegendTypography.Section, color = LegendColors.TextPrimary)
                    Text(if (isCreatingGroup) "Choose at least two connections" else "Search your LEGEND network", style = LegendTypography.Supporting, color = LegendColors.TextSecondary)
                }
                TextButton(onClick = dismiss) { Text("Cancel", color = LegendColors.Gold) }
            }
            Row(horizontalArrangement = Arrangement.spacedBy(LegendSpacing.Xs), modifier = Modifier.padding(top = LegendSpacing.Xs)) {
                FilterChip(selected = !isCreatingGroup, onClick = { isCreatingGroup = false; groupRecipients = emptyList() }, label = { Text("Direct") }, colors = legendCompactChipColors(!isCreatingGroup))
                FilterChip(selected = isCreatingGroup, onClick = { isCreatingGroup = true }, label = { Text("Group") }, colors = legendCompactChipColors(isCreatingGroup))
            }
            if (isCreatingGroup) {
                Row(Modifier.padding(top = LegendSpacing.Sm), verticalAlignment = Alignment.CenterVertically) {
                    IconButton(onClick = { groupPhotoPicker.launch(PickVisualMediaRequest(ActivityResultContracts.PickVisualMedia.ImageOnly)) }, modifier = Modifier.background(LegendColors.Navy, CircleShape)) {
                        Icon(if (groupImage == null) Icons.Default.AddAPhoto else Icons.Default.Image, "Choose group photo", tint = LegendColors.GoldBright)
                    }
                    Spacer(Modifier.width(LegendSpacing.Xs))
                    OutlinedTextField(
                        value = groupSubject,
                        onValueChange = { groupSubject = it },
                        modifier = Modifier.weight(1f),
                        singleLine = true,
                        placeholder = { Text("Group name") },
                        shape = LegendShapes.Control,
                        colors = legendMessagingFieldColors(),
                    )
                }
                if (groupRecipients.isNotEmpty()) {
                    Text("${groupRecipients.size} members selected", style = LegendTypography.Label, color = LegendColors.Gold, modifier = Modifier.padding(top = LegendSpacing.Xs))
                }
                LegendPrimaryButton(
                    text = if (isSending) "Creating group…" else "Create group (${groupRecipients.size})",
                    modifier = Modifier.fillMaxWidth().padding(top = LegendSpacing.Xs),
                    enabled = !isSending && groupSubject.isNotBlank() && groupRecipients.size >= 2,
                ) { createGroup(groupSubject, groupRecipients, groupImage) }
            }
            OutlinedTextField(value = search, onValueChange = { search = it }, modifier = Modifier.fillMaxWidth().padding(top = LegendSpacing.Sm), singleLine = true, leadingIcon = { Icon(Icons.Default.Search, null) }, placeholder = { Text("Search people") })
            LazyRow(Modifier.padding(vertical = LegendSpacing.Sm), horizontalArrangement = Arrangement.spacedBy(LegendSpacing.Xs)) {
                item {
                    FilterChip(selected = scope == null, onClick = { scope = null }, label = { Text("All") })
                }
                items(listOf("Clients", "Agents", "Leads")) { value ->
                    FilterChip(selected = scope == value, onClick = { scope = value }, label = { Text(value) })
                }
            }
            when (state) {
                LoadState.Idle, LoadState.Loading -> LegendLoadingState()
                is LoadState.Error -> LegendErrorState(state.message) { load(search, scope) }
                is LoadState.Data -> {
                    if (state.value.isEmpty()) {
                        LegendEmptyState("No people found", "Try another category or search.")
                    } else {
                        LazyColumn(verticalArrangement = Arrangement.spacedBy(LegendSpacing.Xs), contentPadding = PaddingValues(bottom = LegendSpacing.Lg)) {
                            items(state.value, key = { "${it.identity.userId}:${it.identity.participantType}" }) { recipient ->
                                Surface(
                                    modifier = Modifier.fillMaxWidth().clickable {
                                        if (isCreatingGroup) {
                                            groupRecipients = if (groupRecipients.any { it.identity == recipient.identity }) groupRecipients.filterNot { it.identity == recipient.identity } else groupRecipients + recipient
                                        } else choose(recipient)
                                    },
                                    color = LegendColors.Surface,
                                    shape = LegendShapes.Control,
                                ) {
                                    Row(Modifier.padding(LegendSpacing.Sm), verticalAlignment = Alignment.CenterVertically) {
                                        LegendProtectedAvatar(recipient.avatar, recipient.displayName, participantType, mediaRepository)
                                        Spacer(Modifier.width(LegendSpacing.Sm))
                                        Column(Modifier.weight(1f)) {
                                            Text(recipient.displayName, style = LegendTypography.CardTitle, color = LegendColors.TextPrimary)
                                            Text(recipient.relationshipLabel ?: recipient.roleLabel ?: "LEGEND member", style = LegendTypography.Label, color = LegendColors.TextSecondary)
                                        }
                                        if (isCreatingGroup) Icon(
                                            if (groupRecipients.any { it.identity == recipient.identity }) Icons.Default.CheckCircle else Icons.Default.AddCircleOutline,
                                            if (groupRecipients.any { it.identity == recipient.identity }) "Remove group member" else "Add group member",
                                            tint = if (groupRecipients.any { it.identity == recipient.identity }) LegendColors.Success else LegendColors.Gold,
                                        ) else Icon(Icons.Default.ChevronRight, "Start conversation", tint = LegendColors.Gold)
                                    }
                                }
                            }
                        }
                    }
                }
            }
        }
    }
}

@Composable
private fun legendCompactChipColors(selected: Boolean) = FilterChipDefaults.filterChipColors(
    containerColor = LegendColors.SurfaceInset,
    labelColor = LegendColors.TextSecondary,
    selectedContainerColor = LegendColors.Navy,
    selectedLabelColor = LegendColors.GoldBright,
)

@Composable
private fun legendMessagingFieldColors() = OutlinedTextFieldDefaults.colors(
    focusedContainerColor = LegendColors.SurfaceInset,
    unfocusedContainerColor = LegendColors.SurfaceInset,
    focusedTextColor = LegendColors.TextPrimary,
    unfocusedTextColor = LegendColors.TextPrimary,
    focusedBorderColor = LegendColors.Gold,
    unfocusedBorderColor = LegendColors.Divider,
    focusedPlaceholderColor = LegendColors.TextTertiary,
    unfocusedPlaceholderColor = LegendColors.TextTertiary,
)

@Composable
private fun LegendGroupMemberPicker(
    state: LoadState<List<MessagingRecipient>>,
    mediaRepository: AuthenticatedMediaRepository,
    participantType: String,
    load: (String?, String?) -> Unit,
    dismiss: () -> Unit,
    select: (MessagingRecipient) -> Unit,
) {
    var search by remember { mutableStateOf("") }
    LaunchedEffect(search) { load(search.trim().takeIf(String::isNotBlank), null) }
    ModalBottomSheet(onDismissRequest = dismiss, containerColor = LegendColors.Canvas) {
        Column(Modifier.fillMaxHeight(.82f).padding(horizontal = LegendSpacing.PageHorizontal)) {
            Row(verticalAlignment = Alignment.CenterVertically) {
                Column(Modifier.weight(1f)) {
                    Text("Add group member", style = LegendTypography.Section, color = LegendColors.TextPrimary)
                    Text("The server confirms whether this member can join.", style = LegendTypography.Supporting, color = LegendColors.TextSecondary)
                }
                TextButton(onClick = dismiss) { Text("Cancel", color = LegendColors.Gold) }
            }
            OutlinedTextField(search, { search = it }, modifier = Modifier.fillMaxWidth().padding(vertical = LegendSpacing.Sm), singleLine = true, leadingIcon = { Icon(Icons.Default.Search, null, tint = LegendColors.Gold) }, placeholder = { Text("Search people") }, shape = LegendShapes.Control, colors = legendMessagingFieldColors())
            when (state) {
                LoadState.Idle, LoadState.Loading -> LegendLoadingState()
                is LoadState.Error -> LegendErrorState(state.message) { load(search, null) }
                is LoadState.Data -> LazyColumn(verticalArrangement = Arrangement.spacedBy(LegendSpacing.Xs), contentPadding = PaddingValues(bottom = LegendSpacing.Lg)) {
                    if (state.value.isEmpty()) item { LegendEmptyState("No people found", "Try another name or return when a member is available.") }
                    items(state.value, key = { "${it.identity.userId}:${it.identity.participantType}" }) { recipient ->
                        Surface(Modifier.fillMaxWidth().clickable { select(recipient) }, color = LegendColors.Surface, shape = LegendShapes.Control) {
                            Row(Modifier.padding(LegendSpacing.Sm), verticalAlignment = Alignment.CenterVertically) {
                                LegendProtectedAvatar(recipient.avatar, recipient.displayName, participantType, mediaRepository, size = 42.dp)
                                Spacer(Modifier.width(LegendSpacing.Sm))
                                Column(Modifier.weight(1f)) {
                                    Text(recipient.displayName, style = LegendTypography.CardTitle, color = LegendColors.TextPrimary)
                                    Text(recipient.relationshipLabel ?: recipient.roleLabel ?: "LEGEND member", style = LegendTypography.Label, color = LegendColors.TextSecondary)
                                }
                                Icon(Icons.Default.PersonAdd, "Add member", tint = LegendColors.Gold)
                            }
                        }
                    }
                }
            }
        }
    }
}

@Composable
private fun LegendGroupManagementSheet(
    conversation: ConversationDetail,
    mediaRepository: AuthenticatedMediaRepository,
    participantType: String,
    dismiss: () -> Unit,
    addMember: () -> Unit,
    updateSubject: (String) -> Unit,
    updateImage: (Uri) -> Unit,
    updateMeeting: (MessagingGroupMeetingRequest) -> Unit,
    setManager: (MobileParticipant, Boolean) -> Unit,
    setPromotion: (Boolean) -> Unit,
    deleteGroup: () -> Unit,
) {
    var subject by remember(conversation.id, conversation.title) { mutableStateOf(conversation.title) }
    var confirmDelete by remember { mutableStateOf(false) }
    var editingMeeting by remember { mutableStateOf(false) }
    val groupImagePicker = rememberLauncherForActivityResult(ActivityResultContracts.PickVisualMedia()) { uri -> uri?.let(updateImage) }
    ModalBottomSheet(onDismissRequest = dismiss, containerColor = LegendColors.Canvas) {
        LazyColumn(
            modifier = Modifier.fillMaxHeight(.92f),
            contentPadding = PaddingValues(horizontal = LegendSpacing.PageHorizontal, vertical = LegendSpacing.Md),
            verticalArrangement = Arrangement.spacedBy(LegendSpacing.Sm),
        ) {
            item {
                Row(verticalAlignment = Alignment.CenterVertically) {
                    LegendProtectedAvatar(conversation.groupAvatar, conversation.title, participantType, mediaRepository, size = 56.dp)
                    Spacer(Modifier.width(LegendSpacing.Sm))
                    Column(Modifier.weight(1f)) {
                        Text("GROUP SETTINGS", style = LegendTypography.Eyebrow, color = LegendColors.Gold)
                        Text(conversation.title, style = LegendTypography.Section, color = LegendColors.TextPrimary)
                    }
                    TextButton(onClick = dismiss) { Text("Done", color = LegendColors.Gold) }
                }
            }
            if (conversation.canManageMembers) item {
                OutlinedButton(
                    onClick = { groupImagePicker.launch(PickVisualMediaRequest(ActivityResultContracts.PickVisualMedia.ImageOnly)) },
                    modifier = Modifier.fillMaxWidth(),
                    shape = LegendShapes.Control,
                ) {
                    Icon(Icons.Default.PhotoCamera, null, tint = LegendColors.Gold)
                    Spacer(Modifier.width(LegendSpacing.Xs))
                    Text("Change group photo", color = LegendColors.TextPrimary)
                }
            }
            if (conversation.canManageMembers || conversation.canManageMeeting) item {
                Surface(color = LegendColors.Surface, shape = LegendShapes.Card, modifier = Modifier.fillMaxWidth()) {
                    Column(Modifier.padding(LegendSpacing.Md), verticalArrangement = Arrangement.spacedBy(LegendSpacing.Xs)) {
                        Text("GROUP PROFILE", style = LegendTypography.Eyebrow, color = LegendColors.Gold)
                        OutlinedTextField(subject, { subject = it }, modifier = Modifier.fillMaxWidth(), label = { Text("Group name") }, singleLine = true, shape = LegendShapes.Control, colors = legendMessagingFieldColors())
                        LegendPrimaryButton("Save group name", modifier = Modifier.fillMaxWidth(), enabled = subject.isNotBlank() && subject.trim() != conversation.title) { updateSubject(subject) }
                        conversation.meeting?.let { meeting ->
                            Text("Meeting: ${meeting.linkLabel ?: meeting.linkUrl ?: "Scheduled"}", style = LegendTypography.Supporting, color = LegendColors.TextSecondary)
                        }
                        if (conversation.canManageMeeting) {
                            OutlinedButton(onClick = { editingMeeting = true }, modifier = Modifier.fillMaxWidth(), shape = LegendShapes.Control) {
                                Icon(Icons.Default.VideoCall, null, tint = LegendColors.Gold)
                                Spacer(Modifier.width(LegendSpacing.Xs))
                                Text(if (conversation.meeting == null) "Add group meeting" else "Edit group meeting", color = LegendColors.TextPrimary)
                            }
                        }
                    }
                }
            }
            if (conversation.canManagePromotion) item {
                Surface(color = LegendColors.Navy, shape = LegendShapes.Card, modifier = Modifier.fillMaxWidth()) {
                    Row(Modifier.padding(LegendSpacing.Md), verticalAlignment = Alignment.CenterVertically) {
                        Column(Modifier.weight(1f)) {
                            Text("Promoted group", style = LegendTypography.Label, color = LegendColors.OnNavy)
                            Text("Server-authorized group discovery visibility.", style = LegendTypography.Supporting, color = LegendColors.GoldSoft)
                        }
                        Switch(checked = conversation.isPromoted, onCheckedChange = setPromotion, colors = SwitchDefaults.colors(checkedThumbColor = LegendColors.Navy, checkedTrackColor = LegendColors.Gold))
                    }
                }
            }
            item { Text("MEMBERS", style = LegendTypography.Eyebrow, color = LegendColors.Gold) }
            if (conversation.canManageMembers) item { OutlinedButton(onClick = addMember, shape = LegendShapes.Control, modifier = Modifier.fillMaxWidth()) { Icon(Icons.Default.PersonAdd, null, tint = LegendColors.Gold); Spacer(Modifier.width(LegendSpacing.Xs)); Text("Add member", color = LegendColors.TextPrimary) } }
            items(conversation.participants, key = { "${it.identity.userId}:${it.identity.participantType}" }) { member ->
                Surface(color = LegendColors.Surface, shape = LegendShapes.Control, modifier = Modifier.fillMaxWidth()) {
                    Row(Modifier.padding(LegendSpacing.Sm), verticalAlignment = Alignment.CenterVertically) {
                        LegendProtectedAvatar(member.avatar, member.displayName, participantType, mediaRepository, size = 42.dp)
                        Spacer(Modifier.width(LegendSpacing.Sm))
                        Column(Modifier.weight(1f)) {
                            Text(member.displayName, style = LegendTypography.Label, color = LegendColors.TextPrimary)
                            Text(if (member.isGroupManager) "Group manager" else member.roleLabel ?: "Member", style = LegendTypography.Supporting, color = LegendColors.TextSecondary)
                        }
                        if (conversation.canManageCollaborators) Switch(checked = member.isGroupManager, onCheckedChange = { setManager(member, it) }, colors = SwitchDefaults.colors(checkedThumbColor = LegendColors.Navy, checkedTrackColor = LegendColors.Gold))
                    }
                }
            }
            if (conversation.canDeleteGroup) item {
                TextButton(onClick = { confirmDelete = true }, modifier = Modifier.fillMaxWidth()) { Text("Delete group", color = LegendColors.Error) }
            }
        }
    }
    if (confirmDelete) AlertDialog(
        onDismissRequest = { confirmDelete = false },
        title = { Text("Delete this group?") },
        text = { Text("This is a server-authorized group deletion and cannot be undone from Android.") },
        confirmButton = { TextButton(onClick = { confirmDelete = false; deleteGroup() }) { Text("Delete group", color = LegendColors.Error) } },
        dismissButton = { TextButton(onClick = { confirmDelete = false }) { Text("Cancel") } },
    )
    if (editingMeeting) {
        LegendGroupMeetingEditorSheet(
            conversation = conversation,
            dismiss = { editingMeeting = false },
            save = { meeting -> updateMeeting(meeting); editingMeeting = false },
        )
    }
}

@Composable
private fun LegendGroupMeetingEditorSheet(
    conversation: ConversationDetail,
    dismiss: () -> Unit,
    save: (MessagingGroupMeetingRequest) -> Unit,
) {
    val existing = conversation.meeting
    var enabled by remember(conversation.id) { mutableStateOf(existing?.linkLabel != null || existing?.linkUrl != null) }
    var hostKey by remember(conversation.id) { mutableStateOf(existing?.host?.let { "${it.identity.userId}:${it.identity.participantType}" } ?: conversation.participants.firstOrNull()?.let { "${it.identity.userId}:${it.identity.participantType}" }.orEmpty()) }
    var hostsOpen by remember { mutableStateOf(false) }
    var label by remember(conversation.id) { mutableStateOf(existing?.linkLabel.orEmpty()) }
    var url by remember(conversation.id) { mutableStateOf(existing?.linkUrl.orEmpty()) }
    var scheduleEnabled by remember(conversation.id) { mutableStateOf(existing?.schedule != null) }
    var frequency by remember(conversation.id) { mutableStateOf(existing?.schedule?.frequency ?: "Weekly") }
    var weekday by remember(conversation.id) { mutableStateOf(existing?.schedule?.weekdays?.firstOrNull() ?: "Wednesday") }
    var localTime by remember(conversation.id) { mutableStateOf(existing?.schedule?.localTime ?: "18:00") }
    var timeZoneId by remember(conversation.id) { mutableStateOf(existing?.schedule?.timeZoneId ?: java.util.TimeZone.getDefault().id) }
    var startsUtc by remember(conversation.id) { mutableStateOf(existing?.schedule?.startsUtc.orEmpty()) }
    var customDescription by remember(conversation.id) { mutableStateOf(existing?.schedule?.customDescription.orEmpty()) }
    val host = conversation.participants.firstOrNull { "${it.identity.userId}:${it.identity.participantType}" == hostKey }
    val recurring = frequency in setOf("Daily", "Weekly", "Biweekly", "Monthly")
    val needsWeekday = frequency in setOf("Weekly", "Biweekly")
    val needsStart = frequency in setOf("OneTime", "Monthly")
    val meetingValid = !enabled || (host != null && label.trim().isNotBlank() && url.trim().let { it.startsWith("https://") || it.startsWith("http://") } && (!scheduleEnabled || (!recurring || (localTime.matches(Regex("[0-2]\\d:[0-5]\\d")) && timeZoneId.isNotBlank())) && (!needsWeekday || weekday.isNotBlank()) && (!needsStart || startsUtc.isNotBlank()) && (frequency != "Custom" || customDescription.isNotBlank())))
    ModalBottomSheet(onDismissRequest = dismiss, containerColor = LegendColors.Canvas) {
        LazyColumn(
            modifier = Modifier.fillMaxHeight(.94f),
            contentPadding = PaddingValues(horizontal = LegendSpacing.PageHorizontal, vertical = LegendSpacing.Md),
            verticalArrangement = Arrangement.spacedBy(LegendSpacing.Sm),
        ) {
            item {
                Row(verticalAlignment = Alignment.CenterVertically) {
                    Column(Modifier.weight(1f)) {
                        Text("GROUP MEETING", style = LegendTypography.Eyebrow, color = LegendColors.Gold)
                        Text("Meeting details", style = LegendTypography.Section, color = LegendColors.TextPrimary)
                        Text("Only the server-authorized group owner can save host or meeting details.", style = LegendTypography.Supporting, color = LegendColors.TextSecondary)
                    }
                    TextButton(onClick = dismiss) { Text("Cancel", color = LegendColors.Gold) }
                }
            }
            item {
                Surface(color = LegendColors.Navy, shape = LegendShapes.Control, modifier = Modifier.fillMaxWidth()) {
                    Row(Modifier.padding(LegendSpacing.Sm), verticalAlignment = Alignment.CenterVertically) {
                        Column(Modifier.weight(1f)) {
                            Text("Online meeting", style = LegendTypography.Label, color = LegendColors.OnNavy)
                            Text("Add a meeting link and optional schedule for the group.", style = LegendTypography.Supporting, color = LegendColors.GoldSoft)
                        }
                        Switch(checked = enabled, onCheckedChange = { enabled = it }, colors = SwitchDefaults.colors(checkedThumbColor = LegendColors.Navy, checkedTrackColor = LegendColors.Gold))
                    }
                }
            }
            if (enabled) {
                item {
                    Text("HOST", style = LegendTypography.Eyebrow, color = LegendColors.Gold)
                    Box {
                        OutlinedButton(onClick = { hostsOpen = true }, modifier = Modifier.fillMaxWidth(), shape = LegendShapes.Control) {
                            Text(host?.displayName ?: "Choose group host", color = LegendColors.TextPrimary, modifier = Modifier.weight(1f))
                            Icon(Icons.Default.ExpandMore, null, tint = LegendColors.Gold)
                        }
                        DropdownMenu(expanded = hostsOpen, onDismissRequest = { hostsOpen = false }, containerColor = LegendColors.Surface) {
                            conversation.participants.forEach { participant ->
                                DropdownMenuItem(text = { Text(participant.displayName, color = LegendColors.TextPrimary) }, onClick = { hostKey = "${participant.identity.userId}:${participant.identity.participantType}"; hostsOpen = false })
                            }
                        }
                    }
                }
                item { AccountEditorField("Meeting link name", label) { label = it } }
                item { AccountEditorField("Meeting link (https://)", url) { url = it } }
                item {
                    Surface(color = LegendColors.Surface, shape = LegendShapes.Control, modifier = Modifier.fillMaxWidth()) {
                        Row(Modifier.padding(LegendSpacing.Sm), verticalAlignment = Alignment.CenterVertically) {
                            Column(Modifier.weight(1f)) {
                                Text("Add a schedule", style = LegendTypography.Label, color = LegendColors.TextPrimary)
                                Text("A schedule is optional; the link is always required when a meeting is enabled.", style = LegendTypography.Supporting, color = LegendColors.TextSecondary)
                            }
                            Switch(checked = scheduleEnabled, onCheckedChange = { scheduleEnabled = it }, colors = SwitchDefaults.colors(checkedThumbColor = LegendColors.Navy, checkedTrackColor = LegendColors.Gold))
                        }
                    }
                }
                if (scheduleEnabled) {
                    item {
                        Text("FREQUENCY", style = LegendTypography.Eyebrow, color = LegendColors.Gold)
                        LazyRow(horizontalArrangement = Arrangement.spacedBy(LegendSpacing.Xs)) {
                            items(listOf("OneTime", "Daily", "Weekly", "Biweekly", "Monthly", "Custom")) { candidate ->
                                FilterChip(selected = frequency == candidate, onClick = { frequency = candidate }, label = { Text(candidate.replace("OneTime", "One time").replace("Biweekly", "Every other week")) }, colors = legendCompactChipColors(frequency == candidate))
                            }
                        }
                    }
                    if (needsWeekday) item {
                        Text("WEEKDAY", style = LegendTypography.Eyebrow, color = LegendColors.Gold)
                        LazyRow(horizontalArrangement = Arrangement.spacedBy(LegendSpacing.Xs)) {
                            items(listOf("Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday", "Sunday")) { day -> FilterChip(selected = weekday == day, onClick = { weekday = day }, label = { Text(day.take(3)) }, colors = legendCompactChipColors(weekday == day)) }
                        }
                    }
                    if (recurring) {
                        item { AccountEditorField("Local time (HH:mm)", localTime) { localTime = it } }
                        item { AccountEditorField("Time zone ID", timeZoneId) { timeZoneId = it } }
                    }
                    if (needsStart) item { AccountEditorField("Start (UTC ISO-8601)", startsUtc) { startsUtc = it } }
                    if (frequency == "Custom") item { AccountEditorField("Custom schedule", customDescription, minLines = 3) { customDescription = it } }
                }
            }
            item {
                LegendPrimaryButton("Save group meeting", enabled = meetingValid, modifier = Modifier.fillMaxWidth()) {
                    val request = if (!enabled) {
                        MessagingGroupMeetingRequest()
                    } else {
                        MessagingGroupMeetingRequest(
                            host = host?.let { MessagingGroupParticipantRequest(it.identity.userId, it.identity.participantType) },
                            linkLabel = label.trim(),
                            linkUrl = url.trim(),
                            schedule = if (!scheduleEnabled) null else MessagingGroupMeetingScheduleRequest(
                                frequency = frequency,
                                weekdays = if (needsWeekday) listOf(weekday) else emptyList(),
                                localTime = if (recurring) localTime.trim() else null,
                                timeZoneId = if (recurring) timeZoneId.trim() else null,
                                startsUtc = if (needsStart) startsUtc.trim() else null,
                                customDescription = if (frequency == "Custom") customDescription.trim() else null,
                            ),
                        )
                    }
                    save(request)
                }
            }
        }
    }
}

@Composable
private fun LegendConversationActions(
    conversation: ConversationSummary,
    dismiss: () -> Unit,
    pin: () -> Unit,
    mute: () -> Unit,
    remove: () -> Unit,
) {
    AlertDialog(
        onDismissRequest = dismiss,
        title = { Text(conversation.title) },
        text = { Text("Manage this server-authorized conversation. Removing it only removes it from your inbox; a new message will bring it back.") },
        confirmButton = {
            Column(horizontalAlignment = Alignment.End) {
                TextButton(onClick = { pin(); dismiss() }) { Text(if (conversation.isPinned) "Unpin conversation" else "Pin conversation") }
                TextButton(onClick = { mute(); dismiss() }) { Text(if (conversation.isMuted) "Unmute conversation" else "Mute conversation") }
                TextButton(onClick = { remove(); dismiss() }) { Text("Remove from inbox", color = LegendColors.Error) }
            }
        },
        dismissButton = { TextButton(onClick = dismiss) { Text("Cancel") } },
    )
}

@Composable
private fun MessageThread(
    conversation: ConversationDetail,
    mediaRepository: AuthenticatedMediaRepository,
    participantType: String,
    isSending: Boolean,
    back: () -> Unit,
    send: (android.content.Context, String, String, String?, List<Uri>) -> Unit,
    loadOlder: () -> Unit,
    delete: (ConversationMessage) -> Unit,
    manageGroup: (ConversationDetail) -> Unit,
    resolveVerification: (VerificationReview, Boolean, String?) -> Unit,
) {
    val context = LocalContext.current
    var draft by remember { mutableStateOf("") }
    var replyTo by remember { mutableStateOf<ConversationMessage?>(null) }
    var attachments by remember { mutableStateOf<List<Uri>>(emptyList()) }
    val photoPicker = rememberLauncherForActivityResult(ActivityResultContracts.PickMultipleVisualMedia()) { selected -> attachments = attachments + selected }
    val filePicker = rememberLauncherForActivityResult(ActivityResultContracts.OpenMultipleDocuments()) { selected -> attachments = attachments + selected }
    Column(Modifier.fillMaxSize().background(LegendColors.Canvas)) {
        Surface(color = LegendColors.Navy) {
            Row(Modifier.fillMaxWidth().padding(horizontal = LegendSpacing.Sm, vertical = LegendSpacing.Xs), verticalAlignment = Alignment.CenterVertically) {
                IconButton(onClick = back) { Icon(Icons.AutoMirrored.Filled.ArrowBack, "Back to messages", tint = LegendColors.OnNavy) }
                Column(Modifier.weight(1f)) {
                    Text(conversation.title, style = LegendTypography.CardTitle, color = LegendColors.OnNavy, maxLines = 1, overflow = TextOverflow.Ellipsis)
                    conversation.purpose?.let { Text(it, style = LegendTypography.Label, color = LegendColors.GoldSoft, maxLines = 1) }
                }
                conversation.meeting?.linkLabel?.let { Icon(Icons.Default.Event, "${it} meeting", tint = LegendColors.Gold) }
                if (conversation.canManageMembers || conversation.canManageCollaborators || conversation.canManagePromotion || conversation.canDeleteGroup) {
                    IconButton(onClick = { manageGroup(conversation) }) { Icon(Icons.Default.Group, "Manage group", tint = LegendColors.GoldBright) }
                }
            }
        }
        LazyColumn(
            modifier = Modifier.weight(1f),
            contentPadding = PaddingValues(horizontal = LegendSpacing.PageHorizontal, vertical = LegendSpacing.Md),
            verticalArrangement = Arrangement.spacedBy(LegendSpacing.Xs),
        ) {
            if (conversation.hasOlderMessages) {
                item { TextButton(onClick = loadOlder, modifier = Modifier.fillMaxWidth()) { Text("Load earlier messages", color = LegendColors.Gold) } }
            }
            items(conversation.messages, key = { it.id }) { message ->
                LegendMessageBubble(
                    message = message,
                    mediaRepository = mediaRepository,
                    participantType = participantType,
                    reply = { replyTo = message },
                    delete = { delete(message) },
                    resolveVerification = resolveVerification,
                )
            }
        }
        if (conversation.isClosed) {
            Text("Conversation closed · New messages cannot be sent.", style = LegendTypography.Supporting, color = LegendColors.TextSecondary, modifier = Modifier.fillMaxWidth().background(LegendColors.Surface).padding(LegendSpacing.Md))
        } else {
            replyTo?.let { reply ->
                Row(Modifier.fillMaxWidth().background(LegendColors.GoldSoft).padding(horizontal = LegendSpacing.PageHorizontal, vertical = LegendSpacing.Xs), verticalAlignment = Alignment.CenterVertically) {
                    Text("Replying to ${reply.sender.displayName}: ${reply.body}", modifier = Modifier.weight(1f), style = LegendTypography.Label, maxLines = 1, overflow = TextOverflow.Ellipsis)
                    IconButton(onClick = { replyTo = null }) { Icon(Icons.Default.Close, "Cancel reply") }
                }
            }
            if (attachments.isNotEmpty()) {
                LazyRow(Modifier.fillMaxWidth().background(LegendColors.Surface).padding(horizontal = LegendSpacing.PageHorizontal, vertical = LegendSpacing.Xs), horizontalArrangement = Arrangement.spacedBy(LegendSpacing.Xs)) {
                    items(attachments, key = { it.toString() }) { uri ->
                        AssistChip(onClick = { attachments = attachments - uri }, label = { Text(context.contentResolver.legendDisplayName(uri), maxLines = 1, overflow = TextOverflow.Ellipsis) }, trailingIcon = { Icon(Icons.Default.Close, "Remove attachment") })
                    }
                }
            }
            Row(Modifier.fillMaxWidth().background(LegendColors.Surface).padding(horizontal = LegendSpacing.PageHorizontal, vertical = LegendSpacing.Sm), verticalAlignment = Alignment.Bottom) {
                IconButton(onClick = { photoPicker.launch(PickVisualMediaRequest(ActivityResultContracts.PickVisualMedia.ImageOnly)) }) { Icon(Icons.Default.AddPhotoAlternate, "Add photo", tint = LegendColors.Gold) }
                IconButton(onClick = { filePicker.launch(arrayOf("image/*", "application/pdf", "text/plain", "application/msword", "application/vnd.openxmlformats-officedocument.wordprocessingml.document", "application/vnd.ms-excel", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet")) }) { Icon(Icons.Default.AttachFile, "Add file", tint = LegendColors.Gold) }
                OutlinedTextField(value = draft, onValueChange = { draft = it }, modifier = Modifier.weight(1f), placeholder = { Text("Write a message") }, maxLines = 5, shape = LegendShapes.Control)
                IconButton(
                    onClick = {
                        send(context, conversation.id, draft, replyTo?.id, attachments)
                        draft = ""
                        replyTo = null
                        attachments = emptyList()
                    },
                    enabled = draft.isNotBlank() && !isSending,
                    modifier = Modifier.padding(start = LegendSpacing.Xs).background(if (draft.isNotBlank()) LegendColors.Gold else LegendColors.SurfaceInset, CircleShape),
                ) {
                    if (isSending) CircularProgressIndicator(modifier = Modifier.size(18.dp), color = LegendColors.Midnight, strokeWidth = 2.dp)
                    else Icon(Icons.AutoMirrored.Filled.Send, "Send message", tint = LegendColors.Midnight)
                }
            }
        }
    }
}

@Composable
private fun LegendMessageBubble(
    message: ConversationMessage,
    mediaRepository: AuthenticatedMediaRepository,
    participantType: String,
    reply: () -> Unit,
    delete: () -> Unit,
    resolveVerification: (VerificationReview, Boolean, String?) -> Unit,
) {
    Row(Modifier.fillMaxWidth(), horizontalArrangement = if (message.isMine) Arrangement.End else Arrangement.Start, verticalAlignment = Alignment.Bottom) {
        if (!message.isMine) {
            LegendProtectedAvatar(message.sender.avatar, message.sender.displayName, participantType, mediaRepository, size = 28.dp)
            Spacer(Modifier.width(LegendSpacing.Xs))
        }
        Surface(color = if (message.isMine) LegendColors.Navy else LegendColors.GoldSoft, shape = LegendShapes.Control, modifier = Modifier.widthIn(max = 300.dp)) {
            Column(Modifier.padding(LegendSpacing.Sm), verticalArrangement = Arrangement.spacedBy(LegendSpacing.Xs)) {
                if (!message.isMine) Text(message.sender.displayName, style = LegendTypography.Label, color = LegendColors.TextSecondary)
                message.reply?.let { replyPreview ->
                    Text("${replyPreview.sender.displayName}: ${if (replyPreview.isDeleted) "Message unsent" else replyPreview.body}", style = LegendTypography.Label, color = if (message.isMine) LegendColors.GoldSoft else LegendColors.TextSecondary, maxLines = 2, overflow = TextOverflow.Ellipsis)
                }
                Text(if (message.isDeleted) "Message unsent" else message.body, color = if (message.isMine) LegendColors.OnNavy else LegendColors.TextPrimary)
                message.originalBody?.takeIf { it != message.body }?.let { original ->
                    Text("${LegendCopy.value("message.original")}: $original", style = LegendTypography.Label, color = if (message.isMine) LegendColors.GoldSoft else LegendColors.TextSecondary)
                }
                message.translation?.let { translation ->
                    Text("Translated ${translation.originalLanguage} → ${translation.targetLanguage}", style = LegendTypography.Label, color = if (message.isMine) LegendColors.GoldSoft else LegendColors.TextTertiary)
                }
                message.verificationReview?.let { review ->
                    Text("${review.resourceType}: ${review.status}", style = LegendTypography.Label, color = if (message.isMine) LegendColors.GoldSoft else LegendColors.TextSecondary)
                    if (review.canResolve) {
                        Row(horizontalArrangement = Arrangement.spacedBy(LegendSpacing.Xs)) {
                            TextButton(onClick = { resolveVerification(review, true, null) }) { Text("Approve", color = LegendColors.Success) }
                            TextButton(onClick = { resolveVerification(review, false, null) }) { Text("Decline", color = LegendColors.Error) }
                        }
                    }
                }
                message.attachments.forEach { attachment ->
                    Text("${attachment.originalFileName} · ${attachment.scanStatus}", style = LegendTypography.Label, color = if (message.isMine) LegendColors.GoldSoft else LegendColors.TextSecondary)
                }
                Row(verticalAlignment = Alignment.CenterVertically) {
                    Text(legendCompactTime(message.sentUtc), style = LegendTypography.Label, color = if (message.isMine) LegendColors.GoldSoft else LegendColors.TextTertiary, modifier = Modifier.weight(1f))
                    IconButton(onClick = reply, modifier = Modifier.size(28.dp)) { Icon(Icons.AutoMirrored.Filled.Reply, "Reply", modifier = Modifier.size(15.dp), tint = if (message.isMine) LegendColors.GoldSoft else LegendColors.TextSecondary) }
                    if (message.isMine && !message.isDeleted) IconButton(onClick = delete, modifier = Modifier.size(28.dp)) { Icon(Icons.Default.DeleteOutline, "Unsend message", modifier = Modifier.size(15.dp), tint = if (message.isMine) LegendColors.GoldSoft else LegendColors.TextSecondary) }
                }
            }
        }
    }
}

private fun legendCompactTime(value: String): String = value.substringAfter('T', value).take(5).takeIf { it.isNotBlank() } ?: value

private enum class SocialCollection {
    POSTS,
    STORIES,
    HACS;

    val label: String
        get() = when (this) {
            POSTS -> "${LegendCopy.value("content.post")}s"
            STORIES -> "${LegendCopy.value("content.story")}s"
            HACS -> "${LegendCopy.value("content.hac")}s"
        }
}

/**
 * Android's one native presentation for sharing any Post, Story, or Hac.
 *
 * The only state here is transient UI state. Recipient search, direct
 * conversation resolution, message delivery, and the share metric retain
 * their existing single owners: MessagingViewModel and SocialViewModel.
 */
@Composable
private fun LegendGlobalSocialShareSheet(
    post: SocialPost,
    messaging: MessagingViewModel,
    social: SocialViewModel,
    mediaRepository: AuthenticatedMediaRepository,
    participantType: String,
    dismiss: () -> Unit,
) {
    val context = LocalContext.current
    val recipients by messaging.recipients.collectAsStateWithLifecycle()
    val isSending by messaging.isSending.collectAsStateWithLifecycle()
    var query by remember(post.id) { mutableStateOf("") }
    var recipientBeingSent by remember(post.id) { mutableStateOf<String?>(null) }

    LaunchedEffect(query) {
        kotlinx.coroutines.delay(120)
        messaging.loadRecipients(query.trim().takeIf(String::isNotBlank))
    }

    val internalMessageBody = remember(post) { post.legendInternalShareBody() }
    val externalMessageBody = remember(post) { post.legendExternalShareBody() }
    ModalBottomSheet(
        onDismissRequest = dismiss,
        containerColor = LegendColors.Canvas,
        dragHandle = null,
    ) {
        Column(
            modifier = Modifier
                .fillMaxHeight(.92f)
                .fillMaxWidth()
                .background(LegendColors.Canvas),
        ) {
            Row(
                modifier = Modifier
                    .fillMaxWidth()
                    .padding(horizontal = LegendSpacing.PageHorizontal, vertical = LegendSpacing.Sm),
                verticalAlignment = Alignment.CenterVertically,
            ) {
                Text("Share", style = LegendTypography.Section, color = LegendColors.TextPrimary, modifier = Modifier.weight(1f))
                TextButton(onClick = dismiss) { Text("Done", style = LegendTypography.BodyEmphasis, color = LegendColors.Gold) }
            }
            Column(
                modifier = Modifier.padding(horizontal = LegendSpacing.PageHorizontal),
                verticalArrangement = Arrangement.spacedBy(LegendSpacing.Sm),
            ) {
                Text("SEND IN LEGEND", style = LegendTypography.Eyebrow, color = LegendColors.Gold)
                OutlinedTextField(
                    value = query,
                    onValueChange = { query = it },
                    modifier = Modifier.fillMaxWidth(),
                    singleLine = true,
                    placeholder = { Text("Search LEGEND members") },
                    leadingIcon = { Icon(Icons.Default.Search, null, tint = LegendColors.Gold) },
                    shape = LegendShapes.Control,
                    colors = legendMessagingFieldColors(),
                )
                Surface(
                    color = LegendColors.SurfaceInset,
                    shape = LegendShapes.Control,
                    modifier = Modifier
                        .fillMaxWidth()
                        .clickable {
                            social.recordShare(post.id)
                            context.startActivity(
                                android.content.Intent.createChooser(
                                    android.content.Intent(android.content.Intent.ACTION_SEND)
                                        .setType("text/plain")
                                        .putExtra(android.content.Intent.EXTRA_TEXT, externalMessageBody),
                                    "Share outside LEGEND",
                                ),
                            )
                        },
                ) {
                    Row(
                        modifier = Modifier.padding(horizontal = LegendSpacing.Md, vertical = LegendSpacing.Sm),
                        verticalAlignment = Alignment.CenterVertically,
                    ) {
                        Icon(Icons.Default.Share, null, tint = LegendColors.Gold)
                        Spacer(Modifier.width(LegendSpacing.Sm))
                        Column(Modifier.weight(1f)) {
                            Text("Share outside LEGEND", style = LegendTypography.BodyEmphasis, color = LegendColors.TextPrimary)
                            Text("Messages, email, and other apps", style = LegendTypography.Caption, color = LegendColors.TextSecondary)
                        }
                        Icon(Icons.Default.ChevronRight, null, tint = LegendColors.TextSecondary)
                    }
                }
            }
            Spacer(Modifier.height(LegendSpacing.Sm))
            when (val state = recipients) {
                LoadState.Idle,
                LoadState.Loading -> LegendLoadingState()

                is LoadState.Error -> LegendErrorState(state.message) {
                    messaging.loadRecipients(query.trim().takeIf(String::isNotBlank))
                }

                is LoadState.Data -> {
                    if (state.value.isEmpty()) {
                        LegendEmptyState(
                            if (query.isBlank()) "No LEGEND members available" else "No matching LEGEND members",
                            if (query.isBlank()) "Try another member category." else "Try a different search.",
                        )
                    } else {
                        LazyColumn(
                            contentPadding = PaddingValues(horizontal = LegendSpacing.PageHorizontal, vertical = LegendSpacing.Sm),
                        ) {
                            items(state.value, key = { it.identity.userId + it.identity.participantType }) { recipient ->
                                val recipientKey = recipient.identity.userId + recipient.identity.participantType
                                val isThisRecipientSending = recipientBeingSent == recipientKey
                                Row(
                                    modifier = Modifier
                                        .fillMaxWidth()
                                        .clickable(enabled = !isSending && recipientBeingSent == null) {
                                            recipientBeingSent = recipientKey
                                            messaging.startConversation(recipient) { conversationId ->
                                                messaging.send(
                                                    context = context,
                                                    id = conversationId,
                                                    body = internalMessageBody,
                                                    completed = { succeeded ->
                                                        recipientBeingSent = null
                                                        if (succeeded) {
                                                            social.recordShare(post.id)
                                                            dismiss()
                                                        }
                                                    },
                                                )
                                            }
                                        }
                                        .padding(vertical = LegendSpacing.Sm),
                                    verticalAlignment = Alignment.CenterVertically,
                                ) {
                                    LegendProtectedAvatar(
                                        avatar = recipient.avatar,
                                        displayName = recipient.displayName,
                                        participantType = participantType,
                                        repository = mediaRepository,
                                        size = 44.dp,
                                    )
                                    Spacer(Modifier.width(LegendSpacing.Sm))
                                    Column(Modifier.weight(1f)) {
                                        Row(verticalAlignment = Alignment.CenterVertically) {
                                            Text(recipient.displayName, style = LegendTypography.BodyEmphasis, color = LegendColors.TextPrimary, maxLines = 1, overflow = TextOverflow.Ellipsis)
                                            if (recipient.isVerified) Icon(Icons.Default.Verified, "Verified", modifier = Modifier.padding(start = LegendSpacing.Xs).size(16.dp), tint = LegendColors.Verified)
                                        }
                                        Text(
                                            recipient.email ?: recipient.relationshipLabel ?: recipient.roleLabel.orEmpty(),
                                            style = LegendTypography.Caption,
                                            color = LegendColors.TextSecondary,
                                            maxLines = 1,
                                            overflow = TextOverflow.Ellipsis,
                                        )
                                    }
                                    if (isThisRecipientSending) {
                                        CircularProgressIndicator(modifier = Modifier.size(20.dp), color = LegendColors.Gold, strokeWidth = 2.dp)
                                    } else {
                                        Icon(Icons.AutoMirrored.Filled.Send, "Send ${post.displayContentLabel()} to ${recipient.displayName}", tint = LegendColors.Gold)
                                    }
                                }
                                HorizontalDivider(color = LegendColors.Divider, modifier = Modifier.padding(start = 56.dp))
                            }
                        }
                    }
                }
            }
        }
    }
}

private fun SocialPost.legendInternalShareBody(): String {
    val heading = "Shared a LEGEND ${displayContentLabel()} by ${author.displayName}"
    return body.trim().takeIf(String::isNotBlank)?.let { "$heading\n\n$it" } ?: heading
}

private fun SocialPost.legendExternalShareBody(): String =
    body.trim().takeIf(String::isNotBlank)
        ?: "LEGEND ${displayContentLabel()} by ${author.displayName}"

@Composable
private fun SocialScreen(
    viewModel: SocialViewModel,
    mediaRepository: AuthenticatedMediaRepository,
    participantType: String,
) {
    val state by viewModel.state.collectAsStateWithLifecycle()
    val sharePost = LocalLegendSocialShare.current
    var commentingPost by remember { mutableStateOf<SocialPost?>(null) }
    var profileAuthor by remember { mutableStateOf<SocialAuthor?>(null) }
    LaunchedEffect(Unit) { viewModel.load() }
    LaunchedEffect(state, commentingPost?.id) {
        val current = commentingPost ?: return@LaunchedEffect
        val snapshot = (state as? LoadState.Data<SocialSnapshot>)?.value ?: return@LaunchedEffect
        (snapshot.posts + snapshot.stories + snapshot.hacs).firstOrNull { it.id == current.id }?.let { commentingPost = it }
    }

    when (state) {
        LoadState.Idle, LoadState.Loading -> LegendLoadingState()
        is LoadState.Error -> LegendErrorState((state as LoadState.Error).message, viewModel::load)
        is LoadState.Data -> {
            val snapshot = (state as LoadState.Data<SocialSnapshot>).value
            // iOS's For You tab is not a second mixed social feed. It is the
            // dedicated full-viewport Hac experience, backed by the exact same
            // server-issued Hac projection as the Home and profile surfaces.
            val hacs = snapshot.hacs.filter { it.legendContentType == LegendSocialContentType.HAC }
            if (hacs.isEmpty()) {
                Box(Modifier.fillMaxSize().background(LegendColors.Midnight)) {
                    Column(
                        modifier = Modifier.align(Alignment.Center).padding(LegendSpacing.Xl),
                        horizontalAlignment = Alignment.CenterHorizontally,
                        verticalArrangement = Arrangement.spacedBy(LegendSpacing.Sm),
                    ) {
                        Icon(Icons.Default.VideoLibrary, null, tint = LegendColors.GoldBright, modifier = Modifier.size(32.dp))
                        Text("No Hacs yet", style = LegendTypography.Section, color = LegendColors.OnNavy)
                        Text(
                            "Video Hacs will appear here as they are shared.",
                            style = LegendTypography.Supporting,
                            color = LegendColors.GoldSoft,
                        )
                    }
                }
            } else {
                val pagerState = rememberPagerState(pageCount = { hacs.size })
                LaunchedEffect(pagerState.currentPage, hacs) {
                    hacs.getOrNull(pagerState.currentPage)?.let { hac ->
                        // The server owns view accounting. Android reports an
                        // open event only; it does not derive engagement rules.
                        viewModel.recordView(hac.id, storyInteractionType = "Opened")
                    }
                }
                VerticalPager(
                    state = pagerState,
                    modifier = Modifier.fillMaxSize().background(LegendColors.Midnight),
                    beyondViewportPageCount = 1,
                    key = { page -> hacs[page].id },
                ) { page ->
                    LegendHacViewportPage(
                        post = hacs[page],
                        isActive = pagerState.currentPage == page,
                        mediaRepository = mediaRepository,
                        participantType = participantType,
                        openProfile = { profileAuthor = hacs[page].author },
                        react = { viewModel.react(hacs[page].id) },
                        comment = { commentingPost = hacs[page] },
                        repost = { viewModel.toggleRepost(hacs[page].id) },
                        save = { viewModel.toggleSave(hacs[page].id) },
                        share = { sharePost(hacs[page]) },
                    )
                }
            }
        }
    }

    commentingPost?.let { post ->
        LegendCommentsSheet(
            post = post,
            mediaRepository = mediaRepository,
            participantType = participantType,
            onDismiss = { commentingPost = null },
            submit = { body, parentCommentId -> viewModel.comment(post.id, body, parentCommentId) },
        )
    }
    profileAuthor?.let { author ->
        LegendSocialProfileSheet(
            author = author,
            viewModel = viewModel,
            mediaRepository = mediaRepository,
            participantType = participantType,
            dismiss = { profileAuthor = null },
        )
    }
}

@Composable
private fun LegendHacViewportPage(
    post: SocialPost,
    isActive: Boolean,
    mediaRepository: AuthenticatedMediaRepository,
    participantType: String,
    openProfile: () -> Unit,
    react: () -> Unit,
    comment: () -> Unit,
    repost: () -> Unit,
    save: () -> Unit,
    share: () -> Unit,
) {
    val video = post.media.firstOrNull { it.mediaKind.equals("video", ignoreCase = true) }
    Box(Modifier.fillMaxSize().background(LegendColors.Midnight)) {
        if (video != null) {
            LegendProtectedSocialMedia(
                assetId = video.id,
                mediaKind = video.mediaKind,
                participantType = participantType,
                repository = mediaRepository,
                contentDescription = video.accessibilityText,
                modifier = Modifier.fillMaxSize(),
                contentScale = androidx.compose.ui.layout.ContentScale.Crop,
                videoHeight = null,
                autoPlayVideo = isActive,
                showVideoControls = false,
                loopVideo = true,
            )
        } else {
            Icon(
                Icons.Default.VideoLibrary,
                "Hac media unavailable",
                tint = LegendColors.GoldBright,
                modifier = Modifier.align(Alignment.Center).size(40.dp),
            )
        }
        Box(
            Modifier
                .fillMaxSize()
                .background(Brush.verticalGradient(listOf(androidx.compose.ui.graphics.Color.Transparent, LegendColors.Midnight.copy(alpha = 0.78f)))),
        )
        Column(
            modifier = Modifier
                .align(Alignment.BottomStart)
                .padding(
                    start = LegendSpacing.PageHorizontal,
                    end = LegendSize.HacAction + LegendSpacing.Xxl + LegendSpacing.Xs,
                    bottom = LegendSpacing.Lg,
                ),
            verticalArrangement = Arrangement.spacedBy(LegendSpacing.Xs),
        ) {
            Row(
                modifier = Modifier.clickable(onClick = openProfile),
                verticalAlignment = Alignment.CenterVertically,
            ) {
                LegendProtectedAvatar(
                    avatar = post.author.avatar,
                    displayName = post.author.displayName,
                    participantType = participantType,
                    repository = mediaRepository,
                    size = 38.dp,
                )
                Spacer(Modifier.width(LegendSpacing.Xs))
                Text(post.author.displayName, style = LegendTypography.CardTitle, color = LegendColors.OnNavy, maxLines = 1, overflow = TextOverflow.Ellipsis)
                if (post.author.isVerified) Icon(Icons.Default.Verified, "Verified", tint = LegendColors.Verified, modifier = Modifier.padding(start = LegendSpacing.Xs).size(17.dp))
            }
            post.body.takeIf(String::isNotBlank)?.let { body ->
                Text(body, style = LegendTypography.Supporting, color = LegendColors.OnNavy, maxLines = 3, overflow = TextOverflow.Ellipsis)
            }
            post.music?.let { music ->
                Text("${music.trackTitle} · ${music.artistName}", style = LegendTypography.Label, color = LegendColors.GoldSoft, maxLines = 1, overflow = TextOverflow.Ellipsis)
            }
        }
        Column(
            modifier = Modifier
                .align(Alignment.BottomEnd)
                .padding(end = LegendSpacing.Sm, bottom = LegendSpacing.Lg),
            horizontalAlignment = Alignment.CenterHorizontally,
            verticalArrangement = Arrangement.spacedBy(LegendSpacing.Xs),
        ) {
            LegendHacAction(post.reactedByCurrentActor, if (post.reactedByCurrentActor) Icons.Default.Favorite else Icons.Default.FavoriteBorder, "Appreciate Hac", post.metrics.reactionCount.takeIf { it > 0 }, if (post.reactedByCurrentActor) LegendColors.Error else LegendColors.OnNavy, react)
            LegendHacAction(false, Icons.Default.ChatBubbleOutline, "Comment on Hac", post.metrics.commentCount.takeIf { it > 0 }, LegendColors.OnNavy, comment)
            LegendHacAction(post.repostedByCurrentActor, Icons.Default.Repeat, "Repost Hac", post.metrics.repostCount.takeIf { it > 0 }, if (post.repostedByCurrentActor) LegendColors.Info else LegendColors.OnNavy, repost)
            LegendHacAction(false, Icons.AutoMirrored.Filled.Send, "Share Hac", post.metrics.shareCount.takeIf { it > 0 }, LegendColors.OnNavy, share)
            LegendHacAction(post.savedByCurrentActor, if (post.savedByCurrentActor) Icons.Default.Bookmark else Icons.Default.BookmarkBorder, "Save Hac", null, if (post.savedByCurrentActor) LegendColors.GoldBright else LegendColors.OnNavy, save)
        }
    }
}

@Composable
private fun LegendHacAction(
    selected: Boolean,
    icon: androidx.compose.ui.graphics.vector.ImageVector,
    contentDescription: String,
    count: Int?,
    tint: androidx.compose.ui.graphics.Color,
    onClick: () -> Unit,
) {
    Column(horizontalAlignment = Alignment.CenterHorizontally) {
        IconButton(
            onClick = onClick,
            modifier = Modifier
                .size(LegendSize.HacAction)
                .background(LegendColors.Navy.copy(alpha = if (selected) 0.92f else 0.72f), CircleShape),
        ) {
            Icon(icon, contentDescription, tint = tint, modifier = Modifier.size(21.dp))
        }
        count?.let { Text(it.toString(), style = LegendTypography.Caption, color = LegendColors.OnNavy) }
    }
}

@Composable
private fun LegendStoryRail(
    stories: List<SocialPost>,
    mediaRepository: AuthenticatedMediaRepository,
    participantType: String,
    select: (SocialPost) -> Unit,
) {
    Column(verticalArrangement = Arrangement.spacedBy(LegendSpacing.Xs)) {
        Text("Stories", style = LegendTypography.Section, color = LegendColors.TextPrimary)
        LazyRow(horizontalArrangement = Arrangement.spacedBy(LegendSpacing.Md)) {
            items(stories, key = { it.id }) { story ->
                Column(horizontalAlignment = Alignment.CenterHorizontally, modifier = Modifier.width(72.dp).clickable { select(story) }) {
                    Box(Modifier.size(60.dp).background(Brush.linearGradient(listOf(LegendColors.GoldBright, LegendColors.Gold)), CircleShape).padding(3.dp)) {
                        LegendProtectedAvatar(story.author.avatar, story.author.displayName, participantType, mediaRepository, modifier = Modifier.fillMaxSize(), size = 54.dp)
                    }
                    Text(story.author.displayName, style = LegendTypography.Label, color = LegendColors.TextPrimary, maxLines = 1, overflow = TextOverflow.Ellipsis)
                }
            }
        }
    }
}

@Composable
private fun LegendSocialPostCard(
    post: SocialPost,
    mediaRepository: AuthenticatedMediaRepository,
    participantType: String,
    isCurrentActor: Boolean,
    onProfile: (() -> Unit)?,
    onReact: () -> Unit,
    onComment: () -> Unit,
    onFollow: () -> Unit,
    onSave: () -> Unit,
    onRepost: () -> Unit,
    onEdit: (() -> Unit)? = null,
    onDelete: (() -> Unit)? = null,
) {
    val sharePost = LocalLegendSocialShare.current
    var showActions by remember { mutableStateOf(false) }
    Surface(color = LegendColors.Surface, shape = LegendShapes.Card, shadowElevation = 1.dp, modifier = Modifier.fillMaxWidth()) {
        Column(Modifier.padding(LegendSpacing.CardContent), verticalArrangement = Arrangement.spacedBy(LegendSpacing.Sm)) {
        Row(verticalAlignment = Alignment.CenterVertically, modifier = if (onProfile == null) Modifier else Modifier.clickable(onClick = onProfile)) {
            LegendProtectedAvatar(
                avatar = post.author.avatar,
                displayName = post.author.displayName,
                participantType = participantType,
                repository = mediaRepository,
            )
            Spacer(Modifier.width(LegendSpacing.Sm))
            Column(Modifier.weight(1f)) {
                Row(verticalAlignment = Alignment.CenterVertically) {
                    Text(post.author.displayName, style = LegendTypography.CardTitle, color = LegendColors.TextPrimary)
                    if (post.author.isVerified) Icon(Icons.Default.Verified, "Verified", modifier = Modifier.padding(start = LegendSpacing.Xs).size(17.dp), tint = LegendColors.Verified)
                }
                Text(post.author.username?.let { "@$it" } ?: post.displayContentLabel(), style = LegendTypography.Label, color = LegendColors.TextSecondary)
            }
            IconButton(onClick = { showActions = true }) { Icon(Icons.Default.MoreHoriz, "Post actions", tint = LegendColors.TextSecondary) }
        }
        if (post.body.isNotBlank()) Text(post.body, style = LegendTypography.Body, color = LegendColors.TextPrimary)
        if (post.media.isNotEmpty()) {
            val media = remember(post.id, post.media) { post.media.sortedBy { it.displayOrder } }
            val mediaPager = rememberPagerState(pageCount = { media.size })
            val format = post.legendContentType?.sharedFormat
            val aspectRatio = when {
                format?.usesFixedCanvasAspectRatio == true -> format.mediaAspectRatio
                else -> media.firstOrNull()?.aspectRatio?.coerceIn(0.5, 2.0) ?: 1.0
            }.toFloat()
            Box {
                HorizontalPager(
                    state = mediaPager,
                    key = { page -> media[page].id },
                    modifier = Modifier
                        .fillMaxWidth()
                        .aspectRatio(aspectRatio)
                        .clip(LegendShapes.Control),
                ) { page ->
                    val item = media[page]
                    LegendProtectedSocialMedia(
                        assetId = item.id,
                        mediaKind = item.mediaKind,
                        participantType = participantType,
                        repository = mediaRepository,
                        contentDescription = item.accessibilityText,
                        modifier = Modifier.fillMaxSize(),
                        contentScale = androidx.compose.ui.layout.ContentScale.Crop,
                        videoHeight = null,
                    )
                }
                if (media.size > 1) {
                    Row(
                        modifier = Modifier
                            .align(Alignment.BottomCenter)
                            .padding(LegendSpacing.Sm),
                        horizontalArrangement = Arrangement.spacedBy(LegendSpacing.Micro),
                    ) {
                        repeat(media.size) { page ->
                            Box(
                                Modifier
                                    .size(if (page == mediaPager.currentPage) 7.dp else 5.dp)
                                    .background(
                                        if (page == mediaPager.currentPage) LegendColors.OnNavy else LegendColors.OnNavy.copy(alpha = .48f),
                                        CircleShape,
                                    ),
                            )
                        }
                    }
                }
            }
            if (post.media.any { !it.processingState.equals("Ready", ignoreCase = true) }) Text("Preparing media", style = LegendTypography.Label, color = LegendColors.TextSecondary)
        }
        Row(verticalAlignment = Alignment.CenterVertically) {
            TextButton(onClick = onReact) {
                Icon(
                    if (post.reactedByCurrentActor) Icons.Default.Favorite else Icons.Default.FavoriteBorder,
                    "React",
                    tint = LegendColors.Gold,
                )
                Spacer(Modifier.width(LegendSpacing.Micro))
                Text(post.reactionCount.toString())
            }
            TextButton(onClick = onComment, enabled = post.commentsEnabled) {
                Icon(Icons.Default.ChatBubbleOutline, "Comments")
                Spacer(Modifier.width(LegendSpacing.Micro))
                Text(post.commentCount.toString())
            }
            IconButton(onClick = onSave) { Icon(if (post.savedByCurrentActor) Icons.Default.Bookmark else Icons.Default.BookmarkBorder, "Save", tint = LegendColors.Gold) }
            IconButton(onClick = onRepost) {
                Icon(
                    Icons.Default.Repeat,
                    if (post.repostedByCurrentActor) "Undo repost" else "Repost",
                    tint = if (post.repostedByCurrentActor) LegendColors.Info else LegendColors.TextSecondary,
                )
            }
            IconButton(onClick = { sharePost(post) }) { Icon(Icons.Default.Share, "Share", tint = LegendColors.TextSecondary) }
        }
        post.music?.let { music -> Text("♫ ${music.trackTitle} · ${music.artistName}", style = LegendTypography.Label, color = LegendColors.TextSecondary) }
        if (showActions) {
            LegendSocialPostActionsSheet(
                post = post,
                isCurrentActor = isCurrentActor,
                dismiss = { showActions = false },
                follow = onFollow,
                repost = onRepost,
                edit = onEdit,
                delete = onDelete,
            )
        }
    }
    }
}

@Composable
private fun LegendSocialPostActionsSheet(
    post: SocialPost,
    isCurrentActor: Boolean,
    dismiss: () -> Unit,
    follow: () -> Unit,
    repost: () -> Unit,
    edit: (() -> Unit)?,
    delete: (() -> Unit)?,
) {
    ModalBottomSheet(onDismissRequest = dismiss, containerColor = LegendColors.Canvas, dragHandle = null) {
        Column(
            modifier = Modifier
                .fillMaxWidth()
                .padding(horizontal = LegendSpacing.PageHorizontal, vertical = LegendSpacing.Md),
            verticalArrangement = Arrangement.spacedBy(LegendSpacing.Xs),
        ) {
            Row(verticalAlignment = Alignment.CenterVertically) {
                Text("${post.displayContentLabel()} actions", style = LegendTypography.Section, color = LegendColors.TextPrimary, modifier = Modifier.weight(1f))
                TextButton(onClick = dismiss) { Text("Done", style = LegendTypography.BodyEmphasis, color = LegendColors.Gold) }
            }
            if (!isCurrentActor) {
                LegendSocialActionRow(
                    icon = if (post.followedByCurrentActor) Icons.Default.PersonRemove else Icons.Default.PersonAdd,
                    title = if (post.followedByCurrentActor) "Unfollow" else if (post.followRequestPending) "Follow request pending" else "Follow",
                    detail = if (post.followRequestPending) "Waiting for the server-authorized response" else "Update your LEGEND relationship",
                    onClick = { follow(); dismiss() },
                )
            }
            LegendSocialActionRow(
                icon = Icons.Default.Repeat,
                title = if (post.repostedByCurrentActor) "Undo repost" else "Repost",
                detail = "Share through the existing LEGEND social authority",
                onClick = { repost(); dismiss() },
            )
            if (isCurrentActor) {
                edit?.let { action ->
                    LegendSocialActionRow(
                        icon = Icons.Default.Edit,
                        title = "Edit ${post.displayContentLabel().lowercase()}",
                        detail = "Update the server-owned publication",
                        onClick = { action(); dismiss() },
                    )
                }
                delete?.let { action ->
                    LegendSocialActionRow(
                        icon = Icons.Default.DeleteOutline,
                        title = "Delete ${post.displayContentLabel().lowercase()}",
                        detail = "Remove this publication through LEGEND",
                        tint = LegendColors.Error,
                        onClick = { action(); dismiss() },
                    )
                }
            }
        }
    }
}

@Composable
private fun LegendSocialActionRow(
    icon: androidx.compose.ui.graphics.vector.ImageVector,
    title: String,
    detail: String,
    tint: androidx.compose.ui.graphics.Color = LegendColors.TextPrimary,
    onClick: () -> Unit,
) {
    Surface(
        color = LegendColors.SurfaceInset,
        shape = LegendShapes.Control,
        modifier = Modifier.fillMaxWidth().clickable(onClick = onClick),
    ) {
        Row(
            modifier = Modifier.padding(horizontal = LegendSpacing.Md, vertical = LegendSpacing.Sm),
            verticalAlignment = Alignment.CenterVertically,
        ) {
            Icon(icon, null, tint = tint)
            Spacer(Modifier.width(LegendSpacing.Sm))
            Column(Modifier.weight(1f)) {
                Text(title, style = LegendTypography.BodyEmphasis, color = tint)
                Text(detail, style = LegendTypography.Caption, color = LegendColors.TextSecondary)
            }
            Icon(Icons.Default.ChevronRight, null, tint = LegendColors.TextSecondary)
        }
    }
}

@Composable
private fun CreatePostSheet(
    onDismiss: () -> Unit,
    createText: (CreateSocialPostRequest) -> Unit,
    createMedia: (List<Uri>, SocialMediaPublishOptions, Uri?) -> Unit,
) {
    val context = LocalContext.current
    var creationStage by remember { mutableStateOf(LegendSocialCreationStage.MODE) }
    var contentType by remember { mutableStateOf<LegendSocialContentType?>(null) }
    var body by remember { mutableStateOf("") }
    var topicsAndMentions by remember { mutableStateOf("") }
    var location by remember { mutableStateOf("") }
    var accessibilityText by remember { mutableStateOf("") }
    var commentsEnabled by remember { mutableStateOf(true) }
    var selected by remember { mutableStateOf<List<Uri>>(emptyList()) }
    var mediaSelectionError by remember { mutableStateOf<String?>(null) }
    val acceptSelection: (List<Uri>) -> Unit = { candidate ->
        val type = contentType
        if (type == null) {
            mediaSelectionError = "Choose a LEGEND format before selecting media."
        } else {
            val limited = candidate.take(type.maximumMediaItems)
            when {
                limited.isEmpty() -> Unit
                type == LegendSocialContentType.HAC && !context.isPortableHacVideo(limited.single()) -> {
                    mediaSelectionError = "A Hac requires one playable MP4 video. LEGEND will process the selected upload on the server."
                }
                else -> {
                    selected = limited
                    mediaSelectionError = null
                }
            }
        }
    }
    val postPicker = rememberLauncherForActivityResult(ActivityResultContracts.PickMultipleVisualMedia(LegendSocialContentType.POST.maximumMediaItems)) {
        acceptSelection(it)
    }
    val singlePicker = rememberLauncherForActivityResult(ActivityResultContracts.PickVisualMedia()) { uri ->
        acceptSelection(uri?.let(::listOf).orEmpty())
    }
    val creatorSheetState = rememberModalBottomSheetState(skipPartiallyExpanded = true)

    ModalBottomSheet(
        onDismissRequest = onDismiss,
        sheetState = creatorSheetState,
        containerColor = if (creationStage == LegendSocialCreationStage.SHARE) LegendColors.Canvas else LegendColors.Midnight,
        dragHandle = null,
    ) {
        when (creationStage) {
            LegendSocialCreationStage.MODE -> LegendSocialCreationModeMenu(
                dismiss = onDismiss,
                select = { type ->
                    contentType = type
                    selected = emptyList()
                    mediaSelectionError = null
                    creationStage = LegendSocialCreationStage.LIBRARY
                },
            )

            LegendSocialCreationStage.LIBRARY -> {
                val type = requireNotNull(contentType)
                LegendSocialMediaLibrary(
                    type = type,
                    selected = selected,
                    selectionError = mediaSelectionError,
                    chooseMedia = {
                        when (type) {
                            LegendSocialContentType.POST -> postPicker.launch(PickVisualMediaRequest(ActivityResultContracts.PickVisualMedia.ImageAndVideo))
                            LegendSocialContentType.STORY -> singlePicker.launch(PickVisualMediaRequest(ActivityResultContracts.PickVisualMedia.ImageAndVideo))
                            LegendSocialContentType.HAC -> singlePicker.launch(PickVisualMediaRequest(ActivityResultContracts.PickVisualMedia.VideoOnly))
                        }
                    },
                    remove = { selected = selected - it },
                    next = { creationStage = LegendSocialCreationStage.SHARE },
                    dismiss = onDismiss,
                    context = context,
                )
            }

            LegendSocialCreationStage.SHARE -> {
                val type = requireNotNull(contentType)
                val publicationBody = listOf(body.trim(), topicsAndMentions.trim())
                    .filter(String::isNotBlank)
                    .joinToString("\n\n")
                val canPublish = when (type) {
                    LegendSocialContentType.POST -> publicationBody.isNotBlank() || selected.isNotEmpty()
                    LegendSocialContentType.STORY -> selected.size == 1
                    LegendSocialContentType.HAC -> selected.size == 1 && context.isPortableHacVideo(selected.single())
                }
                LegendSocialShareDetails(
                    type = type,
                    selected = selected,
                    body = body,
                    updateBody = { body = it },
                    topicsAndMentions = topicsAndMentions,
                    updateTopicsAndMentions = { topicsAndMentions = it },
                    location = location,
                    updateLocation = { location = it },
                    accessibilityText = accessibilityText,
                    updateAccessibilityText = { accessibilityText = it },
                    commentsEnabled = commentsEnabled,
                    updateCommentsEnabled = { commentsEnabled = it },
                    selectionError = mediaSelectionError,
                    back = { creationStage = LegendSocialCreationStage.LIBRARY },
                    share = {
                        if (selected.isEmpty()) {
                            createText(CreateSocialPostRequest(type.apiValue, publicationBody, "AuthorizedNetwork", location.trim().takeIf(String::isNotBlank), commentsEnabled))
                        } else {
                            createMedia(
                                selected,
                                SocialMediaPublishOptions(
                                    type.apiValue,
                                    publicationBody,
                                    "AuthorizedNetwork",
                                    location.trim().takeIf(String::isNotBlank),
                                    commentsEnabled,
                                    accessibilityText.trim().takeIf(String::isNotBlank),
                                ),
                                null,
                            )
                        }
                    },
                    canPublish = canPublish,
                    context = context,
                )
            }
        }
    }
}

private enum class LegendSocialCreationStage { MODE, LIBRARY, SHARE }

private val LegendSocialContentType.sharedFormat
    get() = LegendSocialFormats.named(
        when (this) {
            LegendSocialContentType.POST -> "post"
            LegendSocialContentType.STORY -> "story"
            LegendSocialContentType.HAC -> "hac"
        },
    )

private val LegendSocialContentType.maximumMediaItems: Int
    get() = sharedFormat.maximumMediaItems

private fun LegendSocialContentType.label(): String = when (this) {
    LegendSocialContentType.POST -> LegendCopy.value("content.post")
    LegendSocialContentType.STORY -> LegendCopy.value("content.story")
    LegendSocialContentType.HAC -> LegendCopy.value("content.hac")
}

private fun SocialPost.displayContentLabel(): String = legendContentType?.label() ?: contentType

private fun LegendSocialContentType.newContentTitle(): String = "New ${label().lowercase()}"

private fun LegendSocialContentType.selectionHint(): String = when (this) {
    LegendSocialContentType.POST -> "Select up to 10 photos or videos."
    LegendSocialContentType.STORY -> "Select one photo or video for a 24-hour moment."
    LegendSocialContentType.HAC -> "Select one playable MP4 video for your Hac."
}

private fun LegendSocialContentType.icon() = when (this) {
    LegendSocialContentType.POST -> Icons.Default.Create
    LegendSocialContentType.STORY -> Icons.Default.RadioButtonUnchecked
    LegendSocialContentType.HAC -> Icons.Default.VideoLibrary
}

@Composable
private fun LegendSocialCreationModeMenu(
    dismiss: () -> Unit,
    select: (LegendSocialContentType) -> Unit,
) {
    Column(
        modifier = Modifier.fillMaxHeight(.94f).fillMaxWidth().background(
            Brush.verticalGradient(listOf(LegendColors.Navy, LegendColors.Midnight)),
        ).padding(horizontal = LegendSpacing.Lg, vertical = LegendSpacing.Sm),
    ) {
        Row(verticalAlignment = Alignment.CenterVertically) {
            IconButton(
                onClick = dismiss,
                modifier = Modifier.size(LegendSize.MinimumTapTarget).background(LegendColors.OnNavy.copy(alpha = .12f), CircleShape),
            ) { Icon(Icons.Default.Close, "Close creator", tint = LegendColors.OnNavy) }
            Spacer(Modifier.weight(1f))
            Text("Create", style = LegendTypography.CardTitle, color = LegendColors.OnNavy)
            Spacer(Modifier.weight(1f))
            Spacer(Modifier.size(LegendSize.MinimumTapTarget))
        }
        Column(
            modifier = Modifier.weight(1f),
            verticalArrangement = Arrangement.Center,
        ) {
            Text("Choose a format", style = LegendTypography.Title, color = LegendColors.OnNavy)
            Spacer(Modifier.height(LegendSpacing.Md))
            LegendSocialContentType.entries.forEach { type ->
                TextButton(
                    onClick = { select(type) },
                    modifier = Modifier.fillMaxWidth().height(64.dp).padding(vertical = LegendSpacing.Micro),
                    shape = LegendShapes.Control,
                    colors = ButtonDefaults.textButtonColors(containerColor = LegendColors.OnNavy.copy(alpha = .07f), contentColor = LegendColors.OnNavy),
                ) {
                    Box(
                        Modifier.size(40.dp).background(LegendColors.OnNavy.copy(alpha = .11f), CircleShape),
                        contentAlignment = Alignment.Center,
                    ) { Icon(type.icon(), null, tint = LegendColors.GoldBright) }
                    Spacer(Modifier.width(LegendSpacing.Sm))
                    Column(Modifier.weight(1f), horizontalAlignment = Alignment.Start) {
                        Text(type.label(), style = LegendTypography.BodyEmphasis)
                    }
                    Icon(Icons.Default.ChevronRight, null, tint = LegendColors.OnNavy.copy(alpha = .55f))
                }
            }
        }
    }
}

@Composable
private fun LegendSocialMediaLibrary(
    type: LegendSocialContentType,
    selected: List<Uri>,
    selectionError: String?,
    chooseMedia: () -> Unit,
    remove: (Uri) -> Unit,
    next: () -> Unit,
    dismiss: () -> Unit,
    context: android.content.Context,
) {
    Column(
        modifier = Modifier.fillMaxHeight(.94f).fillMaxWidth().background(
            Brush.verticalGradient(listOf(LegendColors.Navy, LegendColors.Midnight)),
        ),
    ) {
        Row(
            modifier = Modifier.fillMaxWidth().padding(horizontal = LegendSpacing.Sm, vertical = LegendSpacing.Xs),
            verticalAlignment = Alignment.CenterVertically,
        ) {
            IconButton(
                onClick = dismiss,
                modifier = Modifier.size(LegendSize.MinimumTapTarget).background(LegendColors.OnNavy.copy(alpha = .12f), CircleShape),
            ) { Icon(Icons.Default.Close, "Cancel ${type.label()} creation", tint = LegendColors.OnNavy) }
            Spacer(Modifier.weight(1f))
            Text(type.newContentTitle(), style = LegendTypography.CardTitle, color = LegendColors.OnNavy)
            Spacer(Modifier.weight(1f))
            TextButton(onClick = next, enabled = selected.isNotEmpty() || type == LegendSocialContentType.POST) {
                Text("Next", style = LegendTypography.BodyEmphasis, color = if (selected.isNotEmpty() || type == LegendSocialContentType.POST) LegendColors.GoldBright else LegendColors.OnNavy.copy(alpha = .36f))
            }
        }
        LazyColumn(
            modifier = Modifier.fillMaxSize(),
            contentPadding = PaddingValues(horizontal = LegendSpacing.Sm, vertical = LegendSpacing.Sm),
            verticalArrangement = Arrangement.spacedBy(LegendSpacing.Md),
        ) {
            item {
                Row(verticalAlignment = Alignment.CenterVertically) {
                    Column(Modifier.weight(1f)) {
                        Text("Recents", style = LegendTypography.Section, color = LegendColors.OnNavy)
                        Text(type.selectionHint(), style = LegendTypography.Supporting, color = LegendColors.GoldSoft)
                    }
                    IconButton(
                        onClick = chooseMedia,
                        modifier = Modifier.size(42.dp).background(LegendColors.GoldBright, CircleShape),
                    ) { Icon(Icons.Default.PermMedia, "Choose ${type.label()} media", tint = LegendColors.Midnight) }
                }
            }
            item {
                Surface(
                    color = LegendColors.OnNavy.copy(alpha = .07f),
                    shape = LegendShapes.Control,
                    modifier = Modifier.fillMaxWidth().clickable(onClick = chooseMedia),
                ) {
                    Column(
                        Modifier.padding(LegendSpacing.Xl),
                        horizontalAlignment = Alignment.CenterHorizontally,
                        verticalArrangement = Arrangement.spacedBy(LegendSpacing.Xs),
                    ) {
                        Icon(type.icon(), null, tint = LegendColors.GoldBright, modifier = Modifier.size(28.dp))
                        Text(if (selected.isEmpty()) "Choose from your device" else "Replace selected media", style = LegendTypography.BodyEmphasis, color = LegendColors.OnNavy)
                        Text("Android's system photo picker keeps your library private until you select media for LEGEND.", style = LegendTypography.Caption, color = LegendColors.GoldSoft)
                    }
                }
            }
            if (selectionError != null) item {
                Text(selectionError, style = LegendTypography.Supporting, color = LegendColors.Warning)
            }
            if (selected.isNotEmpty()) {
                items(selected, key = { it.toString() }) { uri ->
                    LegendSocialSelectedMediaCard(
                        uri = uri,
                        type = type,
                        context = context,
                        remove = { remove(uri) },
                    )
                }
            }
        }
    }
}

@Composable
private fun LegendSocialSelectedMediaCard(
    uri: Uri,
    type: LegendSocialContentType,
    context: android.content.Context,
    remove: () -> Unit,
) {
    val mimeType = context.contentResolver.getType(uri).orEmpty()
    Surface(color = LegendColors.OnNavy.copy(alpha = .07f), shape = LegendShapes.Control, modifier = Modifier.fillMaxWidth()) {
        Column(Modifier.padding(LegendSpacing.Sm), verticalArrangement = Arrangement.spacedBy(LegendSpacing.Xs)) {
            if (mimeType.startsWith("video/")) {
                LegendLocalVideoPreview(uri, Modifier.fillMaxWidth().height(if (type == LegendSocialContentType.POST) 190.dp else 320.dp))
            } else {
                AsyncImage(
                    model = uri,
                    contentDescription = "Selected ${type.label()} media",
                    modifier = Modifier.fillMaxWidth().heightIn(max = if (type == LegendSocialContentType.POST) 260.dp else 420.dp),
                )
            }
            Row(verticalAlignment = Alignment.CenterVertically) {
                Text(context.contentResolver.legendDisplayName(uri), style = LegendTypography.Label, color = LegendColors.OnNavy, modifier = Modifier.weight(1f), maxLines = 1, overflow = TextOverflow.Ellipsis)
                IconButton(onClick = remove) { Icon(Icons.Default.Close, "Remove selected media", tint = LegendColors.GoldBright) }
            }
        }
    }
}

@Composable
private fun LegendSocialShareDetails(
    type: LegendSocialContentType,
    selected: List<Uri>,
    body: String,
    updateBody: (String) -> Unit,
    topicsAndMentions: String,
    updateTopicsAndMentions: (String) -> Unit,
    location: String,
    updateLocation: (String) -> Unit,
    accessibilityText: String,
    updateAccessibilityText: (String) -> Unit,
    commentsEnabled: Boolean,
    updateCommentsEnabled: (Boolean) -> Unit,
    selectionError: String?,
    back: () -> Unit,
    share: () -> Unit,
    canPublish: Boolean,
    context: android.content.Context,
) {
    Column(Modifier.fillMaxHeight(.94f).fillMaxWidth().background(LegendColors.Canvas)) {
        Row(
            modifier = Modifier.fillMaxWidth().padding(horizontal = LegendSpacing.Sm, vertical = LegendSpacing.Xs),
            verticalAlignment = Alignment.CenterVertically,
        ) {
            IconButton(onClick = back, modifier = Modifier.size(LegendSize.MinimumTapTarget).background(LegendColors.SurfaceInset, CircleShape)) {
                Icon(Icons.AutoMirrored.Filled.ArrowBack, "Back to media", tint = LegendColors.TextPrimary)
            }
            Spacer(Modifier.weight(1f))
            Text(type.newContentTitle(), style = LegendTypography.CardTitle, color = LegendColors.TextPrimary)
            Spacer(Modifier.weight(1f))
            TextButton(onClick = share, enabled = canPublish) {
                Text("Share", style = LegendTypography.BodyEmphasis, color = if (canPublish) LegendColors.Gold else LegendColors.TextTertiary)
            }
        }
        LazyColumn(
            modifier = Modifier.fillMaxSize(),
            contentPadding = PaddingValues(horizontal = LegendSpacing.Md, vertical = LegendSpacing.Sm),
            verticalArrangement = Arrangement.spacedBy(LegendSpacing.Md),
        ) {
            if (selected.isNotEmpty()) item {
                LegendSocialSelectedMediaCard(selected.first(), type, context) {}
            }
            item {
                OutlinedTextField(
                    value = body,
                    onValueChange = updateBody,
                    label = { Text(if (type == LegendSocialContentType.STORY) "Add a story message..." else "Write a caption...") },
                    modifier = Modifier.fillMaxWidth(),
                    minLines = 3,
                    maxLines = 7,
                    shape = LegendShapes.Control,
                    colors = legendShareFieldColors(),
                )
            }
            item {
                Surface(color = LegendColors.SurfaceInset, shape = LegendShapes.Control, modifier = Modifier.fillMaxWidth()) {
                    Column {
                        LegendSocialDetailField("Topics & mentions", topicsAndMentions, updateTopicsAndMentions)
                        HorizontalDivider(color = LegendColors.Divider, modifier = Modifier.padding(start = 52.dp))
                        LegendSocialDetailField("Location", location, updateLocation)
                        HorizontalDivider(color = LegendColors.Divider, modifier = Modifier.padding(start = 52.dp))
                        Row(Modifier.fillMaxWidth().heightIn(min = 52.dp).padding(horizontal = LegendSpacing.Sm), verticalAlignment = Alignment.CenterVertically) {
                            Icon(Icons.Default.Group, null, tint = LegendColors.TextPrimary, modifier = Modifier.size(24.dp))
                            Spacer(Modifier.width(LegendSpacing.Sm))
                            Text("Audience", style = LegendTypography.Body, color = LegendColors.TextPrimary, modifier = Modifier.weight(1f))
                            Text("Legend network", style = LegendTypography.Caption, color = LegendColors.TextSecondary)
                        }
                        HorizontalDivider(color = LegendColors.Divider, modifier = Modifier.padding(start = 52.dp))
                        LegendSocialDetailField("Alt text", accessibilityText, updateAccessibilityText)
                        HorizontalDivider(color = LegendColors.Divider, modifier = Modifier.padding(start = 52.dp))
                        Row(Modifier.fillMaxWidth().heightIn(min = 52.dp).padding(horizontal = LegendSpacing.Sm), verticalAlignment = Alignment.CenterVertically) {
                            Icon(Icons.Default.ChatBubbleOutline, null, tint = LegendColors.TextPrimary, modifier = Modifier.size(24.dp))
                            Spacer(Modifier.width(LegendSpacing.Sm))
                            Text("Allow comments", style = LegendTypography.Body, color = LegendColors.TextPrimary, modifier = Modifier.weight(1f))
                            Switch(checked = commentsEnabled, onCheckedChange = updateCommentsEnabled, colors = SwitchDefaults.colors(checkedThumbColor = LegendColors.Navy, checkedTrackColor = LegendColors.GoldBright))
                        }
                    }
                }
            }
            if (selectionError != null) item {
                Text(selectionError, style = LegendTypography.Supporting, color = LegendColors.Warning)
            }
            item {
                Text("Android supplies native selection and playback. LEGEND's existing protected upload and media-processing authority owns publication.", style = LegendTypography.Caption, color = LegendColors.TextSecondary)
            }
        }
    }
}

@Composable
private fun LegendSocialDetailField(label: String, value: String, update: (String) -> Unit) {
    OutlinedTextField(
        value = value,
        onValueChange = update,
        label = { Text(label) },
        modifier = Modifier.fillMaxWidth().padding(horizontal = LegendSpacing.Sm, vertical = LegendSpacing.Tiny),
        singleLine = true,
        leadingIcon = { Icon(Icons.Default.Edit, null, tint = LegendColors.TextPrimary) },
        shape = LegendShapes.Compact,
        colors = legendShareFieldColors(),
    )
}

private fun android.content.Context.isPortableHacVideo(uri: Uri): Boolean {
    val mimeType = contentResolver.getType(uri).orEmpty()
    val fileName = contentResolver.legendDisplayName(uri)
    return mimeType.startsWith("video/", ignoreCase = true) && fileName.endsWith(".mp4", ignoreCase = true)
}

@Composable
private fun legendDarkFieldColors() = OutlinedTextFieldDefaults.colors(
    focusedContainerColor = LegendColors.Navy,
    unfocusedContainerColor = LegendColors.Navy,
    focusedTextColor = LegendColors.OnNavy,
    unfocusedTextColor = LegendColors.OnNavy,
    focusedBorderColor = LegendColors.Gold,
    unfocusedBorderColor = LegendColors.NavyElevated,
    focusedLabelColor = LegendColors.GoldBright,
    unfocusedLabelColor = LegendColors.GoldSoft,
)

@Composable
private fun legendShareFieldColors() = OutlinedTextFieldDefaults.colors(
    focusedContainerColor = LegendColors.SurfaceInset,
    unfocusedContainerColor = LegendColors.SurfaceInset,
    focusedTextColor = LegendColors.TextPrimary,
    unfocusedTextColor = LegendColors.TextPrimary,
    focusedBorderColor = LegendColors.Gold,
    unfocusedBorderColor = LegendColors.Divider,
    focusedLabelColor = LegendColors.Gold,
    unfocusedLabelColor = LegendColors.TextSecondary,
)

@Composable
private fun LegendCommentsSheet(
    post: SocialPost,
    mediaRepository: AuthenticatedMediaRepository,
    participantType: String,
    onDismiss: () -> Unit,
    submit: (String, String?) -> Unit,
) {
    var body by remember(post.id) { mutableStateOf("") }
    var replyTo by remember(post.id) { mutableStateOf<SocialComment?>(null) }
    val parents = remember(post.comments) { post.comments.filter { it.parentCommentId == null } }
    fun replies(parentId: String) = post.comments.filter { it.parentCommentId == parentId }
    ModalBottomSheet(onDismissRequest = onDismiss, containerColor = LegendColors.Canvas) {
        Column(Modifier.fillMaxHeight(.9f)) {
            Row(Modifier.fillMaxWidth().padding(horizontal = LegendSpacing.PageHorizontal, vertical = LegendSpacing.Sm), verticalAlignment = Alignment.CenterVertically) {
                Text("Comments", style = LegendTypography.Section, color = LegendColors.TextPrimary, modifier = Modifier.weight(1f))
                Text("${post.commentCount}", style = LegendTypography.Label, color = LegendColors.Gold)
                TextButton(onClick = onDismiss) { Text("Done", color = LegendColors.Gold) }
            }
            if (parents.isEmpty()) {
                Box(Modifier.weight(1f)) { LegendEmptyState("No comments yet", "Be the first to join this LEGEND conversation.") }
            } else LazyColumn(
                modifier = Modifier.weight(1f),
                contentPadding = PaddingValues(horizontal = LegendSpacing.PageHorizontal, vertical = LegendSpacing.Xs),
                verticalArrangement = Arrangement.spacedBy(LegendSpacing.Sm),
            ) {
                items(parents, key = { it.id }) { comment ->
                    LegendCommentRow(comment, mediaRepository, participantType, { replyTo = comment })
                    replies(comment.id).forEach { reply ->
                        Row(Modifier.padding(start = LegendSpacing.Xl)) { LegendCommentRow(reply, mediaRepository, participantType, { replyTo = comment }) }
                    }
                }
            }
            replyTo?.let { target ->
                Row(Modifier.fillMaxWidth().background(LegendColors.GoldSoft).padding(horizontal = LegendSpacing.PageHorizontal, vertical = LegendSpacing.Xs), verticalAlignment = Alignment.CenterVertically) {
                    Text("Replying to ${target.author.displayName}", style = LegendTypography.Label, color = LegendColors.Midnight, modifier = Modifier.weight(1f))
                    IconButton(onClick = { replyTo = null }) { Icon(Icons.Default.Close, "Cancel reply", tint = LegendColors.Midnight) }
                }
            }
            Row(Modifier.fillMaxWidth().background(LegendColors.Surface).padding(horizontal = LegendSpacing.PageHorizontal, vertical = LegendSpacing.Sm), verticalAlignment = Alignment.Bottom) {
                OutlinedTextField(body, { body = it }, modifier = Modifier.weight(1f), placeholder = { Text("Add a comment") }, maxLines = 4, shape = LegendShapes.Control, colors = legendMessagingFieldColors())
                IconButton(onClick = { if (body.isNotBlank()) { submit(body.trim(), replyTo?.id); body = ""; replyTo = null } }, enabled = body.isNotBlank(), modifier = Modifier.padding(start = LegendSpacing.Xs).background(if (body.isNotBlank()) LegendColors.Gold else LegendColors.SurfaceInset, CircleShape)) { Icon(Icons.AutoMirrored.Filled.Send, "Post comment", tint = LegendColors.Midnight) }
            }
        }
    }
}

@Composable
private fun LegendCommentRow(comment: SocialComment, mediaRepository: AuthenticatedMediaRepository, participantType: String, reply: () -> Unit) {
    Row(verticalAlignment = Alignment.Top) {
        LegendProtectedAvatar(comment.author.avatar, comment.author.displayName, participantType, mediaRepository, size = 30.dp)
        Spacer(Modifier.width(LegendSpacing.Xs))
        Surface(color = LegendColors.SurfaceInset, shape = LegendShapes.Control, modifier = Modifier.weight(1f)) {
            Column(Modifier.padding(LegendSpacing.Xs), verticalArrangement = Arrangement.spacedBy(LegendSpacing.Micro)) {
                Text(comment.author.displayName, style = LegendTypography.Label, color = LegendColors.TextPrimary)
                Text(comment.body, style = LegendTypography.Supporting, color = LegendColors.TextPrimary)
                Row(verticalAlignment = Alignment.CenterVertically) {
                    Text(legendCompactTime(comment.createdUtc), style = LegendTypography.Label, color = LegendColors.TextTertiary, modifier = Modifier.weight(1f))
                    TextButton(onClick = reply, modifier = Modifier.height(28.dp)) { Text("Reply", style = LegendTypography.Label, color = LegendColors.Gold) }
                }
            }
        }
    }
}

@Composable
private fun EditPostDialog(post: SocialPost, dismiss: () -> Unit, submit: (String) -> Unit) {
    var body by remember(post.id) { mutableStateOf(post.body) }
    AlertDialog(
        onDismissRequest = dismiss,
        title = { Text("Edit ${post.displayContentLabel()}") },
        text = { OutlinedTextField(value = body, onValueChange = { body = it }, modifier = Modifier.fillMaxWidth(), minLines = 3, label = { Text("Caption") }) },
        confirmButton = { TextButton(onClick = { submit(body) }) { Text("Save") } },
        dismissButton = { TextButton(onClick = dismiss) { Text("Cancel") } },
    )
}

@Composable
private fun LegendStoryViewer(
    story: SocialPost,
    mediaRepository: AuthenticatedMediaRepository,
    participantType: String,
    dismiss: () -> Unit,
    recordView: () -> Unit,
) {
    LaunchedEffect(story.id) { recordView() }
    ModalBottomSheet(onDismissRequest = dismiss, containerColor = LegendColors.Midnight) {
        Column(Modifier.fillMaxWidth().padding(LegendSpacing.PageHorizontal), verticalArrangement = Arrangement.spacedBy(LegendSpacing.Sm)) {
            Row(verticalAlignment = Alignment.CenterVertically) {
                LegendProtectedAvatar(story.author.avatar, story.author.displayName, participantType, mediaRepository)
                Spacer(Modifier.width(LegendSpacing.Sm))
                Text(story.author.displayName, style = LegendTypography.CardTitle, color = LegendColors.OnNavy, modifier = Modifier.weight(1f))
                IconButton(onClick = dismiss) { Icon(Icons.Default.Close, "Close story", tint = LegendColors.OnNavy) }
            }
            story.media.forEach { media ->
                LegendProtectedSocialMedia(media.id, media.mediaKind, participantType, mediaRepository, media.accessibilityText, Modifier.fillMaxWidth())
            }
            if (story.body.isNotBlank()) Text(story.body, style = LegendTypography.Body, color = LegendColors.OnNavy)
        }
    }
}

@Composable
private fun LegendSocialProfileSheet(
    author: SocialAuthor,
    viewModel: SocialViewModel,
    mediaRepository: AuthenticatedMediaRepository,
    participantType: String,
    dismiss: () -> Unit,
) {
    val posts by viewModel.publicProfilePosts.collectAsStateWithLifecycle()
    val metrics by viewModel.publicProfileMetrics.collectAsStateWithLifecycle()
    var commentingPost by remember { mutableStateOf<SocialPost?>(null) }
    LaunchedEffect(author.identity.userId, author.identity.participantType) { viewModel.loadPublicProfile(author) }
    ModalBottomSheet(onDismissRequest = dismiss, containerColor = LegendColors.Canvas) {
        LazyColumn(
            modifier = Modifier.fillMaxHeight(0.92f),
            contentPadding = PaddingValues(horizontal = LegendSpacing.PageHorizontal, vertical = LegendSpacing.Md),
            verticalArrangement = Arrangement.spacedBy(LegendSpacing.Sm),
        ) {
            item {
                Row(verticalAlignment = Alignment.CenterVertically) {
                    LegendProtectedAvatar(author.avatar, author.displayName, participantType, mediaRepository, size = 68.dp)
                    Spacer(Modifier.width(LegendSpacing.Md))
                    Column(Modifier.weight(1f)) {
                        Row(verticalAlignment = Alignment.CenterVertically) {
                            Text(author.displayName, style = LegendTypography.Section, color = LegendColors.TextPrimary)
                            if (author.isVerified) Icon(Icons.Default.Verified, "Verified", modifier = Modifier.padding(start = LegendSpacing.Xs).size(18.dp), tint = LegendColors.Verified)
                        }
                        author.username?.let { Text("@$it", style = LegendTypography.Label, color = LegendColors.TextSecondary) }
                        author.roleLabel?.let { Text(it, style = LegendTypography.Label, color = LegendColors.Gold) }
                    }
                    TextButton(onClick = dismiss) { Text("Done", color = LegendColors.Gold) }
                }
            }
            author.bio?.takeIf(String::isNotBlank)?.let { bio -> item { Text(bio, style = LegendTypography.Body, color = LegendColors.TextPrimary) } }
            item {
                when (metrics) {
                    is LoadState.Data -> {
                        val value = (metrics as LoadState.Data<SocialProfileMetrics>).value
                        Surface(color = LegendColors.Navy, shape = LegendShapes.Control, modifier = Modifier.fillMaxWidth()) {
                            Row(Modifier.padding(LegendSpacing.Sm), horizontalArrangement = Arrangement.SpaceEvenly) {
                                LegendMetric("Posts", value.postCount.toString())
                                LegendMetric("Followers", value.followerCount.toString())
                                LegendMetric("Following", value.followingCount.toString())
                            }
                        }
                    }
                    else -> Unit
                }
            }
            item { Text("Posts", style = LegendTypography.Section, color = LegendColors.TextPrimary) }
            when (posts) {
                LoadState.Idle, LoadState.Loading -> item { LegendLoadingState() }
                is LoadState.Error -> item { LegendErrorState((posts as LoadState.Error).message) { viewModel.loadPublicProfile(author) } }
                is LoadState.Data -> {
                    val value = (posts as LoadState.Data<List<SocialPost>>).value
                    if (value.isEmpty()) item { LegendMessagingEmptyCard("No posts yet", "This profile has no server-visible social content.", action = dismiss) }
                    else items(value, key = { it.id }) { post ->
                        LegendSocialPostCard(
                            post = post,
                            mediaRepository = mediaRepository,
                            participantType = participantType,
                            isCurrentActor = false,
                            onProfile = null,
                            onReact = { viewModel.react(post.id) },
                            onComment = { commentingPost = post },
                            onFollow = { viewModel.toggleFollow(post) },
                            onSave = { viewModel.toggleSave(post.id) },
                            onRepost = { viewModel.toggleRepost(post.id) },
                        )
                    }
                }
            }
        }
    }
    commentingPost?.let { post ->
        LegendCommentsSheet(post, mediaRepository, participantType, { commentingPost = null }) { body, parentCommentId -> viewModel.comment(post.id, body, parentCommentId) }
    }
}

@Composable
private fun LegendMetric(label: String, value: String) {
    Column(horizontalAlignment = Alignment.CenterHorizontally) {
        Text(value, style = LegendTypography.CardTitle, color = LegendColors.OnNavy)
        Text(label, style = LegendTypography.Label, color = LegendColors.GoldSoft)
    }
}

@Composable
private fun AccountScreen(
    viewModel: AccountViewModel,
    socialViewModel: SocialViewModel,
    founderAccountsViewModel: FounderAccountsViewModel,
    controlledResourceViewModel: ControlledResourceViewModel,
    dailyScriptureManagementViewModel: DailyScriptureManagementViewModel,
    communitySafetyViewModel: CommunitySafetyReviewViewModel,
    isFounder: Boolean,
    canManageScripture: Boolean,
    canManageCommunity: Boolean,
    financialRepository: FinancialRepository,
    mediaRepository: AuthenticatedMediaRepository,
    participantType: String,
    alternateParticipantTypes: List<String>,
    switchRole: (String) -> Unit,
    signOut: () -> Unit,
) {
    val profile by viewModel.profile.collectAsStateWithLifecycle()
    val lifecycle by viewModel.lifecycle.collectAsStateWithLifecycle()
    val usernameAvailability by viewModel.usernameAvailability.collectAsStateWithLifecycle()
    val socialState by socialViewModel.state.collectAsStateWithLifecycle()
    val socialMetrics by socialViewModel.profileMetrics.collectAsStateWithLifecycle()
    val socialPosts by socialViewModel.profilePosts.collectAsStateWithLifecycle()
    var financial by remember { mutableStateOf(false) }
    var settingsOpen by remember { mutableStateOf(false) }
    var creatorInsightsOpen by remember { mutableStateOf(false) }
    var selectedContent by remember { mutableStateOf(SocialCollection.POSTS) }
    var deletePrompt by remember { mutableStateOf(false) }
    var languageAccount by remember { mutableStateOf<MobileAccountProfile?>(null) }
    var editing by remember { mutableStateOf<MobileAccountProfile?>(null) }
    var founderManagement by remember { mutableStateOf(false) }
    var controlledResourceType by remember { mutableStateOf<LegendFounderResource?>(null) }
    var followRequestsOpen by remember { mutableStateOf(false) }
    var scriptureManagementOpen by remember { mutableStateOf(false) }
    var communitySafetyOpen by remember { mutableStateOf(false) }
    var commentingPost by remember { mutableStateOf<SocialPost?>(null) }
    var editingPost by remember { mutableStateOf<SocialPost?>(null) }
    var selectedProfilePost by remember { mutableStateOf<SocialPost?>(null) }
    val context = LocalContext.current
    val avatarPicker = rememberLauncherForActivityResult(ActivityResultContracts.PickVisualMedia()) { uri -> uri?.let { viewModel.updateAvatar(context, it) } }
    LaunchedEffect(Unit) { viewModel.load(); socialViewModel.load(); socialViewModel.loadCurrentProfile() }
    val profilePagerState = rememberPagerState(pageCount = { 2 })
    val profilePagerScope = rememberCoroutineScope()

    LaunchedEffect(financial) {
        val destination = if (financial) 1 else 0
        if (profilePagerState.currentPage != destination) {
            profilePagerState.animateScrollToPage(destination)
        }
    }
    LaunchedEffect(profilePagerState.currentPage) {
        financial = profilePagerState.currentPage == 1
    }
    BackHandler(enabled = profilePagerState.currentPage == 1) {
        profilePagerScope.launch { profilePagerState.animateScrollToPage(0) }
    }

    HorizontalPager(
        state = profilePagerState,
        modifier = Modifier.fillMaxSize(),
    ) { page ->
        if (page == 1) {
            FinancialScreen(financialRepository, participantType) {
                profilePagerScope.launch { profilePagerState.animateScrollToPage(0) }
            }
        } else {
        when (profile) {
                LoadState.Idle,
                LoadState.Loading -> LegendLoadingState()

                is LoadState.Error -> LegendErrorState((profile as LoadState.Error).message, viewModel::load)
                is LoadState.Data -> {
                    val account = (profile as LoadState.Data<MobileAccountProfile>).value
                    val metrics = (socialMetrics as? LoadState.Data<SocialProfileMetrics>)?.value
                    val profileItems = (socialPosts as? LoadState.Data<List<SocialPost>>)?.value.orEmpty()
                    val selectedItems = profileItems.filter { post ->
                        post.legendContentType == selectedContent.socialContentType
                    }
                    LazyColumn(
                        modifier = Modifier.fillMaxSize().background(LegendColors.Canvas),
                        verticalArrangement = Arrangement.spacedBy(LegendSpacing.Sm),
                        contentPadding = PaddingValues(horizontal = LegendSpacing.PageHorizontal, vertical = LegendSpacing.Md),
                    ) {
                        item {
                            LegendProfileIdentityCard(
                                account = account,
                                metrics = metrics,
                                hacCount = profileItems.count { it.legendContentType == LegendSocialContentType.HAC }
                                    .takeIf { profileItems.isNotEmpty() } ?: metrics?.videoCount.orZero,
                                participantType = participantType,
                                mediaRepository = mediaRepository,
                                changeAvatar = { avatarPicker.launch(PickVisualMediaRequest(ActivityResultContracts.PickVisualMedia.ImageOnly)) },
                                openSettings = { settingsOpen = true },
                                alternateParticipantTypes = alternateParticipantTypes,
                                switchRole = switchRole,
                            )
                        }
                        item {
                            Button(
                                onClick = { editing = account },
                                modifier = Modifier.fillMaxWidth().height(LegendSize.ProfileControlHeight),
                                shape = LegendShapes.Control,
                                colors = ButtonDefaults.buttonColors(containerColor = LegendColors.Navy, contentColor = LegendColors.OnNavy),
                            ) { Text("Edit profile", style = LegendTypography.BodyEmphasis) }
                        }
                        item {
                            LegendProfileContentSelector(selectedContent) { selectedContent = it }
                        }
                        when {
                            socialPosts is LoadState.Loading || socialPosts is LoadState.Idle -> item {
                                LegendProfileGridSkeleton()
                            }
                            socialPosts is LoadState.Error -> item {
                                LegendErrorState((socialPosts as LoadState.Error).message, socialViewModel::loadCurrentProfile)
                            }
                            selectedItems.isEmpty() -> item {
                                LegendProfileContentEmptyState(selectedContent)
                            }
                            else -> items(selectedItems.chunked(3)) { row ->
                                LegendProfileGridRow(
                                    posts = row,
                                    mediaRepository = mediaRepository,
                                    participantType = participantType,
                                    open = { selectedProfilePost = it },
                                )
                            }
                        }
                    }
                }
            }
        }
    }

    if (deletePrompt) {
        DeletionDialog(
            onDismiss = { deletePrompt = false },
            submit = {
                viewModel.requestDeletion(it)
                deletePrompt = false
            },
        )
    }
    languageAccount?.let { account ->
        LanguageDialog(
            current = account.translationAccess?.preferredCommunicationLanguage.orEmpty(),
            onDismiss = { languageAccount = null },
            submit = { language ->
                viewModel.updateLanguage(account, language)
                languageAccount = null
            },
        )
    }
    editing?.let { account ->
        AccountEditorSheet(
            account = account,
            usernameAvailability = usernameAvailability,
            checkUsernameAvailability = viewModel::checkUsernameAvailability,
            dismiss = { editing = null },
            submit = { displayName, phone, title, shortBio, username, bio, website, location, email, emailVisible, phoneVisible, private ->
                viewModel.update(account, displayName, phone, title, shortBio, username, bio, website, location, email, emailVisible, phoneVisible, private)
                editing = null
            },
        )
    }
    if (founderManagement) {
        LegendFounderAccountsSheet(
            viewModel = founderAccountsViewModel,
            dismiss = { founderManagement = false },
        )
    }
    controlledResourceType?.let { resource ->
        LegendControlledResourceAccessSheet(resource, controlledResourceViewModel, mediaRepository) { controlledResourceType = null }
    }
    if (scriptureManagementOpen) {
        LegendDailyScriptureManagementSheet(dailyScriptureManagementViewModel) { scriptureManagementOpen = false }
    }
    if (communitySafetyOpen) {
        LegendCommunitySafetyReviewSheet(communitySafetyViewModel, isFounder) { communitySafetyOpen = false }
    }
    if (followRequestsOpen) {
        LegendFollowRequestsSheet(socialViewModel, mediaRepository, participantType, { followRequestsOpen = false })
    }
    if (creatorInsightsOpen) {
        LegendCreatorInsightsSheet(
            insights = (socialState as? LoadState.Data<SocialSnapshot>)?.value?.creatorInsights,
            profileMetrics = (socialMetrics as? LoadState.Data<SocialProfileMetrics>)?.value,
            dismiss = { creatorInsightsOpen = false },
        )
    }
    commentingPost?.let { post ->
        LegendCommentsSheet(post, mediaRepository, participantType, { commentingPost = null }) { body, parentCommentId -> socialViewModel.comment(post.id, body, parentCommentId) }
    }
    editingPost?.let { post ->
        EditPostDialog(post, { editingPost = null }) { body -> socialViewModel.updatePost(post.id, body); editingPost = null }
    }
    selectedProfilePost?.let { post ->
        ModalBottomSheet(onDismissRequest = { selectedProfilePost = null }, containerColor = LegendColors.Canvas) {
            // A multi-image post may be taller than a viewport. Keep the same
            // post renderer used everywhere else, inside one scroll container,
            // so its real action row never becomes unreachable.
            LazyColumn(
                modifier = Modifier.fillMaxHeight(.92f),
                contentPadding = PaddingValues(
                    horizontal = LegendSpacing.PageHorizontal,
                    vertical = LegendSpacing.Md,
                ),
            ) {
                item {
                    LegendSocialPostCard(
                        post = post,
                        mediaRepository = mediaRepository,
                        participantType = participantType,
                        isCurrentActor = true,
                        onProfile = null,
                        onReact = { socialViewModel.react(post.id) },
                        onComment = { commentingPost = post },
                        onFollow = { socialViewModel.toggleFollow(post) },
                        onSave = { socialViewModel.toggleSave(post.id) },
                        onRepost = { socialViewModel.toggleRepost(post.id) },
                        onEdit = { editingPost = post },
                        onDelete = { socialViewModel.deletePost(post.id); selectedProfilePost = null },
                    )
                }
            }
        }
    }
    if (settingsOpen && profile is LoadState.Data) {
        LegendAccountSettingsSheet(
            account = (profile as LoadState.Data<MobileAccountProfile>).value,
            lifecycle = lifecycle as? LoadState.Data<AccountLifecycle>,
            isFounder = isFounder,
            canManageScripture = canManageScripture,
            canManageCommunity = canManageCommunity,
            dismiss = { settingsOpen = false },
            edit = { editing = it; settingsOpen = false },
            creatorInsights = {
                socialViewModel.load()
                creatorInsightsOpen = true
                settingsOpen = false
            },
            financial = { financial = true; settingsOpen = false },
            language = { languageAccount = it; settingsOpen = false },
            founderManagement = { founderManagement = true; settingsOpen = false },
            memberAuthority = { controlledResourceType = LegendFounderResource.LanguageTranslation; settingsOpen = false },
            scriptureManagement = { scriptureManagementOpen = true; settingsOpen = false },
            communitySafety = { communitySafetyOpen = true; settingsOpen = false },
            updatePrivacy = viewModel::updatePrivacy,
            updateTranslationLearningConsent = viewModel::updateTranslationLearningConsent,
            followRequests = { socialViewModel.loadFollowRequests(); followRequestsOpen = true; settingsOpen = false },
            resume = viewModel::resumeAccount,
            pause = viewModel::pauseAccount,
            deleteAccount = { deletePrompt = true; settingsOpen = false },
            signOut = signOut,
        )
    }
}

private val Int?.orZero get() = this ?: 0

private val SocialCollection.socialContentType: LegendSocialContentType
    get() = when (this) {
        SocialCollection.POSTS -> LegendSocialContentType.POST
        SocialCollection.HACS -> LegendSocialContentType.HAC
        SocialCollection.STORIES -> LegendSocialContentType.STORY
    }

@Composable
private fun LegendProfileIdentityCard(
    account: MobileAccountProfile,
    metrics: SocialProfileMetrics?,
    hacCount: Int,
    participantType: String,
    mediaRepository: AuthenticatedMediaRepository,
    changeAvatar: () -> Unit,
    openSettings: () -> Unit,
    alternateParticipantTypes: List<String>,
    switchRole: (String) -> Unit,
) {
    var accountMenuOpen by remember { mutableStateOf(false) }
    Surface(
        color = LegendColors.SurfaceElevated,
        shape = LegendShapes.ProminentCard,
        shadowElevation = 1.dp,
        modifier = Modifier.fillMaxWidth().border(1.dp, LegendColors.Divider, LegendShapes.ProminentCard),
    ) {
        Column(
            modifier = Modifier.padding(LegendSpacing.Sm),
            verticalArrangement = Arrangement.spacedBy(LegendSpacing.Xs),
        ) {
            Row(verticalAlignment = Alignment.CenterVertically) {
                Box {
                    LegendProtectedAvatar(
                        avatar = account.avatar,
                        displayName = account.displayName,
                        participantType = participantType,
                        repository = mediaRepository,
                        size = LegendSize.ProfileAvatar,
                    )
                    IconButton(
                        onClick = changeAvatar,
                        modifier = Modifier
                            .align(Alignment.BottomEnd)
                            .size(LegendSize.MinimumTapTarget),
                    ) {
                        Box(
                            Modifier
                                .size(LegendSize.ProfileAvatarCamera)
                                .background(LegendColors.Gold, CircleShape)
                                .border(1.dp, LegendColors.OnNavy, CircleShape),
                            contentAlignment = Alignment.Center,
                        ) {
                            Icon(Icons.Default.PhotoCamera, "Change profile photo", tint = LegendColors.Midnight, modifier = Modifier.size(14.dp))
                        }
                    }
                }
                Spacer(Modifier.width(LegendSpacing.Md))
                Column(Modifier.weight(1f), verticalArrangement = Arrangement.spacedBy(LegendSpacing.Micro)) {
                    val handle = account.username?.takeIf(String::isNotBlank)?.let { "@$it" } ?: account.displayName
                    if (alternateParticipantTypes.isEmpty()) {
                        Text(handle, style = LegendTypography.Label, color = LegendColors.TextPrimary)
                    } else {
                        Box {
                            Row(
                                modifier = Modifier.clickable { accountMenuOpen = true },
                                verticalAlignment = Alignment.CenterVertically,
                            ) {
                                Text(handle, style = LegendTypography.Label, color = LegendColors.TextPrimary)
                                Icon(
                                    Icons.Default.KeyboardArrowDown,
                                    "Switch LEGEND account",
                                    tint = LegendColors.TextPrimary,
                                    modifier = Modifier.size(18.dp),
                                )
                            }
                            DropdownMenu(
                                expanded = accountMenuOpen,
                                onDismissRequest = { accountMenuOpen = false },
                            ) {
                                DropdownMenuItem(
                                    text = { Text("Current: ${account.participantType}") },
                                    leadingIcon = { Icon(Icons.Default.CheckCircle, null, tint = LegendColors.Gold) },
                                    onClick = { accountMenuOpen = false },
                                    enabled = false,
                                )
                                alternateParticipantTypes.forEach { role ->
                                    DropdownMenuItem(
                                        text = { Text("Continue as $role") },
                                        leadingIcon = {
                                            Icon(
                                                if (role.equals("Agent", ignoreCase = true)) Icons.Default.BusinessCenter else Icons.Default.Person,
                                                null,
                                                tint = LegendColors.Navy,
                                            )
                                        },
                                        onClick = {
                                            accountMenuOpen = false
                                            switchRole(role)
                                        },
                                    )
                                }
                            }
                        }
                    }
                    Row(verticalAlignment = Alignment.CenterVertically) {
                        Text(account.displayName, style = LegendTypography.Title, color = LegendColors.TextPrimary, maxLines = 1, overflow = TextOverflow.Ellipsis)
                        if (account.isVerified) Icon(Icons.Default.Verified, "Verified", modifier = Modifier.padding(start = LegendSpacing.Xs).size(20.dp), tint = LegendColors.Verified)
                    }
                }
                IconButton(
                    onClick = openSettings,
                    modifier = Modifier.size(LegendSize.MinimumTapTarget),
                ) {
                    Box(
                        Modifier.size(LegendSize.ProfileSettingsIcon).background(LegendColors.Navy, CircleShape),
                        contentAlignment = Alignment.Center,
                    ) { Icon(Icons.Default.Settings, "Open profile settings", tint = LegendColors.OnNavy) }
                }
            }

            val bio = account.bio?.takeIf(String::isNotBlank) ?: account.shortBio?.takeIf(String::isNotBlank)
            bio?.let { Text(it, style = LegendTypography.Caption, color = LegendColors.TextPrimary, maxLines = 2, overflow = TextOverflow.Ellipsis) }
            account.location?.takeIf(String::isNotBlank)?.let { LegendProfileDetail(Icons.Default.LocationOn, it, LegendColors.TextSecondary) }
            account.website?.takeIf(String::isNotBlank)?.let { LegendProfileDetail(Icons.Default.Link, it, LegendColors.Gold) }
            if (account.isEmailVisible) account.profileEmail?.takeIf(String::isNotBlank)?.let { LegendProfileDetail(Icons.Default.Email, it, LegendColors.TextSecondary) }
            if (account.isPhoneVisible) account.phone?.takeIf(String::isNotBlank)?.let { LegendProfileDetail(Icons.Default.Phone, it, LegendColors.TextSecondary) }

            Row(Modifier.fillMaxWidth().padding(top = LegendSpacing.Xs), horizontalArrangement = Arrangement.SpaceEvenly) {
                LegendProfileMetric(hacCount, "Hacs")
                LegendProfileMetric(metrics?.followingCount.orZero, "Following")
                LegendProfileMetric(metrics?.followerCount.orZero, "Followers")
            }
        }
    }
}

@Composable
private fun LegendProfileDetail(icon: androidx.compose.ui.graphics.vector.ImageVector, value: String, color: androidx.compose.ui.graphics.Color) {
    Row(verticalAlignment = Alignment.CenterVertically) {
        Icon(icon, null, tint = color, modifier = Modifier.size(18.dp))
        Spacer(Modifier.width(LegendSpacing.Xs))
        Text(value, style = LegendTypography.Caption, color = color, maxLines = 1, overflow = TextOverflow.Ellipsis)
    }
}

@Composable
private fun LegendProfileMetric(value: Int, label: String) {
    Column(horizontalAlignment = Alignment.CenterHorizontally) {
        Text(value.toString(), style = LegendTypography.Section, color = LegendColors.TextPrimary)
        Text(label, style = LegendTypography.Caption, color = LegendColors.TextPrimary)
    }
}

@Composable
private fun LegendProfileContentSelector(selected: SocialCollection, select: (SocialCollection) -> Unit) {
    Row(
        modifier = Modifier
            .fillMaxWidth()
            .background(LegendColors.BrandBlueSurface, LegendShapes.Control)
            .border(1.dp, LegendColors.Navy.copy(alpha = 0.22f), LegendShapes.Control)
            .padding(LegendSpacing.Tiny),
        horizontalArrangement = Arrangement.spacedBy(LegendSpacing.Xs),
    ) {
        listOf(SocialCollection.POSTS, SocialCollection.HACS, SocialCollection.STORIES).forEach { option ->
            val isSelected = option == selected
            TextButton(
                onClick = { select(option) },
                modifier = Modifier.weight(1f).height(LegendSize.ProfileControlHeight),
                shape = LegendShapes.Compact,
                colors = ButtonDefaults.textButtonColors(
                    containerColor = if (isSelected) LegendColors.Navy else androidx.compose.ui.graphics.Color.Transparent,
                    contentColor = if (isSelected) LegendColors.OnNavy else LegendColors.Navy,
                ),
            ) {
                Icon(
                    when (option) {
                        SocialCollection.POSTS -> Icons.Default.GridView
                        SocialCollection.HACS -> Icons.Default.VideoLibrary
                        SocialCollection.STORIES -> Icons.Default.RadioButtonUnchecked
                    },
                    null,
                    modifier = Modifier.size(18.dp),
                )
                Spacer(Modifier.width(LegendSpacing.Xs))
                Text(option.label, style = LegendTypography.CardTitle)
            }
        }
    }
}

@Composable
private fun LegendProfileGridRow(
    posts: List<SocialPost>,
    mediaRepository: AuthenticatedMediaRepository,
    participantType: String,
    open: (SocialPost) -> Unit,
) {
    Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.spacedBy(LegendSpacing.Tiny)) {
        posts.forEach { post ->
            LegendProfileGridTile(post, mediaRepository, participantType, { open(post) }, Modifier.weight(1f))
        }
        repeat(3 - posts.size) { Spacer(Modifier.weight(1f)) }
    }
}

@Composable
private fun LegendProfileGridTile(
    post: SocialPost,
    mediaRepository: AuthenticatedMediaRepository,
    participantType: String,
    open: () -> Unit,
    modifier: Modifier = Modifier,
) {
    val media = post.media.firstOrNull()
    Box(
        modifier = modifier
            .aspectRatio(4f / 5f)
            .clip(LegendShapes.Compact)
            .background(LegendColors.Navy)
            .clickable(onClick = open),
    ) {
        if (media != null && media.mediaKind.equals("image", ignoreCase = true)) {
            LegendProtectedSocialMedia(
                assetId = media.id,
                mediaKind = media.mediaKind,
                participantType = participantType,
                repository = mediaRepository,
                contentDescription = media.accessibilityText,
                modifier = Modifier.fillMaxSize(),
                contentScale = androidx.compose.ui.layout.ContentScale.Crop,
                videoHeight = null,
            )
        } else {
            Column(
                modifier = Modifier.fillMaxSize().padding(LegendSpacing.Sm),
                horizontalAlignment = Alignment.CenterHorizontally,
                verticalArrangement = Arrangement.Center,
            ) {
                Icon(
                    if (post.legendContentType == LegendSocialContentType.HAC) Icons.Default.PlayCircle else Icons.Default.ChatBubble,
                    null,
                    tint = LegendColors.GoldBright,
                    modifier = Modifier.size(28.dp),
                )
                if (post.body.isNotBlank()) Text(post.body, style = LegendTypography.Label, color = LegendColors.OnNavy, maxLines = 4, overflow = TextOverflow.Ellipsis)
            }
        }
        if (post.legendContentType == LegendSocialContentType.HAC) {
            Icon(Icons.Default.PlayCircle, "Play Hac", tint = LegendColors.OnNavy, modifier = Modifier.align(Alignment.TopEnd).padding(LegendSpacing.Xs).size(22.dp))
        }
        if (post.legendContentType == LegendSocialContentType.STORY) {
            Icon(Icons.Default.RadioButtonUnchecked, "Story", tint = LegendColors.GoldBright, modifier = Modifier.align(Alignment.TopEnd).padding(LegendSpacing.Xs).size(20.dp))
        }
    }
}

@Composable
private fun LegendProfileGridSkeleton() {
    Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.spacedBy(LegendSpacing.Tiny)) {
        repeat(3) {
            Box(
                Modifier.weight(1f).aspectRatio(4f / 5f).background(LegendColors.BrandBlueSurface, LegendShapes.Compact),
            )
        }
    }
}

@Composable
private fun LegendProfileContentEmptyState(content: SocialCollection) {
    Surface(color = LegendColors.BrandBlueSurface, shape = LegendShapes.Card, modifier = Modifier.fillMaxWidth()) {
        Column(
            Modifier.padding(LegendSpacing.Xl),
            horizontalAlignment = Alignment.CenterHorizontally,
            verticalArrangement = Arrangement.spacedBy(LegendSpacing.Xs),
        ) {
            Icon(Icons.Default.Collections, null, tint = LegendColors.Gold, modifier = Modifier.size(28.dp))
            Text("No ${content.label.lowercase()} yet", style = LegendTypography.Section, color = LegendColors.TextPrimary)
            Text("Published ${content.label.lowercase()} will appear here.", style = LegendTypography.Supporting, color = LegendColors.TextSecondary)
        }
    }
}

@Composable
private fun LegendAccountSettingsSheet(
    account: MobileAccountProfile,
    lifecycle: LoadState.Data<AccountLifecycle>?,
    isFounder: Boolean,
    canManageScripture: Boolean,
    canManageCommunity: Boolean,
    dismiss: () -> Unit,
    edit: (MobileAccountProfile) -> Unit,
    creatorInsights: () -> Unit,
    financial: () -> Unit,
    language: (MobileAccountProfile) -> Unit,
    founderManagement: () -> Unit,
    memberAuthority: () -> Unit,
    scriptureManagement: () -> Unit,
    communitySafety: () -> Unit,
    updatePrivacy: (Boolean) -> Unit,
    updateTranslationLearningConsent: (Boolean) -> Unit,
    followRequests: () -> Unit,
    resume: () -> Unit,
    pause: () -> Unit,
    deleteAccount: () -> Unit,
    signOut: () -> Unit,
) {
    ModalBottomSheet(onDismissRequest = dismiss, containerColor = LegendColors.Canvas) {
        LazyColumn(
            modifier = Modifier.fillMaxHeight(.92f),
            contentPadding = PaddingValues(horizontal = LegendSpacing.PageHorizontal, vertical = LegendSpacing.Md),
            verticalArrangement = Arrangement.spacedBy(LegendSpacing.Xs),
        ) {
            item {
                LegendProfileSheetHeader(
                    eyebrow = "Member experience",
                    title = "Profile settings",
                    detail = "Personalize the details people see here. These settings are private to the LEGEND mobile app.",
                    dismiss = dismiss,
                )
            }
            item { AccountSettingsRow("Edit profile", "Update your public profile, handle, and visibility", Icons.Default.Edit, click = { edit(account) }) }
            item { AccountSettingsRow("Creator insights", "Review reach and engagement", Icons.Default.Insights, creatorInsights) }
            item { AccountSettingsRow("Financial Intelligence", "Server-authoritative projections and priorities", Icons.Default.AccountBalance, financial) }
            item { AccountSettingsRow("Language preferences", account.translationAccess?.preferredCommunicationLanguage ?: "No preferred communication language set", Icons.Default.Translate, { language(account) }, footnote = "Translation is server-only.") }
            if (isFounder) item { AccountSettingsRow("Founder management", "Server-authorized account archive and removal controls", Icons.Default.AdminPanelSettings, founderManagement) }
            if (isFounder) item { AccountSettingsRow("Member authority", "Grant or revoke founder-controlled LEGEND resources", Icons.Default.ManageAccounts, memberAuthority) }
            if (canManageScripture) item { AccountSettingsRow("Daily Scripture", "Manage the server-owned scripture schedule", Icons.AutoMirrored.Filled.MenuBook, scriptureManagement) }
            if (canManageCommunity) item { AccountSettingsRow("Community safety", "Review open reports using your server-issued authority", Icons.Default.GppGood, communitySafety) }
            item {
                Surface(
                    color = LegendColors.ContactNavy,
                    shape = LegendShapes.Card,
                    modifier = Modifier
                        .fillMaxWidth()
                        .border(LegendSpacing.Hairline, LegendColors.Gold.copy(alpha = 0.52f), LegendShapes.Card),
                ) {
                    Column(Modifier.padding(LegendSpacing.CardContent), verticalArrangement = Arrangement.spacedBy(LegendSpacing.Xs)) {
                        Text("Privacy & safety", style = LegendTypography.CardTitle, color = LegendColors.OnNavy)
                        Row(verticalAlignment = Alignment.CenterVertically) {
                            Column(Modifier.weight(1f)) {
                                Text("Private profile", style = LegendTypography.Body, color = LegendColors.OnNavy)
                                Text(if (account.isPrivate) "Only approved followers can view your profile." else "Your public profile is visible to your LEGEND network.", style = LegendTypography.Supporting, color = LegendColors.OnNavy.copy(alpha = 0.76f))
                            }
                            Switch(checked = account.isPrivate, onCheckedChange = updatePrivacy)
                        }
                        HorizontalDivider(color = LegendColors.OnNavy.copy(alpha = 0.16f))
                        Row(verticalAlignment = Alignment.CenterVertically) {
                            Column(Modifier.weight(1f)) {
                                Text("Improve LEGEND Connect", style = LegendTypography.Body, color = LegendColors.OnNavy)
                                Text("When every participant opts in, eligible translated conversations can improve LEGEND Connect.", style = LegendTypography.Supporting, color = LegendColors.OnNavy.copy(alpha = 0.76f))
                            }
                            Switch(checked = account.allowsConsentedTranslationLearning, onCheckedChange = updateTranslationLearningConsent)
                        }
                    }
                }
            }
            if (account.isPrivate) item { AccountSettingsRow("Follow requests", "Approve or decline people waiting to follow you", Icons.Default.PersonAdd, followRequests) }
            item {
                Surface(
                    color = LegendColors.ContactNavy,
                    shape = LegendShapes.Card,
                    modifier = Modifier
                        .fillMaxWidth()
                        .border(LegendSpacing.Hairline, LegendColors.Gold.copy(alpha = 0.52f), LegendShapes.Card),
                ) {
                    Column(Modifier.padding(LegendSpacing.CardContent), verticalArrangement = Arrangement.spacedBy(LegendSpacing.Xs)) {
                        Text("Account lifecycle", style = LegendTypography.CardTitle, color = LegendColors.OnNavy)
                        Text(lifecycle?.value?.state ?: "Loading account status", style = LegendTypography.Supporting, color = LegendColors.OnNavy.copy(alpha = 0.76f))
                        if (lifecycle?.value?.canResume == true) TextButton(onClick = resume) { Text("Resume account", color = LegendColors.GoldBright) }
                        else TextButton(onClick = pause) { Text("Pause account", color = LegendColors.GoldBright) }
                        TextButton(onClick = deleteAccount) { Text("Request account deletion", color = LegendColors.Error) }
                    }
                }
            }
            item { AccountSettingsRow("Sign out", "Securely end this Android session", Icons.AutoMirrored.Filled.Logout, signOut) }
        }
    }
}

/** The same server-projected creator intelligence exposed from iOS Profile settings. */
@Composable
private fun LegendCreatorInsightsSheet(
    insights: CreatorInsights?,
    profileMetrics: SocialProfileMetrics?,
    dismiss: () -> Unit,
) {
    ModalBottomSheet(onDismissRequest = dismiss, containerColor = LegendColors.Canvas) {
        LazyColumn(
            modifier = Modifier.fillMaxHeight(.92f),
            contentPadding = PaddingValues(horizontal = LegendSpacing.PageHorizontal, vertical = LegendSpacing.Md),
            verticalArrangement = Arrangement.spacedBy(LegendSpacing.Md),
        ) {
            item {
                Row(verticalAlignment = Alignment.CenterVertically) {
                    Column(Modifier.weight(1f)) {
                        Text("CREATOR INTELLIGENCE", style = LegendTypography.Eyebrow, color = LegendColors.Gold)
                        Text("Your LEGEND impact", style = LegendTypography.Section, color = LegendColors.TextPrimary)
                        Text("Reach and engagement generated from protected LEGEND activity.", style = LegendTypography.Supporting, color = LegendColors.TextSecondary)
                    }
                    IconButton(onClick = dismiss) { Icon(Icons.Default.Close, "Close creator insights", tint = LegendColors.TextPrimary) }
                }
            }
            if (insights == null) {
                item { LegendLoadingState() }
            } else {
                item {
                    Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.spacedBy(LegendSpacing.Sm)) {
                        LegendCreatorInsightMetric("Views", insights.totalViews.toString(), Icons.Default.PlayCircle, LegendColors.Info, Modifier.weight(1f))
                        LegendCreatorInsightMetric("Reach", insights.totalReach.toString(), Icons.Default.People, LegendColors.Success, Modifier.weight(1f))
                    }
                }
                item {
                    Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.spacedBy(LegendSpacing.Sm)) {
                        LegendCreatorInsightMetric("Followers", insights.followerCount.toString(), Icons.Default.PersonAdd, LegendColors.Gold, Modifier.weight(1f))
                        LegendCreatorInsightMetric("Engagement", legendInsightPercentage(insights.engagementRatePercentage), Icons.AutoMirrored.Filled.TrendingUp, LegendColors.Navy, Modifier.weight(1f))
                    }
                }
                item {
                    Surface(color = LegendColors.BrandBlueSurface, shape = LegendShapes.Card, modifier = Modifier.fillMaxWidth()) {
                        Column(Modifier.padding(LegendSpacing.CardContent), verticalArrangement = Arrangement.spacedBy(LegendSpacing.Xs)) {
                            Text("CONTENT AND COMMUNITY", style = LegendTypography.Eyebrow, color = LegendColors.Gold)
                            LegendCreatorInsightValue("Posts", profileMetrics?.postCount?.toString() ?: "—")
                            LegendCreatorInsightValue("Hacs", profileMetrics?.videoCount?.toString() ?: "—")
                            LegendCreatorInsightValue("Stories", profileMetrics?.storyCount?.toString() ?: "—")
                            LegendCreatorInsightValue("Following", profileMetrics?.followingCount?.toString() ?: "—")
                            LegendCreatorInsightValue("Profile visits", insights.profileVisits.toString())
                            LegendCreatorInsightValue("Followers gained this week", insights.followersGained.toString())
                        }
                    }
                }
                item { LegendCreatorInsightList("Top posts", "Publish a post to begin building performance history.", insights.topPosts) }
                item { LegendCreatorInsightList("Top Hacs", "Publish a Hac to begin building Hac performance history.", insights.topVideos) }
                item { LegendCreatorInsightList("Top stories", "Publish a story to begin building story performance history.", insights.topStories) }
            }
        }
    }
}

@Composable
private fun LegendCreatorInsightMetric(
    label: String,
    value: String,
    icon: androidx.compose.ui.graphics.vector.ImageVector,
    color: androidx.compose.ui.graphics.Color,
    modifier: Modifier = Modifier,
) {
    Surface(color = LegendColors.BrandBlueSurface, shape = LegendShapes.Control, modifier = modifier) {
        Column(Modifier.padding(LegendSpacing.Md), verticalArrangement = Arrangement.spacedBy(LegendSpacing.Xs)) {
            Icon(icon, null, tint = color, modifier = Modifier.size(20.dp))
            Text(value, style = LegendTypography.Section, color = LegendColors.TextPrimary, maxLines = 1, overflow = TextOverflow.Ellipsis)
            Text(label, style = LegendTypography.Caption, color = LegendColors.TextSecondary, maxLines = 1, overflow = TextOverflow.Ellipsis)
        }
    }
}

@Composable
private fun LegendCreatorInsightValue(label: String, value: String) {
    Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.SpaceBetween, verticalAlignment = Alignment.CenterVertically) {
        Text(label, style = LegendTypography.Supporting, color = LegendColors.TextSecondary)
        Text(value, style = LegendTypography.BodyEmphasis, color = LegendColors.TextPrimary)
    }
}

@Composable
private fun LegendCreatorInsightList(title: String, emptyMessage: String, items: List<SocialPostInsight>) {
    Surface(color = LegendColors.SurfaceElevated, shape = LegendShapes.Card, modifier = Modifier.fillMaxWidth()) {
        Column(Modifier.padding(LegendSpacing.CardContent), verticalArrangement = Arrangement.spacedBy(LegendSpacing.Sm)) {
            Text(title, style = LegendTypography.CardTitle, color = LegendColors.TextPrimary)
            if (items.isEmpty()) {
                Text(emptyMessage, style = LegendTypography.Supporting, color = LegendColors.TextSecondary)
            } else {
                items.forEach { insight ->
                    Column(verticalArrangement = Arrangement.spacedBy(LegendSpacing.Micro)) {
                        Text(insight.postedUtc.take(10), style = LegendTypography.BodyEmphasis, color = LegendColors.TextPrimary)
                        Text(
                            "${insight.metrics.uniqueViewerCount} reached · ${insight.metrics.reactionCount} appreciations · ${legendInsightPercentage(insight.engagementRatePercentage)} engagement",
                            style = LegendTypography.Supporting,
                            color = LegendColors.TextSecondary,
                        )
                    }
                }
            }
        }
    }
}

private fun legendInsightPercentage(value: Double): String =
    String.format(java.util.Locale.US, "%.1f%%", value)

@Composable
private fun AccountSettingsRow(title: String, detail: String, icon: androidx.compose.ui.graphics.vector.ImageVector, click: () -> Unit, footnote: String? = null) {
    Surface(
        color = LegendColors.ContactNavy,
        shape = LegendShapes.Control,
        modifier = Modifier
            .fillMaxWidth()
            .clickable(onClick = click)
            .border(LegendSpacing.Hairline, LegendColors.Gold.copy(alpha = 0.52f), LegendShapes.Control),
    ) {
        Row(Modifier.padding(LegendSpacing.Sm), verticalAlignment = Alignment.CenterVertically) {
            Icon(icon, null, tint = LegendColors.GoldBright, modifier = Modifier.size(22.dp))
            Spacer(Modifier.width(LegendSpacing.Sm))
            Column(Modifier.weight(1f)) {
                Text(title, style = LegendTypography.CardTitle, color = LegendColors.OnNavy)
                Text(detail, style = LegendTypography.Supporting, color = LegendColors.OnNavy.copy(alpha = 0.76f), maxLines = 2, overflow = TextOverflow.Ellipsis)
                footnote?.let { Text(it, style = LegendTypography.Label, color = LegendColors.GoldBright) }
            }
            Icon(Icons.Default.ChevronRight, null, tint = LegendColors.OnNavy.copy(alpha = 0.82f))
        }
    }
}

/** iOS-equivalent branded sheet header with an explicit, always-reachable close action. */
@Composable
private fun LegendProfileSheetHeader(
    eyebrow: String,
    title: String,
    detail: String,
    dismiss: () -> Unit,
) {
    Row(
        verticalAlignment = Alignment.Top,
        horizontalArrangement = Arrangement.spacedBy(LegendSpacing.Md),
        modifier = Modifier.fillMaxWidth(),
    ) {
        Column(Modifier.weight(1f), verticalArrangement = Arrangement.spacedBy(LegendSpacing.Xs)) {
            Row(verticalAlignment = Alignment.CenterVertically) {
                Box(
                    Modifier
                        .width(20.dp)
                        .height(3.dp)
                        .background(LegendGradients.Gold, CircleShape),
                )
                Spacer(Modifier.width(LegendSpacing.Xs))
                Text(eyebrow.uppercase(), style = LegendTypography.Eyebrow, color = LegendColors.Gold)
            }
            Text(title, style = LegendTypography.Title, color = LegendColors.TextPrimary)
            Text(detail, style = LegendTypography.Caption, color = LegendColors.TextSecondary, maxLines = 3, overflow = TextOverflow.Ellipsis)
        }
        IconButton(
            onClick = dismiss,
            modifier = Modifier
                .size(LegendSize.ProfileSettingsIcon)
                .background(LegendGradients.Finance, CircleShape)
                .border(LegendSpacing.Hairline, LegendColors.OnNavy.copy(alpha = 0.16f), CircleShape),
        ) {
            Icon(Icons.Default.Close, "Close profile settings", tint = LegendColors.OnNavy, modifier = Modifier.size(14.dp))
        }
    }
}

@Composable
private fun LegendDailyScriptureManagementSheet(
    viewModel: DailyScriptureManagementViewModel,
    dismiss: () -> Unit,
) {
    val state by viewModel.state.collectAsStateWithLifecycle()
    val action by viewModel.action.collectAsStateWithLifecycle()
    var editor by remember { mutableStateOf<DailyScriptureOverride?>(null) }
    var creating by remember { mutableStateOf(false) }
    var removalTarget by remember { mutableStateOf<DailyScriptureOverride?>(null) }
    LaunchedEffect(Unit) { viewModel.load() }
    ModalBottomSheet(onDismissRequest = dismiss, containerColor = LegendColors.Canvas) {
        Column(Modifier.fillMaxHeight(.94f)) {
            Row(
                Modifier.fillMaxWidth().padding(horizontal = LegendSpacing.PageHorizontal, vertical = LegendSpacing.Md),
                verticalAlignment = Alignment.CenterVertically,
            ) {
                Column(Modifier.weight(1f)) {
                    Text("CONTENT MANAGEMENT", style = LegendTypography.Eyebrow, color = LegendColors.Gold)
                    Text("Daily Scripture", style = LegendTypography.Section, color = LegendColors.TextPrimary)
                    Text("The server resolves each LEGEND business day. Scheduled overrides apply only on their date.", style = LegendTypography.Supporting, color = LegendColors.TextSecondary)
                }
                TextButton(onClick = dismiss) { Text("Done", color = LegendColors.Gold) }
            }
            when (state) {
                LoadState.Idle, LoadState.Loading -> LegendLoadingState()
                is LoadState.Error -> LegendErrorState((state as LoadState.Error).message, viewModel::load)
                is LoadState.Data -> {
                    val snapshot = (state as LoadState.Data<DailyScriptureManagementSnapshot>).value
                    LazyColumn(
                        modifier = Modifier.weight(1f),
                        contentPadding = PaddingValues(horizontal = LegendSpacing.PageHorizontal, vertical = LegendSpacing.Xs),
                        verticalArrangement = Arrangement.spacedBy(LegendSpacing.Sm),
                    ) {
                        item {
                            Surface(color = LegendColors.Navy, shape = LegendShapes.Hero, modifier = Modifier.fillMaxWidth()) {
                                Column(Modifier.padding(LegendSpacing.CardContent), verticalArrangement = Arrangement.spacedBy(LegendSpacing.Xs)) {
                                    Text("TODAY · ${snapshot.businessDate}", style = LegendTypography.Eyebrow, color = LegendColors.Gold)
                                    Text(snapshot.current.reference, style = LegendTypography.Section, color = LegendColors.OnNavy)
                                    Text(snapshot.current.text.ifBlank { snapshot.current.passageText }, style = LegendTypography.Supporting, color = LegendColors.GoldSoft, maxLines = 3, overflow = TextOverflow.Ellipsis)
                                    Text(snapshot.current.source.let { if (it == "ScheduledOverride") "Scheduled override" else "Daily collection" }, style = LegendTypography.Label, color = LegendColors.Gold)
                                    LegendPrimaryButton(
                                        text = if (snapshot.upcoming.any { it.displayDate == snapshot.businessDate }) "Edit today" else "Override today",
                                        modifier = Modifier.fillMaxWidth(),
                                    ) {
                                        editor = snapshot.upcoming.firstOrNull { it.displayDate == snapshot.businessDate }
                                        creating = editor == null
                                    }
                                }
                            }
                        }
                        item {
                            Row(Modifier.fillMaxWidth(), verticalAlignment = Alignment.CenterVertically) {
                                Column(Modifier.weight(1f)) {
                                    Text("Schedule", style = LegendTypography.CardTitle, color = LegendColors.TextPrimary)
                                    Text("Date, reference, translation, and exact passage text remain server-authoritative.", style = LegendTypography.Supporting, color = LegendColors.TextSecondary)
                                }
                                TextButton(onClick = { creating = true; editor = null }) { Text("Schedule", color = LegendColors.Gold) }
                            }
                        }
                        if (snapshot.upcoming.isEmpty()) {
                            item { LegendEmptyState("No scheduled overrides", "LEGEND will use the daily collection until a date is scheduled.") }
                        } else {
                            items(snapshot.upcoming, key = { it.id }) { override ->
                                Surface(color = LegendColors.Surface, shape = LegendShapes.Control, modifier = Modifier.fillMaxWidth()) {
                                    Row(Modifier.padding(LegendSpacing.Sm), verticalAlignment = Alignment.CenterVertically) {
                                        Column(Modifier.weight(1f)) {
                                            Text(override.displayDate, style = LegendTypography.Eyebrow, color = LegendColors.Gold)
                                            Text(override.reference, style = LegendTypography.CardTitle, color = LegendColors.TextPrimary)
                                            Text(override.translation, style = LegendTypography.Supporting, color = LegendColors.TextSecondary)
                                        }
                                        TextButton(onClick = { editor = override; creating = false }) { Text("Edit", color = LegendColors.Gold) }
                                        IconButton(onClick = { removalTarget = override }) { Icon(Icons.Default.Delete, "Remove scheduled scripture", tint = LegendColors.Error) }
                                    }
                                }
                            }
                        }
                    }
                }
            }
            if (action is LoadState.Error) Text((action as LoadState.Error).message, style = LegendTypography.Supporting, color = LegendColors.Error, modifier = Modifier.padding(LegendSpacing.PageHorizontal))
        }
    }
    if (creating || editor != null) {
        val businessDate = (state as? LoadState.Data<DailyScriptureManagementSnapshot>)?.value?.businessDate
        LegendDailyScriptureEditorSheet(
            existing = editor,
            defaultDate = businessDate?.let { runCatching { java.time.LocalDate.parse(it).plusDays(1).toString() }.getOrDefault(it) }.orEmpty(),
            dismiss = { creating = false; editor = null },
            submit = { draft -> viewModel.save(editor?.id, draft) { creating = false; editor = null } },
        )
    }
    removalTarget?.let { target ->
        AlertDialog(
            onDismissRequest = { removalTarget = null },
            title = { Text("Remove this scheduled scripture?") },
            text = { Text("LEGEND will return to its daily collection for ${target.displayDate} unless another override is scheduled.") },
            confirmButton = { TextButton(onClick = { viewModel.remove(target.id); removalTarget = null }) { Text("Remove override", color = LegendColors.Error) } },
            dismissButton = { TextButton(onClick = { removalTarget = null }) { Text("Cancel", color = LegendColors.Gold) } },
        )
    }
}

@Composable
private fun LegendDailyScriptureEditorSheet(
    existing: DailyScriptureOverride?,
    defaultDate: String,
    dismiss: () -> Unit,
    submit: (DailyScriptureOverrideRequest) -> Unit,
) {
    var displayDate by remember(existing?.id, defaultDate) { mutableStateOf(existing?.displayDate ?: defaultDate) }
    var reference by remember(existing?.id) { mutableStateOf(existing?.reference.orEmpty()) }
    var translation by remember(existing?.id) { mutableStateOf(existing?.translation ?: "KJV") }
    var passageText by remember(existing?.id) { mutableStateOf(existing?.passageText.orEmpty()) }
    ModalBottomSheet(onDismissRequest = dismiss, containerColor = LegendColors.Canvas) {
        LazyColumn(
            modifier = Modifier.fillMaxHeight(.93f),
            contentPadding = PaddingValues(horizontal = LegendSpacing.PageHorizontal, vertical = LegendSpacing.Md),
            verticalArrangement = Arrangement.spacedBy(LegendSpacing.Sm),
        ) {
            item {
                Row(verticalAlignment = Alignment.CenterVertically) {
                    Column(Modifier.weight(1f)) {
                        Text(if (existing == null) "SCHEDULE SCRIPTURE" else "EDIT SCRIPTURE", style = LegendTypography.Eyebrow, color = LegendColors.Gold)
                        Text(if (existing == null) "New override" else "Scheduled override", style = LegendTypography.Section, color = LegendColors.TextPrimary)
                    }
                    TextButton(onClick = dismiss) { Text("Cancel", color = LegendColors.Gold) }
                }
            }
            item { Text("LEGEND uses America/Phoenix for this date. The passage is stored exactly as entered.", style = LegendTypography.Supporting, color = LegendColors.TextSecondary) }
            item { AccountEditorField("Display date (YYYY-MM-DD)", displayDate) { displayDate = it } }
            item { AccountEditorField("Reference (for example, Psalm 121)", reference) { reference = it } }
            item { AccountEditorField("Translation", translation) { translation = it } }
            item { AccountEditorField("Passage text", passageText, minLines = 8) { passageText = it } }
            item {
                LegendPrimaryButton("Save scheduled scripture", enabled = displayDate.isNotBlank() && reference.isNotBlank() && translation.isNotBlank() && passageText.isNotBlank(), modifier = Modifier.fillMaxWidth()) {
                    submit(DailyScriptureOverrideRequest(displayDate.trim(), reference.trim(), translation.trim(), passageText.trim()))
                }
            }
        }
    }
}

@Composable
private fun LegendCommunitySafetyReviewSheet(
    viewModel: CommunitySafetyReviewViewModel,
    isFounder: Boolean,
    dismiss: () -> Unit,
) {
    val reports by viewModel.reports.collectAsStateWithLifecycle()
    val resolvingId by viewModel.resolvingId.collectAsStateWithLifecycle()
    LaunchedEffect(Unit) { viewModel.load() }
    ModalBottomSheet(onDismissRequest = dismiss, containerColor = LegendColors.Canvas) {
        Column(Modifier.fillMaxHeight(.88f)) {
            Row(Modifier.fillMaxWidth().padding(horizontal = LegendSpacing.PageHorizontal, vertical = LegendSpacing.Md), verticalAlignment = Alignment.CenterVertically) {
                Column(Modifier.weight(1f)) {
                    Text("COMMUNITY", style = LegendTypography.Eyebrow, color = LegendColors.Gold)
                    Text("Safety review", style = LegendTypography.Section, color = LegendColors.TextPrimary)
                    Text("Open reports requiring a recorded server-authorized decision.", style = LegendTypography.Supporting, color = LegendColors.TextSecondary)
                }
                TextButton(onClick = dismiss) { Text("Done", color = LegendColors.Gold) }
            }
            when (reports) {
                LoadState.Idle, LoadState.Loading -> LegendLoadingState()
                is LoadState.Error -> LegendErrorState((reports as LoadState.Error).message, viewModel::load)
                is LoadState.Data -> {
                    val values = (reports as LoadState.Data<List<CommunitySafetyReport>>).value
                    if (values.isEmpty()) Box(Modifier.weight(1f)) { LegendEmptyState("All caught up", "There are no open community reports.") }
                    else LazyColumn(
                        modifier = Modifier.weight(1f),
                        contentPadding = PaddingValues(horizontal = LegendSpacing.PageHorizontal, vertical = LegendSpacing.Xs),
                        verticalArrangement = Arrangement.spacedBy(LegendSpacing.Sm),
                    ) {
                        items(values, key = { it.id }) { report ->
                            val resolving = resolvingId == report.id
                            Surface(color = LegendColors.Surface, shape = LegendShapes.Control, modifier = Modifier.fillMaxWidth()) {
                                Column(Modifier.padding(LegendSpacing.Sm), verticalArrangement = Arrangement.spacedBy(LegendSpacing.Xs)) {
                                    Row(verticalAlignment = Alignment.Top) {
                                        Icon(Icons.Default.GppMaybe, null, tint = LegendColors.Gold, modifier = Modifier.size(26.dp))
                                        Spacer(Modifier.width(LegendSpacing.Xs))
                                        Column(Modifier.weight(1f)) {
                                            Text(report.category, style = LegendTypography.CardTitle, color = LegendColors.TextPrimary)
                                            Text(report.targetKind, style = LegendTypography.Supporting, color = LegendColors.TextSecondary)
                                        }
                                        if (resolving) CircularProgressIndicator(modifier = Modifier.size(22.dp), color = LegendColors.Gold, strokeWidth = 2.dp)
                                        else ReportResolutionMenu(report, isFounder, viewModel)
                                    }
                                    report.detail?.takeIf(String::isNotBlank)?.let { Text(it, style = LegendTypography.Body, color = LegendColors.TextSecondary) }
                                    Text(report.createdUtc, style = LegendTypography.Label, color = LegendColors.TextTertiary)
                                }
                            }
                        }
                    }
                }
            }
        }
    }
}

@Composable
private fun ReportResolutionMenu(report: CommunitySafetyReport, isFounder: Boolean, viewModel: CommunitySafetyReviewViewModel) {
    var expanded by remember { mutableStateOf(false) }
    Box {
        IconButton(onClick = { expanded = true }) { Icon(Icons.Default.MoreHoriz, "Resolve report", tint = LegendColors.Gold) }
        DropdownMenu(expanded = expanded, onDismissRequest = { expanded = false }, containerColor = LegendColors.Surface) {
            DropdownMenuItem(text = { Text("Dismiss", color = LegendColors.TextPrimary) }, onClick = { expanded = false; viewModel.resolve(report, "Dismissed") })
            DropdownMenuItem(text = { Text("Needs investigation", color = LegendColors.TextPrimary) }, onClick = { expanded = false; viewModel.resolve(report, "NeedsInvestigation") })
            if (isFounder && report.targetKind == "SocialPost") {
                DropdownMenuItem(text = { Text("Remove reported content", color = LegendColors.Error) }, onClick = { expanded = false; viewModel.resolve(report, "Actioned") })
            }
        }
    }
}

@Composable
private fun LegendFounderAccountsSheet(
    viewModel: FounderAccountsViewModel,
    dismiss: () -> Unit,
) {
    val accounts by viewModel.accounts.collectAsStateWithLifecycle()
    val action by viewModel.action.collectAsStateWithLifecycle()
    var archive by remember { mutableStateOf(false) }
    var search by remember { mutableStateOf("") }
    var selectedIds by remember { mutableStateOf<Set<String>>(emptySet()) }
    var confirmation by remember { mutableStateOf("") }
    LaunchedEffect(archive) { selectedIds = emptySet(); confirmation = ""; viewModel.load(scope = if (archive) "archive" else null) }
    LaunchedEffect(search) {
        kotlinx.coroutines.delay(180)
        viewModel.load(search.trim().takeIf(String::isNotBlank), if (archive) "archive" else null)
    }
    ModalBottomSheet(onDismissRequest = dismiss, containerColor = LegendColors.Canvas) {
        Column(Modifier.fillMaxHeight(.94f)) {
            Row(Modifier.fillMaxWidth().padding(horizontal = LegendSpacing.PageHorizontal, vertical = LegendSpacing.Md), verticalAlignment = Alignment.CenterVertically) {
                Column(Modifier.weight(1f)) {
                    Text("FOUNDER MANAGEMENT", style = LegendTypography.Eyebrow, color = LegendColors.Gold)
                    Text(if (archive) "Archived accounts" else "Active accounts", style = LegendTypography.Section, color = LegendColors.TextPrimary)
                }
                TextButton(onClick = dismiss) { Text("Done", color = LegendColors.Gold) }
            }
            Row(Modifier.padding(horizontal = LegendSpacing.PageHorizontal), horizontalArrangement = Arrangement.spacedBy(LegendSpacing.Xs)) {
                FilterChip(selected = !archive, onClick = { archive = false }, label = { Text("Active") }, colors = legendCompactChipColors(!archive))
                FilterChip(selected = archive, onClick = { archive = true }, label = { Text("Archive") }, colors = legendCompactChipColors(archive))
            }
            OutlinedTextField(search, { search = it }, modifier = Modifier.fillMaxWidth().padding(horizontal = LegendSpacing.PageHorizontal, vertical = LegendSpacing.Sm), singleLine = true, leadingIcon = { Icon(Icons.Default.Search, null, tint = LegendColors.Gold) }, placeholder = { Text("Search accounts") }, shape = LegendShapes.Control, colors = legendMessagingFieldColors())
            when (accounts) {
                LoadState.Idle, LoadState.Loading -> LegendLoadingState()
                is LoadState.Error -> LegendErrorState((accounts as LoadState.Error).message) { viewModel.load(search.takeIf(String::isNotBlank), if (archive) "archive" else null) }
                is LoadState.Data -> {
                    val entries = (accounts as LoadState.Data<List<FounderManagedAccount>>).value
                    if (entries.isEmpty()) Box(Modifier.weight(1f)) { LegendEmptyState(if (archive) "Archive is empty" else "No accounts found", "The existing founder authority returned no matching accounts.") }
                    else LazyColumn(
                        modifier = Modifier.weight(1f),
                        contentPadding = PaddingValues(horizontal = LegendSpacing.PageHorizontal, vertical = LegendSpacing.Xs),
                        verticalArrangement = Arrangement.spacedBy(LegendSpacing.Xs),
                    ) {
                        items(entries, key = { "${it.profileId}:${it.participantType}" }) { account ->
                            val id = "${account.profileId}:${account.participantType}"
                            Surface(color = LegendColors.Surface, shape = LegendShapes.Control, modifier = Modifier.fillMaxWidth().clickable { selectedIds = if (id in selectedIds) selectedIds - id else selectedIds + id }) {
                                Row(Modifier.padding(LegendSpacing.Sm), verticalAlignment = Alignment.CenterVertically) {
                                    Checkbox(checked = id in selectedIds, onCheckedChange = { selectedIds = if (it) selectedIds + id else selectedIds - id }, colors = CheckboxDefaults.colors(checkedColor = LegendColors.Gold, checkmarkColor = LegendColors.Midnight))
                                    Spacer(Modifier.width(LegendSpacing.Xs))
                                    Column(Modifier.weight(1f)) {
                                        Text(account.displayName, style = LegendTypography.Label, color = LegendColors.TextPrimary)
                                        Text("${account.participantType} · ${account.lifecycleState}${if (account.hasCancelableSubscription) " · subscription" else ""}", style = LegendTypography.Supporting, color = LegendColors.TextSecondary)
                                    }
                                    if (account.isActive) Icon(Icons.Default.Circle, "Active", tint = LegendColors.Success, modifier = Modifier.size(12.dp))
                                }
                            }
                        }
                    }
                    if (selectedIds.isNotEmpty()) {
                        val selected = entries.filter { "${it.profileId}:${it.participantType}" in selectedIds }
                        Surface(color = if (archive) LegendColors.Navy else LegendColors.SurfaceElevated, modifier = Modifier.fillMaxWidth(), shape = LegendShapes.Card) {
                            Column(Modifier.padding(LegendSpacing.Md), verticalArrangement = Arrangement.spacedBy(LegendSpacing.Xs)) {
                                Text(if (archive) "Permanent purge" else "Archive selected accounts", style = LegendTypography.CardTitle, color = if (archive) LegendColors.OnNavy else LegendColors.TextPrimary)
                                Text(if (archive) "Type ERASE to permanently remove ${selected.size} archived account(s)." else "Type DELETE to close and archive ${selected.size} account(s).", style = LegendTypography.Supporting, color = if (archive) LegendColors.GoldSoft else LegendColors.TextSecondary)
                                OutlinedTextField(confirmation, { confirmation = it }, modifier = Modifier.fillMaxWidth(), singleLine = true, label = { Text(if (archive) "Type ERASE" else "Type DELETE") }, shape = LegendShapes.Control, colors = if (archive) legendDarkFieldColors() else legendMessagingFieldColors())
                                LegendPrimaryButton(
                                    text = if (archive) "Permanently purge ${selected.size} account(s)" else "Archive ${selected.size} account(s)",
                                    enabled = confirmation == if (archive) "ERASE" else "DELETE",
                                    modifier = Modifier.fillMaxWidth(),
                                ) {
                                    if (archive) viewModel.purge(selected, confirmation) else viewModel.remove(selected, confirmation, null)
                                    selectedIds = emptySet()
                                    confirmation = ""
                                }
                            }
                        }
                    }
                }
            }
            if (action is LoadState.Error) Text((action as LoadState.Error).message, color = LegendColors.Error, style = LegendTypography.Supporting, modifier = Modifier.padding(LegendSpacing.PageHorizontal))
            if (action is LoadState.Data) {
                val result = (action as LoadState.Data<FounderAccountBatchResponse>).value
                Text("${result.completedCount} completed${if (result.failedCount > 0) ", ${result.failedCount} failed" else ""}.", color = LegendColors.Success, style = LegendTypography.Supporting, modifier = Modifier.padding(LegendSpacing.PageHorizontal))
            }
        }
    }
}

private enum class LegendFounderResource(val apiValue: String, val title: String, val detail: String) {
    LanguageTranslation("LanguageTranslation", "Language translation", "Grant or revoke access to LEGEND language translation."),
    ScriptureManagement("ScriptureManagement", "Daily Scripture", "Delegate Daily Scripture scheduling and editorial management."),
    CommunityManagement("CommunityManagement", "Community safety", "Delegate report triage; content removal remains Founder-only."),
    SocialContentPriority("SocialContentPriority", "Social content priority", "Prioritize eligible Posts and Hacs above standard feed ranking."),
}

@Composable
private fun LegendControlledResourceAccessSheet(
    initialResource: LegendFounderResource,
    viewModel: ControlledResourceViewModel,
    mediaRepository: AuthenticatedMediaRepository,
    dismiss: () -> Unit,
) {
    var resource by remember { mutableStateOf(initialResource) }
    var search by remember { mutableStateOf("") }
    val recipients by viewModel.recipients.collectAsStateWithLifecycle()
    val updating by viewModel.updating.collectAsStateWithLifecycle()
    LaunchedEffect(resource) { search = ""; viewModel.load(resource.apiValue) }
    LaunchedEffect(search, resource) {
        kotlinx.coroutines.delay(180)
        viewModel.load(resource.apiValue, search.trim().takeIf(String::isNotBlank))
    }
    ModalBottomSheet(onDismissRequest = dismiss, containerColor = LegendColors.Canvas) {
        Column(Modifier.fillMaxHeight(.92f)) {
            Row(Modifier.fillMaxWidth().padding(horizontal = LegendSpacing.PageHorizontal, vertical = LegendSpacing.Md), verticalAlignment = Alignment.CenterVertically) {
                Column(Modifier.weight(1f)) {
                    Text("FOUNDER CONTROLS", style = LegendTypography.Eyebrow, color = LegendColors.Gold)
                    Text(resource.title, style = LegendTypography.Section, color = LegendColors.TextPrimary)
                    Text(resource.detail, style = LegendTypography.Supporting, color = LegendColors.TextSecondary)
                }
                TextButton(onClick = dismiss) { Text("Done", color = LegendColors.Gold) }
            }
            LazyRow(
                modifier = Modifier.fillMaxWidth().padding(horizontal = LegendSpacing.PageHorizontal),
                horizontalArrangement = Arrangement.spacedBy(LegendSpacing.Xs),
            ) {
                items(LegendFounderResource.entries) { candidate ->
                    FilterChip(selected = candidate == resource, onClick = { resource = candidate }, label = { Text(candidate.title) }, colors = legendCompactChipColors(candidate == resource))
                }
            }
            OutlinedTextField(search, { search = it }, modifier = Modifier.fillMaxWidth().padding(horizontal = LegendSpacing.PageHorizontal, vertical = LegendSpacing.Sm), singleLine = true, leadingIcon = { Icon(Icons.Default.Search, null, tint = LegendColors.Gold) }, placeholder = { Text("Search people") }, shape = LegendShapes.Control, colors = legendMessagingFieldColors())
            when (recipients) {
                LoadState.Idle, LoadState.Loading -> LegendLoadingState()
                is LoadState.Error -> LegendErrorState((recipients as LoadState.Error).message) { viewModel.load(resource.apiValue, search.trim().takeIf(String::isNotBlank)) }
                is LoadState.Data -> {
                    val values = (recipients as LoadState.Data<List<MessagingRecipient>>).value
                    if (values.isEmpty()) Box(Modifier.weight(1f)) { LegendEmptyState("No profiles found", "Try a name, username, or email address.") }
                    else LazyColumn(
                        modifier = Modifier.weight(1f),
                        contentPadding = PaddingValues(horizontal = LegendSpacing.PageHorizontal, vertical = LegendSpacing.Xs),
                        verticalArrangement = Arrangement.spacedBy(LegendSpacing.Xs),
                    ) {
                        items(values, key = { "${it.identity.userId}:${it.identity.participantType}" }) { recipient ->
                            val id = "${recipient.identity.userId}:${recipient.identity.participantType}"
                            val granted = recipient.resourceAccessState == "Granted"
                            val isUpdating = updating == id
                            Surface(color = LegendColors.Surface, shape = LegendShapes.Control, modifier = Modifier.fillMaxWidth()) {
                                Row(Modifier.padding(LegendSpacing.Sm), verticalAlignment = Alignment.CenterVertically) {
                                    LegendProtectedAvatar(recipient.avatar, recipient.displayName, recipient.identity.participantType, mediaRepository, size = 42.dp)
                                    Spacer(Modifier.width(LegendSpacing.Sm))
                                    Column(Modifier.weight(1f)) {
                                        Text(recipient.displayName, style = LegendTypography.CardTitle, color = LegendColors.TextPrimary)
                                        Text(recipient.email ?: recipient.roleLabel ?: recipient.identity.participantType, style = LegendTypography.Supporting, color = LegendColors.TextSecondary)
                                    }
                                    if (isUpdating) CircularProgressIndicator(modifier = Modifier.size(24.dp), color = LegendColors.Gold, strokeWidth = 2.dp)
                                    else OutlinedButton(onClick = { viewModel.setGrant(resource.apiValue, recipient, !granted) }, shape = LegendShapes.Control) { Text(if (granted) "Remove" else "Grant", color = if (granted) LegendColors.Error else LegendColors.Gold) }
                                }
                            }
                        }
                    }
                }
            }
        }
    }
}

@Composable
private fun LegendFollowRequestsSheet(
    viewModel: SocialViewModel,
    mediaRepository: AuthenticatedMediaRepository,
    participantType: String,
    dismiss: () -> Unit,
) {
    val requests by viewModel.followRequests.collectAsStateWithLifecycle()
    ModalBottomSheet(onDismissRequest = dismiss, containerColor = LegendColors.Canvas) {
        Column(Modifier.fillMaxHeight(.78f)) {
            Row(Modifier.fillMaxWidth().padding(horizontal = LegendSpacing.PageHorizontal, vertical = LegendSpacing.Md), verticalAlignment = Alignment.CenterVertically) {
                Column(Modifier.weight(1f)) {
                    Text("FOLLOW REQUESTS", style = LegendTypography.Eyebrow, color = LegendColors.Gold)
                    Text("Your private audience", style = LegendTypography.Section, color = LegendColors.TextPrimary)
                }
                TextButton(onClick = dismiss) { Text("Done", color = LegendColors.Gold) }
            }
            when (requests) {
                LoadState.Idle, LoadState.Loading -> LegendLoadingState()
                is LoadState.Error -> LegendErrorState((requests as LoadState.Error).message, viewModel::loadFollowRequests)
                is LoadState.Data -> {
                    val values = (requests as LoadState.Data<List<SocialFollowRequestItem>>).value
                    if (values.isEmpty()) Box(Modifier.weight(1f)) { LegendEmptyState("No follow requests", "New requests will appear here for your review.") }
                    else LazyColumn(modifier = Modifier.weight(1f), contentPadding = PaddingValues(horizontal = LegendSpacing.PageHorizontal), verticalArrangement = Arrangement.spacedBy(LegendSpacing.Xs)) {
                        items(values, key = { it.id }) { request ->
                            Surface(color = LegendColors.Surface, shape = LegendShapes.Control, modifier = Modifier.fillMaxWidth()) {
                                Row(Modifier.padding(LegendSpacing.Sm), verticalAlignment = Alignment.CenterVertically) {
                                    LegendProtectedAvatar(request.profile.avatar, request.profile.displayName, participantType, mediaRepository, size = 42.dp)
                                    Spacer(Modifier.width(LegendSpacing.Sm))
                                    Column(Modifier.weight(1f)) {
                                        Text(request.profile.displayName, style = LegendTypography.Label, color = LegendColors.TextPrimary)
                                        Text(request.profile.username?.let { "@$it" } ?: "LEGEND member", style = LegendTypography.Supporting, color = LegendColors.TextSecondary)
                                    }
                                    IconButton(onClick = { viewModel.decideFollowRequest(request.id, false) }) { Icon(Icons.Default.Close, "Decline follow request", tint = LegendColors.Error) }
                                    IconButton(onClick = { viewModel.decideFollowRequest(request.id, true) }) { Icon(Icons.Default.Check, "Approve follow request", tint = LegendColors.Success) }
                                }
                            }
                        }
                    }
                }
            }
        }
    }
}

@Composable
private fun AccountEditorSheet(
    account: MobileAccountProfile,
    usernameAvailability: LoadState<MobileUsernameAvailability>,
    checkUsernameAvailability: (String?) -> Unit,
    dismiss: () -> Unit,
    submit: (String, String?, String?, String?, String?, String?, String?, String?, String?, Boolean, Boolean, Boolean?) -> Unit,
) {
    var displayName by remember(account.profileId) { mutableStateOf(account.displayName) }
    var phone by remember(account.profileId) { mutableStateOf(account.phone.orEmpty()) }
    var title by remember(account.profileId) { mutableStateOf(account.title.orEmpty()) }
    var shortBio by remember(account.profileId) { mutableStateOf(account.shortBio.orEmpty()) }
    var username by remember(account.profileId) { mutableStateOf(account.username.orEmpty()) }
    var bio by remember(account.profileId) { mutableStateOf(account.bio.orEmpty()) }
    var website by remember(account.profileId) { mutableStateOf(account.website.orEmpty()) }
    var location by remember(account.profileId) { mutableStateOf(account.location.orEmpty()) }
    var email by remember(account.profileId) { mutableStateOf(account.profileEmail.orEmpty()) }
    var emailVisible by remember(account.profileId) { mutableStateOf(account.isEmailVisible) }
    var phoneVisible by remember(account.profileId) { mutableStateOf(account.isPhoneVisible) }
    var privateProfile by remember(account.profileId) { mutableStateOf(account.isPrivate) }
    LaunchedEffect(username) {
        kotlinx.coroutines.delay(260)
        checkUsernameAvailability(username.trim().takeIf(String::isNotBlank))
    }
    val usernameIsValid = username.isBlank() || (usernameAvailability as? LoadState.Data<MobileUsernameAvailability>)?.value?.isAvailable == true
    ModalBottomSheet(onDismissRequest = dismiss, containerColor = LegendColors.Canvas) {
        LazyColumn(
            modifier = Modifier.fillMaxHeight(0.95f),
            contentPadding = PaddingValues(horizontal = LegendSpacing.PageHorizontal, vertical = LegendSpacing.Md),
            verticalArrangement = Arrangement.spacedBy(LegendSpacing.Sm),
        ) {
            item {
                Row(verticalAlignment = Alignment.CenterVertically) {
                    Text("Edit profile", style = LegendTypography.Section, color = LegendColors.TextPrimary, modifier = Modifier.weight(1f))
                    TextButton(onClick = dismiss) { Text("Cancel", color = LegendColors.Gold) }
                }
            }
            item { AccountEditorField("Name", displayName) { displayName = it } }
            item {
                AccountEditorField("Username", username) { username = it }
                when (usernameAvailability) {
                    LoadState.Loading -> Text("Checking username…", style = LegendTypography.Label, color = LegendColors.TextSecondary)
                    is LoadState.Data -> {
                        val result = usernameAvailability.value
                        Text(result.message ?: if (result.isAvailable) "Username available" else "Username unavailable", style = LegendTypography.Label, color = if (result.isAvailable) LegendColors.Success else LegendColors.Error)
                    }
                    is LoadState.Error -> Text(usernameAvailability.message, style = LegendTypography.Label, color = LegendColors.Error)
                    LoadState.Idle -> Unit
                }
            }
            item { AccountEditorField("Title", title) { title = it } }
            item { AccountEditorField("Short bio", shortBio, minLines = 2) { shortBio = it } }
            item { AccountEditorField("Biography", bio, minLines = 4) { bio = it } }
            item { AccountEditorField("Website", website) { website = it } }
            item { AccountEditorField("Location", location) { location = it } }
            item { AccountEditorField("Phone", phone) { phone = it } }
            item { AccountEditorField("Public email", email) { email = it } }
            item {
                Surface(color = LegendColors.Surface, shape = LegendShapes.Control, modifier = Modifier.fillMaxWidth()) {
                    Column(Modifier.padding(LegendSpacing.Sm)) {
                        AccountEditorSwitch("Show email", emailVisible) { emailVisible = it }
                        AccountEditorSwitch("Show phone", phoneVisible) { phoneVisible = it }
                        AccountEditorSwitch("Private profile", privateProfile) { privateProfile = it }
                    }
                }
            }
            item {
                LegendPrimaryButton("Save profile", enabled = displayName.trim().isNotEmpty() && usernameIsValid) {
                    submit(
                        displayName.trim(), phone.trim().takeIf(String::isNotBlank), title.trim().takeIf(String::isNotBlank), shortBio.trim().takeIf(String::isNotBlank), username.trim().takeIf(String::isNotBlank), bio.trim().takeIf(String::isNotBlank), website.trim().takeIf(String::isNotBlank), location.trim().takeIf(String::isNotBlank), email.trim().takeIf(String::isNotBlank), emailVisible, phoneVisible, privateProfile,
                    )
                }
            }
        }
    }
}

@Composable
private fun AccountEditorField(label: String, value: String, minLines: Int = 1, change: (String) -> Unit) {
    OutlinedTextField(value = value, onValueChange = change, label = { Text(label) }, modifier = Modifier.fillMaxWidth(), minLines = minLines, shape = LegendShapes.Control)
}

@Composable
private fun AccountEditorSwitch(label: String, checked: Boolean, change: (Boolean) -> Unit) {
    Row(Modifier.fillMaxWidth(), verticalAlignment = Alignment.CenterVertically) {
        Text(label, modifier = Modifier.weight(1f), style = LegendTypography.Body, color = LegendColors.TextPrimary)
        Switch(checked = checked, onCheckedChange = change)
    }
}

@Composable
private fun LanguageDialog(current: String, onDismiss: () -> Unit, submit: (String) -> Unit) {
    var language by remember(current) { mutableStateOf(current) }
    AlertDialog(
        onDismissRequest = onDismiss,
        title = { Text("Preferred language") },
        text = {
            Column {
                Text("LEGEND sends this preference to the server. Android does not translate messages locally.")
                OutlinedTextField(
                    value = language,
                    onValueChange = { language = it },
                    label = { Text("Language") },
                    modifier = Modifier.fillMaxWidth(),
                    singleLine = true,
                )
            }
        },
        confirmButton = { TextButton(onClick = { submit(language) }) { Text("Save") } },
        dismissButton = { TextButton(onClick = onDismiss) { Text("Cancel") } },
    )
}

@Composable
private fun DeletionDialog(onDismiss: () -> Unit, submit: (String) -> Unit) {
    var confirmation by remember { mutableStateOf("") }
    AlertDialog(
        onDismissRequest = onDismiss,
        title = { Text("Close your LEGEND account?") },
        text = {
            Column {
                Text("Type DELETE to submit the server-authoritative account closure request.")
                OutlinedTextField(
                    value = confirmation,
                    onValueChange = { confirmation = it },
                    label = { Text("Confirmation") },
                )
            }
        },
        confirmButton = {
            TextButton(onClick = { submit(confirmation) }, enabled = confirmation == "DELETE") {
                Text("Request deletion", color = LegendColors.Error)
            }
        },
        dismissButton = { TextButton(onClick = onDismiss) { Text("Cancel") } },
    )
}

@Composable
private fun FinancialScreen(repository: FinancialRepository, participantType: String, back: () -> Unit) {
    val viewModel: FinancialViewModel = viewModel(
        factory = LegendViewModelFactory { FinancialViewModel(repository, participantType) },
    )
    val state by viewModel.state.collectAsStateWithLifecycle()
    var detailKey by remember { mutableStateOf<String?>(null) }
    LaunchedEffect(Unit) { viewModel.load() }
    when (state) {
        LoadState.Idle, LoadState.Loading -> LegendLoadingState()
        is LoadState.Error -> LegendErrorState((state as LoadState.Error).message, viewModel::load)
        is LoadState.Data -> {
            val snapshot = (state as LoadState.Data<FinancialSnapshot>).value
            val selectedSection = detailKey?.let { key -> snapshot.healthSnapshot?.sections?.firstOrNull { it.key == key } }
            LazyColumn(
                modifier = Modifier.fillMaxSize().background(LegendColors.Canvas),
                contentPadding = PaddingValues(horizontal = LegendSpacing.PageHorizontal, vertical = LegendSpacing.Md),
                verticalArrangement = Arrangement.spacedBy(LegendSpacing.Sm),
            ) {
                item {
                    Row(verticalAlignment = Alignment.CenterVertically) {
                        IconButton(onClick = { if (detailKey == null) back() else detailKey = null }, modifier = Modifier.background(LegendColors.Surface, CircleShape)) { Icon(Icons.AutoMirrored.Filled.ArrowBack, "Back", tint = LegendColors.Navy) }
                        Spacer(Modifier.width(LegendSpacing.Sm))
                        Text(if (selectedSection == null) "Financial Intelligence" else selectedSection.title, style = LegendTypography.Section, color = LegendColors.TextPrimary)
                    }
                }
                if (selectedSection != null) {
                    item { FinancialHealthSectionDetail(selectedSection) }
                } else {
                    snapshot.position?.let { position -> item { FinancialPositionHero(position) } }
                    snapshot.presentation?.assignedAgent?.takeIf { it.hasAssignedAgent }?.displayName?.let { name -> item { Text("Your LEGEND guide: $name", style = LegendTypography.Supporting, color = LegendColors.TextSecondary) } }
                    val priorities = snapshot.presentation?.prioritySections.orEmpty()
                    if (priorities.isNotEmpty()) {
                        item { Text("Your priorities", style = LegendTypography.Section, color = LegendColors.TextPrimary) }
                        items(priorities.sortedBy { it.priority }, key = { it.key }) { section -> FinancialPriorityCard(section) { detailKey = section.key } }
                    }
                    snapshot.intelligence?.let { intelligence ->
                        item { FinancialIntelligenceCard(intelligence) }
                    }
                    snapshot.upcomingBills.takeIf { it.isNotEmpty() }?.let { bills ->
                        item { Text("Upcoming activity", style = LegendTypography.Section, color = LegendColors.TextPrimary) }
                        items(bills, key = { it.id }) { bill ->
                            Surface(color = LegendColors.Surface, shape = LegendShapes.Control, modifier = Modifier.fillMaxWidth()) {
                                Row(Modifier.padding(LegendSpacing.Sm), verticalAlignment = Alignment.CenterVertically) {
                                    Column(Modifier.weight(1f)) {
                                        Text(bill.displayName, style = LegendTypography.CardTitle, color = LegendColors.TextPrimary)
                                        Text("${bill.cadence} · ${bill.nextExpectedDateUtc}", style = LegendTypography.Label, color = LegendColors.TextSecondary)
                                    }
                                    Text(financialCurrencyCents(bill.averageAmountCents), style = LegendTypography.CardTitle, color = LegendColors.Navy)
                                }
                            }
                        }
                    }
                    snapshot.operatingSystem?.projection?.summary?.takeIf(String::isNotBlank)?.let { summary -> item { FinancialOperatingSystemCard(summary) } }
                    if (priorities.isEmpty() && snapshot.healthSnapshot == null && snapshot.position == null) item { LegendMessagingEmptyCard("Financial snapshot incomplete", "Your saved financial health data will appear here after it is completed in your account workspace.", action = back) }
                }
            }
        }
    }
}

@Composable
private fun FinancialPositionHero(position: FinancialPosition) {
    Surface(color = LegendColors.Navy, shape = LegendShapes.Hero, modifier = Modifier.fillMaxWidth()) {
        Column(Modifier.padding(LegendSpacing.Lg), verticalArrangement = Arrangement.spacedBy(LegendSpacing.Xs)) {
            Text(position.positionStatus.uppercase(), style = LegendTypography.Eyebrow, color = LegendColors.GoldBright)
            Text(position.positionSummary, style = LegendTypography.Body, color = LegendColors.OnNavy)
            Row(horizontalArrangement = Arrangement.spacedBy(LegendSpacing.Lg)) {
                FinancialHeroMetric("Health", position.healthScore.toString())
                FinancialHeroMetric("Net worth", financialCurrency(position.netWorth))
                FinancialHeroMetric("Protection gap", financialCurrency(position.protectionGapTotal))
            }
        }
    }
}

@Composable private fun FinancialHeroMetric(label: String, value: String) = Column { Text(label.uppercase(), style = LegendTypography.Label, color = LegendColors.GoldSoft); Text(value, style = LegendTypography.CardTitle, color = LegendColors.OnNavy) }

@Composable
private fun FinancialPriorityCard(section: FinancialPrioritySection, open: () -> Unit) {
    Surface(color = LegendColors.Navy, shape = LegendShapes.ProminentCard, modifier = Modifier.fillMaxWidth().clickable(onClick = open)) {
        Row(Modifier.padding(LegendSpacing.Md), verticalAlignment = Alignment.CenterVertically) {
            Icon(Icons.Default.AccountBalance, null, tint = financialTone(section.primaryMetric.semantic), modifier = Modifier.size(25.dp))
            Spacer(Modifier.width(LegendSpacing.Sm))
            Column(Modifier.weight(1f), verticalArrangement = Arrangement.spacedBy(LegendSpacing.Micro)) {
                Text(section.eyebrow.uppercase(), style = LegendTypography.Eyebrow, color = LegendColors.GoldBright)
                Text(section.title, style = LegendTypography.CardTitle, color = LegendColors.OnNavy)
                Text(section.reason, style = LegendTypography.Label, color = LegendColors.GoldSoft, maxLines = 2, overflow = TextOverflow.Ellipsis)
            }
            Column(horizontalAlignment = Alignment.End) {
                Text(financialMetricValue(section.primaryMetric), style = LegendTypography.CardTitle, color = financialTone(section.primaryMetric.semantic))
                Icon(Icons.Default.ChevronRight, null, tint = LegendColors.OnNavy)
            }
        }
    }
}

@Composable
private fun FinancialIntelligenceCard(intelligence: FinancialIntelligence) {
    Surface(color = LegendColors.Surface, shape = LegendShapes.Card, modifier = Modifier.fillMaxWidth()) {
        Column(Modifier.padding(LegendSpacing.CardContent), verticalArrangement = Arrangement.spacedBy(LegendSpacing.Xs)) {
            Text("Financial intelligence", style = LegendTypography.CardTitle, color = LegendColors.TextPrimary)
            Text(intelligence.currentRiskSummary, style = LegendTypography.Body, color = LegendColors.TextPrimary)
            intelligence.currentOpportunitySummary.takeIf(String::isNotBlank)?.let { Text(it, style = LegendTypography.Supporting, color = LegendColors.Success) }
            intelligence.currentLeakageSummary.takeIf(String::isNotBlank)?.let { Text(it, style = LegendTypography.Supporting, color = LegendColors.Warning) }
        }
    }
}

@Composable
private fun FinancialOperatingSystemCard(summary: String) {
    Surface(color = LegendColors.SurfaceInset, shape = LegendShapes.Control, modifier = Modifier.fillMaxWidth()) {
        Row(Modifier.padding(LegendSpacing.Sm), verticalAlignment = Alignment.CenterVertically) {
            Icon(Icons.Default.CalendarMonth, null, tint = LegendColors.Gold)
            Spacer(Modifier.width(LegendSpacing.Sm))
            Text(summary, style = LegendTypography.Supporting, color = LegendColors.TextSecondary)
        }
    }
}

@Composable
private fun FinancialHealthSectionDetail(section: FinancialHealthSection) {
    Column(verticalArrangement = Arrangement.spacedBy(LegendSpacing.Sm)) {
        Text(section.period ?: section.semantic, style = LegendTypography.Supporting, color = LegendColors.TextSecondary)
        section.total?.let { total -> FinancialHealthMetricRow(total, emphasized = true) }
        section.groups.forEach { group ->
            Surface(color = LegendColors.Surface, shape = LegendShapes.Card, modifier = Modifier.fillMaxWidth()) {
                Column(Modifier.padding(LegendSpacing.CardContent), verticalArrangement = Arrangement.spacedBy(LegendSpacing.Xs)) {
                    group.title?.let { Text(it, style = LegendTypography.CardTitle, color = LegendColors.TextPrimary) }
                    group.metrics.forEach { metric -> FinancialHealthMetricRow(metric, false) }
                }
            }
        }
    }
}

@Composable
private fun FinancialHealthMetricRow(metric: FinancialMetric, emphasized: Boolean) {
    Row(Modifier.fillMaxWidth(), verticalAlignment = Alignment.CenterVertically) {
        Text(metric.label, modifier = Modifier.weight(1f), style = if (emphasized) LegendTypography.CardTitle else LegendTypography.Body, color = LegendColors.TextPrimary)
        Text(financialMetricValue(metric), style = if (emphasized) LegendTypography.CardTitle else LegendTypography.BodyEmphasis, color = LegendColors.Navy)
    }
}

private fun financialMetricValue(metric: FinancialSummaryMetric): String = metric.amountCents?.let(::financialCurrencyCents) ?: metric.date ?: metric.textValue ?: "Not available"
private fun financialMetricValue(metric: FinancialMetric): String = metric.amountCents?.let(::financialCurrencyCents) ?: metric.numericValue?.toString() ?: metric.textValue ?: "Not available"
private fun financialCurrencyCents(value: Long): String = java.text.NumberFormat.getCurrencyInstance(java.util.Locale.US).format(value / 100.0)
private fun financialCurrency(value: Double): String = java.text.NumberFormat.getCurrencyInstance(java.util.Locale.US).format(value)
private fun financialTone(semantic: String) = when (semantic.lowercase()) { "positive" -> LegendColors.Success; "negative" -> LegendColors.Error; "caution" -> LegendColors.Warning; else -> LegendColors.GoldBright }
