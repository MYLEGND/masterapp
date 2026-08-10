package com.mylegnd.legend.registered.feature

import androidx.lifecycle.ViewModel
import androidx.lifecycle.viewModelScope
import com.mylegnd.legend.registered.core.model.*
import com.mylegnd.legend.registered.core.network.DiscoveryPage
import com.mylegnd.legend.registered.core.network.DiscoveryResult
import com.mylegnd.legend.registered.core.network.JourneyDashboard
import com.mylegnd.legend.registered.data.*
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.launch
import android.content.Context
import android.net.Uri

class HomeViewModel(private val repository: HomeRepository, private val role: String) : ViewModel() { private val _state = MutableStateFlow<LoadState<MobileHomeResponse>>(LoadState.Idle); val state: StateFlow<LoadState<MobileHomeResponse>> = _state.asStateFlow(); fun load() = viewModelScope.launch { _state.value = LoadState.Loading; _state.value = repository.load(role) } }
class FinancialViewModel(private val repository: FinancialRepository, private val role: String) : ViewModel() { private val _state = MutableStateFlow<LoadState<FinancialSnapshot>>(LoadState.Idle); val state: StateFlow<LoadState<FinancialSnapshot>> = _state.asStateFlow(); fun load() = viewModelScope.launch { _state.value = LoadState.Loading; _state.value = repository.load(role) } }
class MessagingViewModel(private val repository: MessagingRepository, private val role: String) : ViewModel() {
    private val _conversations = MutableStateFlow<LoadState<List<ConversationSummary>>>(LoadState.Idle); val conversations: StateFlow<LoadState<List<ConversationSummary>>> = _conversations.asStateFlow()
    private val _messages = MutableStateFlow<LoadState<List<ConversationMessage>>>(LoadState.Idle); val messages: StateFlow<LoadState<List<ConversationMessage>>> = _messages.asStateFlow()
    fun load() = viewModelScope.launch { _conversations.value = LoadState.Loading; _conversations.value = repository.conversations(role) }
    fun open(id: String) = viewModelScope.launch { _messages.value = LoadState.Loading; _messages.value = repository.messages(role, id); repository.markRead(role, id) }
    fun send(id: String, body: String) = viewModelScope.launch { if (body.isBlank()) return@launch; repository.send(role, id, body); open(id) }
}
class SocialViewModel(private val repository: SocialRepository, private val role: String) : ViewModel() {
    private val _state = MutableStateFlow<LoadState<SocialSnapshot>>(LoadState.Idle); val state: StateFlow<LoadState<SocialSnapshot>> = _state.asStateFlow()
    fun load() = viewModelScope.launch { _state.value = LoadState.Loading; _state.value = repository.feed(role) }
    fun create(contentType: String, body: String, audience: String?) = viewModelScope.launch { repository.createPost(role, contentType, body, audience); load() }
    fun createMedia(context: Context, uris: List<Uri>, contentType: String, body: String, audience: String) = viewModelScope.launch { repository.createMediaPost(context, role, uris, contentType, body, audience); load() }
    fun react(id: String) = viewModelScope.launch { repository.react(role, id); load() }
    fun comment(id: String, body: String) = viewModelScope.launch { repository.comment(role, id, body); load() }
}
class DiscoveryViewModel(private val discovery: DiscoveryRepository, private val journey: JourneyRepository, private val community: CommunityRepository, private val role: String) : ViewModel() {
    private val _page = MutableStateFlow<LoadState<DiscoveryPage>>(LoadState.Idle); val page: StateFlow<LoadState<DiscoveryPage>> = _page.asStateFlow()
    private val _journey = MutableStateFlow<LoadState<JourneyDashboard>>(LoadState.Idle); val journeyState: StateFlow<LoadState<JourneyDashboard>> = _journey.asStateFlow()
    fun load() = viewModelScope.launch { _page.value = LoadState.Loading; _page.value = discovery.search(role, sort = "Recommended"); if (role.equals("Client", ignoreCase = true)) { _journey.value = LoadState.Loading; _journey.value = journey.dashboard(role) } }
    fun search(query: String) = viewModelScope.launch { _page.value = LoadState.Loading; _page.value = discovery.search(role, query.takeIf(String::isNotBlank), sort = if (query.isBlank()) "Recommended" else "Relevance") }
    fun requestConnection(clientProfileId: String) = viewModelScope.launch { journey.requestConnection(role, clientProfileId); load() }
    fun block(result: DiscoveryResult) = viewModelScope.launch { community.block(role, result.identity.userId, result.identity.participantType); load() }
    fun report(result: DiscoveryResult, category: String, detail: String) = viewModelScope.launch { community.report(role, result.identity.userId, result.identity.participantType, "Profile", null, category, detail); load() }
}
class AccountViewModel(private val repository: AccountRepository, private val role: String) : ViewModel() {
    private val _profile = MutableStateFlow<LoadState<MobileAccountProfile>>(LoadState.Idle); val profile: StateFlow<LoadState<MobileAccountProfile>> = _profile.asStateFlow()
    private val _lifecycle = MutableStateFlow<LoadState<AccountLifecycle>>(LoadState.Idle); val lifecycle: StateFlow<LoadState<AccountLifecycle>> = _lifecycle.asStateFlow()
    fun load() = viewModelScope.launch { _profile.value = LoadState.Loading; _profile.value = repository.profile(role); _lifecycle.value = repository.lifecycle(role) }
    fun updateLanguage(account: MobileAccountProfile, language: String?) = viewModelScope.launch { _profile.value = LoadState.Loading; _profile.value = repository.update(role, AccountUpdateRequest(account.displayName, account.phone, account.title, account.shortBio, account.username, account.bio, account.website, account.location, account.profileEmail, account.isEmailVisible, account.isPhoneVisible, account.isPrivate, language?.trim()?.takeIf(String::isNotBlank))) }
    fun updatePrivacy(isPrivate: Boolean) = viewModelScope.launch { _profile.value = LoadState.Loading; _profile.value = repository.updatePrivacy(role, isPrivate) }
    fun requestDeletion(confirmation: String) = viewModelScope.launch { _lifecycle.value = repository.requestDeletion(role, confirmation) }
}
