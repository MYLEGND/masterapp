package com.mylegnd.legend.registered.feature.calling

import kotlinx.serialization.Serializable

@Serializable data class LegendCallCommand(val action: String, val deviceId: String, val callId: String? = null, val conversationId: String? = null, val video: Boolean = false, val signalKind: String? = null, val signalData: String? = null, val epoch: Int = 0)
@Serializable data class LegendCallPolicy(val stunUrls: List<String>, val wifiWidth: Int, val wifiHeight: Int, val wifiFps: Int, val cellularWidth: Int, val cellularHeight: Int, val cellularFps: Int, val videoBitrate: Int, val audioBitrate: Int, val ringSeconds: Int, val connectSeconds: Int, val recoveryAttempts: Int, val adaptation: LegendCallAdaptationPolicy? = null, val relay: LegendCallRelay? = null)
@Serializable data class LegendCallSnapshot(val id: String, val conversationId: String, val callerUserId: String, val callerType: String, val calleeUserId: String, val calleeType: String, val callerDeviceId: String, val calleeDeviceId: String? = null, val callerName: String, val calleeName: String, val video: Boolean, val status: String, val createdUtc: String, val expiresUtc: String, val epoch: Int, val callerUserIds: List<String>? = null, val calleeUserIds: List<String>? = null, val receivedUtc: String? = null, val failureMessage: String? = null, val callerImagePath: String? = null) {
    val terminal: Boolean get() = status in setOf("ended", "declined", "missed")
}
@Serializable data class LegendCallEvent(val call: LegendCallSnapshot, val signalKind: String? = null, val signalData: String? = null, val fromDeviceId: String? = null, val toDeviceId: String? = null)
@Serializable data class LegendCallResult(val succeeded: Boolean, val error: String? = null, val call: LegendCallSnapshot? = null, val activeCalls: List<LegendCallSnapshot>? = null, val policy: LegendCallPolicy? = null)

@Serializable data class LegendCallAdaptationPolicy(val sampleSeconds: Int, val recoverySamples: Int, val lowBandwidth: Int, val highBandwidth: Int, val highLatencySeconds: Double, val lowWidth: Int, val lowHeight: Int, val lowFps: Int, val lowBitrate: Int, val mediumWidth: Int, val mediumHeight: Int, val mediumFps: Int, val mediumBitrate: Int, val audioPriority: Double)

@Serializable data class LegendCallRelay(val urls: List<String>, val username: String, val credential: String, val expiresUtc: String)

/** Missing bandwidth never proves recovery. High RTT can still prove congestion. */
internal fun LegendCallAdaptationPolicy.targetQuality(bandwidth: Double?, latency: Double?): Int? {
    if (latency != null && latency.isFinite() && latency > highLatencySeconds) return 0
    if (bandwidth == null || !bandwidth.isFinite() || bandwidth < 0) return null
    return if (bandwidth < lowBandwidth) 0 else if (bandwidth < highBandwidth) 1 else 2
}
