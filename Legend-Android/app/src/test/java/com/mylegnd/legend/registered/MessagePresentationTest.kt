package com.mylegnd.legend.registered

import com.mylegnd.legend.registered.core.model.*
import kotlinx.serialization.json.Json
import org.junit.Assert.*
import org.junit.Test

class MessagePresentationTest {
    private val sender = MobileParticipant(MobileIdentity("same-id", "Client"), "profile", "Sender")
    private fun message(id: String, second: Int, mine: Boolean = true, deleted: Boolean = false) =
        ConversationMessage(id, "thread", sender, "Body", "2026-09-11T00:00:${second.toString().padStart(2, '0')}Z", isMine = mine, isDeleted = deleted)

    @Test fun latestReadAndNewerSentUseCanonicalOrderAndExcludeDeletedAndIncoming() {
        val page = listOf(message("older", 1), message("tie-first", 2), message("tie-last", 2),
            message("incoming", 3, mine = false), message("deleted", 4, deleted = true), message("new", 5))
        val reader = MessagingReadReceipt("other", "Client", "2026-09-11T00:00:02Z")
        assertEquals(mapOf("tie-last" to "Read", "new" to "Sent"), messageReceiptLabels(page, listOf(reader)))
    }

    @Test fun privacyFilteredReadersDoNotInventReadAndTypedActorIsDistinct() {
        val page = listOf(message("one", 1), message("two", 2))
        val expected = mapOf("one" to "Sent", "two" to "Sent")
        assertEquals(expected, messageReceiptLabels(page, emptyList()))
        assertEquals(expected, messageReceiptLabels(page, listOf(MessagingReadReceipt("same-id", "Client", "2026-09-11T00:00:03Z"))))
        assertEquals(mapOf("two" to "Read"), messageReceiptLabels(page, listOf(MessagingReadReceipt("same-id", "Agent", "2026-09-11T00:00:03Z"))))
        assertEquals(expected, messageReceiptLabels(page, listOf(MessagingReadReceipt("other", "Client", "invalid"))))
    }

    @Test fun reactionCountsAndActorSelectionAreDecodedFromServer() {
        val result = Json.decodeFromString<MessageReactionResult>("""{"messageId":"message","reactions":[{"emoji":"❤️","count":2,"reactedByCurrentActor":true}]}""")
        assertEquals("message", result.messageId)
        assertEquals(MessageReaction("❤️", 2, true), result.reactions.single())
    }
}
