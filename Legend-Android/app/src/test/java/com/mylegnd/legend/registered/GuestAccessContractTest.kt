package com.mylegnd.legend.registered

import com.mylegnd.legend.registered.core.network.AccessTokenProvider
import com.mylegnd.legend.registered.core.network.LegendApiClient
import com.mylegnd.legend.registered.data.GuestRepository
import com.mylegnd.legend.registered.data.LoadState
import java.io.DataInputStream
import java.net.ServerSocket
import java.util.concurrent.CompletableFuture
import java.util.concurrent.TimeUnit
import kotlinx.coroutines.runBlocking
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

class GuestAccessContractTest {
    @Test
    fun publicContentLoadsWithoutAccountCredentials() = runBlocking {
        ServerSocket(0).use { server ->
            server.soTimeout = 5_000
            val peer = CompletableFuture.supplyAsync {
                server.accept().use { socket ->
                    socket.soTimeout = 5_000
                    val input = DataInputStream(socket.getInputStream())
                    val headers = StringBuilder()
                    while (!headers.endsWith("\r\n\r\n")) headers.append(input.readUnsignedByte().toChar())
                    val body = """{"title":"Explore Legend","subtitle":"Welcome","introduction":"Public reading","readings":[],"guides":[],"accountTitle":"Your account","accountDescription":"Sign in","links":[]}""".toByteArray()
                    socket.getOutputStream().apply {
                        write("HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: ${body.size}\r\nConnection: close\r\n\r\n".toByteArray())
                        write(body)
                        flush()
                    }
                    headers.toString()
                }
            }
            val client = LegendApiClient.create("http://127.0.0.1:${server.localPort}/",
                object : AccessTokenProvider { override suspend fun accessToken(): String? = null })
            val result = GuestRepository(client).load()
            assertTrue(result is LoadState.Data)
            assertEquals("Explore Legend", (result as LoadState.Data).value.title)
            val headers = peer.get(5, TimeUnit.SECONDS)
            assertTrue(headers.startsWith("GET /api/v1/mobile/guest HTTP/1.1"))
            assertFalse(headers.contains("Authorization:", ignoreCase = true))
            assertFalse(headers.contains("X-Legend-Participant-Type:", ignoreCase = true))
            assertFalse(headers.contains("Cookie:", ignoreCase = true))
        }
    }
}
