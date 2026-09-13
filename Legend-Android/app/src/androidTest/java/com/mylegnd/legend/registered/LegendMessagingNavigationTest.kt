package com.mylegnd.legend.registered

import androidx.test.platform.app.InstrumentationRegistry
import com.mylegnd.legend.registered.core.model.*
import com.mylegnd.legend.registered.core.network.LegendApi
import com.mylegnd.legend.registered.core.network.LegendApiClient
import com.mylegnd.legend.registered.data.LoadState
import com.mylegnd.legend.registered.data.MessagingRepository
import com.mylegnd.legend.registered.feature.MessagingViewModel
import com.mylegnd.legend.registered.core.realtime.LegendMessagingRealtimeEvent
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
    @Test fun heldHistoryCannotOverwriteNewerOptimisticReaction() {
        val participant = MobileParticipant(MobileIdentity("client", "Client"), "profile", "Client")
        val message = ConversationMessage("current", "chat", participant, "Visible", "2026-09-11T00:00:00Z", isMine = false, isDeleted = false)
        val recent = ConversationDetail("chat", "Direct", "Chat", messages = listOf(message), isMuted = false, isClosed = false, canManageMembers = false, hasOlderMessages = true)
        var calls = 0
        var history: Continuation<Any>? = null
        var reaction: Continuation<Any>? = null
        val api = Proxy.newProxyInstance(LegendApi::class.java.classLoader, arrayOf(LegendApi::class.java)) { _, method, args ->
            @Suppress("UNCHECKED_CAST")
            when (method.name) {
                "markRead" -> Response.success(Unit)
                "conversation" -> if (++calls == 1) Response.success(recent) else {
                    history = args!!.last() as Continuation<Any>
                    COROUTINE_SUSPENDED
                }
                "setMessageReaction" -> {
                    reaction = args!!.last() as Continuation<Any>
                    COROUTINE_SUSPENDED
                }
                else -> error("Unexpected request: ${method.name}")
            }
        } as LegendApi
        val constructor = LegendApiClient::class.java.getDeclaredConstructor(LegendApi::class.java, OkHttpClient::class.java, String::class.java).apply { isAccessible = true }
        val model = MessagingViewModel(MessagingRepository(constructor.newInstance(api, OkHttpClient(), "https://example.test/")), "Agent")
        InstrumentationRegistry.getInstrumentation().runOnMainSync {
            model.open("chat")
            model.loadOlder()
            model.react(message, "❤️")
            val expected = listOf(MessageReaction("❤️", 1, true))
            assertEquals(expected, (model.detail.value as LoadState.Data).value.messages.single().reactions)
            history!!.resume(Response.success(recent.copy(messages = listOf(message.copy(id = "older"), message), hasOlderMessages = false)))
            val merged = (model.detail.value as LoadState.Data).value
            assertEquals(listOf("older", "current"), merged.messages.map { it.id })
            assertEquals(expected, merged.messages.last().reactions)
            reaction!!.resume(Response.success(MessageReactionResult(message.id, expected)))
            model.deactivate()
        }
    }

    @Test fun olderHistoryEvictsRevokedContentButPreservesTemporaryFailure() {
        for (status in listOf(401, 403, 404, 410, 503)) {
            val participant = MobileParticipant(MobileIdentity("client", "Client"), "profile", "Client")
            val message = ConversationMessage("oldest", "chat", participant, "Visible", "2026-09-11T00:00:00Z", isMine = false, isDeleted = false)
            val recent = ConversationDetail("chat", "Direct", "Chat", messages = listOf(message), isMuted = false, isClosed = false, canManageMembers = false, hasOlderMessages = true)
            var calls = 0
            val api = Proxy.newProxyInstance(LegendApi::class.java.classLoader, arrayOf(LegendApi::class.java)) { _, method, _ ->
                when (method.name) {
                    "markRead" -> Response.success(Unit)
                    "conversation" -> if (++calls == 1) Response.success(recent) else Response.error<ConversationDetail>(status, okhttp3.ResponseBody.create(null, ""))
                    else -> error("Unexpected request: ${method.name}")
                }
            } as LegendApi
            val constructor = LegendApiClient::class.java.getDeclaredConstructor(LegendApi::class.java, OkHttpClient::class.java, String::class.java).apply { isAccessible = true }
            val model = MessagingViewModel(MessagingRepository(constructor.newInstance(api, OkHttpClient(), "https://example.test/")), "Agent")
            InstrumentationRegistry.getInstrumentation().runOnMainSync {
                model.open("chat")
                model.loadOlder()
                if (status == 503) assertEquals(recent, (model.detail.value as LoadState.Data).value)
                else assertTrue("History revocation must remove visible content", model.detail.value is LoadState.Error)
                model.deactivate()
            }
        }
    }

    @Test fun olderHistoryUsesServerTieOrderAndSendsMessageCursor() {
        val participant = MobileParticipant(MobileIdentity("client", "Client"), "profile", "Client")
        fun message(id: String) = ConversationMessage(id, "chat", participant, id, "2026-09-11T00:00:00Z", isMine = false, isDeleted = false)
        val recent = ConversationDetail("chat", "Direct", "Chat", messages = listOf(message("z"), message("a")), isMuted = false, isClosed = false, canManageMembers = false, hasOlderMessages = true)
        var calls = 0
        val api = Proxy.newProxyInstance(LegendApi::class.java.classLoader, arrayOf(LegendApi::class.java)) { _, method, args ->
            when (method.name) {
                "markRead" -> Response.success(Unit)
                "conversation" -> {
                    calls++
                    assertEquals(60, args!![3])
                    if (calls == 1) Response.success(recent) else {
                        assertEquals("2026-09-11T00:00:00Z", args[2])
                        assertEquals("z", args[4])
                        Response.success(recent.copy(messages = listOf(message("older"), message("z").copy(body = "Stale edit")), hasOlderMessages = false))
                    }
                }
                else -> error("Unexpected request: ${method.name}")
            }
        } as LegendApi
        val constructor = LegendApiClient::class.java.getDeclaredConstructor(LegendApi::class.java, OkHttpClient::class.java, String::class.java).apply { isAccessible = true }
        val model = MessagingViewModel(MessagingRepository(constructor.newInstance(api, OkHttpClient(), "https://example.test/")), "Agent")
        InstrumentationRegistry.getInstrumentation().runOnMainSync {
            model.open("chat")
            model.loadOlder()
            assertEquals(listOf("older", "z", "a"), (model.detail.value as LoadState.Data).value.messages.map { it.id })
            assertEquals("z", (model.detail.value as LoadState.Data).value.messages[1].body)
            model.deactivate()
        }
    }

    @Test fun returningChatUsesAccountOwnedSnapshotAndRevocationEvictsIt() {
        for (status in listOf(401, 403, 404, 410)) {
        val pending = mutableMapOf<String, MutableList<Continuation<Any>>>()
        val api = Proxy.newProxyInstance(LegendApi::class.java.classLoader, arrayOf(LegendApi::class.java)) { _, method, args ->
            check(method.name == "conversation")
            @Suppress("UNCHECKED_CAST")
            pending.getOrPut(args!![1] as String) { mutableListOf() }.add(args.last() as Continuation<Any>)
            COROUTINE_SUSPENDED
        } as LegendApi
        val constructor = LegendApiClient::class.java.getDeclaredConstructor(LegendApi::class.java, OkHttpClient::class.java, String::class.java).apply { isAccessible = true }
        val model = MessagingViewModel(MessagingRepository(constructor.newInstance(api, OkHttpClient(), "https://example.test/")), "Agent")
        val first = ConversationDetail("first", "Direct", "First", isMuted = false, isClosed = false, canManageMembers = false)
        InstrumentationRegistry.getInstrumentation().runOnMainSync {
            model.open("first")
            pending.getValue("first").removeAt(0).resume(Response.success(first))
            model.open("second")
            model.open("first")
            assertEquals(first, (model.detail.value as LoadState.Data).value)
            pending.getValue("first").removeAt(0).resume(Response.error<ConversationDetail>(503, okhttp3.ResponseBody.create(null, "")))
            assertEquals("Temporary failure must preserve available content", first, (model.detail.value as LoadState.Data).value)
            model.open("first")
            // The server revokes access while the cached snapshot is visible.
            pending.getValue("first").removeAt(0).resume(Response.error<ConversationDetail>(status, okhttp3.ResponseBody.create(null, "")))
            assertTrue(model.detail.value is LoadState.Error)
            model.open("second")
            model.open("first")
            assertTrue(model.detail.value is LoadState.Loading)
            pending.getValue("first").removeAt(0).resume(Response.success(first))
            model.deactivate()
            model.open("first")
            assertTrue("Account deactivation must discard prior content", model.detail.value is LoadState.Loading)
            model.deactivate()
            pending.values.flatMap { it.toList() }.forEach { it.resume(Response.success(first)) }
            assertTrue("Late cancelled responses cannot repopulate another session", model.detail.value is LoadState.Idle)
        }
    }
        }

    @Test fun olderInboxResponseCannotUndoNewIncomingOrSentActivityOrder() {
        val pending = mutableListOf<Continuation<Any>>()
        val api = Proxy.newProxyInstance(LegendApi::class.java.classLoader, arrayOf(LegendApi::class.java)) { _, method, args ->
            check(method.name == "conversations")
            @Suppress("UNCHECKED_CAST")
            pending.add(args!!.last() as Continuation<Any>)
            COROUTINE_SUSPENDED
        } as LegendApi
        val constructor = LegendApiClient::class.java.getDeclaredConstructor(LegendApi::class.java, OkHttpClient::class.java, String::class.java).apply { isAccessible = true }
        val model = MessagingViewModel(MessagingRepository(constructor.newInstance(api, OkHttpClient(), "https://example.test/")), "Agent")
        val participant = MobileParticipant(MobileIdentity("client", "Client"), "profile", "Client")
        val older = ConversationSummary("older", "Direct", "Older", participant,
            lastMessageUtc = "2026-09-10T08:00:00Z", unreadCount = 1, isClosed = false, isPinned = true)
        val newer = older.copy(id = "newer", lastMessageUtc = "2026-09-10T08:01:00Z", isPinned = false)
        val sent = older.copy(lastMessageUtc = "2026-09-10T08:02:00Z", unreadCount = 0)
        InstrumentationRegistry.getInstrumentation().runOnMainSync {
            model.load()
            model.reconcileRealtime(LegendMessagingRealtimeEvent(conversationId = "newer"))
            assertEquals("Concurrent inbox refreshes must coalesce", 1, pending.size)
            pending[0].resume(Response.success(listOf(older)))
            assertFalse("Obsolete response must not flash stale inbox content", model.conversations.value is LoadState.Data)
            assertEquals(2, pending.size)
            pending[1].resume(Response.success(listOf(newer, older)))
            assertEquals(listOf("newer", "older"), (model.conversations.value as LoadState.Data).value.map { it.id })
            model.reconcileRealtime(LegendMessagingRealtimeEvent(conversationId = "older"))
            pending[2].resume(Response.success(listOf(sent, newer)))
            assertEquals(listOf("older", "newer"), (model.conversations.value as LoadState.Data).value.map { it.id })
        }
    }

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
