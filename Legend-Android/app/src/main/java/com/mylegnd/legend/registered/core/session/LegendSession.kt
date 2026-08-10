package com.mylegnd.legend.registered.core.session

import android.app.Activity
import androidx.lifecycle.ViewModel
import androidx.lifecycle.viewModelScope
import com.mylegnd.legend.registered.core.auth.CachedLegendSession
import com.mylegnd.legend.registered.core.auth.LegendAuthClient
import com.mylegnd.legend.registered.core.auth.SecureSessionStore
import com.mylegnd.legend.registered.core.config.LegendRuntimeConfiguration
import com.mylegnd.legend.registered.core.model.*
import com.mylegnd.legend.registered.core.network.LegendApiClient
import com.mylegnd.legend.registered.core.network.legendBody
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.launch
import java.time.Instant

data class ActiveLegendSession(val actor: MobileActor, val permittedParticipantTypes: List<String>, val capabilities: MobileCapabilities)
sealed interface SessionState {
    data object Loading : SessionState; data object ConfigurationRequired : SessionState; data object SignedOut : SessionState; data object Authenticating : SessionState
    data class RoleSelection(val roles: List<String>) : SessionState
    data class Authenticated(val session: ActiveLegendSession) : SessionState
    data class Failure(val message: String, val correlationId: String? = null) : SessionState
}

class SessionRepository(
    private val configuration: LegendRuntimeConfiguration,
    private val auth: LegendAuthClient,
    private val apiClient: () -> LegendApiClient,
    private val cache: SecureSessionStore,
    private val beforeSignOut: suspend () -> Unit = {},
) {
    suspend fun restore(): SessionState {
        if (!configuration.isReady) return SessionState.ConfigurationRequired
        val cachedParticipantType = runCatching { cache.read()?.participantType }.getOrNull()
        runCatching { auth.restoreAccessToken() }.getOrNull() ?: return SessionState.SignedOut
        // The cache can suggest the previously selected presentation role only; the server validates it.
        return establish(cachedParticipantType)
    }
    suspend fun signIn(activity: Activity): SessionState { auth.signIn(activity); return establish(null) }
    suspend fun selectRole(role: String): SessionState {
        val response = apiClient().api.selectRole(SelectRoleRequest(role)).legendBody()
        return authenticated(response.actor, response.permittedParticipantTypes, response.capabilities ?: MobileCapabilities())
    }
    suspend fun signOut() { runCatching { beforeSignOut() }; runCatching { auth.signOut() }; cache.clear() }
    private suspend fun establish(preferredRole: String?): SessionState {
        val response = apiClient().api.session(preferredRole).legendBody()
        if (!response.authenticated) return SessionState.SignedOut
        if (response.requiresParticipantSelection) return SessionState.RoleSelection(response.permittedParticipantTypes)
        val actor = response.actor ?: return SessionState.Failure("Legend could not resolve this account.", response.correlationId)
        return authenticated(actor, response.permittedParticipantTypes, response.capabilities)
    }
    private suspend fun authenticated(actor: MobileActor, roles: List<String>, capabilities: MobileCapabilities): SessionState {
        cache.write(CachedLegendSession(actor.identity.userId, actor.identity.participantType, actor.displayName, Instant.now().toString()))
        return SessionState.Authenticated(ActiveLegendSession(actor, roles, capabilities))
    }
}

class SessionViewModel(private val repository: SessionRepository) : ViewModel() {
    private val _state = MutableStateFlow<SessionState>(SessionState.Loading); val state: StateFlow<SessionState> = _state.asStateFlow()
    fun restore() = viewModelScope.launch { _state.value = runCatching { repository.restore() }.getOrElse { SessionState.Failure("We could not restore your secure Legend session.") } }
    fun signIn(activity: Activity) = viewModelScope.launch { _state.value = SessionState.Authenticating; _state.value = runCatching { repository.signIn(activity) }.getOrElse { SessionState.Failure("Secure sign-in could not be completed.") } }
    fun selectRole(role: String) = viewModelScope.launch { _state.value = SessionState.Authenticating; _state.value = runCatching { repository.selectRole(role) }.getOrElse { SessionState.Failure("That Legend account is not available.") } }
    fun signOut() = viewModelScope.launch { repository.signOut(); _state.value = SessionState.SignedOut }
}
