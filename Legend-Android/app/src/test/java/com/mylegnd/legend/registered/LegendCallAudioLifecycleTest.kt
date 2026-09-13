package com.mylegnd.legend.registered

import com.mylegnd.legend.registered.feature.calling.*
import kotlinx.coroutines.*
import kotlinx.serialization.encodeToString
import kotlinx.serialization.json.Json
import kotlinx.serialization.json.jsonObject
import org.junit.Assert.*
import org.junit.Test

class LegendCallAudioLifecycleTest {
    @Test fun permissionResultsCannotAnswerEndOrShareIntoReplacementCallOrAccount() {
        val original = LegendCallPermissionScope("owner-a", "device-a", "call-a")
        for (replacement in listOf(original.copy(callId = "call-b"), original.copy(ownerId = "owner-b"),
            original.copy(deviceId = "device-b"), null)) {
            for (granted in listOf(true, false)) {
                val answer = LegendCallPermissionGate()
                val projection = LegendCallPermissionGate()
                assertTrue(answer.begin(original)); assertTrue(projection.begin(original))
                assertFalse(answer.begin(original)) // A second launch cannot overwrite the pending request.
                var answered = 0; var ended = 0; var captured = 0
                if (answer.consume(replacement) != null) { if (granted) answered++ else ended++ }
                if (projection.consume(replacement) != null && granted) captured++
                assertEquals(0, answered); assertEquals(0, ended); assertEquals(0, captured)
                assertNull(answer.consume(original)); assertNull(projection.consume(original))
            }
        }
        val current = LegendCallPermissionGate()
        assertTrue(current.begin(original)); assertEquals(original, current.consume(original))
        assertNull(current.consume(original)) // One OS consent result cannot be replayed.
        assertTrue(current.begin(original))
        val recreated = LegendCallPermissionGate(current.pendingScope())
        assertFalse(recreated.begin(original.copy(ownerId = "new-account")))
        assertNull(recreated.consume(original.copy(ownerId = "new-account")))
        assertTrue(recreated.begin(original.copy(callId = "next")))
        assertEquals(original.copy(callId = "next"), recreated.consume(original.copy(callId = "next")))
    }

    @Test fun sharedCallingPreferencesUseExistingCommandAndPreserveServerChoices() {
        val json = Json { ignoreUnknownKeys = true }
        val read = json.parseToJsonElement(json.encodeToString(LegendCallCommand("preferences", "device"))).jsonObject
        assertFalse(read.containsKey("preferences"))
        val value = LegendCallPreferences("soft", "profile")
        val write = json.decodeFromString<LegendCallCommand>(json.encodeToString(LegendCallCommand("preferences", "device", preferences = value)))
        assertEquals(value, write.preferences)
        assertEquals("preferences", write.action)
        val result = json.decodeFromString<LegendCallResult>("""{"succeeded":true,"preferences":{"ringtoneId":"soft","wallpaperMode":"profile"},"ringtones":[{"id":"soft","label":"Soft","resource":"legend_ringback"}],"wallpapers":[{"id":"profile","label":"Profile photo"}]}""")
        assertEquals(value, result.preferences)
        assertEquals("legend_ringback", result.ringtones!!.single().resource)
        assertEquals("profile", result.wallpapers!!.single().id)
        val legacy = json.decodeFromString<LegendCallResult>("""{"succeeded":true}""")
        assertNull(legacy.preferences)
        assertNull(legacy.ringtones)
        assertNull(legacy.wallpapers)
        val call = snapshot("incoming").copy(calleeImagePath = "/api/v1/mobile/notifications/id/sender-image?token=opaque",
            callerWallpaperMode = "profile", calleeWallpaperMode = "legend", incomingRingtoneResource = "legend_ringback")
        assertEquals(call, json.decodeFromString<LegendCallSnapshot>(json.encodeToString(call)))
    }

    @Test fun systemAnswerRequiresCurrentRecipientDeviceAndUnexpiredRingingCall() {
        val now = java.time.Instant.parse("2026-09-13T12:00:00Z")
        val call = snapshot("incoming").copy(calleeUserId = "recipient", calleeType = "Client",
            status = "ringing", calleeDeviceId = null, expiresUtc = now.plusSeconds(30).toString())
        assertTrue(call.canAnswerOnDevice("recipient", "Client", "device-a", now))
        assertFalse(call.canAnswerOnDevice("other-user", "Client", "device-a", now))
        assertFalse(call.canAnswerOnDevice("recipient", "Agent", "device-a", now))
        assertFalse(call.canAnswerOnDevice("", "Client", "device-a", now))
        assertFalse(call.copy(calleeDeviceId = "device-b").canAnswerOnDevice("recipient", "Client", "device-a", now))
        assertFalse(call.copy(status = "ended").canAnswerOnDevice("recipient", "Client", "device-a", now))
        assertFalse(call.copy(expiresUtc = now.toString()).canAnswerOnDevice("recipient", "Client", "device-a", now))
        assertFalse(call.copy(expiresUtc = "invalid").canAnswerOnDevice("recipient", "Client", "device-a", now))
        assertTrue(call.copy(calleeUserIds = listOf("recipient", "canonical-alias"))
            .canAnswerOnDevice("canonical-alias", "Client", "device-a", now))
    }

    @Test fun mediaWaitsForBothForegroundServiceAndActualTelecomFocus() = runBlocking {
        val service = Any()
        val gate = LegendCallAudioGate().apply { bindService(service) }
        gate.begin("call-a")
        val waiting = async(start = CoroutineStart.UNDISPATCHED) { gate.awaitReady("call-a", true) }
        gate.foregroundReady("call-a")
        yield()
        assertFalse(waiting.isCompleted)
        gate.focusChanged(service, true)
        waiting.await()
    }

    @Test fun focusBeforeForegroundServiceDoesNotStartMedia() = runBlocking {
        val service = Any()
        val gate = LegendCallAudioGate().apply { bindService(service) }
        gate.begin("call-a")
        gate.focusChanged(service, true)
        val waiting = async(start = CoroutineStart.UNDISPATCHED) { gate.awaitReady("call-a", true) }
        assertFalse(waiting.isCompleted)
        gate.foregroundReady("call-a")
        waiting.await()
    }

    @Test fun deniedFocusTimesOutAndEndedCallCannotBeRevivedByLateCallbacks() = runBlocking {
        val service = Any()
        val gate = LegendCallAudioGate().apply { bindService(service) }
        gate.begin("call-a")
        gate.foregroundReady("call-a")
        val denial = runCatching { withTimeout(20) { gate.awaitReady("call-a", true) } }.exceptionOrNull()
        assertTrue(denial is TimeoutCancellationException)
        gate.ended()
        gate.focusChanged(service, true)
        gate.foregroundReady("call-a")
        assertTrue(runCatching { gate.awaitReady("call-a", true) }.exceptionOrNull() is CancellationException)
    }

    @Test fun previousCallForegroundCallbackCannotUnlockReplacementCall() = runBlocking {
        val service = Any()
        val gate = LegendCallAudioGate().apply { bindService(service) }
        gate.begin("old")
        val old = async(start = CoroutineStart.UNDISPATCHED) { runCatching { gate.awaitReady("old", true) }.exceptionOrNull() }
        gate.ended()
        gate.begin("new")
        gate.foregroundReady("old")
        gate.focusChanged(service, true)
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

    @Test fun consecutiveCallReusesServiceFocusButStillRequiresItsOwnForegroundReadiness() = runBlocking {
        val service = Any()
        val gate = LegendCallAudioGate().apply { bindService(service) }
        gate.begin("first")
        gate.focusChanged(service, true)
        gate.foregroundReady("first")
        gate.awaitReady("first", true)
        gate.ended()
        gate.begin("second")
        val waiting = async(start = CoroutineStart.UNDISPATCHED) { gate.awaitReady("second", true) }
        assertFalse(waiting.isCompleted)
        gate.foregroundReady("first")
        yield()
        assertFalse(waiting.isCompleted)
        gate.foregroundReady("second")
        waiting.await()
        assertEquals(2, gate.mediaSession)
    }

    @Test fun focusLossBetweenCallsAndReplacedServiceCallbacksCannotGrantMedia() = runBlocking {
        val old = Any()
        val current = Any()
        val gate = LegendCallAudioGate().apply { bindService(old) }
        gate.begin("first")
        gate.focusChanged(old, true)
        gate.ended()
        gate.focusChanged(old, false)
        assertFalse(gate.hasFocus)
        gate.bindService(current)
        gate.begin("next")
        gate.foregroundReady("next")
        assertFalse(gate.focusChanged(old, true))
        assertFalse(gate.unbindService(old))
        val waiting = async(start = CoroutineStart.UNDISPATCHED) { gate.awaitReady("next", true) }
        assertFalse(waiting.isCompleted)
        gate.focusChanged(current, true)
        waiting.await()
        gate.unbindService(current)
        assertFalse(gate.hasFocus)
    }

    private fun snapshot(id: String) = LegendCallSnapshot(id, "conversation", "caller", "Agent", "callee", "Client",
        "caller-device", "callee-device", "Caller", "Callee", false, "connecting", "2026-09-12T00:00:00Z",
        "2026-09-12T01:00:00Z", 1)
}
