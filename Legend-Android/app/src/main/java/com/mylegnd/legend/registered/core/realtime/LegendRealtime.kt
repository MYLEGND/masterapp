package com.mylegnd.legend.registered.core.realtime

import com.mylegnd.legend.registered.core.network.AccessTokenProvider
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.Job
import kotlinx.coroutines.cancel
import kotlinx.coroutines.ensureActive
import kotlinx.coroutines.delay
import kotlin.time.Duration.Companion.milliseconds
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.asStateFlow
import com.mylegnd.legend.registered.core.model.*
import kotlinx.serialization.json.JsonElement
import kotlinx.coroutines.TimeoutCancellationException
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.isActive
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
import com.mylegnd.legend.registered.feature.calling.*
import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.withTimeoutOrNull
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.withTimeout
import kotlinx.serialization.encodeToString
import kotlinx.serialization.json.buildJsonObject
import kotlinx.serialization.json.put
import kotlinx.serialization.json.JsonArray
import java.util.concurrent.ConcurrentHashMap

/**
 * Server events are intentionally small wake-up signals. The Android UI never
 * accepts a message body over realtime transport. Conversation events wake
 * the existing repositories so they refetch the same authorized REST
 * projection used during normal load and recovery. Badge values are the
 * server-issued notification projection, versioned by the server revision.
 */
data class LegendMessagingRealtimeEvent(
    val requiresResync: Boolean = false,
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
    private var heartbeat: Job? = null
    @Volatile private var retiring = false
    @Volatile private var callReady = false
    private val callRequests = ConcurrentHashMap<String, CompletableDeferred<JsonElement>>()
    var onCall: ((LegendCallEvent) -> Unit)? = null
    var onCallReconnect: (() -> Unit)? = null

    suspend fun call(command: LegendCallCommand, existingConnectionOnly: Boolean = false): LegendCallResult =
        json.decodeFromJsonElement(LegendCallResult.serializer(), invoke("Call", json.parseToJsonElement(json.encodeToString(command)), existingConnectionOnly))

    private suspend fun invoke(target: String, argument: JsonElement, existingConnectionOnly: Boolean = false): JsonElement = withTimeout(12_000) {
        if (existingConnectionOnly) check(callReady) { "Messaging is disconnected." } else start()
        while (!callReady) delay(100)
        ensureActive()
        check(target != "Presence" || !retiring) { "Messaging account is retiring." }
        val invocationSocket = socket
        val invocationGeneration = generation
        val id = java.util.UUID.randomUUID().toString()
        val pending = CompletableDeferred<JsonElement>()
        callRequests[id] = pending
        try {
            val frame = buildJsonObject {
                put("type", 1); put("invocationId", id); put("target", target)
                put("arguments", JsonArray(listOf(argument)))
            }
            check(invocationSocket?.send(frame.toString() + RECORD_SEPARATOR) == true) { "Messaging is disconnected." }
            pending.await()
        } catch (timeout: TimeoutCancellationException) {
            if (target == "Presence" && generation == invocationGeneration && socket === invocationSocket) invocationSocket?.cancel()
            throw timeout
        } finally { callRequests.remove(id) }
    }

    private val observedPresence = MutableStateFlow<MessagingPresenceResult?>(null)
    val presence = observedPresence.asStateFlow()
    private val presenceTargets = java.util.concurrent.ConcurrentHashMap<String, MessagingPresenceRequest>()
    private var presenceLoop: Job? = null
    @Volatile private var presenceRevision = 0L
    @Volatile private var presenceForeground = true
    private val presenceChanges = MutableStateFlow(0L)

    @Synchronized fun setPresenceForeground(foreground: Boolean) {
        if (presenceForeground == foreground) return
        presenceForeground = foreground; presenceRevision++; presenceChanges.value = presenceRevision
        restartPresence()
    }

    @Synchronized fun observePresence(owner: String, participant: MessagingPresenceParticipant? = null, conversationId: String? = null) {
        presenceTargets[owner] = MessagingPresenceRequest(listOfNotNull(participant), listOfNotNull(conversationId))
        presenceRevision++; presenceChanges.value = presenceRevision
        restartPresence()
    }
    @Synchronized fun removePresenceObserver(owner: String) {
        if (presenceTargets.remove(owner) != null) { presenceRevision++; presenceChanges.value = presenceRevision; restartPresence() }
    }
    @Synchronized private fun restartPresence() {
        observedPresence.value = null
        if (!callReady || retiring || presenceLoop?.isActive == true) return
        val connectionGeneration = generation
        presenceLoop = scope.launch {
            while (isActive && callReady && !retiring && connectionGeneration == generation) {
                delay(150) // One trailing snapshot; never cancel an already-sent invocation on row changes.
                val revision = presenceRevision
                val requested = if (!presenceForeground) MessagingPresenceRequest() else MessagingPresenceRequest(
                    presenceTargets.values.flatMap { it.participants }.distinct().take(50),
                    presenceTargets.values.flatMap { it.conversationIds }.distinct().take(50))
                var refreshSeconds = 30
                try {
                    val result = json.decodeFromJsonElement(MessagingPresenceResult.serializer(),
                        invoke("Presence", json.parseToJsonElement(json.encodeToString(requested)), true))
                    check(result.refreshSeconds in 5..90 && result.participants.size <= 50 && result.conversations.size <= 50)
                    java.time.Instant.parse(result.observedUtc)
                    if (isActive && revision == presenceRevision && connectionGeneration == generation && callReady) {
                        observedPresence.value = result.copy(
                            participants = result.participants.filter { MessagingPresenceParticipant(it.userId, it.participantType) in requested.participants },
                            conversations = result.conversations.filter { it.conversationId in requested.conversationIds })
                        refreshSeconds = result.refreshSeconds
                    }
                } catch (cancelled: CancellationException) { throw cancelled }
                catch (_: Exception) { if (revision == presenceRevision && connectionGeneration == generation) observedPresence.value = null }
                withTimeoutOrNull(refreshSeconds * 1000L) { presenceChanges.first { it != revision } }
                if (revision == presenceRevision && connectionGeneration == generation) observedPresence.value = null
            }
        }
    }
    @Synchronized private fun clearPresence() {
        presenceLoop?.cancel(); presenceLoop = null; observedPresence.value = null
    }

    fun retireAccountConnection() {
        retiring = true
        presenceTargets.clear(); clearPresence()
        onCall = null; onCallReconnect = null
        if (!callReady) stop()
    }

    fun start() {
        if (retiring || shouldRemainConnected || hubUrl == null) return
        shouldRemainConnected = true
        reconnectAttempt = 0
        connect(generation)
    }

    fun stop() {
        callReady = false
        clearPresence()
        callRequests.values.forEach { it.cancel() }
        callRequests.clear()
        shouldRemainConnected = false
        generation += 1
        heartbeat?.cancel()
        heartbeat = null
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
            if (retiring || !shouldRemainConnected || connectionGeneration != generation) return@launch
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
            if (retiring || !shouldRemainConnected || connectionGeneration != generation) {
                webSocket.close(1000, "legend-stale")
                return
            }
            // ASP.NET Core SignalR JSON handshake. The record separator is part
            // of the established protocol and matches the iOS implementation.
            webSocket.send("{\"protocol\":\"json\",\"version\":1}\u001e")
        }

        override fun onMessage(webSocket: WebSocket, text: String) {
            if (socket !== webSocket || connectionGeneration != generation) return
            if (!retiring && shouldRemainConnected && text.split(RECORD_SEPARATOR).any { it.trim() == "{}" }) {
                callReady = true
                restartPresence()
                onCallReconnect?.invoke()
                reconnectAttempt = 0
                heartbeat?.cancel()
                heartbeat = scope.launch {
                    while (shouldRemainConnected && socket === webSocket && connectionGeneration == generation) {
                        delay(15_000)
                        if (!webSocket.send("{\"type\":6}$RECORD_SEPARATOR")) {
                            webSocket.cancel()
                            return@launch
                        }
                    }
                }
                LegendRealtimeEvents.publish(LegendMessagingRealtimeEvent(requiresResync = true))
            }
            text.split(RECORD_SEPARATOR).forEach(::reconcileFrame)
        }

        override fun onFailure(webSocket: WebSocket, t: Throwable, response: Response?) {
            if (socket !== webSocket) return
            callReady = false
            clearPresence()
            heartbeat?.cancel()
            socket = null
            scheduleReconnect(connectionGeneration)
        }

        override fun onClosing(webSocket: WebSocket, code: Int, reason: String) {
            webSocket.close(code, reason)
        }

        override fun onClosed(webSocket: WebSocket, code: Int, reason: String) {
            if (socket !== webSocket) return
            callReady = false
            clearPresence()
            heartbeat?.cancel()
            socket = null
            scheduleReconnect(connectionGeneration)
        }
    }

    private fun reconcileFrame(frame: String) {
        if (frame.isBlank()) return
        val envelope = runCatching { json.parseToJsonElement(frame).jsonObject }.getOrNull() ?: return
        if (envelope["type"]?.jsonPrimitive?.content == "3") {
            val id = envelope.string("invocationId") ?: return
            val pending = callRequests.remove(id) ?: return
            val result = envelope["result"]
            if (result != null) pending.complete(result) else pending.completeExceptionally(IllegalStateException("The call request could not be completed."))
            return
        }
        if (envelope.string("target")?.lowercase() == "callupdated") {
            envelope["arguments"]?.jsonArray?.firstOrNull()?.let { value ->
                runCatching { json.decodeFromString<LegendCallEvent>(value.toString()) }.getOrNull()?.let { onCall?.invoke(it) }
            }
            return
        }
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
        if (retiring || !shouldRemainConnected || connectionGeneration != generation) return
        val delayMillis = RECONNECT_DELAYS_MILLIS[minOf(reconnectAttempt, RECONNECT_DELAYS_MILLIS.lastIndex)]
        reconnectAttempt += 1
        scope.launch {
            delay(delayMillis.milliseconds)
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
