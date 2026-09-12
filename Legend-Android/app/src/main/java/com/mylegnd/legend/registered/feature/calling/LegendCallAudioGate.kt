package com.mylegnd.legend.registered.feature.calling

import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.first

/** One readiness gate for the existing Telecom focus and call foreground service. */
internal class LegendCallAudioGate {
    private data class Readiness(val callId: String? = null, val foreground: Boolean = false, val focus: Boolean = false)
    private val state = MutableStateFlow(Readiness())
    private var service: Any? = null
    var mediaSession: Int = 0
        private set
    val hasFocus: Boolean get() = state.value.focus
    val foregroundIsReady: Boolean get() = state.value.foreground
    fun bindService(owner: Any) {
        if (service !== owner) { service = owner; state.value = state.value.copy(focus = false) }
    }
    fun unbindService(owner: Any): Boolean {
        if (service !== owner) return false
        service = null
        state.value = state.value.copy(focus = false)
        return true
    }

    fun begin(callId: String) {
        if (state.value.callId != callId) {
            mediaSession++
            state.value = state.value.copy(callId = callId, foreground = false)
        }
    }
    fun foregroundReady(callId: String) {
        if (state.value.callId == callId) state.value = state.value.copy(foreground = true)
    }
    fun focusChanged(owner: Any, granted: Boolean): Boolean {
        if (service !== owner) return false
        state.value = state.value.copy(focus = granted)
        return true
    }
    // Telecom grants focus to ConnectionService, not an individual connection. A
    // consecutive call may reuse that grant until the service receives focus lost.
    fun ended() { state.value = state.value.copy(callId = null, foreground = false) }

    suspend fun awaitReady(callId: String, requireTelecomFocus: Boolean) {
        val ready = state.first { it.callId != callId || (it.foreground && (!requireTelecomFocus || it.focus)) }
        if (ready.callId != callId) throw CancellationException("The call ended before Android audio was ready.")
    }
}
