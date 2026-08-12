package com.mylegnd.legend.registered.core.realtime

import com.mylegnd.legend.registered.core.network.AccessTokenProvider
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.MutableSharedFlow
import kotlinx.coroutines.flow.asSharedFlow
import kotlinx.coroutines.launch
import kotlinx.serialization.json.Json
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.jsonArray
import kotlinx.serialization.json.jsonObject
import kotlinx.serialization.json.jsonPrimitive
import kotlinx.serialization.json.contentOrNull
import okhttp3.OkHttpClient
import okhttp3.Request
import okhttp3.Response
import okhttp3.WebSocket
import okhttp3.WebSocketListener
import okhttp3.HttpUrl.Companion.toHttpUrlOrNull
import java.util.concurrent.TimeUnit

/**
 * Server events are intentionally small wake-up signals. The Android UI never
 * accepts a message body over realtime transport. Conversation events wake
 * the existing repositories so they refetch the same authorized REST
 * projection used during normal load and recovery. Badge values are the
 * server-issued notification projection, versioned by the server revision.
 */
data class LegendMessagingRealtimeEvent(
    val conversationId: String? = null,
    val messageId: String? = null,
    val notificationId: String? = null,
    val unreadCount: Int? = null,
    val revision: Long? = null,
    val occurredUtc: String? = null,
)

object LegendRealtimeEvents {
    private val mutableEvents = MutableSharedFlow<LegendMessagingRealtimeEvent>(extraBufferCapacity = 8)
    val events = mutableEvents.asSharedFlow()

    fun publish(event: LegendMessagingRealtimeEvent) {
        mutableEvents.tryEmit(event)
    }
}

/**
 * Native Android peer of iOS `MobileMessagingRealtimeClient`.
 *
 * AgentPortal's existing `/messaginghub` accepts the sanctioned bearer token
 * and role header. No Android hub, notification engine, or message authority
 * is introduced here.
 */
class MobileMessagingRealtimeClient(
    apiBaseUrl: String,
    private val participantType: String,
    private val tokenProvider: AccessTokenProvider,
    private val httpClient: OkHttpClient = OkHttpClient.Builder()
        .pingInterval(30, TimeUnit.SECONDS)
        .build(),
) {
    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.IO)
    private val hubUrl = apiBaseUrl.toHubUrl()
    private val json = Json { ignoreUnknownKeys = true }
    private var socket: WebSocket? = null
    private var shouldRemainConnected = false
    private var reconnectAttempt = 0
    private var generation = 0L

    fun start() {
        if (shouldRemainConnected || hubUrl == null) return
        shouldRemainConnected = true
        reconnectAttempt = 0
        connect(generation)
    }

    fun stop() {
        shouldRemainConnected = false
        generation += 1
        socket?.close(1000, "legend-background")
        socket = null
    }

    fun close() {
        stop()
        scope.cancel()
        httpClient.dispatcher.executorService.shutdown()
    }

    private fun connect(connectionGeneration: Long) {
        val endpoint = hubUrl ?: return
        scope.launch {
            val token = tokenProvider.accessToken() ?: run {
                scheduleReconnect(connectionGeneration)
                return@launch
            }
            if (!shouldRemainConnected || connectionGeneration != generation) return@launch
            val request = Request.Builder()
                .url(endpoint)
                .header("Authorization", "Bearer $token")
                .header("Accept", "application/json")
                .header("X-Legend-Participant-Type", participantType)
                .build()
            socket = httpClient.newWebSocket(request, listener(connectionGeneration))
        }
    }

    private fun listener(connectionGeneration: Long) = object : WebSocketListener() {
        override fun onOpen(webSocket: WebSocket, response: Response) {
            if (!shouldRemainConnected || connectionGeneration != generation) {
                webSocket.close(1000, "legend-stale")
                return
            }
            // ASP.NET Core SignalR JSON handshake. The record separator is part
            // of the established protocol and matches the iOS implementation.
            webSocket.send("{\"protocol\":\"json\",\"version\":1}\u001e")
            reconnectAttempt = 0
        }

        override fun onMessage(webSocket: WebSocket, text: String) {
            text.split(RECORD_SEPARATOR).forEach(::reconcileFrame)
        }

        override fun onFailure(webSocket: WebSocket, t: Throwable, response: Response?) {
            if (socket === webSocket) socket = null
            scheduleReconnect(connectionGeneration)
        }

        override fun onClosed(webSocket: WebSocket, code: Int, reason: String) {
            if (socket === webSocket) socket = null
            scheduleReconnect(connectionGeneration)
        }
    }

    private fun reconcileFrame(frame: String) {
        if (frame.isBlank()) return
        val envelope = runCatching { json.parseToJsonElement(frame).jsonObject }.getOrNull() ?: return
        if (envelope["type"]?.jsonPrimitive?.content != "1") return
        val target = envelope["target"]?.jsonPrimitive?.content?.lowercase() ?: return
        if (target !in EVENT_TARGETS) return
        val event = envelope["arguments"]?.jsonArray?.firstOrNull()?.jsonObject
        val update = LegendMessagingRealtimeEvent(
            conversationId = event.string("conversationId"),
            messageId = event.string("messageId"),
            notificationId = event.string("notificationId"),
            unreadCount = event.string("unreadCount")?.toIntOrNull(),
            revision = event.string("revision")?.toLongOrNull(),
            occurredUtc = event.string("occurredUtc"),
        )
        if (update.conversationId != null || update.notificationId != null || update.unreadCount != null) {
            LegendRealtimeEvents.publish(update)
        }
    }

    private fun scheduleReconnect(connectionGeneration: Long) {
        if (!shouldRemainConnected || connectionGeneration != generation) return
        val delayMillis = RECONNECT_DELAYS_MILLIS[minOf(reconnectAttempt, RECONNECT_DELAYS_MILLIS.lastIndex)]
        reconnectAttempt += 1
        scope.launch {
            delay(delayMillis)
            if (shouldRemainConnected && connectionGeneration == generation && socket == null) connect(connectionGeneration)
        }
    }

    private companion object {
        const val RECORD_SEPARATOR = "\u001e"
        val EVENT_TARGETS = setOf("messagereceived", "conversationupdated", "notificationupdated")
        val RECONNECT_DELAYS_MILLIS = longArrayOf(1_000, 2_000, 5_000, 10_000, 30_000)
    }
}

private fun JsonObject?.string(name: String): String? =
    this?.get(name)?.jsonPrimitive?.contentOrNull

/**
 * OkHttp upgrades an HTTP(S) request to WebSocket transport in
 * [OkHttpClient.newWebSocket]. Its [okhttp3.HttpUrl] type intentionally only
 * accepts HTTP and HTTPS schemes, so converting this value to ws/wss here
 * crashes during application composition. Keep the canonical HTTP(S) URL and
 * let the WebSocket client perform the protocol upgrade.
 */
internal fun String.toHubUrl(): String? {
    val base = toHttpUrlOrNull() ?: return null
    when (base.scheme) {
        "https", "http" -> Unit
        else -> return null
    }
    val path = base.encodedPath.trim('/').let { if (it.isEmpty()) "/messaginghub" else "/$it/messaginghub" }
    return base.newBuilder().encodedPath(path).query(null).fragment(null).build().toString()
}
