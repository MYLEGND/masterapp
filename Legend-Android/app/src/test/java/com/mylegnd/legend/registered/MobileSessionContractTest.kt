package com.mylegnd.legend.registered

import com.mylegnd.legend.registered.core.model.MobileSessionResponse
import com.mylegnd.legend.registered.core.model.ConversationMessage
import com.mylegnd.legend.registered.core.model.SocialPost
import kotlinx.serialization.json.Json
import org.junit.Assert.*
import org.junit.Test

class MobileSessionContractTest {
    private val json = Json { ignoreUnknownKeys = true }
    @Test fun `session fixture preserves typed actor and server capabilities`() {
        val fixture = """{"authenticated":true,"actor":{"identity":{"userId":"sanitized-user","participantType":"Client"},"profileId":"00000000-0000-0000-0000-000000000001","displayName":"Sanitized Member","avatar":{"kind":"remote","contentType":"image/jpeg","resourcePath":"/api/v1/mobile/profile-images/Client/00000000-0000-0000-0000-000000000001"}},"permittedParticipantTypes":["Client"],"requiresParticipantSelection":false,"capabilities":{"messaging":true,"isFounder":false,"canManageScripture":false,"canManageCommunity":false},"correlationId":"sanitized-correlation"}"""
        val response = json.decodeFromString(MobileSessionResponse.serializer(), fixture)
        assertTrue(response.authenticated); assertEquals("sanitized-user", response.actor?.identity?.userId); assertEquals("Client", response.actor?.identity?.participantType); assertTrue(response.capabilities.messaging)
    }

    @Test fun `message fixture keeps server recipient body and optional original body distinct`() {
        val fixture = """{"id":"message-1","conversationId":"conversation-1","sender":{"identity":{"userId":"member-2","participantType":"Client"},"profileId":"profile-2","displayName":"Sanitized Sender"},"body":"Bonjour","originalBody":"Hello","sentUtc":"2026-01-01T00:00:00Z","isMine":false,"isDeleted":false}"""
        val message = json.decodeFromString(ConversationMessage.serializer(), fixture)
        assertEquals("Bonjour", message.body)
        assertEquals("Hello", message.originalBody)
    }

    @Test fun `social fixture preserves server processing and visibility state`() {
        val fixture = """{"id":"post-1","author":{"identity":{"userId":"member-1","participantType":"Client"},"profileId":"profile-1","displayName":"Sanitized Member"},"contentType":"Post","body":"Server content","audience":"Public","commentsEnabled":true,"postedUtc":"2026-01-01T00:00:00Z","reactionCount":2,"commentCount":1,"reactedByCurrentActor":false,"followedByCurrentActor":false,"followRequestPending":false,"savedByCurrentActor":false,"repostedByCurrentActor":false,"media":[{"id":"asset-1","displayOrder":0,"mediaKind":"Video","mimeType":"video/mp4","fileSizeBytes":100,"processingState":"Ready","hasPreviewImage":true}]}"""
        val post = json.decodeFromString(SocialPost.serializer(), fixture)
        assertEquals("Ready", post.media.single().processingState)
        assertEquals("Public", post.audience)
    }
}
