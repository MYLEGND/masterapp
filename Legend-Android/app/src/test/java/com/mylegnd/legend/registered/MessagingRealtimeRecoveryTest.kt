package com.mylegnd.legend.registered

import com.mylegnd.legend.registered.core.network.AccessTokenProvider
import com.mylegnd.legend.registered.core.realtime.LegendRealtimeEvents
import com.mylegnd.legend.registered.core.realtime.MobileMessagingRealtimeClient
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
                    readTextFrame(input)
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

    private fun readTextFrame(input: DataInputStream): String {
        assertEquals(0x81, input.readUnsignedByte())
        val flags = input.readUnsignedByte()
        assertTrue(flags and 0x80 != 0)
        val length = flags and 0x7f
        require(length < 126)
        val mask = ByteArray(4).also(input::readFully)
        val payload = ByteArray(length).also(input::readFully)
        return payload.mapIndexed { index, byte -> (byte.toInt() xor mask[index % 4].toInt()).toByte() }
            .toByteArray().toString(Charsets.UTF_8)
    }
}
