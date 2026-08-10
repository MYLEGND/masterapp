package com.mylegnd.legend.registered.core.auth

import android.app.Activity
import android.content.Context
import com.microsoft.identity.client.*
import com.microsoft.identity.client.exception.MsalException
import com.mylegnd.legend.registered.R
import com.mylegnd.legend.registered.core.config.LegendRuntimeConfiguration
import kotlinx.coroutines.suspendCancellableCoroutine
import kotlin.coroutines.resume
import kotlin.coroutines.resumeWithException

/** MSAL owns OAuth/refresh-token persistence; bearer tokens are never written by Legend code. */
interface LegendAuthClient {
    suspend fun restoreAccessToken(): String?
    suspend fun signIn(activity: Activity): String
    suspend fun signOut()
}

class MsalLegendAuthClient(private val context: Context, private val configuration: LegendRuntimeConfiguration) : LegendAuthClient {
    private suspend fun application(): ISingleAccountPublicClientApplication = suspendCancellableCoroutine { continuation ->
        PublicClientApplication.createSingleAccountPublicClientApplication(context.applicationContext, R.raw.legend_msal_config,
            object : IPublicClientApplication.ISingleAccountApplicationCreatedListener {
                override fun onCreated(application: ISingleAccountPublicClientApplication) = continuation.resume(application)
                override fun onError(exception: MsalException) = continuation.resumeWithException(exception)
            })
    }

    override suspend fun restoreAccessToken(): String? {
        if (!configuration.isReady) return null
        val app = application()
        val account = suspendCancellableCoroutine<IAccount?> { continuation ->
            app.getCurrentAccountAsync(object : ISingleAccountPublicClientApplication.CurrentAccountCallback {
                override fun onAccountLoaded(activeAccount: IAccount?) = continuation.resume(activeAccount)
                override fun onAccountChanged(priorAccount: IAccount?, currentAccount: IAccount?) = continuation.resume(currentAccount)
                override fun onError(exception: MsalException) = continuation.resumeWithException(exception)
            })
        } ?: return null
        return acquireSilent(app, account)
    }

    override suspend fun signIn(activity: Activity): String {
        check(configuration.isReady) { "Mobile configuration is incomplete." }
        val app = application()
        return suspendCancellableCoroutine { continuation ->
            val parameters = SignInParameters.builder().withActivity(activity)
                .withScopes(resourceScopes())
                .withCallback(object : AuthenticationCallback {
                    override fun onSuccess(result: IAuthenticationResult) = continuation.resume(result.accessToken)
                    override fun onError(exception: MsalException) = continuation.resumeWithException(exception)
                    override fun onCancel() = continuation.resumeWithException(AuthenticationCancelledException())
                }).build()
            app.signIn(parameters)
        }
    }

    override suspend fun signOut() {
        if (!configuration.isReady) return
        val app = application()
        suspendCancellableCoroutine<Unit> { continuation -> app.signOut(object : ISingleAccountPublicClientApplication.SignOutCallback {
            override fun onSignOut() = continuation.resume(Unit)
            override fun onError(exception: MsalException) = continuation.resumeWithException(exception)
        }) }
    }

    private suspend fun acquireSilent(app: ISingleAccountPublicClientApplication, account: IAccount): String? = suspendCancellableCoroutine { continuation ->
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
