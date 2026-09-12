package com.mylegnd.legend.registered.feature.calling

import kotlinx.serialization.Serializable
import kotlinx.serialization.json.Json

@Serializable data class LegendCallCommand(val action: String, val deviceId: String, val callId: String? = null, val conversationId: String? = null, val video: Boolean = false, val signalKind: String? = null, val signalData: String? = null, val epoch: Int = 0)
@Serializable data class LegendCallPolicy(val stunUrls: List<String>, val wifiWidth: Int, val wifiHeight: Int, val wifiFps: Int, val cellularWidth: Int, val cellularHeight: Int, val cellularFps: Int, val videoBitrate: Int, val audioBitrate: Int, val ringSeconds: Int, val connectSeconds: Int, val recoveryAttempts: Int, val adaptation: LegendCallAdaptationPolicy? = null, val relay: LegendCallRelay? = null, val screenShare: LegendCallScreenSharePolicy? = null)
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


@Serializable data class LegendCallScreenSharePolicy(
    val highWidth: Int, val highHeight: Int, val highFps: Int, val highBitrate: Int,
    val mediumWidth: Int, val mediumHeight: Int, val mediumFps: Int, val mediumBitrate: Int,
    val lowWidth: Int, val lowHeight: Int, val lowFps: Int, val lowBitrate: Int,
    val transportHeadroomFraction: Double,
)

internal data class LegendScreenCaptureLimits(val width: Int, val height: Int, val fps: Int, val bitrate: Int)

internal fun LegendCallScreenSharePolicy.limits(quality: Int, cellular: Boolean): LegendScreenCaptureLimits =
    when (if (cellular) minOf(quality, 1) else quality) {
        0 -> LegendScreenCaptureLimits(lowWidth, lowHeight, lowFps, lowBitrate)
        1 -> LegendScreenCaptureLimits(mediumWidth, mediumHeight, mediumFps, mediumBitrate)
        else -> LegendScreenCaptureLimits(highWidth, highHeight, highFps, highBitrate)
    }

/** Fit the policy's long and short edges without rotating, stretching or upscaling content. */
internal fun LegendScreenCaptureLimits.dimensions(contentWidth: Int, contentHeight: Int): Pair<Int, Int> {
    val landscape = contentWidth >= contentHeight
    val boundWidth = if (landscape) maxOf(width, height) else minOf(width, height)
    val boundHeight = if (landscape) minOf(width, height) else maxOf(width, height)
    val scale = minOf(1.0, boundWidth.toDouble() / contentWidth.coerceAtLeast(1),
        boundHeight.toDouble() / contentHeight.coerceAtLeast(1))
    return ((contentWidth * scale).toInt() / 2 * 2).coerceAtLeast(2) to
        ((contentHeight * scale).toInt() / 2 * 2).coerceAtLeast(2)
}


/** A valid bandwidth sample reserves transport and audio before allocating screen video. */
internal fun LegendCallScreenSharePolicy.videoBudget(ceiling: Int, availableBandwidth: Double?, audioBitrate: Int): Int {
    if (availableBandwidth == null || !availableBandwidth.isFinite() || availableBandwidth < 0) return ceiling
    val budget = availableBandwidth * (1.0 - transportHeadroomFraction.coerceIn(0.0, 1.0)) - audioBitrate
    return minOf(ceiling.toDouble(), budget.coerceAtLeast(0.0)).toInt()
}


@Serializable internal data class LegendCallMediaState(val screenSharing: Boolean, val request: Boolean = false)

/** Presentation metadata cannot tear down healthy media when malformed or from a newer client. */
internal fun Json.callMediaState(data: String): LegendCallMediaState? =
    runCatching { decodeFromString<LegendCallMediaState>(data) }.getOrNull()
