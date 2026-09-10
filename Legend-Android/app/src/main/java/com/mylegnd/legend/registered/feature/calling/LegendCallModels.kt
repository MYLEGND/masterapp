package com.mylegnd.legend.registered.feature.calling

import kotlinx.serialization.Serializable

@Serializable data class LegendCallCommand(val action: String, val deviceId: String, val callId: String? = null, val conversationId: String? = null, val video: Boolean = false, val signalKind: String? = null, val signalData: String? = null, val epoch: Int = 0)
@Serializable data class LegendCallPolicy(val stunUrls: List<String>, val wifiWidth: Int, val wifiHeight: Int, val wifiFps: Int, val cellularWidth: Int, val cellularHeight: Int, val cellularFps: Int, val videoBitrate: Int, val audioBitrate: Int, val ringSeconds: Int, val connectSeconds: Int, val recoveryAttempts: Int)
@Serializable data class LegendCallSnapshot(val id: String, val conversationId: String, val callerUserId: String, val callerType: String, val calleeUserId: String, val calleeType: String, val callerDeviceId: String, val calleeDeviceId: String? = null, val callerName: String, val calleeName: String, val video: Boolean, val status: String, val createdUtc: String, val expiresUtc: String, val epoch: Int, val callerUserIds: List<String>? = null, val calleeUserIds: List<String>? = null) {
    val terminal: Boolean get() = status in setOf("ended", "declined", "missed")
}
@Serializable data class LegendCallEvent(val call: LegendCallSnapshot, val signalKind: String? = null, val signalData: String? = null, val fromDeviceId: String? = null, val toDeviceId: String? = null)
@Serializable data class LegendCallResult(val succeeded: Boolean, val error: String? = null, val call: LegendCallSnapshot? = null, val activeCalls: List<LegendCallSnapshot>? = null, val policy: LegendCallPolicy? = null)
