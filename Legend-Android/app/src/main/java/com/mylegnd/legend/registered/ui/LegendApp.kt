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
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.Logout
import androidx.compose.material.icons.automirrored.filled.Send
import androidx.compose.material.icons.filled.*
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import androidx.lifecycle.viewmodel.compose.viewModel
import androidx.core.content.ContextCompat
import com.mylegnd.legend.registered.LegendContainer
import com.mylegnd.legend.registered.LegendViewModelFactory
import com.mylegnd.legend.registered.core.design.LegendColors
import com.mylegnd.legend.registered.core.design.LegendShapes
import com.mylegnd.legend.registered.core.design.LegendSpacing
import com.mylegnd.legend.registered.core.model.*
import com.mylegnd.legend.registered.core.media.AuthenticatedMediaRepository
import com.mylegnd.legend.registered.core.media.LegendProtectedSocialMedia
import com.mylegnd.legend.registered.core.network.DiscoveryPage
import com.mylegnd.legend.registered.core.network.DiscoveryResult
import com.mylegnd.legend.registered.core.network.JourneyDashboard
import com.mylegnd.legend.registered.core.realtime.LegendRealtimeEvents
import com.mylegnd.legend.registered.core.session.ActiveLegendSession
import com.mylegnd.legend.registered.core.session.SessionState
import com.mylegnd.legend.registered.core.session.SessionViewModel
import com.mylegnd.legend.registered.data.FinancialRepository
import com.mylegnd.legend.registered.data.LoadState
import com.mylegnd.legend.registered.feature.*
import kotlinx.coroutines.flow.collectLatest

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
        Modifier.fillMaxSize().padding(32.dp),
        horizontalAlignment = Alignment.CenterHorizontally,
        verticalArrangement = Arrangement.Center,
    ) {
        Text("LEGEND®", style = MaterialTheme.typography.displayLarge, color = LegendColors.Navy)
        Spacer(Modifier.height(12.dp))
        Text(
            "Your account is secured by the LEGEND mobile identity service.",
            color = LegendColors.TextSecondary,
        )
        Spacer(Modifier.height(24.dp))
        LegendPrimaryButton("Sign in securely", enabled = activity != null) {
            activity?.let(onSignIn)
        }
    }
}

@Composable
private fun RoleSelectionScreen(roles: List<String>, select: (String) -> Unit) {
    Column(Modifier.fillMaxSize().padding(24.dp), verticalArrangement = Arrangement.Center) {
        Text("Choose your experience", style = MaterialTheme.typography.headlineMedium, color = LegendColors.Navy)
        Spacer(Modifier.height(16.dp))
        roles.forEach { role ->
            LegendCard(Modifier.fillMaxWidth().clickable { select(role) }) {
                Text("Continue as $role", style = MaterialTheme.typography.titleMedium)
            }
        }
    }
}

private enum class LegendTab(val label: String) {
    HOME("Home"),
    DISCOVER("Discover"),
    SOCIAL("For You"),
    MESSAGES("Messages"),
    ACCOUNT("Account"),
}

@Composable
private fun AuthenticatedShell(
    session: ActiveLegendSession,
    container: LegendContainer,
    signOut: () -> Unit,
) {
    var tab by remember { mutableStateOf(LegendTab.HOME) }
    val participantType = session.actor.identity.participantType
    val home: HomeViewModel = viewModel(
        factory = LegendViewModelFactory { HomeViewModel(container.homeRepository, participantType) },
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

    Scaffold(
        bottomBar = {
            NavigationBar(containerColor = MaterialTheme.colorScheme.surface) {
                LegendTab.entries.forEach { item ->
                    NavigationBarItem(
                        selected = tab == item,
                        onClick = { tab = item },
                        icon = {
                            Icon(
                                imageVector = when (item) {
                                    LegendTab.HOME -> Icons.Default.Home
                                    LegendTab.DISCOVER -> Icons.Default.Explore
                                    LegendTab.SOCIAL -> Icons.Default.PlayCircle
                                    LegendTab.MESSAGES -> Icons.Default.Email
                                    LegendTab.ACCOUNT -> Icons.Default.Person
                                },
                                contentDescription = item.label,
                            )
                        },
                        label = { Text(item.label) },
                        colors = NavigationBarItemDefaults.colors(
                            selectedIconColor = LegendColors.Gold,
                            selectedTextColor = LegendColors.Navy,
                            indicatorColor = LegendColors.GoldSoft,
                        ),
                    )
                }
            }
        },
    ) { padding ->
        Box(Modifier.padding(padding)) {
            when (tab) {
                LegendTab.HOME -> HomeScreen(home)
                LegendTab.DISCOVER -> DiscoverScreen(discovery, participantType)
                LegendTab.SOCIAL -> SocialScreen(social, container.authenticatedMediaRepository, participantType)
                LegendTab.MESSAGES -> MessagesScreen(messages)
                LegendTab.ACCOUNT -> AccountScreen(account, container.financialRepository, participantType, signOut)
            }
        }
    }
}

@Composable
private fun DiscoverScreen(viewModel: DiscoveryViewModel, participantType: String) {
    val page by viewModel.page.collectAsStateWithLifecycle()
    val journey by viewModel.journeyState.collectAsStateWithLifecycle()
    var query by remember { mutableStateOf("") }
    var safetyTarget by remember { mutableStateOf<DiscoveryResult?>(null) }
    LaunchedEffect(Unit) { viewModel.load() }
    LegendScreen("Discover") {
        when (page) {
            LoadState.Idle,
            LoadState.Loading -> LegendLoadingState()

            is LoadState.Error -> LegendErrorState((page as LoadState.Error).message, viewModel::load)
            is LoadState.Data -> {
                val results = (page as LoadState.Data<DiscoveryPage>).value.results
                LazyColumn(
                    verticalArrangement = Arrangement.spacedBy(LegendSpacing.Sm),
                    contentPadding = PaddingValues(vertical = LegendSpacing.Sm),
                ) {
                    item {
                        Row(verticalAlignment = Alignment.CenterVertically) {
                            OutlinedTextField(
                                value = query,
                                onValueChange = { query = it },
                                modifier = Modifier.weight(1f),
                                label = { Text("Search LEGEND") },
                                singleLine = true,
                            )
                            IconButton(onClick = { viewModel.search(query) }) {
                                Icon(Icons.Default.Search, "Search")
                            }
                        }
                    }
                    if (participantType.equals("Client", ignoreCase = true)) {
                        item { JourneySummary(journey, viewModel::requestConnection) }
                    }
                    if (results.isEmpty()) {
                        item {
                            LegendEmptyState(
                                "No members found",
                                "The server-authorized LEGEND directory has no matching members.",
                            )
                        }
                    } else {
                        items(results, key = { it.clientProfileId }) { result ->
                            LegendCard {
                                Row(verticalAlignment = Alignment.CenterVertically) {
                                    LegendAvatar(result.displayName)
                                    Spacer(Modifier.width(10.dp))
                                    Column(Modifier.weight(1f)) {
                                        Text(result.displayName, style = MaterialTheme.typography.titleMedium)
                                        result.headline?.let { Text(it, color = LegendColors.TextSecondary) }
                                        result.location?.let { Text(it, style = MaterialTheme.typography.labelSmall, color = LegendColors.TextTertiary) }
                                    }
                                    if (result.isVerified) Icon(Icons.Default.Verified, "Verified", tint = LegendColors.Info)
                                    IconButton(onClick = { safetyTarget = result }) {
                                        Icon(Icons.Default.MoreVert, "Safety actions")
                                    }
                                }
                                result.matchExplanation?.let { Text(it, color = LegendColors.TextSecondary) }
                                if (result.relationship.canRequestConnection && participantType.equals("Client", ignoreCase = true)) {
                                    TextButton(onClick = { viewModel.requestConnection(result.clientProfileId) }) {
                                        Text("Request connection")
                                    }
                                }
                            }
                        }
                    }
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
                Column(verticalArrangement = Arrangement.spacedBy(8.dp)) {
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
private fun JourneySummary(
    state: LoadState<JourneyDashboard>,
    requestConnection: (String) -> Unit,
) {
    when (state) {
        LoadState.Idle,
        LoadState.Loading -> LegendCard { Text("Journey Circles", style = MaterialTheme.typography.titleLarge) }

        is LoadState.Error -> Unit
        is LoadState.Data -> {
            val dashboard = state.value
            LegendCard {
                Text("Journey Circles", style = MaterialTheme.typography.titleLarge)
                dashboard.profile?.let { Text("${it.displayName}'s circle", color = LegendColors.TextSecondary) }
                dashboard.recommendations.take(3).forEach { recommendation ->
                    Row(verticalAlignment = Alignment.CenterVertically) {
                        Column(Modifier.weight(1f)) {
                            Text(recommendation.profile.displayName, style = MaterialTheme.typography.titleMedium)
                            Text(recommendation.explanation, color = LegendColors.TextSecondary)
                        }
                        TextButton(onClick = { requestConnection(recommendation.profile.clientProfileId) }) {
                            Text("Connect")
                        }
                    }
                }
                if (dashboard.recommendations.isEmpty()) {
                    Text("Server-authorized connections and recommendations will appear here.", color = LegendColors.TextSecondary)
                }
            }
        }
    }
}

@Composable
private fun HomeScreen(viewModel: HomeViewModel) {
    val state by viewModel.state.collectAsStateWithLifecycle()
    LaunchedEffect(Unit) { viewModel.load() }
    LegendScreen("LEGEND®") {
        when (state) {
            LoadState.Idle,
            LoadState.Loading -> LegendLoadingState()

            is LoadState.Error -> LegendErrorState((state as LoadState.Error).message, viewModel::load)
            is LoadState.Data -> {
                val home = (state as LoadState.Data<MobileHomeResponse>).value
                LazyColumn(
                    verticalArrangement = Arrangement.spacedBy(LegendSpacing.Sm),
                    contentPadding = PaddingValues(vertical = LegendSpacing.Sm),
                ) {
                    item {
                        LegendCard {
                            Text("Welcome back, ${home.identity.displayName}", style = MaterialTheme.typography.headlineMedium)
                            Text(
                                "${home.messaging.unreadCount} unread messages across ${home.messaging.conversationCount} conversations",
                                color = LegendColors.TextSecondary,
                            )
                        }
                    }
                    item {
                        LegendCard {
                            Text(home.dailyScripture.reference, style = MaterialTheme.typography.titleLarge, color = LegendColors.Navy)
                            Text(home.dailyScripture.passageText, style = MaterialTheme.typography.bodyMedium)
                            Text(home.dailyScripture.translation, style = MaterialTheme.typography.labelLarge, color = LegendColors.Gold)
                        }
                    }
                    items(home.actions, key = { it.id }) { action ->
                        LegendCard {
                            Text(action.title, style = MaterialTheme.typography.titleMedium)
                            Text("${action.priority} · ${action.status}", color = LegendColors.TextSecondary)
                        }
                    }
                }
            }
        }
    }
}

@Composable
private fun MessagesScreen(viewModel: MessagingViewModel) {
    val conversations by viewModel.conversations.collectAsStateWithLifecycle()
    val messages by viewModel.messages.collectAsStateWithLifecycle()
    var selected by remember { mutableStateOf<ConversationSummary?>(null) }
    LaunchedEffect(Unit) { viewModel.load() }
    LaunchedEffect(selected?.id) {
        LegendRealtimeEvents.conversationUpdates.collectLatest { changed ->
            if (changed == null || changed == selected?.id) {
                viewModel.load()
                selected?.let { viewModel.open(it.id) }
            }
        }
    }

    if (selected == null) {
        LegendScreen("Messages") {
            when (conversations) {
                LoadState.Idle,
                LoadState.Loading -> LegendLoadingState()

                is LoadState.Error -> LegendErrorState((conversations as LoadState.Error).message, viewModel::load)
                is LoadState.Data -> {
                    val rows = (conversations as LoadState.Data<List<ConversationSummary>>).value
                    if (rows.isEmpty()) {
                        LegendEmptyState("No conversations", "Your authorized LEGEND conversations will appear here.")
                    } else {
                        LazyColumn {
                            items(rows, key = { it.id }) { row ->
                                ListItem(
                                    headlineContent = { Text(row.title) },
                                    supportingContent = {
                                        Text(
                                            row.lastMessagePreview ?: "No messages yet",
                                            maxLines = 1,
                                            overflow = TextOverflow.Ellipsis,
                                        )
                                    },
                                    leadingContent = { LegendAvatar(row.counterparty.displayName) },
                                    trailingContent = {
                                        if (row.unreadCount > 0) Badge { Text(row.unreadCount.toString()) }
                                    },
                                    modifier = Modifier.clickable {
                                        selected = row
                                        viewModel.open(row.id)
                                    },
                                )
                                HorizontalDivider()
                            }
                        }
                    }
                }
            }
        }
    } else {
        LegendScreen(
            title = selected!!.title,
            actions = {
                IconButton(onClick = { selected = null }) {
                    Icon(Icons.Default.Close, "Close conversation")
                }
            },
        ) {
            when (messages) {
                LoadState.Idle,
                LoadState.Loading -> LegendLoadingState()

                is LoadState.Error -> LegendErrorState((messages as LoadState.Error).message) {
                    viewModel.open(selected!!.id)
                }

                is LoadState.Data -> MessageThread(
                    messages = (messages as LoadState.Data<List<ConversationMessage>>).value,
                    conversationId = selected!!.id,
                    send = viewModel::send,
                )
            }
        }
    }
}

@Composable
private fun MessageThread(
    messages: List<ConversationMessage>,
    conversationId: String,
    send: (String, String) -> Unit,
) {
    var draft by remember { mutableStateOf("") }
    Column(Modifier.fillMaxSize()) {
        LazyColumn(Modifier.weight(1f), verticalArrangement = Arrangement.spacedBy(8.dp)) {
            items(messages, key = { it.id }) { message ->
                Row(
                    Modifier.fillMaxWidth(),
                    horizontalArrangement = if (message.isMine) Arrangement.End else Arrangement.Start,
                ) {
                    Surface(
                        color = if (message.isMine) LegendColors.Navy else LegendColors.SurfaceInset,
                        shape = LegendShapes.Control,
                    ) {
                        Column(Modifier.padding(10.dp)) {
                            Text(message.body, color = if (message.isMine) Color.White else LegendColors.TextPrimary)
                            message.originalBody?.takeIf { it != message.body }?.let {
                                Text("Original: $it", style = MaterialTheme.typography.labelSmall, color = LegendColors.TextSecondary)
                            }
                        }
                    }
                }
            }
        }
        Row(verticalAlignment = Alignment.CenterVertically) {
            OutlinedTextField(
                value = draft,
                onValueChange = { draft = it },
                modifier = Modifier.weight(1f),
                placeholder = { Text("Message") },
            )
            IconButton(
                onClick = { send(conversationId, draft); draft = "" },
                enabled = draft.isNotBlank(),
            ) {
                Icon(Icons.AutoMirrored.Filled.Send, "Send")
            }
        }
    }
}

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
    var collection by remember { mutableStateOf(SocialCollection.POSTS) }
    var creating by remember { mutableStateOf(false) }
    var commentingPostId by remember { mutableStateOf<String?>(null) }
    LaunchedEffect(Unit) { viewModel.load() }

    LegendScreen(
        title = "For You",
        actions = {
            IconButton(onClick = { creating = true }) {
                Icon(Icons.Default.Add, "Create LEGEND update", tint = LegendColors.Navy)
            }
        },
    ) {
        when (state) {
            LoadState.Idle,
            LoadState.Loading -> LegendLoadingState()

            is LoadState.Error -> LegendErrorState((state as LoadState.Error).message, viewModel::load)
            is LoadState.Data -> {
                val snapshot = (state as LoadState.Data<SocialSnapshot>).value
                val posts = when (collection) {
                    SocialCollection.POSTS -> snapshot.posts
                    SocialCollection.STORIES -> snapshot.stories
                    SocialCollection.HACS -> snapshot.hacs
                }
                Column {
                    Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                        SocialCollection.entries.forEach { item ->
                            FilterChip(
                                selected = collection == item,
                                onClick = { collection = item },
                                label = { Text(item.label) },
                            )
                        }
                    }
                    if (posts.isEmpty()) {
                        LegendEmptyState(
                            "No ${collection.label.lowercase()} yet",
                            "Server-authorized LEGEND content will appear here.",
                        )
                    } else {
                        LazyColumn(
                            verticalArrangement = Arrangement.spacedBy(LegendSpacing.Sm),
                            contentPadding = PaddingValues(vertical = LegendSpacing.Sm),
                        ) {
                            items(posts, key = { it.id }) { post ->
                                SocialPostCard(
                                    post = post,
                                    mediaRepository = mediaRepository,
                                    participantType = participantType,
                                    onReact = { viewModel.react(post.id) },
                                    onComment = { commentingPostId = post.id },
                                )
                            }
                        }
                    }
                }
            }
        }
    }

    if (creating) {
        CreatePostSheet(
            onDismiss = { creating = false },
            createText = { type, text, audience ->
                viewModel.create(type, text, audience)
                creating = false
            },
            createMedia = { uris, type, text, audience ->
                viewModel.createMedia(context, uris, type, text, audience)
                creating = false
            },
        )
    }
    commentingPostId?.let { postId ->
        CommentDialog(
            onDismiss = { commentingPostId = null },
            submit = { body ->
                viewModel.comment(postId, body)
                commentingPostId = null
            },
        )
    }
}

@Composable
private fun SocialPostCard(
    post: SocialPost,
    mediaRepository: AuthenticatedMediaRepository,
    participantType: String,
    onReact: () -> Unit,
    onComment: () -> Unit,
) {
    LegendCard(Modifier.fillMaxWidth()) {
        Row(verticalAlignment = Alignment.CenterVertically) {
            LegendAvatar(post.author.displayName)
            Spacer(Modifier.width(10.dp))
            Column(Modifier.weight(1f)) {
                Text(post.author.displayName, style = MaterialTheme.typography.titleMedium)
                post.author.username?.let { Text("@$it", style = MaterialTheme.typography.labelLarge, color = LegendColors.TextSecondary) }
            }
            if (post.author.isVerified) Icon(Icons.Default.Verified, "Verified", tint = LegendColors.Info)
        }
        if (post.body.isNotBlank()) Text(post.body, style = MaterialTheme.typography.bodyLarge)
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
            Text(
                "${post.media.size} ${if (post.media.size == 1) "media item" else "media items"} · ${post.media.first().processingState}",
                color = LegendColors.TextSecondary,
            )
        }
        Row {
            TextButton(onClick = onReact) {
                Icon(
                    if (post.reactedByCurrentActor) Icons.Default.Favorite else Icons.Default.FavoriteBorder,
                    "React",
                    tint = LegendColors.Gold,
                )
                Spacer(Modifier.width(4.dp))
                Text(post.reactionCount.toString())
            }
            TextButton(onClick = onComment, enabled = post.commentsEnabled) {
                Icon(Icons.Default.ChatBubbleOutline, "Comments")
                Spacer(Modifier.width(4.dp))
                Text(post.commentCount.toString())
            }
        }
    }
}

@Composable
private fun CreatePostSheet(
    onDismiss: () -> Unit,
    createText: (String, String, String?) -> Unit,
    createMedia: (List<Uri>, String, String, String) -> Unit,
) {
    var contentType by remember { mutableStateOf("Post") }
    var body by remember { mutableStateOf("") }
    var audience by remember { mutableStateOf("Public") }
    var selected by remember { mutableStateOf<List<Uri>>(emptyList()) }
    val picker = rememberLauncherForActivityResult(ActivityResultContracts.PickMultipleVisualMedia()) {
        selected = it
    }

    ModalBottomSheet(onDismissRequest = onDismiss, containerColor = LegendColors.Midnight) {
        Column(
            Modifier.fillMaxWidth().padding(20.dp),
            verticalArrangement = Arrangement.spacedBy(12.dp),
        ) {
            Text("Create a LEGEND update", color = Color.White, style = MaterialTheme.typography.headlineMedium)
            Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                listOf("Post", "Story", "Hac").forEach { choice ->
                    FilterChip(
                        selected = contentType == choice,
                        onClick = { contentType = choice },
                        label = { Text(choice) },
                    )
                }
            }
            OutlinedTextField(
                value = body,
                onValueChange = { body = it },
                label = { Text("Caption") },
                modifier = Modifier.fillMaxWidth(),
                minLines = 4,
            )
            OutlinedTextField(
                value = audience,
                onValueChange = { audience = it },
                label = { Text("Audience") },
                modifier = Modifier.fillMaxWidth(),
            )
            OutlinedButton(
                onClick = {
                    picker.launch(PickVisualMediaRequest(ActivityResultContracts.PickVisualMedia.ImageAndVideo))
                },
                modifier = Modifier.fillMaxWidth(),
            ) {
                Icon(Icons.Default.PermMedia, null)
                Spacer(Modifier.width(8.dp))
                Text(if (selected.isEmpty()) "Add photo or video" else "${selected.size} media item(s) selected")
            }
            Button(
                onClick = {
                    if (selected.isEmpty()) createText(contentType, body, audience)
                    else createMedia(selected, contentType, body, audience)
                },
                enabled = body.isNotBlank() || selected.isNotEmpty(),
                modifier = Modifier.fillMaxWidth(),
                colors = ButtonDefaults.buttonColors(containerColor = LegendColors.Gold),
            ) {
                Text(if (selected.isEmpty()) "Publish" else "Upload and publish")
            }
            Text(
                "Selected media streams to the existing server /social/posts/media lifecycle. Android does not transcode, host, or process social media.",
                color = LegendColors.GoldSoft,
                style = MaterialTheme.typography.labelSmall,
            )
        }
    }
}

@Composable
private fun CommentDialog(onDismiss: () -> Unit, submit: (String) -> Unit) {
    var body by remember { mutableStateOf("") }
    AlertDialog(
        onDismissRequest = onDismiss,
        title = { Text("Comment") },
        text = {
            OutlinedTextField(
                value = body,
                onValueChange = { body = it },
                label = { Text("Your comment") },
                modifier = Modifier.fillMaxWidth(),
            )
        },
        confirmButton = {
            TextButton(onClick = { submit(body) }, enabled = body.isNotBlank()) { Text("Post") }
        },
        dismissButton = { TextButton(onClick = onDismiss) { Text("Cancel") } },
    )
}

@Composable
private fun AccountScreen(
    viewModel: AccountViewModel,
    financialRepository: FinancialRepository,
    participantType: String,
    signOut: () -> Unit,
) {
    val profile by viewModel.profile.collectAsStateWithLifecycle()
    val lifecycle by viewModel.lifecycle.collectAsStateWithLifecycle()
    var financial by remember { mutableStateOf(false) }
    var deletePrompt by remember { mutableStateOf(false) }
    var languageAccount by remember { mutableStateOf<MobileAccountProfile?>(null) }
    LaunchedEffect(Unit) { viewModel.load() }

    if (financial) {
        FinancialScreen(financialRepository, participantType) { financial = false }
    } else {
        LegendScreen(
            title = "Account",
            actions = {
                IconButton(onClick = signOut) { Icon(Icons.AutoMirrored.Filled.Logout, "Sign out") }
            },
        ) {
            when (profile) {
                LoadState.Idle,
                LoadState.Loading -> LegendLoadingState()

                is LoadState.Error -> LegendErrorState((profile as LoadState.Error).message, viewModel::load)
                is LoadState.Data -> {
                    val account = (profile as LoadState.Data<MobileAccountProfile>).value
                    LazyColumn(
                        verticalArrangement = Arrangement.spacedBy(LegendSpacing.Sm),
                        contentPadding = PaddingValues(vertical = LegendSpacing.Sm),
                    ) {
                        item {
                            LegendCard {
                                Row(verticalAlignment = Alignment.CenterVertically) {
                                    LegendAvatar(account.displayName)
                                    Spacer(Modifier.width(12.dp))
                                    Column {
                                        Text(account.displayName, style = MaterialTheme.typography.headlineMedium)
                                        account.username?.let { Text("@$it", color = LegendColors.TextSecondary) }
                                    }
                                }
                            }
                        }
                        item {
                            LegendCard(Modifier.clickable { financial = true }) {
                                Text("Financial intelligence", style = MaterialTheme.typography.titleLarge)
                                Text("Server-authoritative projections and priorities", color = LegendColors.TextSecondary)
                            }
                        }
                        item {
                            LegendCard(Modifier.clickable { languageAccount = account }) {
                                Text("Language preferences", style = MaterialTheme.typography.titleLarge)
                                Text(
                                    account.translationAccess?.preferredCommunicationLanguage
                                        ?: "No preferred communication language set",
                                    color = LegendColors.TextSecondary,
                                )
                                Text(
                                    "Message translation is server-only.",
                                    color = LegendColors.Gold,
                                    style = MaterialTheme.typography.labelLarge,
                                )
                                Text("Edit", color = LegendColors.Gold, style = MaterialTheme.typography.labelLarge)
                            }
                        }
                        item {
                            LegendCard {
                                Text("Privacy & safety", style = MaterialTheme.typography.titleLarge)
                                Text(
                                    if (account.isPrivate) "Private profile" else "Public profile",
                                    color = LegendColors.TextSecondary,
                                )
                                Text(
                                    "Blocking and reporting are enforced by the community-safety service.",
                                    color = LegendColors.TextSecondary,
                                )
                                TextButton(onClick = { viewModel.updatePrivacy(!account.isPrivate) }) {
                                    Text(if (account.isPrivate) "Make profile public" else "Make profile private")
                                }
                            }
                        }
                        item {
                            LegendCard {
                                Text("Account lifecycle", style = MaterialTheme.typography.titleLarge)
                                (lifecycle as? LoadState.Data<AccountLifecycle>)?.value?.let {
                                    Text(it.state, color = LegendColors.TextSecondary)
                                }
                                TextButton(onClick = { deletePrompt = true }) {
                                    Text("Request account deletion", color = LegendColors.Error)
                                }
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
    LaunchedEffect(Unit) { viewModel.load() }
    LegendScreen(
        title = "Financial intelligence",
        actions = { IconButton(onClick = back) { Icon(Icons.Default.Close, "Back") } },
    ) {
        when (state) {
            LoadState.Idle,
            LoadState.Loading -> LegendLoadingState()

            is LoadState.Error -> LegendErrorState((state as LoadState.Error).message, viewModel::load)
            is LoadState.Data -> {
                val snapshot = (state as LoadState.Data<FinancialSnapshot>).value
                val priorities = snapshot.presentation?.prioritySections.orEmpty()
                if (priorities.isEmpty()) {
                    LegendEmptyState(
                        "No financial priorities",
                        "When the server has a current projection, it will appear here.",
                    )
                } else {
                    LazyColumn(
                        verticalArrangement = Arrangement.spacedBy(LegendSpacing.Sm),
                        contentPadding = PaddingValues(vertical = LegendSpacing.Sm),
                    ) {
                        items(priorities, key = { it.key }) { item ->
                            LegendCard {
                                Text(item.eyebrow.uppercase(), color = LegendColors.Gold, style = MaterialTheme.typography.labelLarge)
                                Text(item.title, style = MaterialTheme.typography.titleLarge)
                                Text(item.reason, color = LegendColors.TextSecondary)
                                Text(item.discussionPrompt, style = MaterialTheme.typography.bodyMedium)
                            }
                        }
                    }
                }
            }
        }
    }
}
