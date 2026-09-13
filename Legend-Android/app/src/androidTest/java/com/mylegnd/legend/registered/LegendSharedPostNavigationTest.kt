package com.mylegnd.legend.registered

import androidx.test.platform.app.InstrumentationRegistry
import com.mylegnd.legend.registered.core.model.*
import com.mylegnd.legend.registered.core.network.*
import com.mylegnd.legend.registered.data.*
import com.mylegnd.legend.registered.feature.SocialViewModel
import java.lang.reflect.Proxy
import kotlin.coroutines.Continuation
import kotlin.coroutines.intrinsics.COROUTINE_SUSPENDED
import kotlin.coroutines.resume
import okhttp3.OkHttpClient
import org.junit.Assert.*
import org.junit.Test
import retrofit2.Response
import kotlinx.coroutines.*
import java.net.ServerSocket
import java.util.concurrent.CompletableFuture
import java.util.concurrent.CountDownLatch
import java.util.concurrent.TimeUnit
import com.mylegnd.legend.registered.core.media.AuthenticatedMediaRepository

class LegendSharedPostNavigationTest {
    @Test fun cancelingAttachmentPreviewClosesTransportAndDeletesPartialFile() = runBlocking {
        ServerSocket(0).use { server ->
            val headersSent = CountDownLatch(1)
            val disconnected = CompletableFuture.supplyAsync {
                server.accept().use { socket ->
                    socket.soTimeout = 5_000
                    val reader = socket.getInputStream().bufferedReader()
                    while (!reader.readLine().isNullOrEmpty()) { }
                    socket.getOutputStream().write("HTTP/1.1 200 OK\r\nContent-Length: 1048576\r\nContent-Type: image/jpeg\r\n\r\npartial".toByteArray())
                    socket.getOutputStream().flush()
                    headersSent.countDown()
                    socket.getInputStream().read()
                }
            }
            val api = Proxy.newProxyInstance(LegendApi::class.java.classLoader, arrayOf(LegendApi::class.java)) { _, _, _ -> error("Retrofit is not used for protected file transfers") } as LegendApi
            val constructor = LegendApiClient::class.java.getDeclaredConstructor(LegendApi::class.java, OkHttpClient::class.java, String::class.java).apply { isAccessible = true }
            val context = InstrumentationRegistry.getInstrumentation().targetContext
            val repository = AuthenticatedMediaRepository(context, constructor.newInstance(api, OkHttpClient(), "http://127.0.0.1:${server.localPort}"))
            val filename = "cancel-${java.util.UUID.randomUUID()}.jpg"
            val attachment = MessageAttachment("attachment", filename, "image/jpeg", 1048576, "Clean", "2026-09-11T00:00:00Z", true)
            val download = async(start = CoroutineStart.UNDISPATCHED) { repository.messageAttachmentFile(attachment, "Agent") }
            try {
                assertTrue(headersSent.await(5, TimeUnit.SECONDS))
                withTimeout(2_000) { download.cancelAndJoin() }
                assertEquals(-1, disconnected.get(5, TimeUnit.SECONDS))
                assertTrue(context.cacheDir.resolve("message-attachments").listFiles().orEmpty().none { it.name.endsWith(filename) })
            } finally { download.cancel(); server.close() }
        }
    }

    private fun post(id: String) = SocialPost(id, SocialAuthor(MobileIdentity("author", "Client"), "profile", "Author"), "Post", "Original", "AuthorizedNetwork", commentsEnabled = true, postedUtc = "2026-09-11T00:00:00Z", reactionCount = 0, commentCount = 0, reactedByCurrentActor = false, followedByCurrentActor = false, savedByCurrentActor = false, repostedByCurrentActor = false)
    private fun model(api: LegendApi): SocialViewModel {
        val constructor = LegendApiClient::class.java.getDeclaredConstructor(LegendApi::class.java, OkHttpClient::class.java, String::class.java).apply { isAccessible = true }
        return SocialViewModel(SocialRepository(constructor.newInstance(api, OkHttpClient(), "https://example.test/")), "Agent")
    }
    @Test fun originalPostMutationsRefreshTheFocusedPostWithoutInventedState() {
        var original = post("shared")
        var reads = 0
        val api = Proxy.newProxyInstance(LegendApi::class.java.classLoader, arrayOf(LegendApi::class.java)) { _, method, args ->
            when (method.name) {
                "socialPost" -> { assertEquals("shared", args!![1]); reads++; Response.success(original) }
                "react" -> { original = original.copy(reactionCount = 1, reactedByCurrentActor = true); Response.success(original) }
                "socialFeed" -> Response.success(SocialSnapshot())
                else -> error("Unexpected request ${method.name}")
            }
        } as LegendApi
        InstrumentationRegistry.getInstrumentation().runOnMainSync {
            val model = model(api)
            model.openPost("shared")
            assertEquals(1, reads)
            model.react("shared")
            assertEquals(2, reads)
            assertTrue((model.openedPost.value as LoadState.Data).value.reactedByCurrentActor)
            model.closePost()
            assertEquals(LoadState.Idle, model.openedPost.value)
        }
    }
    @Test fun lateOriginalResponseCannotReopenAClosedOrDifferentShare() {
        val pending = mutableListOf<Continuation<Any>>()
        val api = Proxy.newProxyInstance(LegendApi::class.java.classLoader, arrayOf(LegendApi::class.java)) { _, method, args ->
            check(method.name == "socialPost")
            @Suppress("UNCHECKED_CAST")
            pending.add(args!!.last() as Continuation<Any>)
            COROUTINE_SUSPENDED
        } as LegendApi
        InstrumentationRegistry.getInstrumentation().runOnMainSync {
            val model = model(api)
            model.openPost("first"); model.openPost("second")
            pending[0].resume(Response.success(post("first")))
            assertEquals(LoadState.Loading, model.openedPost.value)
            model.closePost()
            pending[1].resume(Response.success(post("second")))
            assertEquals(LoadState.Idle, model.openedPost.value)
        }
    }
}
