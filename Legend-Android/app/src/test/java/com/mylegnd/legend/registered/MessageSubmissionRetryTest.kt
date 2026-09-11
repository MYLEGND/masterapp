package com.mylegnd.legend.registered

import com.mylegnd.legend.registered.core.model.SendMessageRequest
import com.mylegnd.legend.registered.feature.PendingMessageSubmission
import com.mylegnd.legend.registered.data.LoadState
import kotlinx.coroutines.runBlocking
import kotlinx.serialization.encodeToString
import kotlinx.serialization.json.Json
import kotlinx.serialization.json.jsonObject
import kotlinx.serialization.json.jsonPrimitive
import org.junit.Assert.*
import org.junit.Test

class MessageSubmissionRetryTest {
    @Test
    fun retryRetainsMessageAcknowledgementAndCompletedUploadsUntilPayloadChanges() {
        val first = PendingMessageSubmission.forPayload(null, "conversation", "body", "reply", listOf("first", "second"))
        val afterLostAcknowledgement = PendingMessageSubmission.forPayload(first, "conversation", "body", "reply", listOf("first", "second"))
        assertSame(first, afterLostAcknowledgement)
        assertEquals(first.clientMessageId, afterLostAcknowledgement.clientMessageId)
        assertNull(afterLostAcknowledgement.acknowledgedMessageId)
        first.acknowledgedMessageId = "server-message"
        first.uploadedAttachmentIndexes.add(0)
        val retryAttachment = PendingMessageSubmission.forPayload(first, "conversation", "body", "reply", listOf("first", "second"))
        assertEquals("server-message", retryAttachment.acknowledgedMessageId)
        assertEquals(listOf(1), retryAttachment.attachments.indices.filterNot { it in retryAttachment.uploadedAttachmentIndexes })
        for (changed in listOf(
            PendingMessageSubmission.forPayload(first, "other", "body", "reply", listOf("first", "second")),
            PendingMessageSubmission.forPayload(first, "conversation", "changed", "reply", listOf("first", "second")),
            PendingMessageSubmission.forPayload(first, "conversation", "body", "other-reply", listOf("first", "second")),
            PendingMessageSubmission.forPayload(first, "conversation", "body", "reply", listOf("other-file")),
            PendingMessageSubmission.forPayload(null, "conversation", "body", "reply", listOf("first", "second"))
        )) {
            assertNotEquals(first.clientMessageId, changed.clientMessageId)
            assertNull(changed.acknowledgedMessageId)
            assertTrue(changed.uploadedAttachmentIndexes.isEmpty())
        }
    }

    @Test
    fun failedUploadRetainsOnlyAcknowledgedProgressAndRetryDoesNotDuplicateCompletedFiles() = runBlocking {
        val submission = PendingMessageSubmission.forPayload(null, "conversation", "body", null, listOf("first", "second"))
        val calls = mutableListOf<Int>()
        assertTrue(submission.uploadRemaining { error("No upload before message acknowledgement") } is LoadState.Error)
        submission.acknowledgedMessageId = "server-message"
        val failed = submission.uploadRemaining { index ->
            calls.add(index)
            if (index == 0) LoadState.Data("server-file") else LoadState.Error("Upload unavailable")
        }
        assertEquals(LoadState.Error("Upload unavailable"), failed)
        assertEquals(listOf(0, 1), calls)
        assertEquals(setOf(0), submission.uploadedAttachmentIndexes)
        calls.clear()
        val retry = PendingMessageSubmission.forPayload(submission, "conversation", "body", null, listOf("first", "second"))
        assertTrue(retry.uploadRemaining { index -> calls.add(index); LoadState.Data("server-file") } is LoadState.Data)
        assertEquals(listOf(1), calls)
        assertEquals("server-message", retry.acknowledgedMessageId)
        assertEquals(setOf(0, 1), retry.uploadedAttachmentIndexes)
    }

    @Test
    fun retryIdentityUsesExistingWireContractWithoutClientClaimedSender() {
        val request = SendMessageRequest("body", "reply", "stable-client-message")
        val payload = Json.parseToJsonElement(Json.encodeToString(request)).jsonObject
        assertEquals(setOf("body", "replyToMessageId", "clientMessageId"), payload.keys)
        assertEquals("stable-client-message", payload.getValue("clientMessageId").jsonPrimitive.content)
    }
}
