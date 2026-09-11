package com.mylegnd.legend.registered

import com.mylegnd.legend.registered.core.model.MessagingSharedContent
import com.mylegnd.legend.registered.core.model.SendMessageRequest
import com.mylegnd.legend.registered.feature.PendingMessageSubmission
import kotlinx.serialization.encodeToString
import kotlinx.serialization.json.Json
import kotlinx.serialization.json.jsonObject
import kotlinx.serialization.json.jsonPrimitive
import org.junit.Assert.*
import org.junit.Test

class MessageSharedContentTest {
    @Test fun sourceOnlyRetriesRemainBoundToTheSamePost() {
        val first = PendingMessageSubmission.forPayload(null, "conversation", "", null, emptyList(), "post-a")
        val retry = PendingMessageSubmission.forPayload(first, "conversation", "", null, emptyList(), "post-a")
        val other = PendingMessageSubmission.forPayload(first, "conversation", "", null, emptyList(), "post-b")
        assertSame(first, retry)
        assertNotEquals(first.clientMessageId, other.clientMessageId)
        val body = Json.parseToJsonElement(Json.encodeToString(SendMessageRequest("", clientMessageId = first.clientMessageId, sharedPostId = first.sharedPostId))).jsonObject
        assertEquals("", body["body"]!!.jsonPrimitive.content)
        assertEquals("post-a", body["sharedPostId"]!!.jsonPrimitive.content)
    }

    @Test fun captionlessContentStillCarriesViewableMediaIdentity() {
        val card = Json.decodeFromString<MessagingSharedContent>("""
            {"sourcePostId":"post-a","status":"available","contentType":"Post","body":"","authorDisplayName":"Member",
             "media":[{"id":"asset-a","displayOrder":0,"mediaKind":"Image","mimeType":"image/jpeg","fileSizeBytes":12,"processingState":"Ready","hasPreviewImage":false}],"url":"/Social/Posts/post-a"}
        """)
        assertEquals("asset-a", card.media.single().id)
        assertEquals("", card.body)
        assertEquals("/Social/Posts/post-a", card.url)
    }

    @Test fun unavailableReferenceDoesNotRequirePrivateCaptionOrMedia() {
        val card = Json.decodeFromString<MessagingSharedContent>("""{"sourcePostId":"post-a","status":"unavailable","media":[],"url":"/Social/Posts/post-a"}""")
        assertNull(card.body)
        assertNull(card.authorDisplayName)
        assertTrue(card.media.isEmpty())
    }
}
