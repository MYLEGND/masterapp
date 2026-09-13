package com.mylegnd.legend.registered

import com.mylegnd.legend.registered.feature.calling.*
import org.junit.Assert.*
import org.junit.Test
import org.webrtc.RTCStats
import org.webrtc.RTCStatsReport

class LegendCallTransportDiagnosticsTest {
    @Test fun selectedPairOnlyExposesAllowlistedTransportMetadataAndAggregateAudioFeedback() {
        val result = legendCallTransportDiagnostic(report(
            stat("transport", "private-transport", mapOf("selectedCandidatePairId" to "private-pair", "dtlsState" to "connected")),
            stat("candidate-pair", "private-pair", mapOf("localCandidateId" to "private-local", "remoteCandidateId" to "private-remote", "state" to "succeeded")),
            stat("local-candidate", "private-local", mapOf("candidateType" to "host", "protocol" to "udp", "address" to "192.0.2.1", "url" to "turn:private.example")),
            stat("remote-candidate", "private-remote", mapOf("candidateType" to "relay", "protocol" to "udp", "address" to "192.0.2.2")),
            stat("remote-inbound-rtp", "private-audio", mapOf("kind" to "audio", "packetsLost" to 3L, "roundTripTime" to 0.025)),
        ))
        assertTrue(result.contains("dtlsState=connected selectedPair=true pairState=succeeded"))
        assertTrue(result.contains("localCandidateType=host localProtocol=udp remoteCandidateType=relay"))
        assertTrue(result.contains("remoteAudioInboundReports=1 remoteAudioPacketsLost=3 remoteAudioRttMs=25"))
        for (privateValue in listOf("private-", "192.0.2", "turn:")) assertFalse(result.contains(privateValue))
    }

    @Test fun unavailableOrUnrecognizedStatsStayUnknownWithoutLeakingStringsOrInventingZeroLoss() {
        val empty = legendCallTransportDiagnostic(report())
        assertTrue(empty.contains("selectedPair=false"))
        assertTrue(empty.contains("remoteAudioPacketsLost=unknown"))
        val invalid = legendCallTransportDiagnostic(report(
            stat("transport", "t", mapOf("dtlsState" to "secret-value", "selectedCandidatePairId" to "p")),
            stat("candidate-pair", "p", mapOf("localCandidateId" to "l", "state" to "secret-value")),
            stat("local-candidate", "l", mapOf("candidateType" to "secret-value", "protocol" to "secret-value")),
            stat("remote-inbound-rtp", "a", mapOf("kind" to "audio", "roundTripTime" to Double.NaN)),
        ))
        assertFalse(invalid.contains("secret-value"))
        assertTrue(invalid.contains("dtlsState=unknown"))
        assertTrue(invalid.contains("remoteAudioRttMs=unknown"))
    }
    @Test fun candidateInventoryRemainsVisibleBeforeAnyPairIsSelected() {
        val result = legendCallTransportDiagnostic(report(
            stat("local-candidate", "l1", mapOf("candidateType" to "host", "address" to "192.0.2.1")),
            stat("local-candidate", "l2", mapOf("candidateType" to "srflx")),
            stat("local-candidate", "l3", mapOf("candidateType" to "private-value")),
            stat("remote-candidate", "r1", mapOf("candidateType" to "relay")),
        ))
        assertTrue(result.contains("selectedPair=false"))
        assertTrue(result.contains("localCandidates=count=3,types=host+srflx+unknown"))
        assertTrue(result.contains("remoteCandidates=count=1,types=relay"))
        assertFalse(result.contains("192.0.2"))
        assertFalse(result.contains("private-value"))
    }

    @Test fun cleanupReasonAndTerminalStatusNeverExposeArbitraryErrorMessages() {
        assertEquals("event=call-cleanup reason=REMOTE_TERMINAL status=ended",
            legendCallCleanupDiagnostic(LegendCallCleanupReason.REMOTE_TERMINAL, "ended"))
        assertEquals("event=call-cleanup reason=CALL_DEADLINE status=unknown",
            legendCallCleanupDiagnostic(LegendCallCleanupReason.CALL_DEADLINE, "private server error"))
    }

    private fun stat(type: String, id: String, members: Map<String, Any>) = RTCStats(0, type, id, members)
    private fun report(vararg stats: RTCStats) = RTCStatsReport(0, stats.associateBy { it.id })
}
