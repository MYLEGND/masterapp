package com.mylegnd.legend.registered

import com.mylegnd.legend.registered.core.network.AccessTokenProvider
import com.mylegnd.legend.registered.core.network.LegendApiClient
import com.mylegnd.legend.registered.data.LoadState
import com.mylegnd.legend.registered.data.MessagingRepository
import java.io.DataInputStream
import java.net.ServerSocket
import java.util.concurrent.CompletableFuture
import java.util.concurrent.TimeUnit
import kotlinx.coroutines.runBlocking
import org.junit.Assert.*
import org.junit.Test

class MessageReactionTransportTest {
    @Test fun typedAuthenticatedSetRemoveAndForbiddenUseCanonicalContract() = runBlocking<Unit> {
        ServerSocket(0).use { server ->
            server.soTimeout = 5_000
            val peer = CompletableFuture.runAsync {
                listOf("PUT", "DELETE", "PUT").forEachIndexed { index, method ->
                    server.accept().use { socket ->
                        socket.soTimeout = 5_000
                        val input = DataInputStream(socket.getInputStream())
                        val headers = StringBuilder()
                        while (!headers.endsWith("\r\n\r\n")) {
                            check(headers.length < 16_384)
                            headers.append(input.readUnsignedByte().toChar())
                        }
                        assertTrue(headers.startsWith("$method /api/v1/mobile/messaging/conversations/thread/messages/message/reaction HTTP/1.1"))
                        assertTrue(headers.contains("Authorization: Bearer test-token", ignoreCase = true))
                        assertTrue(headers.contains("X-Legend-Participant-Type: Agent", ignoreCase = true))
                        val length = headers.lines().firstOrNull { it.startsWith("Content-Length:", true) }
                            ?.substringAfter(':')?.trim()?.toInt() ?: 0
                        val body = ByteArray(length).also(input::readFully).toString(Charsets.UTF_8)
                        if (method == "PUT") assertEquals("{\"emoji\":\"👍\"}", body) else assertEquals("", body)
                        val payload = when (index) {
                            0 -> """{"messageId":"message","reactions":[{"emoji":"👍","count":2,"reactedByCurrentActor":true}]}"""
                            1 -> """{"messageId":"message","reactions":[]}"""
                            else -> """{"code":"forbidden","message":"Not a conversation participant."}"""
                        }.toByteArray()
                        val status = if (index == 2) "403 Forbidden" else "200 OK"
                        val output = socket.getOutputStream()
                        output.write("HTTP/1.1 $status\r\nContent-Type: application/json\r\nContent-Length: ${payload.size}\r\nConnection: close\r\n\r\n".toByteArray())
                        output.write(payload)
                        output.flush()
                    }
                }
            }
            val client = LegendApiClient.create("http://127.0.0.1:${server.localPort}", object : AccessTokenProvider {
                override suspend fun accessToken() = "test-token"
            })
            try {
                val repository = MessagingRepository(client)
                val set = repository.react("Agent", "thread", "message", "👍")
                assertTrue(set is LoadState.Data)
                if (set is LoadState.Data) {
                    assertEquals(2, set.value.reactions.single().count)
                    assertTrue(set.value.reactions.single().reactedByCurrentActor)
                }
                val remove = repository.react("Agent", "thread", "message", null)
                assertTrue(remove is LoadState.Data && remove.value.reactions.isEmpty())
                val forbidden = repository.react("Agent", "thread", "message", "👍")
                assertTrue(forbidden is LoadState.Error)
                peer.get(8, TimeUnit.SECONDS)
            } finally {
                client.httpClient.dispatcher.cancelAll()
                client.httpClient.connectionPool.evictAll()
                peer.cancel(true)
            }
        }
    }
}
