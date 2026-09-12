package com.mylegnd.legend.registered

import com.mylegnd.legend.registered.feature.calling.legendCallTransportDiagnostic
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
    private fun stat(type: String, id: String, members: Map<String, Any>) = RTCStats(0, type, id, members)
    private fun report(vararg stats: RTCStats) = RTCStatsReport(0, stats.associateBy { it.id })
}
