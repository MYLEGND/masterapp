package com.mylegnd.legend.registered.core.realtime

import kotlinx.coroutines.flow.MutableSharedFlow
import kotlinx.coroutines.flow.asSharedFlow

/**
 * Current mobile contract decision: FCM wake-up + bounded REST reconciliation.
 * AgentPortal has no bearer-token SignalR mobile contract yet, so this intentionally never opens
 * the browser-session /messaginghub with an Android credential.
 */
object LegendRealtimeEvents {
    private val mutableEvents = MutableSharedFlow<String?>(extraBufferCapacity = 8)
    val conversationUpdates = mutableEvents.asSharedFlow()
    fun conversationUpdated(conversationId: String?) { mutableEvents.tryEmit(conversationId) }
}
