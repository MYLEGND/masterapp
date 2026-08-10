package com.mylegnd.legend.registered.core.navigation

import android.content.Intent
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.asStateFlow

data class LegendNotificationDestination(val conversationId: String?)

/**
 * In-process handoff for server-issued FCM route metadata. It owns no
 * notification meaning; the server has already selected the actor, title,
 * localized detail, and conversation. Destinations are consumed once.
 */
class LegendNotificationNavigation {
    private val mutableDestination = MutableStateFlow<LegendNotificationDestination?>(null)
    val destination = mutableDestination.asStateFlow()

    fun capture(intent: Intent?) {
        val conversationId = intent?.getStringExtra(EXTRA_CONVERSATION_ID)
            ?.trim()
            ?.takeIf(String::isNotBlank)
        if (conversationId != null) mutableDestination.value = LegendNotificationDestination(conversationId)
    }

    fun markHandled(destination: LegendNotificationDestination) {
        if (mutableDestination.value == destination) mutableDestination.value = null
    }

    companion object {
        const val EXTRA_CONVERSATION_ID = "com.mylegnd.legend.registered.CONVERSATION_ID"
    }
}
