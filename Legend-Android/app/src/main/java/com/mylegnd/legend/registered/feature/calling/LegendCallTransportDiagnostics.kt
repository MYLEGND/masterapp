package com.mylegnd.legend.registered.feature.calling

import org.webrtc.RTCStatsReport

internal enum class LegendCallRecoveryReason {
    NETWORK_CHANGED, NETWORK_LOST, ICE_DISCONNECTED, ICE_FAILED, REMOTE_RESTART,
    CANDIDATE_SIGNAL_FAILED, REALTIME_RECONNECTED, HEARTBEAT_FAILED
}

internal enum class LegendCallCleanupReason {
    FOCUS_LOST, USER_END, SYSTEM_END, PLATFORM_FAILURE, SYNC_MISSING,
    OTHER_DEVICE_ACCEPTED, REMOTE_TERMINAL, INCOMING_CONFIRMATION_FAILED,
    LOCAL_FAILURE, CALL_DEADLINE, ACCOUNT_SHUTDOWN
}

internal fun legendCallCleanupDiagnostic(reason: LegendCallCleanupReason, status: String?): String {
    val safeStatus = status?.takeIf { it in setOf("ringing", "connecting", "active", "ended", "declined", "cancelled", "canceled", "missed", "busy", "failed", "expired") } ?: "unknown"
    return "event=call-cleanup reason=${reason.name} status=$safeStatus"
}

/** Only allowlisted states and aggregate counters leave the native stats report. */
internal fun legendCallTransportDiagnostic(report: RTCStatsReport): String {
    fun token(value: Any?, allowed: Set<String>) = (value as? String)?.lowercase()?.takeIf { it in allowed } ?: "unknown"
    val transport = report.statsMap.values.firstOrNull { it.type == "transport" && it.members["selectedCandidatePairId"] != null }
        ?: report.statsMap.values.firstOrNull { it.type == "transport" }
    val pair = (transport?.members?.get("selectedCandidatePairId") as? String)?.let { report.statsMap[it] }
    val local = (pair?.members?.get("localCandidateId") as? String)?.let { report.statsMap[it] }
    val remote = (pair?.members?.get("remoteCandidateId") as? String)?.let { report.statsMap[it] }
    val types = setOf("host", "srflx", "prflx", "relay")
    val protocols = setOf("udp", "tcp")
    fun inventory(kind: String): String {
        val candidates = report.statsMap.values.filter { it.type == kind }
        return "count=${candidates.size},types=" + candidates.map { token(it.members["candidateType"], types) }.distinct().sorted().joinToString("+").ifEmpty { "none" }
    }
    val audioReports = report.statsMap.values.filter { it.type == "remote-inbound-rtp" && (it.members["kind"] ?: it.members["mediaType"]) == "audio" }
    val losses = audioReports.mapNotNull { (it.members["packetsLost"] as? Number)?.toLong() }
    val rtt = audioReports.mapNotNull { (it.members["roundTripTime"] as? Number)?.toDouble()?.takeIf { value -> value.isFinite() && value >= 0 } }.maxOrNull()
    return "dtlsState=${token(transport?.members?.get("dtlsState"), setOf("new", "connecting", "connected", "closed", "failed"))}" +
        " selectedPair=${pair != null} pairState=${token(pair?.members?.get("state"), setOf("frozen", "waiting", "in-progress", "failed", "succeeded"))}" +
        " localCandidateType=${token(local?.members?.get("candidateType"), types)} localProtocol=${token(local?.members?.get("protocol"), protocols)}" +
        " remoteCandidateType=${token(remote?.members?.get("candidateType"), types)} remoteProtocol=${token(remote?.members?.get("protocol"), protocols)}" +
        " localCandidates=${inventory("local-candidate")} remoteCandidates=${inventory("remote-candidate")}" +
        " remoteAudioInboundReports=${audioReports.size} remoteAudioPacketsLost=${if (losses.isEmpty()) "unknown" else losses.sum()}" +
        " remoteAudioRttMs=${rtt?.times(1000)?.toLong() ?: "unknown"}"
}
