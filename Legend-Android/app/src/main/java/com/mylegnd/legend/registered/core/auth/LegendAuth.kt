package com.mylegnd.legend.registered.core.auth

import android.app.Activity
import android.content.Context
import com.microsoft.identity.client.*
import com.microsoft.identity.client.exception.MsalException
import com.mylegnd.legend.registered.R
import com.mylegnd.legend.registered.core.config.LegendRuntimeConfiguration
import com.mylegnd.legend.registered.core.logging.LegendLogger
import com.mylegnd.legend.registered.core.network.AccessTokenProvider
import kotlinx.coroutines.suspendCancellableCoroutine
import kotlin.coroutines.resume
import kotlin.coroutines.resumeWithException
import java.time.Instant

/** MSAL owns OAuth/refresh-token persistence; bearer tokens are never written by Legend code. */
interface LegendAuthClient {
    suspend fun restoreAccessToken(accountId: String? = null): String?
    suspend fun signIn(activity: Activity, forceReauthentication: Boolean = false): LegendAuthenticatedAccount
    suspend fun signedInAccounts(): List<LegendAuthenticatedAccount>
    suspend fun signOut(accountId: String? = null)
}

data class LegendAuthenticatedAccount(val id: String, val displayName: String)

/**
 * One bearer authority for the existing API client. Ordinary accounts remain
 * owned by MSAL. The server-issued App Review token is deliberately held only
 * in memory, has no refresh credential, and can never outlive its signed TTL.
 */
class LegendBearerTokenAuthority(
    private val auth: LegendAuthClient,
) : AccessTokenProvider {
    private data class ReviewCredential(val token: String, val expiresUtc: Instant)
    @Volatile private var reviewCredential: ReviewCredential? = null
    @Volatile private var reviewSessionActive = false

    fun activateReviewCredential(token: String, expiresInSeconds: Int) {
        require(token.isNotBlank() && expiresInSeconds > 0) { "The review credential is invalid." }
        reviewCredential = ReviewCredential(token, Instant.now().plusSeconds(expiresInSeconds.toLong()))
        reviewSessionActive = true
    }

    fun clearReviewCredential() {
        reviewCredential = null
        reviewSessionActive = false
    }

    override suspend fun accessToken(): String? {
        if (reviewSessionActive) {
            val credential = reviewCredential
            if (credential != null && credential.expiresUtc.isAfter(Instant.now().plusSeconds(30))) {
                return credential.token
            }
            reviewCredential = null
            return null
        }
        return auth.restoreAccessToken()
    }
}

class MsalLegendAuthClient(private val context: Context, private val configuration: LegendRuntimeConfiguration) : LegendAuthClient {
    private var activeAccountId: String? = null

    private suspend fun application(): IMultipleAccountPublicClientApplication = suspendCancellableCoroutine { continuation ->
        PublicClientApplication.createMultipleAccountPublicClientApplication(context.applicationContext, R.raw.legend_msal_config,
            object : IPublicClientApplication.IMultipleAccountApplicationCreatedListener {
                override fun onCreated(application: IMultipleAccountPublicClientApplication) = continuation.resume(application)
                override fun onError(exception: MsalException) {
                    LegendLogger.authenticationFailure("initialize", exception)
                    continuation.resumeWithException(exception)
                }
            })
    }

    override suspend fun restoreAccessToken(accountId: String?): String? {
        if (!configuration.isReady) return null
        val app = application()
        val requestedAccountId = accountId ?: activeAccountId
        val account = accounts(app).firstOrNull { requestedAccountId == null || it.id == requestedAccountId } ?: return null
        activeAccountId = account.id
        return acquireSilent(app, account)
    }

    override suspend fun signIn(activity: Activity, forceReauthentication: Boolean): LegendAuthenticatedAccount {
        check(configuration.isReady) { "Mobile configuration is incomplete." }
        val app = application()
        return suspendCancellableCoroutine { continuation ->
            val parameters = AcquireTokenParameters.Builder()
                .startAuthorizationFromActivity(activity)
                .withScopes(resourceScopes())
                .withPrompt(if (forceReauthentication) Prompt.LOGIN else Prompt.SELECT_ACCOUNT)
                .withCallback(object : AuthenticationCallback {
                    override fun onSuccess(result: IAuthenticationResult) {
                        val account = result.account
                        activeAccountId = account.id
                        continuation.resume(LegendAuthenticatedAccount(account.id, account.username.orEmpty()))
                    }
                    override fun onError(exception: MsalException) {
                        LegendLogger.authenticationFailure("interactive", exception)
                        continuation.resumeWithException(exception)
                    }
                    override fun onCancel() = continuation.resumeWithException(AuthenticationCancelledException())
                }).build()
            app.acquireToken(parameters)
        }
    }

    override suspend fun signedInAccounts(): List<LegendAuthenticatedAccount> {
        if (!configuration.isReady) return emptyList()
        return accounts(application()).map { LegendAuthenticatedAccount(it.id, it.username.orEmpty()) }
    }

    override suspend fun signOut(accountId: String?) {
        if (!configuration.isReady) return
        val app = application()
        val account = accounts(app).firstOrNull { it.id == (accountId ?: activeAccountId) } ?: return
        suspendCancellableCoroutine<Unit> { continuation ->
            app.removeAccount(account, object : IMultipleAccountPublicClientApplication.RemoveAccountCallback {
                override fun onRemoved() {
                    if (activeAccountId == account.id) activeAccountId = null
                    continuation.resume(Unit)
                }
                override fun onError(exception: MsalException) {
                    LegendLogger.authenticationFailure("remove_account", exception)
                    continuation.resumeWithException(exception)
                }
            })
        }
    }

    private suspend fun accounts(app: IMultipleAccountPublicClientApplication): List<IAccount> = suspendCancellableCoroutine { continuation ->
        app.getAccounts(object : IPublicClientApplication.LoadAccountsCallback {
            override fun onTaskCompleted(accounts: List<IAccount>) = continuation.resume(accounts)
            override fun onError(exception: MsalException) {
                LegendLogger.authenticationFailure("accounts", exception)
                continuation.resumeWithException(exception)
            }
        })
    }

    private suspend fun acquireSilent(app: IMultipleAccountPublicClientApplication, account: IAccount): String? = suspendCancellableCoroutine { continuation ->
        val parameters = AcquireTokenSilentParameters.Builder().forAccount(account).fromAuthority(configuration.entraAuthority)
            .withScopes(resourceScopes())
            .withCallback(object : SilentAuthenticationCallback {
                override fun onSuccess(result: IAuthenticationResult) = continuation.resume(result.accessToken)
                override fun onError(exception: MsalException) = continuation.resume(null)
            }).build()
        app.acquireTokenSilentAsync(parameters)
    }

    /** MSAL adds OIDC scopes itself; only the sanctioned protected API scope is requested here. */
    private fun resourceScopes(): List<String> = configuration.entraScope
        .split(' ')
        .map(String::trim)
        .filter(String::isNotBlank)
        .filterNot { it in setOf("openid", "profile", "offline_access") }
}

class AuthenticationCancelledException : IllegalStateException("Sign-in was cancelled.")
