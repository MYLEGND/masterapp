package com.mylegnd.legend.registered.core.push

import android.content.Context
import com.google.android.gms.tasks.Task
import com.google.firebase.FirebaseApp
import com.google.firebase.installations.FirebaseInstallations
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
 * Registers an opaque Firebase Installation ID only after the server has
 * resolved an authenticated LEGEND actor. Identifier storage and notification
 * rules remain out of Android; the ID is held only for the registration call.
 */
class FcmPushRegistrationCoordinator(
    private val context: Context,
    private val deviceRepository: NotificationDeviceRepository,
    private val sessionStore: SecureSessionStore,
) {
    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.IO)

    fun registerForAuthenticatedActor(participantType: String) {
        if (participantType.isBlank() || !isFirebaseConfigured()) return
        scope.launch {
            // register() always delivers the current FID through onRegistered,
            // including when this installation was already registered.
            FirebaseMessaging.getInstance().register().awaitCompletion()
        }
    }

    fun registerInstallation(installationId: String) {
        if (installationId.isBlank()) return
        scope.launch {
            val participantType = runCatching { sessionStore.read()?.participantType }.getOrNull()
                ?.trim()
                ?.takeIf(String::isNotBlank)
                ?: return@launch
            deviceRepository.registerFcm(participantType, installationId)
        }
    }

    suspend fun deactivateForCurrentActor() {
        if (!isFirebaseConfigured()) return
        val participantType = runCatching { sessionStore.read()?.participantType }.getOrNull()
            ?.trim()
            ?.takeIf(String::isNotBlank)
        val installationId = firebaseInstallationIdOrNull()
        try {
            if (participantType != null && installationId != null) {
                deviceRepository.deactivateFcm(participantType, installationId)
            }
        } finally {
            FirebaseMessaging.getInstance().unregister().awaitCompletion()
        }
    }

    private fun isFirebaseConfigured(): Boolean {
        // A missing google-services.json is an intentional non-production
        // configuration state, not a reason to manufacture a Firebase identity.
        return FirebaseApp.initializeApp(context.applicationContext) != null
    }

    private suspend fun firebaseInstallationIdOrNull(): String? =
        FirebaseInstallations.getInstance().id.awaitResult()?.takeIf(String::isNotBlank)
}

private suspend fun <T> Task<T>.awaitResult(): T? = suspendCancellableCoroutine { continuation ->
    addOnCompleteListener { task ->
        if (!continuation.isActive) return@addOnCompleteListener
        continuation.resume(if (task.isSuccessful) task.result else null)
    }
}

private suspend fun Task<Void>.awaitCompletion() = suspendCancellableCoroutine { continuation ->
    addOnCompleteListener { task ->
        if (continuation.isActive) continuation.resume(Unit)
    }
}
