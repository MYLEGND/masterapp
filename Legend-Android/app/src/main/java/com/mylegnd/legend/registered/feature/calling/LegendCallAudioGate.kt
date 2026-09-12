package com.mylegnd.legend.registered.feature.calling

import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.first

/** One readiness gate for the existing Telecom focus and call foreground service. */
internal class LegendCallAudioGate {
    private data class Readiness(val callId: String? = null, val foreground: Boolean = false, val focus: Boolean = false)
    private val state = MutableStateFlow(Readiness())
    val hasFocus: Boolean get() = state.value.focus

    fun begin(callId: String) {
        if (state.value.callId != callId) state.value = state.value.copy(callId = callId, foreground = false)
    }
    fun foregroundReady(callId: String) {
        if (state.value.callId == callId) state.value = state.value.copy(foreground = true)
    }
    fun focusChanged(granted: Boolean) { state.value = state.value.copy(focus = granted) }
    fun ended() { state.value = state.value.copy(callId = null, foreground = false, focus = false) }

    suspend fun awaitReady(callId: String, requireTelecomFocus: Boolean) {
        val ready = state.first { it.callId != callId || (it.foreground && (!requireTelecomFocus || it.focus)) }
        if (ready.callId != callId) throw CancellationException("The call ended before Android audio was ready.")
    }
}
