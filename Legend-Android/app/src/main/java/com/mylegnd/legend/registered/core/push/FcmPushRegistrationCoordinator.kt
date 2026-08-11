package com.mylegnd.legend.registered.core.push

import android.content.Context
import com.google.android.gms.tasks.Task
import com.google.firebase.FirebaseApp
import com.google.firebase.messaging.FirebaseMessaging
import com.mylegnd.legend.registered.core.auth.SecureSessionStore
import com.mylegnd.legend.registered.data.NotificationDeviceRepository
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.launch
import kotlinx.coroutines.suspendCancellableCoroutine
import kotlin.coroutines.resume

/**
 * Registers an opaque FCM token only after the server has resolved an
 * authenticated LEGEND actor. Token storage and notification rules remain out
 * of Android; a token is held only for the duration of the registration call.
 */
class FcmPushRegistrationCoordinator(
    private val context: Context,
    private val deviceRepository: NotificationDeviceRepository,
    private val sessionStore: SecureSessionStore,
) {
    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.IO)

    fun registerForAuthenticatedActor(participantType: String) {
        if (!isFirebaseConfigured()) return
        scope.launch {
            fcmTokenOrNull()?.let { token -> deviceRepository.registerFcm(participantType, token) }
        }
    }

    fun registerFreshToken(token: String) {
        if (token.isBlank()) return
        scope.launch {
            val participantType = runCatching { sessionStore.read()?.participantType }.getOrNull()
                ?.trim()
                ?.takeIf(String::isNotBlank)
                ?: return@launch
            deviceRepository.registerFcm(participantType, token)
        }
    }

    suspend fun deactivateForCurrentActor() {
        val participantType = runCatching { sessionStore.read()?.participantType }.getOrNull()
            ?.trim()
            ?.takeIf(String::isNotBlank)
            ?: return
        fcmTokenOrNull()?.let { token ->
            deviceRepository.deactivateFcm(participantType, token)
        }
        if (isFirebaseConfigured()) FirebaseMessaging.getInstance().deleteToken().awaitCompletion()
    }

    private fun isFirebaseConfigured(): Boolean {
        // A missing google-services.json is an intentional non-production
        // configuration state, not a reason to manufacture a Firebase identity.
        return FirebaseApp.initializeApp(context.applicationContext) != null
    }

    private suspend fun fcmTokenOrNull(): String? {
        if (!isFirebaseConfigured()) return null
        return FirebaseMessaging.getInstance().token.awaitResult()
    }
}

private suspend fun <T> Task<T>.awaitResult(): T? = suspendCancellableCoroutine { continuation ->
    addOnCompleteListener { task ->
        if (!continuation.isActive) return@addOnCompleteListener
        continuation.resume(task.result.takeIf { task.isSuccessful })
    }
}

private suspend fun Task<Void>.awaitCompletion() = suspendCancellableCoroutine { continuation ->
    addOnCompleteListener { task ->
        if (continuation.isActive) continuation.resume(Unit)
    }
}
