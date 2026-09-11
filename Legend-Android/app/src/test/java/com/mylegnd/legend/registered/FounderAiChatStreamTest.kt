package com.mylegnd.legend.registered

import com.mylegnd.legend.registered.core.model.*
import com.mylegnd.legend.registered.core.network.*
import kotlinx.coroutines.*
import okhttp3.*
import okhttp3.MediaType.Companion.toMediaType
import okhttp3.ResponseBody.Companion.toResponseBody
import okio.Buffer
import org.junit.Assert.*
import org.junit.Test
import java.io.IOException
import java.util.concurrent.CountDownLatch
import java.util.concurrent.TimeUnit

class FounderAiChatStreamTest {
    private val terminal = """{"type":"result","status":200,"result":{"succeeded":true,"mode":"legend","message":"Computed result","responseAuthority":"LegendAi"}}"""
    private val request = FounderAiChatRequest(mode = "legend", nativeOnly = true,
        messages = listOf(FounderAiChatMessage("user", "Independent input")), conversationId = "conversation-1")
    private fun reader() = FounderAiChatStream(OkHttpClient(), "https://example.test")

    @Test fun advisoryFramesCannotCompleteChat() {
        val progress = mutableListOf<String>()
        val frames = """{"type":"accepted","operationId":"one"}
{"type":"heartbeat","elapsedSeconds":4}
{"type":"progress","progress":{"stage":"evidence","message":"Reading"}}
""" + "\n" + terminal + "\n"
        val result = reader().readResult(Buffer().writeUtf8(frames), { progress += it.progress!!.message })
        assertEquals(listOf("Reading"), progress)
        assertTrue(result.succeeded)
        assertEquals("LegendAi", result.responseAuthority)
    }

    @Test fun eofMalformedAndContradictoryTerminalFail() {
        for (frames in listOf("", "{\"type\":\"heartbeat\",\"elapsedSeconds\":4}\n", "not-json\n",
            "{\"type\":\"result\",\"status\":200}\n", terminal.replace("200", "503") + "\n")) {
            assertThrows(Exception::class.java) { reader().readResult(Buffer().writeUtf8(frames), {}) }
        }
    }

    @Test fun unknownAdvisoryFrameStillRequiresValidTerminal() {
        val unknown = """{"type":"future_advisory","detail":"Still working"}""" + "\n"
        val result = reader().readResult(Buffer().writeUtf8(unknown + terminal + "\n"), {})
        assertTrue(result.succeeded)
        assertEquals("LegendAi", result.responseAuthority)
        assertThrows(IOException::class.java) {
            reader().readResult(Buffer().writeUtf8(unknown), {})
        }
    }

    @Test fun semanticFailurePreservesStructuredDto() {
        val frames = """{"type":"result","status":503,"result":{"succeeded":false,"mode":"legend","error":"Unavailable","reason":"semantic_transition_not_supported","stage":"reasoning","responseAuthority":"LegendAi"}}"""
        val result = reader().readResult(Buffer().writeUtf8(frames + "\n"), {})
        assertFalse(result.succeeded)
        assertEquals("semantic_transition_not_supported", result.reason)
        assertEquals("reasoning", result.stage)
    }

    @Test fun samePostCarriesTypedRequestAndFinalResponse() = runBlocking {
        var calls = 0
        val authenticated = LegendApiClient.create("https://example.test", object : AccessTokenProvider {
            override suspend fun accessToken(): String = "test-token"
        })
        val client = authenticated.httpClient.newBuilder().addInterceptor { chain ->
            calls++
            val sent = chain.request()
            assertEquals("POST", sent.method)
            assertEquals("Bearer test-token", sent.header("Authorization"))
            assertEquals("/api/v1/mobile/founder/legend-ai/chat", sent.url.encodedPath)
            assertEquals("application/x-ndjson", sent.header("Accept"))
            assertEquals("founder", sent.header("X-Legend-Participant-Type"))
            assertEquals("operation-1", sent.header("X-Legend-Ai-Operation-Id"))
            val body = Buffer(); sent.body!!.writeTo(body)
            assertTrue(body.readUtf8().contains("\"conversationId\":\"conversation-1\""))
            Response.Builder().request(sent).protocol(Protocol.HTTP_1_1).code(200).message("OK")
                .body((terminal + "\n").toResponseBody("application/x-ndjson".toMediaType())).build()
        }.build()
        assertTrue(FounderAiChatStream(client, "https://example.test").chat("founder", "operation-1", request, {}).succeeded)
        assertEquals(1, calls)
    }

    @Test fun stopCancelsOriginalPostWhileHeadersAreBlocked() = runBlocking {
        val entered = CountDownLatch(1)
        val release = CountDownLatch(1)
        val cancelled = CountDownLatch(1)
        val client = OkHttpClient.Builder()
            .eventListener(object : EventListener() {
                override fun canceled(call: Call) { cancelled.countDown() }
            })
            .addInterceptor {
                entered.countDown()
                release.await(5, TimeUnit.SECONDS)
                throw IOException("Cancelled test call")
            }.build()
        val operation = launch(Dispatchers.Default) {
            FounderAiChatStream(client, "https://example.test").chat("founder", "operation-1", request, {})
        }
        try {
            assertTrue(entered.await(5, TimeUnit.SECONDS))
            operation.cancelAndJoin()
            assertTrue(cancelled.await(1, TimeUnit.SECONDS))
        } finally { release.countDown(); operation.cancelAndJoin() }
    }
}
