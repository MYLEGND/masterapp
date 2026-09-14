package com.mylegnd.legend.registered

import com.mylegnd.legend.registered.core.network.AccessTokenProvider
import com.mylegnd.legend.registered.core.realtime.LegendRealtimeEvents
import com.mylegnd.legend.registered.core.realtime.MobileMessagingRealtimeClient
import com.mylegnd.legend.registered.core.model.MessagingPresenceParticipant
import kotlinx.serialization.json.*
import java.io.OutputStream
import java.io.DataInputStream
import java.net.ServerSocket
import java.security.MessageDigest
import java.util.Base64
import java.util.concurrent.CompletableFuture
import java.util.concurrent.TimeUnit
import kotlinx.coroutines.CoroutineStart
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.async
import kotlinx.coroutines.runBlocking
import kotlinx.coroutines.withTimeout
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test

class MessagingRealtimeRecoveryTest {
    @Test
    fun handshakeRequestsAuthoritativeResyncAndSendsSignalRHeartbeat() = runBlocking {
        ServerSocket(0).use { server ->
            server.soTimeout = 20_000
            val peer = CompletableFuture.supplyAsync {
                server.accept().use { socket ->
                    socket.soTimeout = 20_000
                    val input = DataInputStream(socket.getInputStream())
                    val headers = StringBuilder()
                    while (!headers.endsWith("\r\n\r\n")) headers.append(input.readUnsignedByte().toChar())
                    assertTrue(headers.contains("X-Legend-Participant-Type: Client", ignoreCase = true))
                    val key = headers.lines().first { it.startsWith("Sec-WebSocket-Key:", true) }.substringAfter(':').trim()
                    val accept = Base64.getEncoder().encodeToString(MessageDigest.getInstance("SHA-1")
                        .digest((key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11").toByteArray()))
                    val output = socket.getOutputStream()
                    output.write(("HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: $accept\r\n\r\n").toByteArray())
                    output.flush()
                    assertEquals("{\"protocol\":\"json\",\"version\":1}\u001e", readTextFrame(input))
                    output.write(byteArrayOf(0x81.toByte(), 3, '{'.code.toByte(), '}'.code.toByte(), 0x1e))
                    output.flush()
                    var presenceCount = 0
                    var next = readTextFrame(input)
                    while (next != "{\"type\":6}\u001e") {
                        val invocation = Json.parseToJsonElement(next.trimEnd('\u001e')).jsonObject
                        assertEquals("Presence", invocation["target"]?.jsonPrimitive?.content)
                        val request = invocation["arguments"]!!.jsonArray.single().jsonObject
                        assertEquals(0, request["participants"]?.jsonArray.orEmpty().size)
                        assertEquals(0, request["conversationIds"]?.jsonArray.orEmpty().size)
                        replyPresence(output, invocation, "[]")
                        presenceCount++
                        next = readTextFrame(input)
                    }
                    assertTrue(presenceCount > 0)
                    next
                }
            }
            val resync = async(start = CoroutineStart.UNDISPATCHED) {
                withTimeout(20_000) { LegendRealtimeEvents.events.first { it.requiresResync } }
            }
            val client = MobileMessagingRealtimeClient("http://127.0.0.1:${server.localPort}", "Client",
                object : AccessTokenProvider { override suspend fun accessToken() = "test-token" })
            try {
                client.start()
                assertTrue(resync.await().requiresResync)
                assertEquals("{\"type\":6}\u001e", peer.get(20, TimeUnit.SECONDS))
            } finally {
                client.close()
                resync.cancel()
            }
        }
    }

    @Test
    fun presenceUsesVisibleTypedTargetsAndClearsOmittedRemovedAndDisconnectedState() = runBlocking {
        ServerSocket(0).use { server ->
            server.soTimeout = 10_000
            val allowClose = CompletableFuture<Unit>()
            val requestHeld = CompletableFuture<Unit>()
            val rowsChanged = CompletableFuture<Unit>()
            val peer = CompletableFuture.runAsync {
                server.accept().use { socket ->
                    socket.soTimeout = 10_000
                    val input = DataInputStream(socket.getInputStream())
                    val headers = StringBuilder()
                    while (!headers.endsWith("\r\n\r\n")) headers.append(input.readUnsignedByte().toChar())
                    assertTrue(headers.contains("Authorization: Bearer test-token", true))
                    assertTrue(headers.contains("X-Legend-Participant-Type: Client", true))
                    val key = headers.lines().first { it.startsWith("Sec-WebSocket-Key:", true) }.substringAfter(':').trim()
                    val accept = Base64.getEncoder().encodeToString(MessageDigest.getInstance("SHA-1")
                        .digest((key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11").toByteArray()))
                    val output = socket.getOutputStream()
                    output.write(("HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: $accept\r\n\r\n").toByteArray()); output.flush()
                    readTextFrame(input)
                    sendTextFrame(output, "{}\u001e")
                    val first = Json.parseToJsonElement(readTextFrame(input).trimEnd('\u001e')).jsonObject
                    assertEquals("Presence", first["target"]!!.jsonPrimitive.content)
                    val targets = first["arguments"]!!.jsonArray.single().jsonObject["participants"]!!.jsonArray
                    assertEquals(2, targets.size)
                    assertEquals(setOf("Agent", "Client"), targets.map { it.jsonObject["participantType"]!!.jsonPrimitive.content }.toSet())
                    requestHeld.complete(Unit)
                    rowsChanged.get(10, TimeUnit.SECONDS)
                    socket.soTimeout = 450
                    try { readTextFrame(input); throw AssertionError("Row churn queued another invocation before the held response") }
                    catch (_: java.net.SocketTimeoutException) { }
                    socket.soTimeout = 10_000
                    replyPresence(output, first, "[]")
                    val trailing = Json.parseToJsonElement(readTextFrame(input).trimEnd('\u001e')).jsonObject
                    assertEquals("Presence", trailing["target"]!!.jsonPrimitive.content)
                    assertEquals(targets.toSet(), trailing["arguments"]!!.jsonArray.single().jsonObject["participants"]!!.jsonArray.toSet())
                    // Same ID with a different type is distinct. An unsolicited ID is never projected.
                    replyPresence(output, trailing, "[{\"userId\":\"same-id\",\"participantType\":\"Agent\",\"isOnline\":true},{\"userId\":\"unsolicited\",\"participantType\":\"Client\",\"isOnline\":false}]")
                    val empty = Json.parseToJsonElement(readTextFrame(input).trimEnd('\u001e')).jsonObject
                    assertEquals("Presence", empty["target"]!!.jsonPrimitive.content)
                    assertTrue(empty["arguments"]!!.jsonArray.single().jsonObject["participants"]?.jsonArray.orEmpty().isEmpty())
                    replyPresence(output, empty, "[]")
                    allowClose.get(10, TimeUnit.SECONDS)
                }
            }
            val client = MobileMessagingRealtimeClient("http://127.0.0.1:${server.localPort}", "Client",
                object : AccessTokenProvider { override suspend fun accessToken() = "test-token" })
            try {
                client.observePresence("agent-row", MessagingPresenceParticipant("same-id", "Agent"))
                client.observePresence("client-row", MessagingPresenceParticipant("same-id", "Client"))
                client.start()
                requestHeld.get(10, TimeUnit.SECONDS)
                repeat(8) { index ->
                    client.observePresence("temporary-$index", MessagingPresenceParticipant("temporary-$index", "Client"))
                    client.removePresenceObserver("temporary-$index")
                }
                rowsChanged.complete(Unit)
                val observed = withTimeout(10_000) { client.presence.first { it != null && it.participants.isNotEmpty() } }!!
                assertEquals(1, observed.participants.size)
                assertEquals("Agent", observed.participants.single().participantType)
                assertTrue(observed.participants.single().isOnline)
                client.setPresenceForeground(false)
                client.removePresenceObserver("agent-row")
                client.removePresenceObserver("client-row")
                assertEquals(null, client.presence.value)
                withTimeout(10_000) { client.presence.first { it != null && it.participants.isEmpty() } }
                allowClose.complete(Unit)
                peer.get(10, TimeUnit.SECONDS)
                withTimeout(10_000) { client.presence.first { it == null } }
                client.retireAccountConnection()
                assertEquals(null, client.presence.value)
            } finally { rowsChanged.complete(Unit); allowClose.complete(Unit); client.close() }
        }
    }

    private fun replyPresence(output: OutputStream, invocation: JsonObject, participants: String) {
        val id = invocation["invocationId"]!!.jsonPrimitive.content
        sendTextFrame(output, "{\"type\":3,\"invocationId\":\"$id\",\"result\":{\"observedUtc\":\"2026-09-13T00:00:00Z\",\"refreshSeconds\":30,\"participants\":$participants,\"conversations\":[]}}\u001e")
    }

    private fun sendTextFrame(output: OutputStream, text: String) {
        val bytes = text.toByteArray()
        output.write(0x81)
        if (bytes.size < 126) output.write(bytes.size)
        else { output.write(126); output.write(bytes.size shr 8); output.write(bytes.size and 255) }
        output.write(bytes); output.flush()
    }

    private fun readTextFrame(input: DataInputStream): String {
        assertEquals(0x81, input.readUnsignedByte())
        val flags = input.readUnsignedByte()
        assertTrue(flags and 0x80 != 0)
        val length = when (val short = flags and 0x7f) { 126 -> input.readUnsignedShort(); 127 -> error("Fixture frame exceeds bound"); else -> short }
        require(length <= 65_536)
        val mask = ByteArray(4).also(input::readFully)
        val payload = ByteArray(length).also(input::readFully)
        return payload.mapIndexed { index, byte -> (byte.toInt() xor mask[index % 4].toInt()).toByte() }
            .toByteArray().toString(Charsets.UTF_8)
    }
}
