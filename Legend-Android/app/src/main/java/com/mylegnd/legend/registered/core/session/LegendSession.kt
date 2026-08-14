package com.mylegnd.legend.registered.core.session

import android.app.Activity
import androidx.lifecycle.ViewModel
import androidx.lifecycle.viewModelScope
import com.mylegnd.legend.registered.core.auth.CachedLegendSession
import com.mylegnd.legend.registered.core.auth.LegendAuthClient
import com.mylegnd.legend.registered.core.auth.LegendAuthenticatedAccount
import com.mylegnd.legend.registered.core.auth.SecureSessionStore
import com.mylegnd.legend.registered.core.config.LegendRuntimeConfiguration
import com.mylegnd.legend.registered.core.design.LegendAccountSessionPolicy
import com.mylegnd.legend.registered.core.model.*
import com.mylegnd.legend.registered.core.network.LegendApiClient
import com.mylegnd.legend.registered.core.network.legendBody
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.launch
import java.time.Instant

/** A server-confirmed account retained on this device; bearer tokens stay in MSAL. */
data class SignedInLegendAccount(
    val accountId: String,
    val displayName: String,
    val participantType: String,
)

data class ActiveLegendSession(
    val actor: MobileActor,
    val permittedParticipantTypes: List<String>,
    val capabilities: MobileCapabilities,
    val accountId: String,
    val signedInAccounts: List<SignedInLegendAccount>,
)

sealed interface SessionState {
    data object Loading : SessionState
    data object ConfigurationRequired : SessionState
    data object SignedOut : SessionState
    data object Authenticating : SessionState
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
    private var activeCredential: LegendAuthenticatedAccount? = null
    private var activeInteractiveSignInUtc: String? = null
    private var requiresFreshInteractiveSignIn = false

    suspend fun restore(): SessionState {
        if (!configuration.isReady) return SessionState.ConfigurationRequired
        val cached = runCatching { cache.read() }.getOrNull() ?: return SessionState.SignedOut
        if (cached.requiresInteractiveSignIn(LegendAccountSessionPolicy.InteractiveSignInRetentionDays)) {
            requiresFreshInteractiveSignIn = true
            return SessionState.SignedOut
        }

        val accountId = cached.accountId ?: return SessionState.SignedOut
        runCatching { auth.restoreAccessToken(accountId) }.getOrNull()
            ?: return SessionState.SignedOut
        activeCredential = auth.signedInAccounts().firstOrNull { it.id == accountId }
            ?: LegendAuthenticatedAccount(accountId, cached.displayName)
        activeInteractiveSignInUtc = cached.interactiveSignInUtc
        return establish(cached.participantType)
    }

    suspend fun signIn(activity: Activity, preservingActiveSession: Boolean = false): SessionState {
        val priorCredential = activeCredential
        val priorInteractiveSignInUtc = activeInteractiveSignInUtc
        return try {
            val credential = auth.signIn(activity, forceReauthentication = requiresFreshInteractiveSignIn)
            requiresFreshInteractiveSignIn = false
            activeCredential = credential
            activeInteractiveSignInUtc = Instant.now().toString()
            establish(null)
        } catch (error: Throwable) {
            if (preservingActiveSession) {
                activeCredential = priorCredential
                activeInteractiveSignInUtc = priorInteractiveSignInUtc
            }
            throw error
        }
    }

    suspend fun selectRole(role: String): SessionState {
        val response = apiClient().api.selectRole(SelectRoleRequest(role)).legendBody()
        return authenticated(response.actor, response.permittedParticipantTypes, response.capabilities ?: MobileCapabilities())
    }

    suspend fun switchSignedInAccount(accountId: String): SessionState {
        val cached = cache.selectAccount(accountId)
            ?: return SessionState.Failure("That signed-in Legend account is no longer available.")
        if (cached.requiresInteractiveSignIn(LegendAccountSessionPolicy.InteractiveSignInRetentionDays)) {
            return SessionState.Failure("This account needs a fresh secure sign-in before it can be reopened.")
        }
        auth.restoreAccessToken(accountId)
            ?: return SessionState.Failure("That signed-in Legend account is no longer available.")
        activeCredential = auth.signedInAccounts().firstOrNull { it.id == accountId }
            ?: LegendAuthenticatedAccount(accountId, cached.displayName)
        activeInteractiveSignInUtc = cached.interactiveSignInUtc
        return establish(cached.participantType)
    }

    suspend fun signOut() {
        val accountId = runCatching { cache.read()?.accountId }.getOrNull()
        runCatching { beforeSignOut() }
        runCatching { auth.signOut(accountId) }
        if (accountId != null) {
            runCatching { cache.removeAccount(accountId) }
        } else {
            cache.clear()
        }
        activeCredential = null
        activeInteractiveSignInUtc = null
    }

    private suspend fun establish(preferredRole: String?): SessionState {
        val response = apiClient().api.session(preferredRole).legendBody()
        if (!response.authenticated) return SessionState.SignedOut
        if (response.requiresParticipantSelection) return SessionState.RoleSelection(response.permittedParticipantTypes)
        val actor = response.actor ?: return SessionState.Failure("Legend could not resolve this account.", response.correlationId)
        return authenticated(actor, response.permittedParticipantTypes, response.capabilities)
    }

    private suspend fun authenticated(actor: MobileActor, roles: List<String>, capabilities: MobileCapabilities): SessionState {
        val existing = cache.read()
        val accountId = activeCredential?.id ?: existing?.accountId ?: actor.identity.userId
        val interactiveSignInUtc = activeInteractiveSignInUtc ?: existing?.interactiveSignInUtc
        cache.write(
            CachedLegendSession(
                actorId = actor.identity.userId,
                participantType = actor.identity.participantType,
                displayName = actor.displayName,
                cachedUtc = Instant.now().toString(),
                accountId = accountId,
                interactiveSignInUtc = interactiveSignInUtc,
            )
        )
        val signedInAccounts = cache.accounts()
            .filter { !it.requiresInteractiveSignIn(LegendAccountSessionPolicy.InteractiveSignInRetentionDays) }
            .mapNotNull { saved ->
                saved.accountId?.let {
                    SignedInLegendAccount(it, saved.displayName, saved.participantType)
                }
            }
        return SessionState.Authenticated(
            ActiveLegendSession(actor, roles, capabilities, accountId, signedInAccounts)
        )
    }
}

class SessionViewModel(private val repository: SessionRepository) : ViewModel() {
    private val _state = MutableStateFlow<SessionState>(SessionState.Loading)
    val state: StateFlow<SessionState> = _state.asStateFlow()

    fun restore() = viewModelScope.launch {
        _state.value = runCatching { repository.restore() }
            .getOrElse { SessionState.Failure("We could not restore your secure Legend session.") }
    }

    fun signIn(activity: Activity) = viewModelScope.launch {
        _state.value = SessionState.Authenticating
        _state.value = runCatching { repository.signIn(activity) }
            .getOrElse { SessionState.Failure("Secure sign-in could not be completed.") }
    }

    fun addAccount(activity: Activity) = viewModelScope.launch {
        val priorState = _state.value
        _state.value = SessionState.Authenticating
        _state.value = runCatching { repository.signIn(activity, preservingActiveSession = true) }
            .getOrElse {
                (priorState as? SessionState.Authenticated)
                    ?: SessionState.Failure("Secure sign-in could not be completed.")
            }
    }

    fun selectRole(role: String) = viewModelScope.launch {
        _state.value = SessionState.Authenticating
        _state.value = runCatching { repository.selectRole(role) }
            .getOrElse { SessionState.Failure("That Legend account is not available.") }
    }

    fun switchSignedInAccount(accountId: String) = viewModelScope.launch {
        _state.value = SessionState.Authenticating
        _state.value = runCatching { repository.switchSignedInAccount(accountId) }
            .getOrElse { SessionState.Failure("That signed-in Legend account is not available.") }
    }

    fun cycleAccount() {
        val active = (_state.value as? SessionState.Authenticated)?.session ?: return
        active.permittedParticipantTypes
            .firstOrNull { !it.equals(active.actor.identity.participantType, ignoreCase = true) }
            ?.let { selectRole(it); return }

        val currentIndex = active.signedInAccounts.indexOfFirst { it.accountId == active.accountId }
        if (currentIndex < 0 || active.signedInAccounts.size < 2) return
        val next = active.signedInAccounts[(currentIndex + 1) % active.signedInAccounts.size]
        switchSignedInAccount(next.accountId)
    }

    fun signOut() = viewModelScope.launch {
        repository.signOut()
        _state.value = SessionState.SignedOut
    }
}
