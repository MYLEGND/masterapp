package com.mylegnd.legend.registered.core.navigation

import android.content.Intent
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.asStateFlow

data class LegendNotificationDestination(val conversationId: String?, val openNotifications: Boolean = false)

/**
 * In-process handoff for server-issued FCM route metadata. It owns no
 * notification meaning; the server has already selected the actor, title,
 * localized detail, and conversation. Destinations are consumed once.
 */
class LegendNotificationNavigation {
    private val mutableDestination = MutableStateFlow<LegendNotificationDestination?>(null)
    val destination = mutableDestination.asStateFlow()

    fun capture(intent: Intent?) {
        // A foreground notification is created by LegendFirebaseMessagingService
        // and uses the app-private extra. In the background, FCM's system tray
        // preserves the server data payload under its original key. Both paths
        // carry the same server-issued conversation identifier.
        val conversationId = intent?.getStringExtra(EXTRA_CONVERSATION_ID)
            ?: intent?.getStringExtra(FCM_CONVERSATION_ID)
        val normalizedConversationId = conversationId
            ?.trim()
            ?.takeIf(String::isNotBlank)
        val notificationId = intent?.getStringExtra(EXTRA_NOTIFICATION_ID)
            ?: intent?.getStringExtra(FCM_NOTIFICATION_ID)
        val hasNotification = !notificationId.isNullOrBlank()

        when {
            normalizedConversationId != null ->
                mutableDestination.value = LegendNotificationDestination(normalizedConversationId)
            hasNotification ->
                mutableDestination.value = LegendNotificationDestination(null, openNotifications = true)
        }
    }

    fun markHandled(destination: LegendNotificationDestination) {
        if (mutableDestination.value == destination) mutableDestination.value = null
    }

    companion object {
        const val EXTRA_CONVERSATION_ID = "com.mylegnd.legend.registered.CONVERSATION_ID"
        const val EXTRA_NOTIFICATION_ID = "com.mylegnd.legend.registered.NOTIFICATION_ID"
        private const val FCM_CONVERSATION_ID = "conversationId"
        private const val FCM_NOTIFICATION_ID = "notificationId"
    }
}
