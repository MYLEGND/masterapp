package com.mylegnd.legend.registered

import androidx.test.platform.app.InstrumentationRegistry
import com.mylegnd.legend.registered.core.model.*
import com.mylegnd.legend.registered.core.network.LegendApi
import com.mylegnd.legend.registered.core.network.LegendApiClient
import com.mylegnd.legend.registered.data.LoadState
import com.mylegnd.legend.registered.data.MessagingRepository
import com.mylegnd.legend.registered.feature.MessagingViewModel
import java.lang.reflect.Proxy
import java.util.concurrent.CountDownLatch
import java.util.concurrent.TimeUnit
import kotlin.coroutines.Continuation
import kotlin.coroutines.intrinsics.COROUTINE_SUSPENDED
import kotlin.coroutines.resume
import okhttp3.OkHttpClient
import org.junit.Assert.*
import org.junit.Test
import retrofit2.Response

class LegendMessagingNavigationTest {
    @Test fun openingChatDoesNotWaitForInboxAndRevalidationKeepsTheReturnedConversation() {
        val opened = CountDownLatch(1)
        val pending = mutableListOf<Continuation<Any>>()
        val conversation = ConversationDetail("chat", "Direct", "Client", isMuted = false, isClosed = false, canManageMembers = false)
        // Hold secondary HTTP completions indefinitely to prove navigation is
        // independent of network speed. The real repository and ViewModel run.
        val api = Proxy.newProxyInstance(LegendApi::class.java.classLoader, arrayOf(LegendApi::class.java)) { _, method, args ->
            when (method.name) {
                "startConversation" -> Response.success(conversation)
                "conversations", "conversation" -> {
                    @Suppress("UNCHECKED_CAST")
                    pending.add(args!!.last() as Continuation<Any>)
                    COROUTINE_SUSPENDED
                }
                else -> error("Unexpected request: ${method.name}")
            }
        } as LegendApi
        val constructor = LegendApiClient::class.java.getDeclaredConstructor(LegendApi::class.java, OkHttpClient::class.java, String::class.java).apply { isAccessible = true }
        val model = MessagingViewModel(MessagingRepository(constructor.newInstance(api, OkHttpClient(), "https://example.test/")), "Agent")
        val instrumentation = InstrumentationRegistry.getInstrumentation()
        try {
            instrumentation.runOnMainSync {
                model.startConversation(MessagingRecipient(MobileIdentity("client", "Client"), "profile", "Client")) { id ->
                    assertEquals("chat", id)
                    opened.countDown()
                }
            }
            assertTrue("Navigation waited for inbox refresh", opened.await(2, TimeUnit.SECONDS))
            instrumentation.runOnMainSync {
                model.open("chat")
                assertEquals(conversation, (model.detail.value as LoadState.Data).value)
                assertFalse(model.isSending.value)
            }
        } finally {
            instrumentation.runOnMainSync {
                pending.toList().forEach { it.resume(Response.error<Any>(503, okhttp3.ResponseBody.create(null, ""))) }
            }
        }
    }
}
