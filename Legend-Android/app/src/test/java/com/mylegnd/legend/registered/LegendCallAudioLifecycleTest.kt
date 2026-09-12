package com.mylegnd.legend.registered

import com.mylegnd.legend.registered.feature.calling.*
import kotlinx.coroutines.*
import org.junit.Assert.*
import org.junit.Test

class LegendCallAudioLifecycleTest {
    @Test fun mediaWaitsForBothForegroundServiceAndActualTelecomFocus() = runBlocking {
        val gate = LegendCallAudioGate()
        gate.begin("call-a")
        val waiting = async(start = CoroutineStart.UNDISPATCHED) { gate.awaitReady("call-a", true) }
        gate.foregroundReady("call-a")
        yield()
        assertFalse(waiting.isCompleted)
        gate.focusChanged(true)
        waiting.await()
    }

    @Test fun focusBeforeForegroundServiceDoesNotStartMedia() = runBlocking {
        val gate = LegendCallAudioGate()
        gate.begin("call-a")
        gate.focusChanged(true)
        val waiting = async(start = CoroutineStart.UNDISPATCHED) { gate.awaitReady("call-a", true) }
        assertFalse(waiting.isCompleted)
        gate.foregroundReady("call-a")
        waiting.await()
    }

    @Test fun deniedFocusTimesOutAndEndedCallCannotBeRevivedByLateCallbacks() = runBlocking {
        val gate = LegendCallAudioGate()
        gate.begin("call-a")
        gate.foregroundReady("call-a")
        val denial = runCatching { withTimeout(20) { gate.awaitReady("call-a", true) } }.exceptionOrNull()
        assertTrue(denial is TimeoutCancellationException)
        gate.ended()
        gate.focusChanged(true)
        gate.foregroundReady("call-a")
        assertTrue(runCatching { gate.awaitReady("call-a", true) }.exceptionOrNull() is CancellationException)
    }

    @Test fun previousCallForegroundCallbackCannotUnlockReplacementCall() = runBlocking {
        val gate = LegendCallAudioGate()
        gate.begin("old")
        val old = async(start = CoroutineStart.UNDISPATCHED) { runCatching { gate.awaitReady("old", true) }.exceptionOrNull() }
        gate.ended()
        gate.begin("new")
        gate.foregroundReady("old")
        gate.focusChanged(true)
        val current = async(start = CoroutineStart.UNDISPATCHED) { gate.awaitReady("new", true) }
        yield()
        assertTrue(old.await() is CancellationException)
        assertFalse(current.isCompleted)
        gate.foregroundReady("new")
        current.await()
    }

    @Test fun lateAcceptNeverRestoresEndedOrReplacementCallAndEndsOnlyOriginal() = runBlocking {
        for (replacement in listOf<String?>(null, "new")) {
            var current: String? = "old"
            val response = CompletableDeferred<LegendCallSnapshot?>()
            val shown = mutableListOf<String>()
            val ended = mutableListOf<String>()
            val accepting = launch(start = CoroutineStart.UNDISPATCHED) {
                completeLegendCallAcceptance("old", { current == "old" }, { response.await() },
                    { shown.add(it.id) }, { ended.add("old") })
            }
            current = replacement
            response.complete(snapshot("old"))
            accepting.join()
            assertTrue(shown.isEmpty())
            assertEquals(listOf("old"), ended)
            assertEquals(replacement, current)
        }
    }

    @Test fun failedLateAcceptStillCleansOriginalButCurrentFailureRemainsVisible() = runBlocking {
        var current = true
        var cleaned = 0
        val response = CompletableDeferred<LegendCallSnapshot?>()
        val accepting = launch(start = CoroutineStart.UNDISPATCHED) {
            completeLegendCallAcceptance("old", { current }, { response.await() }, { fail("stale response") }, { cleaned++ })
        }
        current = false
        response.completeExceptionally(IllegalStateException("transport failed after accept"))
        accepting.join()
        assertEquals(1, cleaned)
        val error = runCatching {
            completeLegendCallAcceptance("old", { true }, { throw IllegalStateException("denied") }, {}, { cleaned++ })
        }.exceptionOrNull()
        assertTrue(error is IllegalStateException)
        assertEquals(1, cleaned)
    }

    private fun snapshot(id: String) = LegendCallSnapshot(id, "conversation", "caller", "Agent", "callee", "Client",
        "caller-device", "callee-device", "Caller", "Callee", false, "connecting", "2026-09-12T00:00:00Z",
        "2026-09-12T01:00:00Z", 1)
}
