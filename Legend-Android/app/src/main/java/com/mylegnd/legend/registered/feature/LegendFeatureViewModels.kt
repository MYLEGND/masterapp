package com.mylegnd.legend.registered.feature

import androidx.lifecycle.ViewModel
import androidx.lifecycle.viewModelScope
import com.mylegnd.legend.registered.core.model.*
import com.mylegnd.legend.registered.core.network.DiscoveryPage
import com.mylegnd.legend.registered.core.network.DiscoveryResult
import com.mylegnd.legend.registered.core.network.DiscoveryProfile
import com.mylegnd.legend.registered.core.network.JourneyDashboard
import com.mylegnd.legend.registered.core.network.NotificationItem
import com.mylegnd.legend.registered.core.network.NotificationBadge
import com.mylegnd.legend.registered.core.network.NotificationSnapshot
import com.mylegnd.legend.registered.core.network.SocialViewRequest
import com.mylegnd.legend.registered.core.realtime.LegendMessagingRealtimeEvent
import com.mylegnd.legend.registered.data.*
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.Deferred
import kotlinx.coroutines.async
import kotlinx.coroutines.Job
import kotlinx.coroutines.ensureActive
import kotlinx.coroutines.cancelChildren
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.update
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.collect
import kotlinx.coroutines.launch
import android.content.Context
import android.net.Uri
import com.mylegnd.legend.registered.core.media.ProfileAvatarPreparer
import java.util.UUID
import java.time.Instant

data class FounderAiTranscriptMessage(
    val role: String,
    val content: String,
    val responseAuthority: String? = null,
)

data class FounderAiConversationState(
    val availability: LoadState<Boolean> = LoadState.Idle,
    val messages: List<FounderAiTranscriptMessage> = emptyList(),
    val isSending: Boolean = false,
    val operationId: String? = null,
    val progress: String? = null,
    val failure: String? = null,
)

/**
 * Android is a client of the same Founder-only conversation authority as web
 * and iOS. It never selects a provider or implements a responder locally.
 */
class FounderAiViewModel(
    private val repository: FounderAiRepository,
    private val role: String,
) : ViewModel() {
    private val _state = MutableStateFlow(FounderAiConversationState())
    val state: StateFlow<FounderAiConversationState> = _state.asStateFlow()
    private var conversationId = UUID.randomUUID().toString()
    private var operation: Job? = null

    fun resolveAvailability() {
        if (_state.value.availability !is LoadState.Idle) return
        viewModelScope.launch {
            _state.value = _state.value.copy(availability = LoadState.Loading)
            _state.value = _state.value.copy(
                availability = when (val result = repository.access(role)) {
                    is LoadState.Data -> LoadState.Data(result.value.available)
                    is LoadState.Error -> LoadState.Error(result.message)
                    else -> LoadState.Error("Founder AI availability could not be determined.")
                },
            )
        }
    }

    fun send(
        rawText: String,
        mode: String,
        nativeOnly: Boolean,
        sourceLanguageCode: String? = null,
    ) {
        val text = rawText.trim()
        if (text.isBlank() || _state.value.isSending || mode !in setOf("legend", "teacher")) return
        if ((_state.value.availability as? LoadState.Data)?.value != true) return

        val operationId = UUID.randomUUID().toString()
        val submitted = _state.value.messages + FounderAiTranscriptMessage("user", text)
        _state.value = _state.value.copy(
            messages = submitted,
            isSending = true,
            operationId = operationId,
            progress = "Preparing governed conversation…",
            failure = null,
        )
        operation = viewModelScope.launch {
            try {
                when (val result = repository.chat(
                    role = role,
                    operationId = operationId,
                    chatRequest = FounderAiChatRequest(
                        mode = mode,
                        nativeOnly = mode == "legend" && nativeOnly,
                        // Free-form input has no client-owned detector. Carry
                        // a code only when a governed upstream selection knows
                        // it; otherwise the server must identify the prompt.
                        sourceLanguageCode = sourceLanguageCode,
                        messages = submitted.map { FounderAiChatMessage(it.role, it.content) },
                        conversationId = conversationId,
                    ),
                    onProgress = { envelope ->
                        val update = envelope.progress?.message?.trim().orEmpty()
                        if (update.isNotBlank()) {
                            _state.update { current ->
                                if (current.operationId != operationId) current else current.copy(
                                    progress = envelope.elapsedSeconds?.let { "$update · ${it}s" } ?: update,
                                )
                            }
                        }
                    },
                )) {
                    is LoadState.Data -> {
                        val response = result.value
                        if (_state.value.operationId != operationId) return@launch
                        if (response.succeeded && !response.message.isNullOrBlank()) {
                            _state.value = _state.value.copy(
                                messages = _state.value.messages + FounderAiTranscriptMessage(
                                    role = "assistant",
                                    content = response.message,
                                    responseAuthority = response.responseAuthority,
                                ),
                            )
                        } else {
                            _state.value = _state.value.copy(failure = response.safeFailure())
                        }
                    }
                    is LoadState.Error -> if (_state.value.operationId == operationId) {
                        _state.value = _state.value.copy(failure = result.message)
                    }
                    else -> Unit
                }
            } catch (_: CancellationException) {
                // cancel() has already returned a truthful local status. The
                // server receives the cancelled mobile request and stops work.
            } finally {
                if (_state.value.operationId == operationId) {
                    _state.value = _state.value.copy(isSending = false, operationId = null, progress = null)
                }
            }
        }
    }

    fun cancel() {
        val active = operation ?: return
        if (!active.isActive) return
        active.cancel()
        operation = null
        _state.value = _state.value.copy(
            isSending = false,
            operationId = null,
            progress = null,
            failure = "Response stopped. Your draft remains available.",
        )
    }

    fun startNewConversation() {
        if (_state.value.isSending) return
        conversationId = UUID.randomUUID().toString()
        _state.value = _state.value.copy(messages = emptyList(), failure = null, progress = null)
    }

    private fun FounderAiChatResponse.safeFailure(): String = buildList {
        error?.trim()?.takeIf(String::isNotBlank)?.let(::add)
        if (failureKind?.isNotBlank() == true) add("Stage: ${stage ?: failureKind}.")
        reason?.trim()?.takeIf(String::isNotBlank)?.let { add("Reason: $it.") }
        reference?.trim()?.takeIf(String::isNotBlank)?.let { add("Reference: $it.") }
    }.ifEmpty { listOf("The Founder AI request did not produce a response.") }.joinToString(" ")
}

class HomeViewModel(private val repository: HomeRepository, private val role: String) : ViewModel() {
    private val _state = MutableStateFlow<LoadState<MobileHomeResponse>>(LoadState.Idle)
    val state: StateFlow<LoadState<MobileHomeResponse>> = _state.asStateFlow()

    fun load() = viewModelScope.launch {
        _state.value = LoadState.Loading
        _state.value = repository.load(role)
    }

    /** Rehydrates the server-owned home aggregate without interrupting the active surface. */
    fun refreshForRealtime() = viewModelScope.launch {
        when (val fresh = repository.load(role)) {
            is LoadState.Data -> _state.value = fresh
            else -> Unit
        }
    }
}
class AgentWorkspaceViewModel(private val repository: AgentWorkspaceRepository, private val role: String) : ViewModel() {
    private val _bookingRevision = MutableStateFlow(0)
    val bookingRevision = _bookingRevision.asStateFlow()
    suspend fun bookingAccess(profileId: String) = repository.bookingAccess(role, profileId)
    suspend fun bookingLaunch(profileId: String) = repository.bookingLaunch(role, profileId)
    fun bookingClosed() { _bookingRevision.value++; load() }
    suspend fun contact(kind: String, id: String, input: MobileCrmContactInput) = repository.contact(role, kind, id, input)
    suspend fun outcome(kind: String, id: String, code: String, note: String) = repository.outcome(role, kind, id, code, note)
    suspend fun cancelAppointment(id: String) = repository.cancelAppointment(role, id)
    suspend fun schedule() = repository.schedule(role)
    suspend fun restoreClient(id: String) = repository.restoreClient(role, id).also { if (it is LoadState.Data) load() }
    suspend fun record(kind: String, id: String) = repository.record(role, kind, id)
    private val _clients = MutableStateFlow<LoadState<List<MobileAgentClient>>>(LoadState.Idle)
    val clients: StateFlow<LoadState<List<MobileAgentClient>>> = _clients.asStateFlow()
    private val _leads = MutableStateFlow<LoadState<List<MobileAgentLead>>>(LoadState.Idle)
    val leads: StateFlow<LoadState<List<MobileAgentLead>>> = _leads.asStateFlow()
    private val _clientCreationPortal = MutableStateFlow<LoadState<MobileClientCreationPortalLaunch>>(LoadState.Idle)
    val clientCreationPortal: StateFlow<LoadState<MobileClientCreationPortalLaunch>> = _clientCreationPortal.asStateFlow()

    fun load() = viewModelScope.launch {
        if (!role.equals("Agent", ignoreCase = true)) return@launch
        _clients.value = LoadState.Loading
        _leads.value = LoadState.Loading
        _clients.value = repository.clients(role)
        _leads.value = repository.leads(role)
    }

    fun launchClientCreationPortal() = viewModelScope.launch {
        if (!role.equals("Agent", ignoreCase = true)) return@launch
        _clientCreationPortal.value = LoadState.Loading
        _clientCreationPortal.value = repository.clientCreationPortalLaunch(role)
    }

    fun clearClientCreationPortal() {
        _clientCreationPortal.value = LoadState.Idle
    }
}
class FinancialViewModel(private val repository: FinancialRepository, private val role: String) : ViewModel() {
    private val _state = MutableStateFlow<LoadState<FinancialSnapshot>>(LoadState.Idle)
    val state: StateFlow<LoadState<FinancialSnapshot>> = _state.asStateFlow()
    private var refreshJob: Job? = null

    fun load(): Job {
        refreshJob?.takeIf { it.isActive }?.let { return it }
        return viewModelScope.launch {
            if (_state.value !is LoadState.Data) _state.value = LoadState.Loading
            _state.value = repository.load(role)
        }.also { refreshJob = it }
    }
}

/** Pending presentation transaction: only server acknowledgements advance it. */
internal class PendingMessageSubmission private constructor(
    val conversationId: String,
    val body: String,
    val replyToMessageId: String?,
    val attachments: List<String>,
    val sharedPostId: String?,
) {
    val clientMessageId: String = UUID.randomUUID().toString()
    var acknowledgedMessageId: String? = null
    val uploadedAttachmentIndexes = mutableSetOf<Int>()

    suspend fun uploadRemaining(upload: suspend (Int) -> LoadState<*>): LoadState<Unit> {
        if (acknowledgedMessageId == null) return LoadState.Error("Message has not been acknowledged.")
        for (index in attachments.indices) {
            if (index in uploadedAttachmentIndexes) continue
            when (val result = upload(index)) {
                is LoadState.Data -> uploadedAttachmentIndexes.add(index)
                is LoadState.Error -> return result
                else -> return LoadState.Error("Attachment upload was not acknowledged.")
            }
        }
        return LoadState.Data(Unit)
    }

    companion object {
        fun forPayload(previous: PendingMessageSubmission?, conversationId: String, body: String,
                       replyToMessageId: String?, attachments: List<String>, sharedPostId: String? = null): PendingMessageSubmission =
            previous?.takeIf { it.conversationId == conversationId && it.body == body &&
                it.replyToMessageId == replyToMessageId && it.attachments == attachments && it.sharedPostId == sharedPostId }
                ?: PendingMessageSubmission(conversationId, body, replyToMessageId, attachments.toList(), sharedPostId)
    }
}

class MessagingViewModel(private val repository: MessagingRepository, private val role: String) : ViewModel() {
    companion object {
        internal fun sessionKey(accountId: String, identity: MobileIdentity): String =
            "messaging:" + listOf(accountId, identity.userId, identity.participantType)
                .joinToString(":") { "${it.length}:$it" }
    }

    fun deactivate() {
        viewModelScope.coroutineContext.cancelChildren()
        // Keep an uncertain send identity in this account-keyed ViewModel.
        // Returning to the same account may retry it; another actor gets a
        // different ViewModel. The cancelled send owns its final busy reset.
        selectedConversationId = null
        detailJob = null
        inboxTask = null
        readJobs.clear()
        readAcknowledgements.clear()
        detailCache.clear()
        presentationRevision++
        inboxRequestRevision++
        _conversations.value = LoadState.Idle
        _detail.value = LoadState.Idle
        _recipients.value = LoadState.Idle
        _historyFailure.value = null
    }

    private var pendingSubmission: PendingMessageSubmission? = null
    private val _conversations = MutableStateFlow<LoadState<List<ConversationSummary>>>(LoadState.Idle)
    val conversations: StateFlow<LoadState<List<ConversationSummary>>> = _conversations.asStateFlow()
    private val _detail = MutableStateFlow<LoadState<ConversationDetail>>(LoadState.Idle)
    val detail: StateFlow<LoadState<ConversationDetail>> = _detail.asStateFlow()
    private val _recipients = MutableStateFlow<LoadState<List<MessagingRecipient>>>(LoadState.Idle)
    val recipients: StateFlow<LoadState<List<MessagingRecipient>>> = _recipients.asStateFlow()
    private val _isSending = MutableStateFlow(false)
    val isSending: StateFlow<Boolean> = _isSending.asStateFlow()
    private var selectedConversationId: String? = null
    private var presentationRevision = 0L
    private var inboxRequestRevision = 0L

    fun load() = viewModelScope.launch {
        if (_conversations.value !is LoadState.Data) _conversations.value = LoadState.Loading
        refreshInboxSilently()
    }


    fun loadMore() = viewModelScope.launch {
        val current = (_conversations.value as? LoadState.Data)?.value ?: return@launch
        val revision = inboxRequestRevision
        when (val next = repository.conversations(role, skip = current.size)) {
            is LoadState.Data -> if (revision == inboxRequestRevision) _conversations.value = LoadState.Data((current + next.value).distinctBy { it.id })
            is LoadState.Error -> Unit
            else -> Unit
        }
    }

    // Presentation snapshots belong to this account/profile ViewModel only.
    private val detailCache = object : LinkedHashMap<String, ConversationDetail>(12, 0.75f, true) {
        override fun removeEldestEntry(eldest: MutableMap.MutableEntry<String, ConversationDetail>?) = size > 12
    }
    private var detailJob: Job? = null
    private var detailRefreshPending = false
    private var detailMarksRead = false
    private var inboxTask: Deferred<Unit>? = null
    private var inboxRefreshPending = false
    private val readJobs = mutableMapOf<String, Job>()
    private val readAcknowledgements = mutableMapOf<String, String>()

    fun open(id: String, beforeUtc: String? = null): Job = requestDetail(id, beforeUtc, marksRead = true)

    private fun requestDetail(id: String, beforeUtc: String? = null, marksRead: Boolean, newerActivity: Boolean = false): Job {
        if (newerActivity) detailCache.remove(id)
        if (selectedConversationId == id && detailJob?.isActive == true) {
            detailMarksRead = detailMarksRead || marksRead
            detailRefreshPending = detailRefreshPending || newerActivity
            return requireNotNull(detailJob)
        }
        detailJob?.cancel()
        selectedConversationId = id
        _historyFailure.value = null
        val revision = ++presentationRevision
        detailMarksRead = marksRead
        if ((_detail.value as? LoadState.Data)?.value?.id != id) {
            _detail.value = detailCache[id]?.let { LoadState.Data(it) } ?: LoadState.Loading
        }
        return viewModelScope.launch {
            do {
                detailRefreshPending = false
                val result = repository.conversation(role, id, beforeUtc)
                ensureActive()
                if (revision != presentationRevision || selectedConversationId != id) return@launch
                if (result is LoadState.Data) {
                    detailCache[id] = result.value
                    _detail.value = result
                    if (detailMarksRead) acknowledgeVisible(id, result.value)
                } else if (result is LoadState.Error && result.status in setOf(401, 403, 404, 410)) {
                    detailCache.remove(id)
                    _detail.value = result
                } else if (_detail.value !is LoadState.Data) _detail.value = result
            } while (detailRefreshPending)
        }.also { detailJob = it }
    }

    private fun acknowledgeVisible(id: String, detail: ConversationDetail) {
        val latest = detail.messages.lastOrNull()?.id ?: return
        if (readAcknowledgements[id] == latest || readJobs[id]?.isActive == true) return
        readJobs[id] = viewModelScope.launch {
            if (repository.markRead(role, id, latest) is LoadState.Data) {
                readAcknowledgements[id] = latest
                if ((_detail.value as? LoadState.Data)?.value?.messages?.lastOrNull()?.id == latest)
                    updateInbox(id) { it.copy(unreadCount = 0) }
            }
            readJobs.remove(id)
            val current = (_detail.value as? LoadState.Data)?.value
            if (selectedConversationId == id && current?.id == id && current.messages.lastOrNull()?.id != latest) {
                acknowledgeVisible(id, current)
            }
        }
    }

    private val _historyFailure = MutableStateFlow<String?>(null)
    val historyFailure = _historyFailure.asStateFlow()
    private var historyJob: Job? = null

    fun loadOlder() {
        if (historyJob?.isActive == true) return
        _historyFailure.value = null
        val current = (_detail.value as? LoadState.Data)?.value ?: return
        val oldest = current.messages.firstOrNull() ?: return
        if (!current.hasOlderMessages) return
        historyJob = viewModelScope.launch {
            val revision = presentationRevision
            when (val page = repository.conversation(role, current.id, oldest.sentUtc, oldest.id)) {
                is LoadState.Data -> {
                    if (revision != presentationRevision || selectedConversationId != current.id) return@launch
                    val latest = (_detail.value as? LoadState.Data)?.value?.takeIf { it.id == current.id } ?: return@launch
                    val currentById = latest.messages.associateBy { it.id }
                    val merged = (page.value.messages + latest.messages)
                        .distinctBy { it.id }
                        .map { currentById[it.id] ?: it }
                        .sortedBy { it.sentUtc }
                    _detail.value = LoadState.Data(latest.copy(messages = merged, hasOlderMessages = page.value.hasOlderMessages))
                }
                is LoadState.Error -> if (revision == presentationRevision && selectedConversationId == current.id) {
                    if (page.status in setOf(401, 403, 404, 410)) {
                        detailCache.remove(current.id)
                        _detail.value = page
                    } else _historyFailure.value = page.message
                }
                else -> Unit
            }
        }
    }

    private var recipientSearchJob: Job? = null
    private var recipientSearchGeneration = 0L
    fun loadRecipients(search: String? = null, scope: String? = null) {
        recipientSearchJob?.cancel()
        val generation = ++recipientSearchGeneration
        _recipients.value = LoadState.Loading
        recipientSearchJob = viewModelScope.launch {
            if (!search.isNullOrBlank()) kotlinx.coroutines.delay(250)
            val result = repository.recipients(role, search, scope)
            ensureActive()
            if (generation == recipientSearchGeneration) _recipients.value = result
        }
    }

    fun startConversation(recipient: MessagingRecipient, opened: (String) -> Unit) = viewModelScope.launch {
        if (_isSending.value) return@launch
        _isSending.value = true
        val openedConversationId: String?
        try {
            openedConversationId = beginConversation(recipient)
        } finally {
            _isSending.value = false
        }
        // A caller may immediately invoke the canonical send path. Invoke its
        // continuation only after the in-flight start state is released.
        openedConversationId?.let(opened)
    }

    /**
     * Matches iOS: a CRM profile is never promoted to a messaging identity on
     * the device. Resolve it through the existing clients recipient scope,
     * then use the one canonical conversation start path.
     */
    fun startConversationForClient(profileId: String, opened: (String) -> Unit) = viewModelScope.launch {
        if (_isSending.value) return@launch
        _isSending.value = true
        val openedConversationId: String?
        try {
            openedConversationId = when (val recipients = repository.recipients(role, scope = "clients")) {
                is LoadState.Data -> {
                    val recipient = recipients.value.singleOrNull {
                        it.profileId == profileId &&
                            it.identity.participantType.equals("Client", ignoreCase = true)
                    }
                    if (recipient == null) {
                        _recipients.value = LoadState.Error("That client is no longer available to message.")
                        null
                    } else {
                        beginConversation(recipient)
                    }
                }
                is LoadState.Error -> {
                    _recipients.value = recipients
                    null
                }
                else -> null
            }
        } finally {
            _isSending.value = false
        }
        openedConversationId?.let(opened)
    }

    private suspend fun beginConversation(recipient: MessagingRecipient): String? =
        when (val result = repository.startConversation(role, recipient)) {
            is LoadState.Data -> {
                selectedConversationId = result.value.id
                ++presentationRevision
                _detail.value = result
                viewModelScope.launch { refreshInboxSilently() }
                result.value.id
            }
            is LoadState.Error -> {
                _recipients.value = LoadState.Error(result.message)
                null
            }
            else -> null
        }

    fun createGroup(
        context: Context,
        subject: String,
        recipients: List<MessagingRecipient>,
        image: Uri? = null,
        opened: (String) -> Unit,
    ) = viewModelScope.launch {
        val normalizedSubject = subject.trim()
        if (normalizedSubject.isBlank() || recipients.size < 2 || _isSending.value) return@launch
        _isSending.value = true
        try {
            val groupImage = image?.let { uri ->
                runCatching {
                    MessagingGroupImageRequest(
                        contentType = "image/jpeg",
                        base64Content = ProfileAvatarPreparer.base64Jpeg(
                            context = context,
                            uri = uri,
                            maximumBytes = 512 * 1024,
                            maximumDimension = 640,
                            label = "group photo",
                        ),
                    )
                }.getOrElse {
                    _recipients.value = LoadState.Error(it.message ?: "Choose another group photo.")
                    return@launch
                }
            }
            val request = CreateMessagingGroupRequest(
                subject = normalizedSubject,
                participants = recipients.map { MessagingGroupParticipantRequest(it.identity.userId, it.identity.participantType) },
                groupImage = groupImage,
            )
            when (val result = repository.createGroup(role, request)) {
                is LoadState.Data -> {
                    _detail.value = result
                    detailCache[result.value.id] = result.value
                    opened(result.value.id)
                    launch { refreshInboxSilently() }
                }
                is LoadState.Error -> _recipients.value = LoadState.Error(result.message)
                else -> Unit
            }
        } finally {
            _isSending.value = false
        }
    }

    fun addGroupParticipant(conversationId: String, recipient: MessagingRecipient) = viewModelScope.launch {
        detailCache.remove(conversationId)
        if (repository.addParticipant(role, conversationId, MessagingGroupParticipantRequest(recipient.identity.userId, recipient.identity.participantType)) is LoadState.Data) open(conversationId)
    }

    fun updateGroup(conversationId: String, subject: String, meeting: MessagingGroupMeetingRequest? = null) = viewModelScope.launch {
        detailCache.remove(conversationId)
        val normalizedSubject = subject.trim()
        if (normalizedSubject.isNotBlank() && repository.updateGroup(role, conversationId, UpdateMessagingGroupRequest(normalizedSubject, meeting = meeting)) is LoadState.Data) open(conversationId)
    }

    fun updateGroupImage(context: Context, conversationId: String, subject: String, image: Uri) = viewModelScope.launch {
        detailCache.remove(conversationId)
        _isSending.value = true
        try {
            val prepared = runCatching {
                MessagingGroupImageRequest(
                    contentType = "image/jpeg",
                    base64Content = ProfileAvatarPreparer.base64Jpeg(
                        context = context,
                        uri = image,
                        maximumBytes = 512 * 1024,
                        maximumDimension = 640,
                        label = "group photo",
                    ),
                )
            }.getOrElse {
                _detail.value = LoadState.Error(it.message ?: "Choose another group photo.")
                return@launch
            }
            if (repository.updateGroup(role, conversationId, UpdateMessagingGroupRequest(subject.trim(), groupImage = prepared)) is LoadState.Data) open(conversationId)
        } finally {
            _isSending.value = false
        }
    }

    fun setGroupManager(conversationId: String, participant: MobileParticipant, isManager: Boolean) = viewModelScope.launch {
        detailCache.remove(conversationId)
        if (repository.setGroupCollaborator(role, conversationId, participant, isManager) is LoadState.Data) open(conversationId)
    }

    fun setGroupPromotion(conversationId: String, isPromoted: Boolean) = viewModelScope.launch {
        detailCache.remove(conversationId)
        when (val result = repository.setGroupPromotion(role, conversationId, isPromoted)) {
            is LoadState.Data -> { _detail.value = result; refreshInboxSilently() }
            is LoadState.Error -> _detail.value = LoadState.Error(result.message)
            else -> Unit
        }
    }

    fun deleteGroup(conversationId: String, completed: () -> Unit) = viewModelScope.launch {
        detailCache.remove(conversationId)
        if (repository.deleteGroup(role, conversationId) is LoadState.Data) {
            updateInbox(conversationId) { null }
            _detail.value = LoadState.Idle
            completed()
        }
    }

    fun resolveVerification(review: VerificationReview, approve: Boolean, note: String? = null) = viewModelScope.launch {
        if (review.canResolve && repository.resolveVerification(role, review.id, approve, note) is LoadState.Data) {
            (_detail.value as? LoadState.Data)?.value?.id?.let(::open)
        }
    }

    fun send(
        context: Context,
        id: String,
        body: String,
        replyToMessageId: String? = null,
        attachmentUris: List<Uri> = emptyList(),
    ) = send(context, id, body, replyToMessageId, attachmentUris) { }

    /**
     * Presentation feedback only. The canonical MessagingRepository still owns
     * conversation creation, send, attachment upload, and inbox reconciliation.
     */
    fun send(
        context: Context,
        id: String,
        body: String,
        replyToMessageId: String? = null,
        attachmentUris: List<Uri> = emptyList(),
        sharedPostId: String? = null,
        completed: (Boolean) -> Unit,
    ) = viewModelScope.launch {
        val normalized = body.trim()
        if ((normalized.isBlank() && sharedPostId == null) || _isSending.value) {
            completed(false)
            return@launch
        }
        val submission = PendingMessageSubmission.forPayload(pendingSubmission, id, normalized,
            replyToMessageId, attachmentUris.map { it.toString() }, sharedPostId)
        pendingSubmission = submission
        _historyFailure.value = null
        _isSending.value = true
        try {
            if (submission.acknowledgedMessageId == null) {
                when (val result = repository.send(role, id, normalized, replyToMessageId, submission.clientMessageId, sharedPostId)) {
                    is LoadState.Data -> submission.acknowledgedMessageId = result.value.id
                    is LoadState.Error -> {
                        _historyFailure.value = result.message
                        completed(false)
                        return@launch
                    }
                    else -> { completed(false); return@launch }
                }
            }
            val messageId = requireNotNull(submission.acknowledgedMessageId)
            when (val uploaded = submission.uploadRemaining { index ->
                repository.uploadAttachment(context, role, id, messageId, attachmentUris[index])
            }) {
                is LoadState.Data -> Unit
                is LoadState.Error -> {
                    _historyFailure.value = "Message sent, but an attachment was not uploaded. " + uploaded.message
                    completed(false)
                    return@launch
                }
                else -> { completed(false); return@launch }
            }
            pendingSubmission = null
            requestDetail(id, marksRead = true, newerActivity = true)
            // Refreshes do not redefine the server's successful send acknowledgement.
            launch { refreshInboxSilently() }
            completed(true)
        } finally {
            _isSending.value = false
        }
    }

    fun markViewed(id: String) {
        val current = (_detail.value as? LoadState.Data)?.value ?: return
        if (selectedConversationId == id && current.id == id) acknowledgeVisible(id, current)
    }

    fun setReadReceipts(id: String, enabled: Boolean, globally: Boolean) = viewModelScope.launch {
        if (globally) detailCache.clear() else detailCache.remove(id)
        when (val result = repository.setReadReceipts(role, id, enabled, globally)) {
            is LoadState.Data -> refreshOpenConversation(id)
            is LoadState.Error -> _historyFailure.value = result.message
            else -> Unit
        }
    }

    fun setPinned(conversation: ConversationSummary, isPinned: Boolean) = viewModelScope.launch {
        if (repository.setPinned(role, conversation.id, isPinned) is LoadState.Data) {
            updateInbox(conversation.id) { it.copy(isPinned = isPinned) }
            refreshInboxSilently()
        }
    }

    fun setMuted(conversation: ConversationSummary, isMuted: Boolean) = viewModelScope.launch {
        detailCache.remove(conversation.id)
        if (repository.setMuted(role, conversation.id, isMuted) is LoadState.Data) {
            updateInbox(conversation.id) { it.copy(isMuted = isMuted) }
            val open = (_detail.value as? LoadState.Data)?.value
            if (open?.id == conversation.id) _detail.value = LoadState.Data(open.copy(isMuted = isMuted))
        }
    }

    fun remove(conversationId: String, completed: () -> Unit) = viewModelScope.launch {
        detailCache.remove(conversationId)
        if (repository.remove(role, conversationId) is LoadState.Data) {
            updateInbox(conversationId) { null }
            _detail.value = LoadState.Idle
            completed()
        }
    }

    fun deleteMessage(message: ConversationMessage) = viewModelScope.launch {
        if (!message.isMine || message.isDeleted) return@launch
        if (repository.deleteMessage(role, message.conversationId, message.id) is LoadState.Data) {
            refreshOpenConversation(message.conversationId)
            refreshInboxSilently()
        }
    }

    private suspend fun refreshOpenConversation(id: String) {
        detailCache.remove(id)
        if (selectedConversationId == id) requestDetail(id, marksRead = false, newerActivity = true).join()
    }

    /**
     * Reconciles the one server wake-up event used by iOS and Android. It
     * deliberately never inserts a realtime body or derives an unread count;
     * both projections are reloaded from AgentPortal.
     */
    private val reactionJobs = mutableMapOf<String, Job>()
    private val reactionVersions = mutableMapOf<String, Int>()
    private val confirmedReactions = mutableMapOf<String, List<com.mylegnd.legend.registered.core.model.MessageReaction>>()
    fun react(message: ConversationMessage, emoji: String?) {
        detailCache.remove(message.conversationId)
        if (message.isDeleted) return
        val currentMessage = (_detail.value as? LoadState.Data)?.value?.takeIf { it.id == message.conversationId }
            ?.messages?.firstOrNull { it.id == message.id } ?: return
        val previousJob = reactionJobs[message.id]
        val mutationVersion = (reactionVersions[message.id] ?: 0) + 1
        reactionVersions[message.id] = mutationVersion
        if (previousJob?.isActive != true) confirmedReactions[message.id] = currentMessage.reactions
        val revision = presentationRevision
        val previousReactions = currentMessage.reactions
        val optimisticReactions = previousReactions.mapNotNull {
            val count = it.count - if (it.reactedByCurrentActor) 1 else 0
            if (count > 0) it.copy(count = count, reactedByCurrentActor = false) else null
        }.toMutableList().apply {
            if (emoji != null) {
                val index = indexOfFirst { it.emoji == emoji }
                if (index >= 0) this[index] = this[index].copy(count = this[index].count + 1, reactedByCurrentActor = true)
                else add(com.mylegnd.legend.registered.core.model.MessageReaction(emoji, 1, true))
            }
        }.toList()
        (_detail.value as? LoadState.Data)?.value?.takeIf { it.id == message.conversationId }?.let { current ->
            _detail.value = LoadState.Data(current.copy(messages = current.messages.map {
                if (it.id == message.id) it.copy(reactions = optimisticReactions) else it
            }))
        }
        reactionJobs[message.id] = viewModelScope.launch {
            previousJob?.join()
            if (revision != presentationRevision || selectedConversationId != message.conversationId) return@launch
            when (val result = repository.react(role, message.conversationId, message.id, emoji)) {
                is LoadState.Data -> if (revision == presentationRevision && selectedConversationId == message.conversationId) {
                    val current = (_detail.value as? LoadState.Data)?.value
                    if (current?.id == message.conversationId && result.value.messageId == message.id) {
                        confirmedReactions[message.id] = result.value.reactions
                        if (reactionVersions[message.id] != mutationVersion) return@launch
                        _detail.value = LoadState.Data(current.copy(messages = current.messages.map {
                            if (it.id == message.id) it.copy(reactions = result.value.reactions) else it
                        }))
                    }
                }
                is LoadState.Error -> if (revision == presentationRevision && selectedConversationId == message.conversationId && reactionVersions[message.id] == mutationVersion) {
                    val confirmed = confirmedReactions[message.id] ?: previousReactions
                    (_detail.value as? LoadState.Data)?.value?.let { current ->
                        _detail.value = LoadState.Data(current.copy(messages = current.messages.map {
                            if (it.id == message.id && it.reactions == optimisticReactions) it.copy(reactions = confirmed) else it
                        }))
                    }
                    _historyFailure.value = result.message
                }
                else -> Unit
            }
        }
    }

    fun reconcileRealtime(event: LegendMessagingRealtimeEvent) {
        if (event.requiresResync) { refreshPresentation(); return }
        val conversationId = event.conversationId ?: return
        detailCache.remove(conversationId)
        viewModelScope.launch { refreshInboxSilently() }
        if (selectedConversationId == conversationId) requestDetail(conversationId, marksRead = false, newerActivity = true)
    }

    fun refreshPresentation() = viewModelScope.launch {
        detailCache.clear()
        detailJob?.cancel()
        detailJob = null
        readAcknowledgements.clear()
        launch { refreshInboxSilently() }
        selectedConversationId?.let { requestDetail(it, marksRead = false) }
    }

    private suspend fun refreshInboxSilently() {
        inboxTask?.takeIf { it.isActive }?.let {
            inboxRefreshPending = true
            inboxRequestRevision++
            it.await()
            return
        }
        val task = viewModelScope.async {
            do {
                inboxRefreshPending = false
                val revision = ++inboxRequestRevision
                when (val fresh = repository.conversations(role)) {
                    is LoadState.Data -> if (revision == inboxRequestRevision) _conversations.value = fresh
                    is LoadState.Error -> if (revision == inboxRequestRevision && _conversations.value !is LoadState.Data) _conversations.value = fresh
                    else -> Unit
                }
            } while (inboxRefreshPending)
        }
        inboxTask = task
        try { task.await() } finally { if (inboxTask === task) inboxTask = null }
    }

    private fun updateInbox(id: String, transform: (ConversationSummary) -> ConversationSummary?) {
        val current = (_conversations.value as? LoadState.Data)?.value ?: return
        _conversations.value = LoadState.Data(current.mapNotNull { row ->
            if (row.id == id) transform(row) else row
        })
    }
}
class SocialViewModel(private val repository: SocialRepository, private val role: String) : ViewModel() {
    suspend fun network(profile: SocialAuthor, list: String) = repository.follows(role, list, profile)
    private val _state = MutableStateFlow<LoadState<SocialSnapshot>>(LoadState.Idle); val state: StateFlow<LoadState<SocialSnapshot>> = _state.asStateFlow()
    private val _profilePosts = MutableStateFlow<LoadState<List<SocialPost>>>(LoadState.Idle); val profilePosts: StateFlow<LoadState<List<SocialPost>>> = _profilePosts.asStateFlow()
    private val _profileMetrics = MutableStateFlow<LoadState<SocialProfileMetrics>>(LoadState.Idle); val profileMetrics: StateFlow<LoadState<SocialProfileMetrics>> = _profileMetrics.asStateFlow()
    private val _followRequests = MutableStateFlow<LoadState<List<SocialFollowRequestItem>>>(LoadState.Idle); val followRequests: StateFlow<LoadState<List<SocialFollowRequestItem>>> = _followRequests.asStateFlow()
    private val _publicProfilePosts = MutableStateFlow<LoadState<List<SocialPost>>>(LoadState.Idle); val publicProfilePosts: StateFlow<LoadState<List<SocialPost>>> = _publicProfilePosts.asStateFlow()
    private val _publicProfileMetrics = MutableStateFlow<LoadState<SocialProfileMetrics>>(LoadState.Idle); val publicProfileMetrics: StateFlow<LoadState<SocialProfileMetrics>> = _publicProfileMetrics.asStateFlow()
    fun load() = viewModelScope.launch { _state.value = LoadState.Loading; _state.value = repository.feed(role) }
    private val _publication = MutableStateFlow<LoadState<SocialPost>>(LoadState.Idle)
    val publication = _publication.asStateFlow()
    private var pendingPublication: (suspend () -> LoadState<SocialPost>)? = null
    private fun publish(action: suspend () -> LoadState<SocialPost>) {
        if (_publication.value is LoadState.Loading) return
        pendingPublication = action
        _publication.value = LoadState.Loading
        viewModelScope.launch {
            val result = action()
            _publication.value = result
            if (result is LoadState.Data) { pendingPublication = null; load() }
        }
    }
    fun retryPublication() { pendingPublication?.let(::publish) }
    fun dismissPublication() { if (_publication.value !is LoadState.Loading) { pendingPublication = null; _publication.value = LoadState.Idle } }
    fun create(request: CreateSocialPostRequest) = publish { repository.createPost(role, request) }
    fun createMedia(context: Context, uris: List<Uri>, options: SocialMediaPublishOptions, previewUri: Uri? = null) = publish { repository.createMediaPost(context.applicationContext, role, uris, options, previewUri) }
    fun react(id: String) = viewModelScope.launch { repository.react(role, id); load() }
    fun comment(id: String, body: String, parentCommentId: String? = null) = viewModelScope.launch { repository.comment(role, id, body, parentCommentId); load() }
    fun updatePost(id: String, body: String) = viewModelScope.launch { repository.updatePost(role, id, body); load() }
    fun deletePost(id: String) = viewModelScope.launch { repository.deletePost(role, id); load() }
    fun toggleFollow(post: SocialPost) = viewModelScope.launch { repository.toggleFollow(role, post.author, post.id); load() }
    fun toggleFollow(author: SocialAuthor) = viewModelScope.launch { repository.toggleFollow(role, author); load() }
    fun toggleSave(id: String) = viewModelScope.launch { repository.toggleSave(role, id); load() }
    fun toggleRepost(id: String) = viewModelScope.launch { repository.toggleRepost(role, id); load() }
    fun recordShare(id: String) = viewModelScope.launch { repository.recordShare(role, id) }
    fun recordView(id: String, watchDurationSeconds: Double? = null, completion: Double? = null, storyInteractionType: String? = null) = viewModelScope.launch { repository.recordView(role, id, SocialViewRequest(watchDurationSeconds, completion, storyInteractionType)) }
    fun loadCurrentProfile() = viewModelScope.launch { _profilePosts.value = LoadState.Loading; _profileMetrics.value = LoadState.Loading; _profilePosts.value = repository.currentProfilePosts(role); _profileMetrics.value = repository.profileMetrics(role) }
    private var publicProfileLoad: Job? = null
    fun loadPublicProfile(author: SocialAuthor) {
        publicProfileLoad?.cancel()
        _publicProfilePosts.value = LoadState.Loading
        _publicProfileMetrics.value = LoadState.Loading
        publicProfileLoad = viewModelScope.launch {
            val posts = repository.publicProfilePosts(role, author)
            kotlinx.coroutines.currentCoroutineContext().ensureActive()
            _publicProfilePosts.value = posts
            val metrics = repository.profileMetrics(role, author)
            kotlinx.coroutines.currentCoroutineContext().ensureActive()
            _publicProfileMetrics.value = metrics
            repository.recordProfileVisit(role, author)
        }
    }
    fun loadFollowRequests() = viewModelScope.launch { _followRequests.value = LoadState.Loading; _followRequests.value = repository.followRequests(role) }
    fun decideFollowRequest(id: String, approve: Boolean) = viewModelScope.launch { repository.decideFollowRequest(role, id, approve); loadFollowRequests(); load() }
    fun joinPromotedGroup(id: String, onJoined: () -> Unit) = viewModelScope.launch {
        if (repository.joinPromotedGroup(role, id) is LoadState.Data) {
            load()
            onJoined()
        }
    }
}
class NotificationsViewModel(private val repository: NotificationRepository, private val role: String) : ViewModel() {
    private val _state = MutableStateFlow<LoadState<NotificationSnapshot>>(LoadState.Idle)
    val state: StateFlow<LoadState<NotificationSnapshot>> = _state.asStateFlow()
    private val _activity = MutableStateFlow<LoadState<List<MessagingActivityNotification>>>(LoadState.Idle)
    val activity = _activity.asStateFlow()
    fun loadActivity() = viewModelScope.launch { _activity.value = repository.activity(role) }
    fun load() = viewModelScope.launch { _state.value = LoadState.Loading; _state.value = repository.snapshot(role) }

    /**
     * Applies the server's own revisioned badge event just as iOS does. The
     * notification list stays server-owned and is reloaded when opened.
     */
    fun applyRealtime(event: LegendMessagingRealtimeEvent) {
        loadActivity()
        val unreadCount = event.unreadCount ?: return
        val current = (_state.value as? LoadState.Data)?.value
        val currentBadge = current?.badge
        val revision = event.revision ?: currentBadge?.revision ?: 0L
        if (currentBadge != null && revision < currentBadge.revision) return
        val nextBadge = NotificationBadge(
            unreadCount = unreadCount,
            revision = revision,
            updatedUtc = event.occurredUtc ?: currentBadge?.updatedUtc ?: Instant.now().toString(),
        )
        _state.value = LoadState.Data((current ?: NotificationSnapshot(nextBadge)).copy(badge = nextBadge))
    }
    fun markRead(id: String, open: (NotificationItem) -> Unit) = viewModelScope.launch {
        val item = (_state.value as? LoadState.Data)?.value?.notifications?.firstOrNull { it.id == id }
        repository.markRead(role, id)
        load()
        item?.let(open)
    }
    fun clearBadges() = viewModelScope.launch { repository.clearBadges(role); load() }
}
class DiscoveryViewModel(private val discovery: DiscoveryRepository, private val journey: JourneyRepository, private val community: CommunityRepository, private val role: String) : ViewModel() {
    private val _page = MutableStateFlow<LoadState<DiscoveryPage>>(LoadState.Idle); val page: StateFlow<LoadState<DiscoveryPage>> = _page.asStateFlow()
    private val _journey = MutableStateFlow<LoadState<JourneyDashboard>>(LoadState.Idle); val journeyState: StateFlow<LoadState<JourneyDashboard>> = _journey.asStateFlow()
    private val _profile = MutableStateFlow<LoadState<DiscoveryProfile>>(LoadState.Idle); val profile: StateFlow<LoadState<DiscoveryProfile>> = _profile.asStateFlow()
    fun load() = viewModelScope.launch { _page.value = LoadState.Loading; _page.value = discovery.search(role, sort = "Recommended"); if (role.equals("Client", ignoreCase = true)) { _journey.value = LoadState.Loading; _journey.value = journey.dashboard(role) } }
    fun search(query: String) = viewModelScope.launch { _page.value = LoadState.Loading; _page.value = discovery.search(role, query.takeIf(String::isNotBlank), sort = if (query.isBlank()) "Recommended" else "Relevance") }
    fun loadMore() = viewModelScope.launch { val current = (_page.value as? LoadState.Data)?.value ?: return@launch; if (!current.hasMore) return@launch; when (val next = discovery.search(role, offset = current.offset + current.results.size, pageSize = current.pageSize, sort = current.sortMode)) { is LoadState.Data -> _page.value = LoadState.Data(next.value.copy(results = current.results + next.value.results)); is LoadState.Error -> _page.value = next; else -> Unit } }
    fun openProfile(profileId: String) = viewModelScope.launch { _profile.value = LoadState.Loading; _profile.value = discovery.profile(role, profileId) }
    fun saveJourneyProfile(input: com.mylegnd.legend.registered.core.network.JourneyProfileInput, completed: () -> Unit = {}) = viewModelScope.launch { journey.saveProfile(role, input); refreshJourney(); completed() }
    fun requestConnection(clientProfileId: String, reason: String? = null, introduction: String? = null) = viewModelScope.launch { journey.requestConnection(role, clientProfileId, reason, introduction); refreshJourney() }
    fun respondToJourneyConnection(id: String, accept: Boolean) = viewModelScope.launch { journey.respond(role, id, accept); refreshJourney() }
    fun disconnectJourneyConnection(id: String) = viewModelScope.launch { journey.disconnect(role, id); refreshJourney() }
    fun blockJourneyProfile(id: String) = viewModelScope.launch { journey.blockProfile(role, id); refreshJourney(); load() }
    fun reportJourneyProfile(id: String, category: String, detail: String? = null) = viewModelScope.launch { journey.reportProfile(role, id, category, detail); refreshJourney() }
    fun block(result: DiscoveryResult) = viewModelScope.launch { community.block(role, result.identity.userId, result.identity.participantType); load() }
    fun report(result: DiscoveryResult, category: String, detail: String) = viewModelScope.launch { community.report(role, result.identity.userId, result.identity.participantType, "Profile", null, category, detail); load() }
    private suspend fun refreshJourney() { if (role.equals("Client", ignoreCase = true)) _journey.value = journey.dashboard(role) }
}
class AccountViewModel(private val repository: AccountRepository, private val role: String) : ViewModel() {
    private val _profile = MutableStateFlow<LoadState<MobileAccountProfile>>(LoadState.Idle); val profile: StateFlow<LoadState<MobileAccountProfile>> = _profile.asStateFlow()
    private val _lifecycle = MutableStateFlow<LoadState<AccountLifecycle>>(LoadState.Idle); val lifecycle: StateFlow<LoadState<AccountLifecycle>> = _lifecycle.asStateFlow()
    private val _usernameAvailability = MutableStateFlow<LoadState<MobileUsernameAvailability>>(LoadState.Idle); val usernameAvailability: StateFlow<LoadState<MobileUsernameAvailability>> = _usernameAvailability.asStateFlow()
    fun load() = viewModelScope.launch { _profile.value = LoadState.Loading; _profile.value = repository.profile(role); _lifecycle.value = repository.lifecycle(role) }
    fun updateLanguage(
        account: MobileAccountProfile,
        language: String?,
        completed: () -> Unit = {},
    ) = viewModelScope.launch {
        _profile.value = LoadState.Loading
        val result = repository.update(
            role,
            AccountUpdateRequest(
                account.displayName,
                account.phone,
                account.title,
                account.shortBio,
                account.username,
                account.bio,
                account.website,
                account.location,
                account.profileEmail,
                account.isEmailVisible,
                account.isPhoneVisible,
                account.isPrivate,
                language?.trim()?.takeIf(String::isNotBlank),
            ),
        )
        _profile.value = result
        if (result is LoadState.Data) completed()
    }
    fun checkUsernameAvailability(username: String?) = viewModelScope.launch { _usernameAvailability.value = LoadState.Loading; _usernameAvailability.value = repository.usernameAvailability(role, username?.trim()?.takeIf(String::isNotBlank)) }
    fun updatePrivacy(isPrivate: Boolean) = viewModelScope.launch { _profile.value = LoadState.Loading; _profile.value = repository.updatePrivacy(role, isPrivate) }
    fun updateTranslationLearningConsent(allowsConsentedTranslationLearning: Boolean) = viewModelScope.launch { _profile.value = LoadState.Loading; _profile.value = repository.updateTranslationLearningConsent(role, allowsConsentedTranslationLearning) }
    fun update(account: MobileAccountProfile, displayName: String, phone: String?, title: String?, shortBio: String?, username: String?, bio: String?, website: String?, location: String?, publicEmail: String?, isEmailVisible: Boolean, isPhoneVisible: Boolean, isPrivate: Boolean?) = viewModelScope.launch { _profile.value = LoadState.Loading; _profile.value = repository.update(role, AccountUpdateRequest(displayName, phone, title, shortBio, username, bio, website, location, publicEmail, isEmailVisible, isPhoneVisible, isPrivate, account.translationAccess?.preferredCommunicationLanguage)) }
    fun updateAvatar(context: Context, uri: Uri) = viewModelScope.launch { _profile.value = LoadState.Loading; _profile.value = runCatching { ProfileAvatarPreparer.base64Jpeg(context, uri) }.fold(onSuccess = { repository.updateAvatar(role, it) }, onFailure = { LoadState.Error(it.message ?: "Choose another profile picture.") }) }
    fun requestDeletion(confirmation: String) = viewModelScope.launch { _lifecycle.value = repository.requestDeletion(role, confirmation) }
    fun pauseAccount() = viewModelScope.launch { _lifecycle.value = repository.pause(role); load() }
    fun resumeAccount() = viewModelScope.launch { _lifecycle.value = repository.resume(role); load() }
}
class DailyScriptureManagementViewModel(private val repository: DailyScriptureManagementRepository, private val role: String) : ViewModel() {
    private val _state = MutableStateFlow<LoadState<DailyScriptureManagementSnapshot>>(LoadState.Idle); val state: StateFlow<LoadState<DailyScriptureManagementSnapshot>> = _state.asStateFlow()
    private val _action = MutableStateFlow<LoadState<Unit>>(LoadState.Idle); val action: StateFlow<LoadState<Unit>> = _action.asStateFlow()
    fun load() = viewModelScope.launch { _state.value = LoadState.Loading; _state.value = repository.management(role) }
    fun save(existingId: String?, draft: DailyScriptureOverrideRequest, completed: () -> Unit = {}) = viewModelScope.launch {
        _action.value = LoadState.Loading
        var failure: String? = null
        val succeeded = if (existingId == null) {
            when (val result = repository.create(role, draft)) {
                is LoadState.Data -> true
                is LoadState.Error -> { failure = result.message; false }
                else -> false
            }
        } else {
            when (val result = repository.update(role, existingId, draft)) {
                is LoadState.Data -> true
                is LoadState.Error -> { failure = result.message; false }
                else -> false
            }
        }
        _action.value = if (succeeded) LoadState.Data(Unit) else LoadState.Error(failure ?: "Legend could not save the scripture.")
        if (succeeded) { load(); completed() }
    }
    fun remove(id: String) = viewModelScope.launch { _action.value = repository.remove(role, id); if (_action.value is LoadState.Data) load() }
}
class CommunitySafetyReviewViewModel(private val repository: CommunityRepository, private val role: String) : ViewModel() {
    private val _reports = MutableStateFlow<LoadState<List<CommunitySafetyReport>>>(LoadState.Idle); val reports: StateFlow<LoadState<List<CommunitySafetyReport>>> = _reports.asStateFlow()
    private val _resolvingId = MutableStateFlow<String?>(null); val resolvingId: StateFlow<String?> = _resolvingId.asStateFlow()
    fun load() = viewModelScope.launch { _reports.value = LoadState.Loading; _reports.value = repository.openReports(role) }
    fun resolve(report: CommunitySafetyReport, resolution: String) = viewModelScope.launch { if (_resolvingId.value != null) return@launch; _resolvingId.value = report.id; when (repository.resolveReport(role, report.id, resolution)) { is LoadState.Data -> { val current = (_reports.value as? LoadState.Data)?.value.orEmpty(); _reports.value = LoadState.Data(current.filterNot { it.id == report.id }) }; is LoadState.Error -> _reports.value = LoadState.Error("Legend could not update this report."); else -> Unit }; _resolvingId.value = null }
}
class FounderAccountsViewModel(private val repository: FounderAccountRepository, private val role: String) : ViewModel() {
    private val _accounts = MutableStateFlow<LoadState<List<FounderManagedAccount>>>(LoadState.Idle); val accounts: StateFlow<LoadState<List<FounderManagedAccount>>> = _accounts.asStateFlow()
    private val _action = MutableStateFlow<LoadState<FounderAccountBatchResponse>>(LoadState.Idle); val action: StateFlow<LoadState<FounderAccountBatchResponse>> = _action.asStateFlow()
    fun load(search: String? = null, scope: String? = null) = viewModelScope.launch { _accounts.value = LoadState.Loading; _accounts.value = repository.accounts(role, search, scope) }
    fun remove(accounts: List<FounderManagedAccount>, confirmation: String, scope: String? = null) = viewModelScope.launch { _action.value = LoadState.Loading; _action.value = repository.removeBatch(role, accounts, confirmation); load(scope = scope) }
    fun restore(account: FounderManagedAccount) = viewModelScope.launch {
        _action.value = LoadState.Loading
        _action.value = when (val result = repository.restore(role, account)) {
            is LoadState.Data -> LoadState.Data(FounderAccountBatchResponse(1, 0, listOf(FounderAccountBatchItemResponse(true, true, message = result.value.message, lifecycleState = result.value.lifecycleState))))
            is LoadState.Error -> result
            else -> LoadState.Idle
        }
        load(scope = "archive")
    }
    fun purge(accounts: List<FounderManagedAccount>, confirmation: String) = viewModelScope.launch { _action.value = LoadState.Loading; _action.value = repository.purge(role, accounts, confirmation); load(scope = "archive") }
}
class ControlledResourceViewModel(private val repository: MessagingRepository, private val role: String) : ViewModel() {
    suspend fun languages() = repository.languages(role)
    private val _recipients = MutableStateFlow<LoadState<List<MessagingRecipient>>>(LoadState.Idle); val recipients: StateFlow<LoadState<List<MessagingRecipient>>> = _recipients.asStateFlow()
    private val _updating = MutableStateFlow<String?>(null); val updating: StateFlow<String?> = _updating.asStateFlow()
    fun load(resourceType: String, search: String? = null) = viewModelScope.launch { _recipients.value = LoadState.Loading; _recipients.value = repository.controlledRecipients(role, resourceType, search) }
    fun setGrant(resourceType: String, recipient: MessagingRecipient, isGranted: Boolean) = viewModelScope.launch { val id = "${recipient.identity.userId}:${recipient.identity.participantType}"; _updating.value = id; if (repository.setControlledGrant(role, resourceType, recipient, isGranted) is LoadState.Data) { val current = (_recipients.value as? LoadState.Data)?.value.orEmpty(); _recipients.value = LoadState.Data(current.map { if (it.identity == recipient.identity) it.copy(resourceAccessState = if (isGranted) "Granted" else "NotGranted") else it }) }; _updating.value = null }
}
