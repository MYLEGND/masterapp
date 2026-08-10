@file:OptIn(ExperimentalMaterial3Api::class)

package com.mylegnd.legend.registered.ui

import android.app.Activity
import android.Manifest
import android.content.pm.PackageManager
import android.os.Build
import android.net.Uri
import androidx.activity.compose.LocalActivity
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
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.Logout
import androidx.compose.material.icons.automirrored.filled.Send
import androidx.compose.material.icons.automirrored.filled.ArrowBack
import androidx.compose.material.icons.automirrored.filled.MenuBook
import androidx.compose.material.icons.automirrored.filled.Reply
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
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import androidx.lifecycle.viewmodel.compose.viewModel
import androidx.core.content.ContextCompat
import com.mylegnd.legend.registered.LegendContainer
import com.mylegnd.legend.registered.LegendViewModelFactory
import com.mylegnd.legend.registered.core.design.LegendColors
import com.mylegnd.legend.registered.core.design.LegendCopy
import com.mylegnd.legend.registered.core.design.LegendShapes
import com.mylegnd.legend.registered.core.design.LegendSize
import com.mylegnd.legend.registered.core.design.LegendSpacing
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
import com.mylegnd.legend.registered.core.realtime.MobileMessagingRealtimeClient
import com.mylegnd.legend.registered.core.session.ActiveLegendSession
import com.mylegnd.legend.registered.core.session.SessionState
import com.mylegnd.legend.registered.core.session.SessionViewModel
import com.mylegnd.legend.registered.data.FinancialRepository
import com.mylegnd.legend.registered.data.LoadState
import com.mylegnd.legend.registered.feature.*
import kotlinx.coroutines.flow.collectLatest
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
            AuthenticatedShell(session, container, sessionViewModel::signOut)
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
        Text("LEGEND®", style = LegendTypography.Title.copy(letterSpacing = 4.6.sp), color = LegendColors.Navy)
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
            .padding(horizontal = LegendSpacing.PageHorizontal, vertical = LegendSpacing.Xs),
    ) {
        Row(
            modifier = Modifier
                .fillMaxWidth()
                .heightIn(min = LegendSize.ProminentControlHeight)
                .clip(CircleShape)
                .background(Brush.linearGradient(listOf(LegendColors.Navy, LegendColors.Midnight)))
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
) {
    var tab by remember { mutableStateOf(LegendTab.HOME) }
    var requestedConversationId by remember { mutableStateOf<String?>(null) }
    var isMessageThreadOpen by remember { mutableStateOf(false) }
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
    DisposableEffect(messagingRealtime) { onDispose { messagingRealtime.close() } }
    val homeState by home.state.collectAsStateWithLifecycle()
    val notificationState by notifications.state.collectAsStateWithLifecycle()
    val notificationCount = (notificationState as? LoadState.Data<NotificationSnapshot>)?.value?.badge?.unreadCount ?: 0
    LaunchedEffect(participantType) { container.fcmPushRegistration.registerForAuthenticatedActor(participantType) }
    LaunchedEffect(notificationDestination) {
        val destination = notificationDestination ?: return@LaunchedEffect
        destination.conversationId?.let {
            tab = LegendTab.MESSAGES
            requestedConversationId = it
        }
        container.notificationNavigation.markHandled(destination)
    }

    Scaffold(
        topBar = {
            if (tab != LegendTab.HOME && !isMessageThreadOpen) {
                LegendHomeBrandBar(
                    notificationCount = notificationCount,
                    create = null,
                    notifications = null,
                    showsHomeActions = false,
                    usesDarkSurface = tab == LegendTab.DISCOVER,
                )
            }
        },
        bottomBar = {
            if (!isMessageThreadOpen) {
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
                    realtimeClient = messagingRealtime,
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
                    signOut = signOut,
                )
            }
        }
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
    Surface(modifier = Modifier.fillMaxWidth().clickable(onClick = open), color = LegendColors.Navy, shape = LegendShapes.Control) {
        Row(Modifier.padding(LegendSpacing.Sm), verticalAlignment = Alignment.CenterVertically) {
            LegendProtectedAvatar(result.avatar, result.displayName, participantType, mediaRepository, size = 48.dp)
            Spacer(Modifier.width(LegendSpacing.Sm))
            Column(Modifier.weight(1f), verticalArrangement = Arrangement.spacedBy(LegendSpacing.Micro)) {
                Row(verticalAlignment = Alignment.CenterVertically) {
                    Text(result.displayName, style = LegendTypography.CardTitle, color = LegendColors.OnNavy)
                    if (result.isVerified) Icon(Icons.Default.Verified, "Verified", modifier = Modifier.padding(start = LegendSpacing.Xs).size(16.dp), tint = LegendColors.Info)
                }
                Text(result.roleLabel ?: result.username?.let { "@$it" } ?: result.matchExplanation ?: result.headline ?: result.location ?: "LEGEND member", style = LegendTypography.Label, color = LegendColors.GoldSoft, maxLines = 2, overflow = TextOverflow.Ellipsis)
            }
            IconButton(onClick = safety) { Icon(Icons.Default.MoreVert, "Safety actions", tint = LegendColors.GoldBright) }
        }
    }
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
                            if (author.isVerified) Icon(Icons.Default.Verified, "Verified", modifier = Modifier.padding(start = LegendSpacing.Xs).size(18.dp), tint = LegendColors.Info)
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
                        LegendSocialPostCard(post, mediaRepository, participantType, false, null, { socialViewModel.react(post.id) }, { commentingPost = post }, { socialViewModel.toggleFollow(post) }, { socialViewModel.toggleSave(post.id) }, { socialViewModel.toggleRepost(post.id) }, { socialViewModel.recordShare(post.id) })
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
    openSocial: () -> Unit,
    openConversation: (String) -> Unit,
) {
    val homeState by homeViewModel.state.collectAsStateWithLifecycle()
    val agentClients by agentWorkspaceViewModel.clients.collectAsStateWithLifecycle()
    val agentLeads by agentWorkspaceViewModel.leads.collectAsStateWithLifecycle()
    val socialState by socialViewModel.state.collectAsStateWithLifecycle()
    val notificationState by notificationsViewModel.state.collectAsStateWithLifecycle()
    val context = LocalContext.current
    var creating by remember { mutableStateOf(false) }
    var scriptureOpen by remember { mutableStateOf(false) }
    var activityOpen by remember { mutableStateOf(false) }
    var notificationsOpen by remember { mutableStateOf(false) }

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
                    LegendHomeBrandBar(
                        notificationCount = notificationCount,
                        create = { creating = true },
                        notifications = {
                            notificationsViewModel.load()
                            notificationsOpen = true
                        },
                        embeddedInPage = true,
                    )
                }
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
                        mediaRepository = mediaRepository,
                        participantType = participantType,
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
                                    onShare = { socialViewModel.recordShare(post.id) },
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
            musicState = socialViewModel.music.collectAsStateWithLifecycle().value,
            searchMusic = socialViewModel::searchMusic,
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
}

@Composable
private fun LegendAgentWorkspaceCard(
    clients: LoadState<List<MobileAgentClient>>,
    leads: LoadState<List<MobileAgentLead>>,
    mediaRepository: AuthenticatedMediaRepository,
    participantType: String,
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
        }
    }
}

@Composable
private fun LegendHomeBrandBar(
    notificationCount: Int,
    create: (() -> Unit)?,
    notifications: (() -> Unit)?,
    showsHomeActions: Boolean = true,
    usesDarkSurface: Boolean = false,
    embeddedInPage: Boolean = false,
) {
    Row(
        modifier = Modifier
            .fillMaxWidth()
            .heightIn(min = LegendSize.MinimumTapTarget)
            .background(if (usesDarkSurface) LegendColors.Midnight else LegendColors.Canvas)
            .padding(horizontal = if (embeddedInPage) 0.dp else LegendSpacing.PageHorizontal, vertical = LegendSpacing.Micro),
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
            style = LegendTypography.Title.copy(letterSpacing = 4.6.sp),
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
        modifier = Modifier.fillMaxWidth(),
        shape = LegendShapes.ProminentCard,
        colors = CardDefaults.cardColors(containerColor = LegendColors.Navy),
        elevation = CardDefaults.cardElevation(defaultElevation = 8.dp),
    ) {
        Column(
            modifier = Modifier
                .fillMaxWidth()
                .background(Brush.linearGradient(listOf(LegendColors.Navy, LegendColors.Midnight)))
                .padding(LegendSpacing.CardContent),
            verticalArrangement = Arrangement.spacedBy(LegendSpacing.Sm),
        ) {
            Row(verticalAlignment = Alignment.CenterVertically) {
                Text("Welcome back,", style = LegendTypography.Supporting.copy(fontWeight = FontWeight.SemiBold), color = LegendColors.GoldBright)
                Spacer(Modifier.width(LegendSpacing.Xs))
                Text(firstName, style = LegendTypography.Section, color = LegendColors.OnNavy, maxLines = 1)
                Spacer(Modifier.weight(1f))
                Icon(Icons.Default.AutoAwesome, contentDescription = null, tint = LegendColors.GoldBright)
            }
            Row(verticalAlignment = Alignment.CenterVertically) {
                Text("DAILY SCRIPTURE", style = LegendTypography.Eyebrow.copy(letterSpacing = 1.sp), color = LegendColors.GoldBright)
                Spacer(Modifier.weight(1f))
                Icon(Icons.Default.NorthEast, contentDescription = null, tint = LegendColors.OnNavy.copy(alpha = 0.72f))
            }
            Text(home.dailyScripture.reference, style = LegendTypography.CardTitle, color = LegendColors.OnNavy)
            Text(
                home.dailyScripture.text.ifBlank { home.dailyScripture.passageText },
                style = LegendTypography.Supporting,
                color = LegendColors.OnNavy.copy(alpha = 0.78f),
                maxLines = 2,
                overflow = TextOverflow.Ellipsis,
            )
        }
    }
}

@Composable
private fun LegendHomeActivityPill(count: Int, hasActivity: Boolean, openActivity: () -> Unit) = Card(
    onClick = openActivity,
    modifier = Modifier.fillMaxWidth(),
    shape = CircleShape,
    colors = CardDefaults.cardColors(containerColor = LegendColors.Navy),
    elevation = CardDefaults.cardElevation(defaultElevation = 5.dp),
) {
    Row(
        modifier = Modifier
            .fillMaxWidth()
            .background(Brush.linearGradient(listOf(LegendColors.Navy, LegendColors.Midnight)))
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
                modifier = Modifier.width(LegendSize.AvatarHero),
            ) {
                Box(contentAlignment = Alignment.BottomEnd) {
                    LegendProtectedAvatar(
                        avatar = currentActor.avatar,
                        displayName = currentActor.displayName,
                        participantType = participantType,
                        repository = mediaRepository,
                        size = LegendSize.AvatarLarge,
                    )
                    IconButton(
                        onClick = create,
                        modifier = Modifier
                            .size(LegendSize.AvatarSmall)
                            .clip(CircleShape)
                            .background(LegendColors.Navy)
                            .border(LegendSpacing.Hairline, LegendColors.OnNavy, CircleShape),
                    ) {
                        Icon(Icons.Default.Add, "Create your story", tint = LegendColors.OnNavy)
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
                    size = LegendSize.AvatarLarge,
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
    realtimeClient: MobileMessagingRealtimeClient,
) {
    val context = LocalContext.current
    val conversations by viewModel.conversations.collectAsStateWithLifecycle()
    val detail by viewModel.detail.collectAsStateWithLifecycle()
    var selectedConversationId by remember { mutableStateOf<String?>(null) }
    var creatingConversation by remember { mutableStateOf(false) }
    var conversationMenu by remember { mutableStateOf<ConversationSummary?>(null) }
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
    LaunchedEffect(selectedConversationId) {
        onThreadOpenChanged(selectedConversationId != null)
        LegendRealtimeEvents.conversationUpdates.collectLatest { changed ->
            if (changed == null || changed == selectedConversationId) {
                viewModel.load()
                selectedConversationId?.let(viewModel::open)
            }
        }
    }
    DisposableEffect(realtimeClient) {
        realtimeClient.start()
        onDispose {
            realtimeClient.stop()
            onThreadOpenChanged(false)
        }
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
private fun LegendMessagesInboxHeader(onNewMessage: () -> Unit) {
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
                modifier = Modifier.background(Brush.linearGradient(listOf(LegendColors.GoldBright, LegendColors.Gold)), CircleShape),
            ) { Icon(Icons.Default.Edit, "Start a new conversation", tint = LegendColors.Midnight) }
        }
    }
}

@Composable
private fun LegendConversationRow(
    conversation: ConversationSummary,
    mediaRepository: AuthenticatedMediaRepository,
    participantType: String,
    open: () -> Unit,
    more: () -> Unit,
) {
    Surface(
        modifier = Modifier.fillMaxWidth().clickable(onClick = open),
        color = LegendColors.Surface,
        shape = LegendShapes.Card,
        shadowElevation = 1.dp,
    ) {
        Row(Modifier.padding(LegendSpacing.Sm), verticalAlignment = Alignment.CenterVertically) {
            LegendProtectedAvatar(
                avatar = conversation.groupAvatar ?: conversation.counterparty.avatar,
                displayName = conversation.title,
                participantType = participantType,
                repository = mediaRepository,
                size = 48.dp,
            )
            Spacer(Modifier.width(LegendSpacing.Sm))
            Column(Modifier.weight(1f), verticalArrangement = Arrangement.spacedBy(LegendSpacing.Micro)) {
                Row(verticalAlignment = Alignment.CenterVertically) {
                    Text(conversation.title, style = LegendTypography.CardTitle, color = LegendColors.TextPrimary, maxLines = 1, overflow = TextOverflow.Ellipsis, modifier = Modifier.weight(1f))
                    if (conversation.isPinned) Icon(Icons.Default.PushPin, "Pinned", modifier = Modifier.size(15.dp), tint = LegendColors.Gold)
                    if (conversation.isMuted) Icon(Icons.Default.NotificationsOff, "Muted", modifier = Modifier.padding(start = LegendSpacing.Micro).size(15.dp), tint = LegendColors.TextTertiary)
                }
                Text(conversation.lastMessagePreview ?: "No messages yet", style = LegendTypography.Supporting, color = LegendColors.TextSecondary, maxLines = 1, overflow = TextOverflow.Ellipsis)
            }
            Spacer(Modifier.width(LegendSpacing.Xs))
            Column(horizontalAlignment = Alignment.End, verticalArrangement = Arrangement.spacedBy(LegendSpacing.Xs)) {
                    conversation.lastMessageUtc?.let { Text(legendCompactTime(it), style = LegendTypography.Label, color = LegendColors.TextTertiary) }
                if (conversation.unreadCount > 0) {
                    Text(conversation.unreadCount.coerceAtMost(99).toString(), style = LegendTypography.Label, color = LegendColors.OnNavy, modifier = Modifier.background(LegendColors.Error, CircleShape).padding(horizontal = LegendSpacing.Xs, vertical = LegendSpacing.Micro))
                }
                IconButton(onClick = more, modifier = Modifier.size(28.dp)) { Icon(Icons.Default.MoreVert, "Conversation actions", tint = LegendColors.TextSecondary) }
            }
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

private enum class SocialCollection(val label: String) {
    POSTS("Posts"),
    STORIES("Stories"),
    HACS("Hacs"),
}

@Composable
private fun SocialScreen(
    viewModel: SocialViewModel,
    mediaRepository: AuthenticatedMediaRepository,
    participantType: String,
) {
    val context = LocalContext.current
    val state by viewModel.state.collectAsStateWithLifecycle()
    val musicState by viewModel.music.collectAsStateWithLifecycle()
    var collection by remember { mutableStateOf(SocialCollection.POSTS) }
    var creating by remember { mutableStateOf(false) }
    var commentingPost by remember { mutableStateOf<SocialPost?>(null) }
    var editingPost by remember { mutableStateOf<SocialPost?>(null) }
    var profileAuthor by remember { mutableStateOf<SocialAuthor?>(null) }
    var viewingStory by remember { mutableStateOf<SocialPost?>(null) }
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
            val entries = when (collection) {
                SocialCollection.POSTS -> snapshot.posts
                SocialCollection.STORIES -> snapshot.stories
                SocialCollection.HACS -> snapshot.hacs
            }
            val currentIdentity = snapshot.currentProfileMetrics?.profile?.identity
            LazyColumn(
                modifier = Modifier.fillMaxSize().background(LegendColors.Canvas),
                verticalArrangement = Arrangement.spacedBy(LegendSpacing.Sm),
                contentPadding = PaddingValues(horizontal = LegendSpacing.PageHorizontal, vertical = LegendSpacing.Md),
            ) {
                item {
                    LegendSocialHeader(
                        collection = collection,
                        selectCollection = { collection = it },
                        create = { creating = true },
                    )
                }
                if (collection == SocialCollection.POSTS && snapshot.stories.isNotEmpty()) {
                    item {
                        LegendStoryRail(
                            stories = snapshot.stories,
                            mediaRepository = mediaRepository,
                            participantType = participantType,
                            select = { viewingStory = it },
                        )
                    }
                }
                if (entries.isEmpty()) {
                    item { LegendMessagingEmptyCard("No ${collection.label.lowercase()} yet", "Server-authorized LEGEND content will appear here.", action = { creating = true }) }
                } else {
                    items(entries, key = { it.id }) { post ->
                        LegendSocialPostCard(
                            post = post,
                            mediaRepository = mediaRepository,
                            participantType = participantType,
                            isCurrentActor = currentIdentity?.userId == post.author.identity.userId && currentIdentity.participantType == post.author.identity.participantType,
                            onProfile = { profileAuthor = post.author },
                            onReact = { viewModel.react(post.id) },
                            onComment = { commentingPost = post },
                            onFollow = { viewModel.toggleFollow(post) },
                            onSave = { viewModel.toggleSave(post.id) },
                            onRepost = { viewModel.toggleRepost(post.id) },
                            onShare = {
                                viewModel.recordShare(post.id)
                                context.startActivity(android.content.Intent.createChooser(
                                    android.content.Intent(android.content.Intent.ACTION_SEND)
                                        .setType("text/plain")
                                        .putExtra(android.content.Intent.EXTRA_TEXT, post.body.ifBlank { "LEGEND ${post.contentType} by ${post.author.displayName}" }),
                                    "Share LEGEND update",
                                ))
                            },
                            onEdit = { editingPost = post },
                            onDelete = { viewModel.deletePost(post.id) },
                        )
                    }
                }
            }
        }
    }

    if (creating) {
        CreatePostSheet(
            onDismiss = { creating = false },
            musicState = musicState,
            searchMusic = viewModel::searchMusic,
            createText = { request ->
                viewModel.create(request)
                creating = false
            },
            createMedia = { uris, options, previewUri ->
                viewModel.createMedia(context, uris, options, previewUri)
                creating = false
            },
        )
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
    editingPost?.let { post ->
        EditPostDialog(
            post = post,
            dismiss = { editingPost = null },
            submit = { body -> viewModel.updatePost(post.id, body); editingPost = null },
        )
    }
    viewingStory?.let { story ->
        LegendStoryViewer(
            story = story,
            mediaRepository = mediaRepository,
            participantType = participantType,
            dismiss = { viewingStory = null },
            recordView = { viewModel.recordView(story.id, storyInteractionType = "Opened") },
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
private fun LegendSocialHeader(
    collection: SocialCollection,
    selectCollection: (SocialCollection) -> Unit,
    create: () -> Unit,
) {
    Surface(color = LegendColors.Navy, shape = LegendShapes.Card, modifier = Modifier.fillMaxWidth()) {
        Column(Modifier.padding(LegendSpacing.Md), verticalArrangement = Arrangement.spacedBy(LegendSpacing.Sm)) {
            Row(verticalAlignment = Alignment.CenterVertically) {
                Column(Modifier.weight(1f)) {
                    Text("For You", style = LegendTypography.Section, color = LegendColors.OnNavy)
                    Text("Your LEGEND community", style = LegendTypography.Label, color = LegendColors.GoldSoft)
                }
                IconButton(onClick = create, modifier = Modifier.background(Brush.linearGradient(listOf(LegendColors.GoldBright, LegendColors.Gold)), CircleShape)) {
                    Icon(Icons.Default.Add, "Create LEGEND update", tint = LegendColors.Midnight)
                }
            }
            Row(horizontalArrangement = Arrangement.spacedBy(LegendSpacing.Xs)) {
                SocialCollection.entries.forEach { candidate ->
                    FilterChip(
                        selected = collection == candidate,
                        onClick = { selectCollection(candidate) },
                        label = { Text(candidate.label) },
                        colors = FilterChipDefaults.filterChipColors(
                            selectedContainerColor = LegendColors.Gold,
                            selectedLabelColor = LegendColors.Midnight,
                            labelColor = LegendColors.OnNavy,
                        ),
                        border = FilterChipDefaults.filterChipBorder(false, false, borderColor = LegendColors.GoldSoft, selectedBorderColor = LegendColors.Gold),
                    )
                }
            }
        }
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
    onShare: () -> Unit,
    onEdit: (() -> Unit)? = null,
    onDelete: (() -> Unit)? = null,
) {
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
                    if (post.author.isVerified) Icon(Icons.Default.Verified, "Verified", modifier = Modifier.padding(start = LegendSpacing.Xs).size(17.dp), tint = LegendColors.Info)
                }
                Text(post.author.username?.let { "@$it" } ?: post.contentType, style = LegendTypography.Label, color = LegendColors.TextSecondary)
            }
            IconButton(onClick = { showActions = true }) { Icon(Icons.Default.MoreHoriz, "Post actions", tint = LegendColors.TextSecondary) }
        }
        if (post.body.isNotBlank()) Text(post.body, style = LegendTypography.Body, color = LegendColors.TextPrimary)
        if (post.media.isNotEmpty()) {
            post.media.forEach { media ->
                LegendProtectedSocialMedia(
                    assetId = media.id,
                    mediaKind = media.mediaKind,
                    participantType = participantType,
                    repository = mediaRepository,
                    contentDescription = media.accessibilityText,
                )
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
            Spacer(Modifier.weight(1f))
            IconButton(onClick = onSave) { Icon(if (post.savedByCurrentActor) Icons.Default.Bookmark else Icons.Default.BookmarkBorder, "Save", tint = LegendColors.Gold) }
            IconButton(onClick = onShare) { Icon(Icons.Default.Share, "Share", tint = LegendColors.TextSecondary) }
        }
        post.music?.let { music -> Text("♫ ${music.trackTitle} · ${music.artistName}", style = LegendTypography.Label, color = LegendColors.TextSecondary) }
        if (showActions) {
            AlertDialog(
                onDismissRequest = { showActions = false },
                title = { Text("${post.contentType} actions") },
                text = { Text("All changes are applied through the existing LEGEND social authority.") },
                confirmButton = {
                    Column(horizontalAlignment = Alignment.End) {
                        if (!isCurrentActor) TextButton(onClick = { onFollow(); showActions = false }) { Text(if (post.followedByCurrentActor) "Unfollow" else if (post.followRequestPending) "Follow request pending" else "Follow") }
                        TextButton(onClick = { onRepost(); showActions = false }) { Text(if (post.repostedByCurrentActor) "Undo repost" else "Repost") }
                        if (isCurrentActor) {
                            onEdit?.let { edit -> TextButton(onClick = { edit(); showActions = false }) { Text("Edit") } }
                            onDelete?.let { delete -> TextButton(onClick = { delete(); showActions = false }) { Text("Delete", color = LegendColors.Error) } }
                        }
                    }
                },
                dismissButton = { TextButton(onClick = { showActions = false }) { Text("Cancel") } },
            )
        }
    }
    }
}

@Composable
private fun CreatePostSheet(
    onDismiss: () -> Unit,
    musicState: LoadState<List<SocialMusic>>,
    searchMusic: (String) -> Unit,
    createText: (CreateSocialPostRequest) -> Unit,
    createMedia: (List<Uri>, SocialMediaPublishOptions, Uri?) -> Unit,
) {
    val context = LocalContext.current
    var contentType by remember { mutableStateOf(LegendCopy.value("content.post")) }
    var body by remember { mutableStateOf("") }
    var audience by remember { mutableStateOf("AuthorizedNetwork") }
    var location by remember { mutableStateOf("") }
    var accessibilityText by remember { mutableStateOf("") }
    var commentsEnabled by remember { mutableStateOf(true) }
    var selected by remember { mutableStateOf<List<Uri>>(emptyList()) }
    var coverImage by remember { mutableStateOf<Uri?>(null) }
    var selectedMusic by remember { mutableStateOf<SocialMusic?>(null) }
    var choosingMusic by remember { mutableStateOf(false) }
    val picker = rememberLauncherForActivityResult(ActivityResultContracts.PickMultipleVisualMedia()) {
        selected = it
    }
    val coverPicker = rememberLauncherForActivityResult(ActivityResultContracts.PickVisualMedia()) { coverImage = it }

    ModalBottomSheet(onDismissRequest = onDismiss, containerColor = LegendColors.Midnight) {
        LazyColumn(
            modifier = Modifier.fillMaxHeight(.94f).fillMaxWidth(),
            contentPadding = PaddingValues(LegendSpacing.Lg),
            verticalArrangement = Arrangement.spacedBy(LegendSpacing.Md),
        ) {
            item {
                Text("CREATE", color = LegendColors.GoldBright, style = LegendTypography.Eyebrow)
                Text("Share with LEGEND", color = LegendColors.OnNavy, style = LegendTypography.Hero)
                Text("Your post is published through the existing protected social authority.", color = LegendColors.GoldSoft, style = LegendTypography.Supporting)
            }
            item {
                LazyRow(horizontalArrangement = Arrangement.spacedBy(LegendSpacing.Xs)) {
                    items(listOf(LegendCopy.value("content.post"), LegendCopy.value("content.story"), LegendCopy.value("content.hac"))) { choice ->
                        FilterChip(selected = contentType == choice, onClick = { contentType = choice }, label = { Text(choice) }, colors = FilterChipDefaults.filterChipColors(selectedContainerColor = LegendColors.Gold, selectedLabelColor = LegendColors.Midnight, labelColor = LegendColors.OnNavy))
                    }
                }
            }
            item {
                OutlinedTextField(body, { body = it }, label = { Text("Caption") }, modifier = Modifier.fillMaxWidth(), minLines = 4, maxLines = 8, shape = LegendShapes.Control, colors = legendCreatorFieldColors())
            }
            item {
                Text("AUDIENCE", style = LegendTypography.Eyebrow, color = LegendColors.GoldBright)
                LazyRow(horizontalArrangement = Arrangement.spacedBy(LegendSpacing.Xs)) {
                    items(listOf("AuthorizedNetwork" to "Network", "Followers" to "Followers", "MutualConnections" to "Mutuals")) { (value, label) ->
                        FilterChip(selected = audience == value, onClick = { audience = value }, label = { Text(label) }, colors = FilterChipDefaults.filterChipColors(selectedContainerColor = LegendColors.Gold, selectedLabelColor = LegendColors.Midnight, labelColor = LegendColors.OnNavy))
                    }
                }
            }
            item {
                OutlinedTextField(location, { location = it }, label = { Text("Location (optional)") }, modifier = Modifier.fillMaxWidth(), singleLine = true, shape = LegendShapes.Control, colors = legendCreatorFieldColors())
                Spacer(Modifier.height(LegendSpacing.Xs))
                OutlinedTextField(accessibilityText, { accessibilityText = it }, label = { Text("Accessibility description (optional)") }, modifier = Modifier.fillMaxWidth(), minLines = 2, maxLines = 4, shape = LegendShapes.Control, colors = legendCreatorFieldColors())
            }
            item {
                Surface(color = LegendColors.Navy, shape = LegendShapes.Control, modifier = Modifier.fillMaxWidth()) {
                    Row(Modifier.padding(LegendSpacing.Sm), verticalAlignment = Alignment.CenterVertically) {
                        Column(Modifier.weight(1f)) {
                            Text("Allow comments", style = LegendTypography.Label, color = LegendColors.OnNavy)
                            Text("Server-authoritative comment visibility.", style = LegendTypography.Supporting, color = LegendColors.GoldSoft)
                        }
                        Switch(checked = commentsEnabled, onCheckedChange = { commentsEnabled = it }, colors = SwitchDefaults.colors(checkedThumbColor = LegendColors.Navy, checkedTrackColor = LegendColors.Gold))
                    }
                }
            }
            item {
                OutlinedButton(onClick = { picker.launch(PickVisualMediaRequest(ActivityResultContracts.PickVisualMedia.ImageAndVideo)) }, modifier = Modifier.fillMaxWidth(), shape = LegendShapes.Control) {
                    Icon(Icons.Default.PermMedia, null, tint = LegendColors.Gold)
                    Spacer(Modifier.width(LegendSpacing.Xs))
                    Text(if (selected.isEmpty()) "Add photo or video" else "Replace ${selected.size} selected media item(s)", color = LegendColors.OnNavy)
                }
            }
            if (selected.isNotEmpty()) items(selected, key = { it.toString() }) { uri ->
                val mimeType = context.contentResolver.getType(uri).orEmpty()
                Surface(color = LegendColors.SurfaceElevated, shape = LegendShapes.Control, modifier = Modifier.fillMaxWidth()) {
                    Column(Modifier.padding(LegendSpacing.Sm), verticalArrangement = Arrangement.spacedBy(LegendSpacing.Xs)) {
                        if (mimeType.startsWith("video/")) LegendLocalVideoPreview(uri, Modifier.fillMaxWidth().height(190.dp))
                        else AsyncImage(model = uri, contentDescription = accessibilityText.takeIf(String::isNotBlank) ?: "Selected media", modifier = Modifier.fillMaxWidth().heightIn(max = 260.dp))
                        Row(verticalAlignment = Alignment.CenterVertically) {
                            Text(context.contentResolver.legendDisplayName(uri), style = LegendTypography.Label, color = LegendColors.OnNavy, modifier = Modifier.weight(1f), maxLines = 1, overflow = TextOverflow.Ellipsis)
                            IconButton(onClick = { selected = selected - uri }) { Icon(Icons.Default.Close, "Remove selected media", tint = LegendColors.Gold) }
                        }
                    }
                }
            }
            if (contentType == LegendCopy.value("content.hac")) item {
                OutlinedButton(onClick = { coverPicker.launch(PickVisualMediaRequest(ActivityResultContracts.PickVisualMedia.ImageOnly)) }, modifier = Modifier.fillMaxWidth(), shape = LegendShapes.Control) {
                    Icon(Icons.Default.Image, null, tint = LegendColors.Gold)
                    Spacer(Modifier.width(LegendSpacing.Xs))
                    Text(if (coverImage == null) "Choose Hac cover image (optional)" else "Hac cover image selected", color = LegendColors.OnNavy)
                }
            }
            item {
                OutlinedButton(onClick = { choosingMusic = true }, modifier = Modifier.fillMaxWidth(), shape = LegendShapes.Control) {
                    Icon(Icons.Default.MusicNote, null, tint = LegendColors.Gold)
                    Spacer(Modifier.width(LegendSpacing.Xs))
                    Text(selectedMusic?.let { "${it.trackTitle} · ${it.artistName}" } ?: "Add server-licensed music (optional)", color = LegendColors.OnNavy, maxLines = 1, overflow = TextOverflow.Ellipsis)
                }
            }
            item {
                LegendPrimaryButton(
                    text = if (selected.isEmpty()) "Publish" else "Upload and publish",
                    enabled = body.isNotBlank() || selected.isNotEmpty(),
                    modifier = Modifier.fillMaxWidth(),
                ) {
                    val textRequest = CreateSocialPostRequest(contentType, body.trim(), audience, location.trim().takeIf(String::isNotBlank), commentsEnabled)
                    if (selected.isEmpty()) createText(textRequest)
                    else createMedia(selected, SocialMediaPublishOptions(contentType, body.trim(), audience, location.trim().takeIf(String::isNotBlank), commentsEnabled, accessibilityText.trim().takeIf(String::isNotBlank), selectedMusic), coverImage)
                }
                Text("Media is selected with Android-native tools, then streams directly to LEGEND. Android does not process, transcode, or host it.", color = LegendColors.GoldSoft, style = LegendTypography.Supporting, modifier = Modifier.padding(top = LegendSpacing.Sm))
            }
        }
    }
    if (choosingMusic) LegendMusicPickerSheet(musicState, searchMusic, { selectedMusic = it; choosingMusic = false }, { choosingMusic = false })
}

@Composable
private fun legendCreatorFieldColors() = OutlinedTextFieldDefaults.colors(
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
private fun LegendMusicPickerSheet(
    state: LoadState<List<SocialMusic>>,
    search: (String) -> Unit,
    select: (SocialMusic) -> Unit,
    dismiss: () -> Unit,
) {
    var query by remember { mutableStateOf("") }
    LaunchedEffect(Unit) { search("") }
    LaunchedEffect(query) { if (query.isNotBlank()) { kotlinx.coroutines.delay(160); search(query) } }
    ModalBottomSheet(onDismissRequest = dismiss, containerColor = LegendColors.Canvas) {
        Column(Modifier.fillMaxHeight(.75f).padding(horizontal = LegendSpacing.PageHorizontal)) {
            Row(verticalAlignment = Alignment.CenterVertically) {
                Column(Modifier.weight(1f)) {
                    Text("Add music", style = LegendTypography.Section, color = LegendColors.TextPrimary)
                    Text("Results are supplied by the existing LEGEND music authority.", style = LegendTypography.Supporting, color = LegendColors.TextSecondary)
                }
                TextButton(onClick = dismiss) { Text("Done", color = LegendColors.Gold) }
            }
            OutlinedTextField(query, { query = it }, modifier = Modifier.fillMaxWidth().padding(vertical = LegendSpacing.Sm), singleLine = true, leadingIcon = { Icon(Icons.Default.Search, null, tint = LegendColors.Gold) }, placeholder = { Text("Search music") }, shape = LegendShapes.Control, colors = legendMessagingFieldColors())
            when (state) {
                LoadState.Idle, LoadState.Loading -> LegendLoadingState()
                is LoadState.Error -> LegendErrorState(state.message) { search(query) }
                is LoadState.Data -> LazyColumn(verticalArrangement = Arrangement.spacedBy(LegendSpacing.Xs)) {
                    if (state.value.isEmpty()) item { LegendEmptyState("No music found", "Try a different artist or track name.") }
                    items(state.value, key = { "${it.providerId}:${it.providerTrackId}" }) { track ->
                        Surface(Modifier.fillMaxWidth().clickable { select(track) }, color = LegendColors.Surface, shape = LegendShapes.Control) {
                            Row(Modifier.padding(LegendSpacing.Sm), verticalAlignment = Alignment.CenterVertically) {
                                Icon(Icons.Default.MusicNote, null, tint = LegendColors.Gold)
                                Spacer(Modifier.width(LegendSpacing.Sm))
                                Column(Modifier.weight(1f)) {
                                    Text(track.trackTitle, style = LegendTypography.Label, color = LegendColors.TextPrimary)
                                    Text(track.artistName, style = LegendTypography.Supporting, color = LegendColors.TextSecondary)
                                }
                                Icon(Icons.Default.AddCircleOutline, "Select music", tint = LegendColors.Gold)
                            }
                        }
                    }
                }
            }
        }
    }
}

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
        title = { Text("Edit ${post.contentType}") },
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
                            if (author.isVerified) Icon(Icons.Default.Verified, "Verified", modifier = Modifier.padding(start = LegendSpacing.Xs).size(18.dp), tint = LegendColors.Info)
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
                            onShare = { viewModel.recordShare(post.id) },
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
    signOut: () -> Unit,
) {
    val profile by viewModel.profile.collectAsStateWithLifecycle()
    val lifecycle by viewModel.lifecycle.collectAsStateWithLifecycle()
    val usernameAvailability by viewModel.usernameAvailability.collectAsStateWithLifecycle()
    val socialMetrics by socialViewModel.profileMetrics.collectAsStateWithLifecycle()
    val socialPosts by socialViewModel.profilePosts.collectAsStateWithLifecycle()
    var financial by remember { mutableStateOf(false) }
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
    val context = LocalContext.current
    val avatarPicker = rememberLauncherForActivityResult(ActivityResultContracts.PickVisualMedia()) { uri -> uri?.let { viewModel.updateAvatar(context, it) } }
    LaunchedEffect(Unit) { viewModel.load(); socialViewModel.loadCurrentProfile() }

    if (financial) {
        FinancialScreen(financialRepository, participantType) { financial = false }
    } else {
        when (profile) {
                LoadState.Idle,
                LoadState.Loading -> LegendLoadingState()

                is LoadState.Error -> LegendErrorState((profile as LoadState.Error).message, viewModel::load)
                is LoadState.Data -> {
                    val account = (profile as LoadState.Data<MobileAccountProfile>).value
                    LazyColumn(
                        modifier = Modifier.fillMaxSize().background(LegendColors.Canvas),
                        verticalArrangement = Arrangement.spacedBy(LegendSpacing.Sm),
                        contentPadding = PaddingValues(horizontal = LegendSpacing.PageHorizontal, vertical = LegendSpacing.Md),
                    ) {
                        item {
                            Surface(color = LegendColors.Navy, shape = LegendShapes.Hero, modifier = Modifier.fillMaxWidth()) {
                                Row(Modifier.padding(LegendSpacing.Lg), verticalAlignment = Alignment.CenterVertically) {
                                    LegendProtectedAvatar(
                                        avatar = account.avatar,
                                        displayName = account.displayName,
                                        participantType = participantType,
                                        repository = mediaRepository,
                                        size = LegendSize.AvatarLarge,
                                    )
                                    Spacer(Modifier.width(LegendSpacing.Md))
                                    Column(Modifier.weight(1f)) {
                                        Row(verticalAlignment = Alignment.CenterVertically) {
                                            Text(account.displayName, style = LegendTypography.Section, color = LegendColors.OnNavy)
                                            if (account.isVerified) Icon(Icons.Default.Verified, "Verified", modifier = Modifier.padding(start = LegendSpacing.Xs).size(18.dp), tint = LegendColors.Info)
                                        }
                                        Text(account.username?.let { "@$it" } ?: account.roleLabel ?: "LEGEND member", style = LegendTypography.Label, color = LegendColors.GoldSoft)
                                    }
                                    IconButton(onClick = { avatarPicker.launch(PickVisualMediaRequest(ActivityResultContracts.PickVisualMedia.ImageOnly)) }, modifier = Modifier.background(LegendColors.Gold, CircleShape)) { Icon(Icons.Default.PhotoCamera, "Change profile photo", tint = LegendColors.Midnight) }
                                }
                            }
                        }
                        if (socialMetrics is LoadState.Data) {
                            item {
                                val metrics = (socialMetrics as LoadState.Data<SocialProfileMetrics>).value
                                Surface(color = LegendColors.Navy, shape = LegendShapes.Control, modifier = Modifier.fillMaxWidth()) {
                                    Row(Modifier.padding(LegendSpacing.Sm), horizontalArrangement = Arrangement.SpaceEvenly) {
                                        LegendMetric("Posts", metrics.postCount.toString())
                                        LegendMetric("Followers", metrics.followerCount.toString())
                                        LegendMetric("Following", metrics.followingCount.toString())
                                    }
                                }
                            }
                        }
                        item {
                            AccountSettingsRow("Edit profile", account.shortBio ?: "Update your public profile, handle, and visibility", Icons.Default.Edit, { editing = account })
                        }
                        item {
                            AccountSettingsRow("Financial Intelligence", "Server-authoritative projections and priorities", Icons.Default.AccountBalance, { financial = true })
                        }
                        item {
                            AccountSettingsRow("Language preferences", account.translationAccess?.preferredCommunicationLanguage ?: "No preferred communication language set", Icons.Default.Translate, { languageAccount = account }, footnote = "Translation is server-only.")
                        }
                        if (isFounder) item {
                            AccountSettingsRow("Founder management", "Server-authorized account archive and removal controls", Icons.Default.AdminPanelSettings, { founderManagement = true })
                        }
                        if (isFounder) item {
                            AccountSettingsRow("Member authority", "Grant or revoke founder-controlled LEGEND resources", Icons.Default.ManageAccounts, { controlledResourceType = LegendFounderResource.LanguageTranslation })
                        }
                        if (canManageScripture) item {
                            AccountSettingsRow("Daily Scripture", "Manage the server-owned scripture schedule", Icons.AutoMirrored.Filled.MenuBook, { scriptureManagementOpen = true })
                        }
                        if (canManageCommunity) item {
                            AccountSettingsRow("Community safety", "Review open reports using your server-issued authority", Icons.Default.GppGood, { communitySafetyOpen = true })
                        }
                        item {
                            Surface(color = LegendColors.Surface, shape = LegendShapes.Card, modifier = Modifier.fillMaxWidth()) {
                                Column(Modifier.padding(LegendSpacing.CardContent), verticalArrangement = Arrangement.spacedBy(LegendSpacing.Xs)) {
                                    Text("Privacy & safety", style = LegendTypography.CardTitle, color = LegendColors.TextPrimary)
                                    Text(if (account.isPrivate) "Private profile" else "Public profile", style = LegendTypography.Supporting, color = LegendColors.TextSecondary)
                                    Row(verticalAlignment = Alignment.CenterVertically) {
                                        Text("Private profile", modifier = Modifier.weight(1f), style = LegendTypography.Body, color = LegendColors.TextPrimary)
                                        Switch(checked = account.isPrivate, onCheckedChange = viewModel::updatePrivacy)
                                    }
                                }
                            }
                        }
                        if (account.isPrivate) item {
                            AccountSettingsRow("Follow requests", "Approve or decline people waiting to follow you", Icons.Default.PersonAdd, { socialViewModel.loadFollowRequests(); followRequestsOpen = true })
                        }
                        item {
                            Surface(color = LegendColors.Surface, shape = LegendShapes.Card, modifier = Modifier.fillMaxWidth()) {
                                Column(Modifier.padding(LegendSpacing.CardContent), verticalArrangement = Arrangement.spacedBy(LegendSpacing.Xs)) {
                                    Text("Account lifecycle", style = LegendTypography.CardTitle, color = LegendColors.TextPrimary)
                                    val currentLifecycle = (lifecycle as? LoadState.Data<AccountLifecycle>)?.value
                                    Text(currentLifecycle?.state ?: "Loading account status", style = LegendTypography.Supporting, color = LegendColors.TextSecondary)
                                    if (currentLifecycle?.canResume == true) TextButton(onClick = viewModel::resumeAccount) { Text("Resume account", color = LegendColors.Gold) }
                                    else TextButton(onClick = viewModel::pauseAccount) { Text("Pause account", color = LegendColors.Gold) }
                                    TextButton(onClick = { deletePrompt = true }) { Text("Request account deletion", color = LegendColors.Error) }
                                }
                            }
                        }
                        item {
                            AccountSettingsRow("Sign out", "Securely end this Android session", Icons.AutoMirrored.Filled.Logout, signOut)
                        }
                        if (socialPosts is LoadState.Data) {
                            val posts = (socialPosts as LoadState.Data<List<SocialPost>>).value
                            if (posts.isNotEmpty()) {
                                item { Text("Your posts", style = LegendTypography.Section, color = LegendColors.TextPrimary) }
                                items(posts.take(3), key = { it.id }) { post ->
                                    LegendSocialPostCard(
                                        post, mediaRepository, participantType, true, null,
                                        { socialViewModel.react(post.id) }, { commentingPost = post },
                                        { socialViewModel.toggleFollow(post) }, { socialViewModel.toggleSave(post.id) },
                                        { socialViewModel.toggleRepost(post.id) }, { socialViewModel.recordShare(post.id) },
                                        { editingPost = post }, { socialViewModel.deletePost(post.id) },
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
    commentingPost?.let { post ->
        LegendCommentsSheet(post, mediaRepository, participantType, { commentingPost = null }) { body, parentCommentId -> socialViewModel.comment(post.id, body, parentCommentId) }
    }
    editingPost?.let { post ->
        EditPostDialog(post, { editingPost = null }) { body -> socialViewModel.updatePost(post.id, body); editingPost = null }
    }
}

@Composable
private fun AccountSettingsRow(title: String, detail: String, icon: androidx.compose.ui.graphics.vector.ImageVector, click: () -> Unit, footnote: String? = null) {
    Surface(color = LegendColors.Surface, shape = LegendShapes.Control, modifier = Modifier.fillMaxWidth().clickable(onClick = click)) {
        Row(Modifier.padding(LegendSpacing.Sm), verticalAlignment = Alignment.CenterVertically) {
            Icon(icon, null, tint = LegendColors.Gold, modifier = Modifier.size(22.dp))
            Spacer(Modifier.width(LegendSpacing.Sm))
            Column(Modifier.weight(1f)) {
                Text(title, style = LegendTypography.CardTitle, color = LegendColors.TextPrimary)
                Text(detail, style = LegendTypography.Supporting, color = LegendColors.TextSecondary, maxLines = 2, overflow = TextOverflow.Ellipsis)
                footnote?.let { Text(it, style = LegendTypography.Label, color = LegendColors.Gold) }
            }
            Icon(Icons.Default.ChevronRight, null, tint = LegendColors.TextTertiary)
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
                                OutlinedTextField(confirmation, { confirmation = it }, modifier = Modifier.fillMaxWidth(), singleLine = true, label = { Text(if (archive) "Type ERASE" else "Type DELETE") }, shape = LegendShapes.Control, colors = if (archive) legendCreatorFieldColors() else legendMessagingFieldColors())
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
