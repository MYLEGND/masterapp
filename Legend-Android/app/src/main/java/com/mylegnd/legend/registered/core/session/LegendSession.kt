package com.mylegnd.legend.registered.core.session

import android.app.Activity
import androidx.lifecycle.ViewModel
import androidx.lifecycle.viewModelScope
import com.mylegnd.legend.registered.core.auth.CachedLegendSession
import com.mylegnd.legend.registered.core.auth.AuthenticationCancelledException
import com.mylegnd.legend.registered.core.auth.AuthenticationConnectivityException
import com.mylegnd.legend.registered.core.auth.LegendAuthClient
import com.mylegnd.legend.registered.core.auth.LegendAuthenticatedAccount
import com.mylegnd.legend.registered.core.auth.LegendBearerTokenAuthority
import com.mylegnd.legend.registered.core.auth.LegendSessionStoring
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
    val requiresSignIn: Boolean = false,
    val avatar: MobileAvatar? = null,
)

data class ActiveLegendSession(
    val actor: MobileActor,
    val permittedParticipantTypes: List<String>,
    val capabilities: MobileCapabilities,
    val accountId: String,
    val signedInAccounts: List<SignedInLegendAccount>,
    val preferredLanguageCode: String? = null,
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
    private val bearerTokenAuthority: LegendBearerTokenAuthority,
    private val apiClient: () -> LegendApiClient,
    private val cache: LegendSessionStoring,
    private val beforeSignOut: suspend () -> Unit = {},
) {
    private var activeCredential: LegendAuthenticatedAccount? = null
    private var activeInteractiveSignInUtc: String? = null
    private var accountIsProvisional = false


    suspend fun restore(): SessionState {
        if (!configuration.isReady) return SessionState.ConfigurationRequired
        val cached = runCatching { cache.read() }.getOrNull() ?: return SessionState.SignedOut
        if (cached.requiresInteractiveSignIn(LegendAccountSessionPolicy.InteractiveSignInRetentionDays)) {
            return SessionState.SignedOut
        }

        val accountId = cached.accountId ?: return SessionState.SignedOut
        runCatching { auth.restoreAccessToken(accountId) }.getOrNull()
            ?: return SessionState.SignedOut
        activeCredential = auth.signedInAccounts().firstOrNull { it.id == accountId }
            ?: LegendAuthenticatedAccount(accountId, cached.displayName)
        activeInteractiveSignInUtc = cached.interactiveSignInUtc
        bearerTokenAuthority.activateAccount(cached)
        return establish(cached.participantType)
    }

    suspend fun signIn(activity: Activity, preservingActiveSession: Boolean = false): SessionState =
        authenticateAccount(preservingActiveSession) { auth.signIn(activity, forceReauthentication = true) }

    internal suspend fun authenticateAccount(
        preservingActiveSession: Boolean,
        authenticate: suspend () -> LegendAuthenticatedAccount,
    ): SessionState {
        val priorCredential = activeCredential
        val priorInteractiveSignInUtc = activeInteractiveSignInUtc
        val priorCached = cache.read()
        return try {
            val credential = authenticate()
            accountIsProvisional = true
            bearerTokenAuthority.clearReviewCredential()
            activeCredential = credential
            activeInteractiveSignInUtc = Instant.now().toString()
            bearerTokenAuthority.activateAccount(CachedLegendSession(credential.id, "", credential.displayName,
                Instant.now().toString(), credential.id, activeInteractiveSignInUtc))
            val result = establish(null)
            check(result is SessionState.Authenticated || result is SessionState.RoleSelection) { "Account could not be confirmed." }
            result
        } catch (error: Throwable) {
            if (preservingActiveSession) {
                activeCredential = priorCredential
                accountIsProvisional = false
                activeInteractiveSignInUtc = priorInteractiveSignInUtc
                bearerTokenAuthority.activateAccount(priorCached)
                priorCached?.accountId?.let { cache.selectAccount(it) }
            }
            throw error
        }
    }

    suspend fun signInForAppReview(username: String, password: String): SessionState {
        check(configuration.isReady) { "Mobile configuration is incomplete." }
        val normalizedUsername = username.trim()
        require(normalizedUsername.isNotBlank() && password.isNotBlank()) {
            "Enter the App Review username and password."
        }
        bearerTokenAuthority.clearReviewCredential()
        return try {
            val response = apiClient().api
                .reviewSession(MobileReviewSignInRequest(normalizedUsername, password))
                .legendBody()
            require(response.accessToken.isNotBlank() && response.expiresIn > 5 * 60) {
                "The App Review credential lifetime is invalid."
            }
            bearerTokenAuthority.activateReviewCredential(response.accessToken, response.expiresIn)
            activeCredential = LegendAuthenticatedAccount("review-session", normalizedUsername)
            activeInteractiveSignInUtc = Instant.now().toString()
            establish(null)
        } catch (error: Throwable) {
            bearerTokenAuthority.clearReviewCredential()
            activeCredential = null
            activeInteractiveSignInUtc = null
            throw error
        }
    }

    suspend fun selectRole(role: String): SessionState {
        val response = apiClient().api.selectRole(SelectRoleRequest(role)).legendBody()
        return authenticated(
            response.actor,
            response.permittedParticipantTypes,
            response.capabilities ?: MobileCapabilities(),
            response.preferredLanguageCode,
        )
    }

    suspend fun accountRequiresSignIn(accountId: String): Boolean = cache.accounts()
        .firstOrNull { it.accountId == accountId }
        ?.requiresInteractiveSignIn(LegendAccountSessionPolicy.InteractiveSignInRetentionDays) != false

    fun requiresInteractiveSignIn(): Boolean = bearerTokenAuthority.requiresInteractiveSignIn()

    suspend fun switchSignedInAccount(accountId: String): SessionState {
        val priorCredential = activeCredential
        val priorDate = activeInteractiveSignInUtc
        val priorCached = cache.read()
        try {
            val cached = cache.accounts().firstOrNull { it.accountId == accountId }
                ?: error("That account is no longer available.")
            check(!cached.requiresInteractiveSignIn(LegendAccountSessionPolicy.InteractiveSignInRetentionDays))
            checkNotNull(auth.restoreAccessToken(accountId))
            activeCredential = LegendAuthenticatedAccount(accountId, cached.displayName)
            activeInteractiveSignInUtc = cached.interactiveSignInUtc
            bearerTokenAuthority.clearReviewCredential()
            bearerTokenAuthority.activateAccount(cached)
            val result = establish(cached.participantType)
            check(result is SessionState.Authenticated || result is SessionState.RoleSelection)
            return result
        } catch (error: Throwable) {
            activeCredential = priorCredential
            activeInteractiveSignInUtc = priorDate
            bearerTokenAuthority.activateAccount(priorCached)
            priorCached?.accountId?.let { cache.selectAccount(it) }
            throw error
        }
    }

    suspend fun signOut() {
        val selectedId = runCatching { cache.read()?.accountId }.getOrNull()
        val accountId = activeCredential?.id ?: selectedId
        if (!accountIsProvisional && accountId == selectedId) runCatching { beforeSignOut() }
        bearerTokenAuthority.clearReviewCredential()
        val retainsExistingAccount = accountIsProvisional && cache.accounts().any { it.accountId == accountId }
        if (!retainsExistingAccount) runCatching { auth.signOut(accountId) }
        if (accountId != null && !retainsExistingAccount) {
            runCatching { cache.removeAccount(accountId) }
        } else if (accountId == null) {
            cache.clear()
        }
        accountIsProvisional = false
        activeCredential = null
        activeInteractiveSignInUtc = null
        bearerTokenAuthority.activateAccount(null)
    }

    private suspend fun establish(preferredRole: String?): SessionState {
        val response = apiClient().api.session(preferredRole).legendBody()
        if (!response.authenticated) return SessionState.SignedOut
        if (response.requiresParticipantSelection) {
            if (preferredRole != null && preferredRole in response.permittedParticipantTypes) return selectRole(preferredRole)
            return SessionState.RoleSelection(response.permittedParticipantTypes)
        }
        val actor = response.actor ?: return SessionState.Failure("Legend could not resolve this account.", response.correlationId)
        return authenticated(
            actor,
            response.permittedParticipantTypes,
            response.capabilities,
            response.preferredLanguageCode,
        )
    }

    private suspend fun authenticated(
        actor: MobileActor,
        roles: List<String>,
        capabilities: MobileCapabilities,
        preferredLanguageCode: String?,
    ): SessionState {
        val existing = cache.read()
        val accountId = activeCredential?.id ?: existing?.accountId ?: actor.identity.userId
        val interactiveSignInUtc = activeInteractiveSignInUtc ?: existing?.interactiveSignInUtc
        cache.write(
            CachedLegendSession(
                actorId = actor.identity.userId,
                participantType = actor.identity.participantType,
                displayName = actor.displayName,
                avatar = actor.avatar,
                cachedUtc = Instant.now().toString(),
                accountId = accountId,
                interactiveSignInUtc = interactiveSignInUtc,
                preferredLanguageCode = preferredLanguageCode,
            )
        )
        accountIsProvisional = false
        val signedInAccounts = cache.accounts()
            .mapNotNull { saved ->
                saved.accountId?.let {
                    SignedInLegendAccount(it, saved.displayName, saved.participantType,
                        saved.requiresInteractiveSignIn(LegendAccountSessionPolicy.InteractiveSignInRetentionDays), saved.avatar)
                }
            }
        return SessionState.Authenticated(
            ActiveLegendSession(
                actor,
                roles,
                capabilities,
                accountId,
                signedInAccounts,
                preferredLanguageCode,
            )
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
            .getOrElse(::signInFailure)
    }

    fun signInForAppReview(username: String, password: String) = viewModelScope.launch {
        _state.value = SessionState.Authenticating
        _state.value = runCatching { repository.signInForAppReview(username, password) }
            .getOrElse { SessionState.Failure("The App Review credentials could not be verified.") }
    }

    fun addAccount(activity: Activity) = viewModelScope.launch {
        val priorState = _state.value
        if (priorState !is SessionState.Authenticated) return@launch
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

    fun switchSignedInAccount(accountId: String, activity: Activity? = null) = viewModelScope.launch {
        val priorState = _state.value
        if (priorState !is SessionState.Authenticated) return@launch
        if (repository.accountRequiresSignIn(accountId)) {
            if (activity != null) addAccount(activity)
            return@launch
        }
        _state.value = SessionState.Authenticating
        _state.value = runCatching { repository.switchSignedInAccount(accountId) }
            .getOrElse { priorState }
    }

    fun enforceAccountSignInLifetime() {
        if (_state.value is SessionState.Authenticated && repository.requiresInteractiveSignIn()) {
            _state.value = SessionState.SignedOut
        }
    }

    fun cycleAccount() {
        val active = (_state.value as? SessionState.Authenticated)?.session ?: return
        active.permittedParticipantTypes
            .firstOrNull { !it.equals(active.actor.identity.participantType, ignoreCase = true) }
            ?.let { selectRole(it); return }

        val currentIndex = active.signedInAccounts.indexOfFirst { it.accountId == active.accountId }
        if (currentIndex < 0 || active.signedInAccounts.size < 2) return
        val next = (active.signedInAccounts.drop(currentIndex + 1) + active.signedInAccounts.take(currentIndex))
            .firstOrNull { !it.requiresSignIn } ?: return
        switchSignedInAccount(next.accountId)
    }

    fun signOut() = viewModelScope.launch {
        val wasSelectingRole = _state.value is SessionState.RoleSelection
        repository.signOut()
        _state.value = if (wasSelectingRole) runCatching { repository.restore() }.getOrDefault(SessionState.SignedOut) else SessionState.SignedOut
    }

    private fun signInFailure(error: Throwable): SessionState = when (error) {
        is AuthenticationCancelledException -> SessionState.SignedOut
        is AuthenticationConnectivityException -> SessionState.Failure(
            "Secure sign-in needs a working internet connection. Check the connection and try again.",
        )
        else -> SessionState.Failure("Secure sign-in could not be completed.")
    }
}
