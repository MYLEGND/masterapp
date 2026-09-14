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
import kotlinx.coroutines.delay
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
    val reason: String? = null,
    val foundationModel: String? = null,
    val foundationHosting: String? = null,
    val externalAnsweringUsed: Boolean? = null,
    val escalationUsed: Boolean? = null,
    val escalationDisposition: String? = null,
    val researchState: String? = null,
    val learningState: String? = null,
    val modelAssistanceState: String? = null,
    val modelVersion: String? = null,
    val modelTrainingRunId: String? = null,
    val modelProvenance: String? = null,
    val id: String? = null,
    val sentUtc: String? = null,
    val stage: String? = null,
)

data class FounderAiConversationState(
    val availability: LoadState<Boolean> = LoadState.Idle,
    val messages: List<FounderAiTranscriptMessage> = emptyList(),
    val isSending: Boolean = false,
    val operationId: String? = null,
    val progress: String? = null,
    val failure: String? = null,
    val conversations: List<FounderAiHistoryThread> = emptyList(),
    val hasMoreConversations: Boolean = false,
    val hasOlderMessages: Boolean = false,
    val hasNewerMessages: Boolean = false,
    val canRetry: Boolean = false,
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
    private var lastMessageId: String? = null
    private var persisted = false
    private var operation: Job? = null
    private var refreshJob: Job? = null
    private var pollJob: Job? = null
    private var generation = 0
    private var retired = false
    private var visible = false
    private var historySkip = 0
    private data class Pending(val id: String, val request: FounderAiChatRequest, val userMessageId: String? = null)
    private var pending: Pending? = null

    fun resolveAvailability() {
        if (retired || _state.value.availability !is LoadState.Idle) return
        val epoch = generation
        viewModelScope.launch {
            _state.update { it.copy(availability = LoadState.Loading) }
            val result = repository.access(role)
            if (retired || epoch != generation) return@launch
            _state.update { it.copy(availability = when (result) {
                is LoadState.Data -> LoadState.Data(result.value.available)
                is LoadState.Error -> LoadState.Error(result.message)
                else -> LoadState.Error("Founder AI availability could not be determined.")
            }) }
            refresh()
        }
    }

    fun visible(value: Boolean) {
        visible = value
        pollJob?.cancel()
        if (!value || retired) { refreshJob?.cancel(); refreshJob = null; return }
        refresh()
        pollJob = viewModelScope.launch {
            while (visible) { delay(20_000); refresh() }
        }
    }

    fun activateSession() {
        if (!retired) return
        // Activity ViewModelStore may return this same canonical account key
        // after A→B→A. Old callbacks retain their earlier generation forever.
        generation++; retired = false; visible = false
        conversationId = UUID.randomUUID().toString(); lastMessageId = null; persisted = false
        pending = null; historySkip = 0
        operation = null; refreshJob = null; pollJob = null
        _state.value = FounderAiConversationState()
    }

    fun disposeSession() {
        retired = true; generation++
        operation?.cancel(); refreshJob?.cancel(); pollJob?.cancel()
        pending = null
        _state.value = FounderAiConversationState()
    }

    fun refresh(more: Boolean = false, older: Boolean = false, newer: Boolean = false) {
        if (retired || !visible || _state.value.isSending || refreshJob?.isActive == true ||
            (_state.value.availability as? LoadState.Data)?.value != true) return
        val epoch = generation
        val thread = conversationId
        refreshJob = viewModelScope.launch {
            fun current() = !retired && epoch == generation && thread == conversationId
            if (!older) {
                when (val list = repository.conversations(role, if (more) historySkip else 0)) {
                    is LoadState.Data -> if (current()) {
                        if (!list.value.succeeded) { _state.update { it.copy(failure = list.value.errorMessage) }; return@launch }
                        if (list.value.conversations.any { it.id == conversationId }) persisted = true
                        historySkip = if (more) historySkip + list.value.conversations.size else maxOf(historySkip, list.value.conversations.size)
                        _state.update { it.copy(conversations = (if (more) it.conversations + list.value.conversations else list.value.conversations + it.conversations).distinctBy { row -> row.id },
                            hasMoreConversations = list.value.conversations.size == 50) }
                    }
                    is LoadState.Error -> { if (current()) historyFailure(list); return@launch }
                    else -> return@launch
                }
            }
            if (!current() || !persisted) return@launch
            val cursor = _state.value.messages.firstOrNull().takeIf { older }
            // Original server timestamp strings preserve SQL sub-millisecond ticks.
            when (val result = repository.conversation(role, thread, cursor?.sentUtc, cursor?.id)) {
                is LoadState.Data -> if (current()) {
                    val detail = result.value.conversation
                    if (!result.value.succeeded || detail == null) { _state.update { it.copy(failure = result.value.errorMessage) }; return@launch }
                    if (detail.messages.any { it.authorKind !in setOf("Human", "Assistant", "Service") ||
                        (it.authorKind != "Human" && it.responseProvenance == null) }) {
                        _state.update { it.copy(failure = "Conversation history could not be verified.") }; return@launch
                    }
                    val messages = detail.messages.map { row -> transcript(row.authorKind, row.body, row.responseProvenance, row.id, row.sentUtc) }
                    val pageIds = messages.map { it.id }.toSet()
                    if (!older && !newer && _state.value.messages.isNotEmpty() && messages.isNotEmpty() &&
                        _state.value.messages.none { it.id in pageIds }) {
                        _state.update { it.copy(hasNewerMessages = true) }; return@launch
                    }
                    if (!older) lastMessageId = detail.messages.lastOrNull()?.id
                    if (pending?.userMessageId != null && detail.messages.any { it.replyToMessageId == pending?.userMessageId }) pending = null
                    _state.update { current ->
                        val ids = messages.map { it.id }.toSet()
                        val retained = if (newer) emptyList() else current.messages.filterNot { it.id in ids }
                        current.copy(messages = if (older) messages + retained else retained + messages,
                            hasOlderMessages = if (older || retained.isEmpty()) detail.hasOlderMessages else current.hasOlderMessages,
                            canRetry = pending != null, hasNewerMessages = if (older) current.hasNewerMessages else false)
                    }
                }
                is LoadState.Error -> if (current()) historyFailure(result)
                else -> Unit
            }
        }
    }

    private fun historyFailure(error: LoadState.Error) {
        if (error.status in listOf(401, 403)) disposeSession()
        else _state.update { it.copy(failure = error.message) }
    }

    fun openConversation(id: String) {
        if (_state.value.isSending || retired) return
        generation++; refreshJob?.cancel(); refreshJob = null
        conversationId = id; persisted = true; lastMessageId = null; pending = null
        _state.update { it.copy(messages = emptyList(), failure = null, hasOlderMessages = false, hasNewerMessages = false, canRetry = false) }
        refresh()
    }

    fun send(rawText: String, mode: String, nativeOnly: Boolean, externalAnsweringBlocked: Boolean = false,
        sourceLanguageCode: String? = null): Boolean {
        val text = rawText.trim()
        if (retired || text.isBlank() || _state.value.isSending || mode !in setOf("legend", "teacher") ||
            (_state.value.availability as? LoadState.Data)?.value != true) return false
        if (_state.value.hasNewerMessages) { _state.update { it.copy(failure = "Load newer messages before sending a reply.") }; return false }
        if (pending != null) { _state.update { it.copy(failure = "Check the pending request before sending another message.") }; return false }
        val submission = Pending(UUID.randomUUID().toString(), FounderAiChatRequest(mode,
            mode == "legend" && nativeOnly, mode == "legend" && externalAnsweringBlocked, sourceLanguageCode,
            listOf(FounderAiChatMessage("user", text)), conversationId, lastMessageId))
        pending = submission
        execute(submission)
        return true
    }

    fun retry() { if (!_state.value.isSending && !retired) pending?.let(::execute) }

    private fun execute(submission: Pending) {
        refreshJob?.cancel(); refreshJob = null
        val epoch = generation
        _state.update { it.copy(isSending = true, operationId = submission.id, canRetry = false,
            progress = "Preparing governed conversation…", failure = null) }
        operation = viewModelScope.launch {
            fun current() = !retired && epoch == generation && _state.value.operationId == submission.id
            try {
                when (val result = repository.chat(role, submission.id, submission.request) { envelope ->
                    if (current()) {
                        _state.update { it.copy(progress = envelope.progress?.message) }
                        if (persisted) refresh()
                    }
                }) {
                    is LoadState.Data -> if (current()) {
                        val response = result.value
                        if (response.failureKind == "authorization") { disposeSession(); return@launch }
                        if (response.conversationId != null) { conversationId = response.conversationId; if (response.userMessageId != null || response.messageId != null) persisted = true }
                        if (response.userMessageId != null) pending = submission.copy(userMessageId = response.userMessageId)
                        if (response.messageId != null) {
                            lastMessageId = response.messageId; pending = null
                            _state.update { it.copy(messages = it.messages.filterNot { item -> item.id == response.messageId } +
                                transcript(if (response.succeeded) "Assistant" else "Service", response.message ?: response.error.orEmpty(),
                                    response, response.messageId, response.lastMessageUtc)) }
                        } else if (response.reason in setOf("FOUNDER_HISTORY_STALE", "FOUNDER_HISTORY_REPLAY_MISMATCH", "FOUNDER_HISTORY_FORBIDDEN", "FOUNDER_HISTORY_CLOSED")) pending = null
                        _state.update { it.copy(failure = if (response.succeeded) (if (response.messageId == null) "The response is missing its saved conversation receipt." else null) else response.error ?: "The Founder AI request did not produce a response.") }
                    }
                    is LoadState.Error -> if (current()) historyFailure(result)
                    else -> Unit
                }
            } catch (_: CancellationException) {
                // Cancellation does not imply that an earlier tool action was rolled back.
            } finally {
                if (current()) {
                    _state.update { it.copy(isSending = false, operationId = null, progress = null, canRetry = pending != null) }
                    refresh()
                }
            }
        }
    }

    fun cancel() {
        if (operation?.isActive != true) return
        operation?.cancel(); operation = null
        _state.update { it.copy(isSending = false, operationId = null, progress = null, canRetry = pending != null,
            failure = "Response stopped. Check the saved outcome before sending again.") }
        refresh()
    }

    fun startNewConversation() {
        if (_state.value.isSending || retired) return
        generation++; refreshJob?.cancel(); refreshJob = null
        conversationId = UUID.randomUUID().toString(); lastMessageId = null; persisted = false; pending = null
        _state.update { it.copy(messages = emptyList(), failure = null, progress = null, canRetry = false, hasOlderMessages = false, hasNewerMessages = false) }
    }

    private fun transcript(author: String, body: String, response: FounderAiChatResponse?, id: String?, sentUtc: String?) =
        FounderAiTranscriptMessage(if (author == "Human") "user" else if (author == "Assistant") "assistant" else "service", body,
            response?.responseAuthority, response?.reason, response?.foundationModel, response?.foundationHosting,
            response?.externalAnsweringUsed, response?.escalationUsed, response?.escalationDisposition,
            response?.researchState, response?.learningState, response?.modelAssistanceState, response?.modelVersion,
            response?.modelTrainingRunId, response?.modelProvenance, id, sentUtc, response?.stage)
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
        if (selectedConversationId != id) { historyJob?.cancel(); historyJob = null }
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
                    _historyFailure.value = null
                    _detail.value = result
                    if (detailMarksRead) acknowledgeVisible(id, result.value)
                } else if (result is LoadState.Error && result.status in setOf(401, 403, 404, 410)) {
                    detailCache.remove(id)
                    _detail.value = result
                } else if (_detail.value !is LoadState.Data) _detail.value = result
                else if (result is LoadState.Error) _historyFailure.value = result.message
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

    fun startConversation(recipient: MessagingRecipient, includeMessages: Boolean = true, opened: (String) -> Unit) = viewModelScope.launch {
        if (_isSending.value) return@launch
        _isSending.value = true
        val openedConversationId: String?
        try {
            openedConversationId = beginConversation(recipient, includeMessages)
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

    private suspend fun beginConversation(recipient: MessagingRecipient, includeMessages: Boolean = true): String? =
        when (val result = repository.startConversation(role, recipient, includeMessages)) {
            is LoadState.Data -> {
                if (includeMessages) {
                    selectedConversationId = result.value.id
                    ++presentationRevision
                    _detail.value = result
                    viewModelScope.launch { refreshInboxSilently() }
                }
                // Metadata-only call setup must never replace canonical chat history.
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
    private val _postActionFailure = MutableStateFlow<String?>(null)
    val postActionFailure = _postActionFailure.asStateFlow()
    private fun postAction(id: String, action: suspend () -> LoadState<*>) = viewModelScope.launch {
        if (openedPostId == id) _postActionFailure.value = null
        when (val result = action()) {
            is LoadState.Data -> { refreshOpenedPost(id); load() }
            is LoadState.Error -> if (openedPostId == id) _postActionFailure.value = result.message
            else -> Unit
        }
    }
    private val _openedPost = MutableStateFlow<LoadState<SocialPost>>(LoadState.Idle)
    val openedPost = _openedPost.asStateFlow()
    private var openedPostId: String? = null
    private var openedPostJob: Job? = null
    fun openPost(id: String) {
        openedPostJob?.cancel()
        openedPostId = id
        if ((_openedPost.value as? LoadState.Data)?.value?.id != id) _openedPost.value = LoadState.Loading
        openedPostJob = viewModelScope.launch {
            val result = repository.post(role, id)
            ensureActive()
            if (openedPostId == id) _openedPost.value = result
        }
    }
    fun closePost() { _postActionFailure.value = null; openedPostJob?.cancel(); openedPostId = null; _openedPost.value = LoadState.Idle }
    private fun refreshOpenedPost(id: String) { if (openedPostId == id) openPost(id) }
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
    fun react(id: String) = postAction(id) { repository.react(role, id) }
    fun comment(id: String, body: String, parentCommentId: String? = null) = postAction(id) { repository.comment(role, id, body, parentCommentId) }
    fun updatePost(id: String, body: String) = postAction(id) { repository.updatePost(role, id, body) }
    fun deletePost(id: String) = viewModelScope.launch {
        when (val result = repository.deletePost(role, id)) {
            is LoadState.Data -> { if (openedPostId == id) _openedPost.value = LoadState.Error("This shared content is no longer available.", 404); load() }
            is LoadState.Error -> if (openedPostId == id) _postActionFailure.value = result.message
            else -> Unit
        }
    }
    fun toggleFollow(post: SocialPost) = postAction(post.id) { repository.toggleFollow(role, post.author, post.id) }
    fun toggleFollow(author: SocialAuthor) = viewModelScope.launch { repository.toggleFollow(role, author); load() }
    fun toggleSave(id: String) = postAction(id) { repository.toggleSave(role, id) }
    fun toggleRepost(id: String) = postAction(id) { repository.toggleRepost(role, id) }
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
