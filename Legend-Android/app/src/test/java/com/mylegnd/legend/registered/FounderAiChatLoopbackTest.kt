package com.mylegnd.legend.registered

import com.mylegnd.legend.registered.core.model.FounderAiChatMessage
import com.mylegnd.legend.registered.core.model.FounderAiChatRequest
import com.mylegnd.legend.registered.core.network.AccessTokenProvider
import com.mylegnd.legend.registered.core.network.FounderAiChatStream
import com.mylegnd.legend.registered.core.network.LegendApiClient
import kotlinx.coroutines.runBlocking
import kotlinx.coroutines.withTimeout
import okhttp3.Call
import okhttp3.EventListener
import org.junit.Assert.*
import org.junit.Test
import java.io.InputStream
import java.net.InetAddress
import java.net.ServerSocket
import java.net.Socket
import java.util.concurrent.TimeUnit
import java.util.concurrent.atomic.AtomicInteger
import java.util.concurrent.atomic.AtomicReference
import kotlin.concurrent.thread

/** Controlled TCP proof only: no production host, gateway, or credentials. */
class FounderAiChatLoopbackTest {
    @Test fun samePostSurvivesNormalReadTimeoutWithFourSecondHeartbeats() = runBlocking {
        val server = ServerSocket(0, 1, InetAddress.getByName("127.0.0.1"))
        server.soTimeout = 5_000
        val accepted = AtomicReference<Socket?>()
        val serverFailure = AtomicReference<Throwable?>()
        val requests = AtomicInteger()
        val calls = AtomicInteger()
        val authenticated = LegendApiClient.create("http://127.0.0.1:${server.localPort}", object : AccessTokenProvider {
            override suspend fun accessToken(): String = "loopback-test-token"
        })
        val client = authenticated.httpClient.newBuilder()
            .eventListener(object : EventListener() {
                override fun callStart(call: Call) { calls.incrementAndGet() }
            }).build()
        assertEquals(30_000, client.readTimeoutMillis)
        val worker = thread(name = "founder-chat-loopback", isDaemon = true) {
            try {
                server.accept().use { socket ->
                    accepted.set(socket)
                    socket.soTimeout = 5_000
                    val input = socket.getInputStream().buffered()
                    assertEquals("POST /api/v1/mobile/founder/legend-ai/chat HTTP/1.1", input.headerLine())
                    requests.incrementAndGet()
                    val headers = mutableMapOf<String, String>()
                    while (true) {
                        val line = input.headerLine()
                        if (line.isEmpty()) break
                        val separator = line.indexOf(':')
                        check(separator > 0)
                        headers[line.substring(0, separator).lowercase()] = line.substring(separator + 1).trim()
                    }
                    assertEquals("Bearer loopback-test-token", headers["authorization"])
                    assertEquals("application/x-ndjson", headers["accept"])
                    assertEquals("Agent", headers["x-legend-participant-type"])
                    val length = headers.getValue("content-length").toInt()
                    check(length in 1..65_536)
                    val body = ByteArray(length)
                    var read = 0
                    while (read < length) {
                        val count = input.read(body, read, length - read)
                        check(count > 0)
                        read += count
                    }
                    assertTrue(String(body, Charsets.UTF_8).contains("\"conversationId\":\"loopback-conversation\""))
                    val output = socket.getOutputStream()
                    output.write(("HTTP/1.1 200 OK\r\nContent-Type: application/x-ndjson\r\n" +
                        "Transfer-Encoding: chunked\r\nConnection: close\r\n\r\n").toByteArray(Charsets.US_ASCII))
                    fun frame(value: String) {
                        val bytes = (value + "\n").toByteArray(Charsets.UTF_8)
                        output.write((bytes.size.toString(16) + "\r\n").toByteArray(Charsets.US_ASCII))
                        output.write(bytes)
                        output.write("\r\n".toByteArray(Charsets.US_ASCII))
                        output.flush()
                    }
                    frame("""{"type":"accepted","operationId":"loopback-operation"}""")
                    repeat(8) { index ->
                        Thread.sleep(4_000)
                        frame("""{"type":"heartbeat","elapsedSeconds":${(index + 1) * 4}}""")
                    }
                    frame("""{"type":"result","status":200,"result":{"succeeded":true,"mode":"legend","message":"Loopback completed","responseAuthority":"LegendAi"}}""")
                    output.write("0\r\n\r\n".toByteArray(Charsets.US_ASCII))
                    output.flush()
                }
            } catch (error: Throwable) { serverFailure.set(error) }
        }
        try {
            val started = System.nanoTime()
            val result = withTimeout(42_000) {
                FounderAiChatStream(client, authenticated.baseUrl).chat("Agent", "loopback-operation",
                    FounderAiChatRequest(mode = "legend", nativeOnly = true,
                        messages = listOf(FounderAiChatMessage("user", "Independent transport control")),
                        conversationId = "loopback-conversation"), {})
            }
            assertTrue(TimeUnit.NANOSECONDS.toMillis(System.nanoTime() - started) >= 30_000)
            assertTrue(result.succeeded)
            assertEquals("Loopback completed", result.message)
            assertEquals("LegendAi", result.responseAuthority)
            assertEquals(1, calls.get())
            assertEquals(1, requests.get())
            worker.join(1_000)
            serverFailure.get()?.let { throw AssertionError("Loopback server failed", it) }
            assertFalse(worker.isAlive)
        } finally {
            client.dispatcher.cancelAll()
            accepted.get()?.close()
            server.close()
            worker.interrupt()
            worker.join(1_000)
            client.connectionPool.evictAll()
            client.dispatcher.executorService.shutdownNow()
        }
    }

    private fun InputStream.headerLine(): String {
        val line = StringBuilder()
        while (line.length < 8_192) {
            val value = read()
            check(value >= 0) { "Unexpected request EOF" }
            if (value == 10) return line.toString().removeSuffix("\r")
            line.append(value.toChar())
        }
        error("Oversized request header")
    }
}
