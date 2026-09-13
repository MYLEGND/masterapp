package com.mylegnd.legend.registered.feature.calling

import kotlinx.coroutines.NonCancellable
import kotlinx.coroutines.withContext
import kotlinx.coroutines.withTimeout

/** An accept response belongs to its original call, even after the user ends or switches calls. */
internal suspend fun completeLegendCallAcceptance(
    callId: String,
    isCurrent: () -> Boolean,
    accept: suspend () -> LegendCallSnapshot?,
    showAccepted: (LegendCallSnapshot) -> Unit,
    endOriginal: suspend () -> Unit,
) {
    if (!isCurrent()) return
    suspend fun cleanup() = withContext(NonCancellable) {
        runCatching { withTimeout(2_000) { endOriginal() } }
        Unit
    }
    try {
        val accepted = accept()
        if (!isCurrent()) {
            if (accepted?.id == callId && !accepted.terminal) cleanup()
            return
        }
        if (accepted?.id == callId && !accepted.terminal) showAccepted(accepted)
    } catch (error: Exception) {
        if (!isCurrent()) { cleanup(); return }
        throw error
    }
}
