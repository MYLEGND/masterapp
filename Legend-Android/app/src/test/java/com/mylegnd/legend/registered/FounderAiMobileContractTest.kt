package com.mylegnd.legend.registered

import com.mylegnd.legend.registered.core.model.FounderAiChatMessage
import com.mylegnd.legend.registered.core.model.FounderAiChatRequest
import com.mylegnd.legend.registered.core.model.FounderAiChatResponse
import com.mylegnd.legend.registered.core.model.FounderAiProgressEnvelope
import kotlinx.serialization.encodeToString
import kotlinx.serialization.json.Json
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

class FounderAiMobileContractTest {
    private val json = Json { ignoreUnknownKeys = true }

    @Test
    fun `teacher request preserves the canonical mode and never enables native-only`() {
        val request = FounderAiChatRequest(
            mode = "teacher",
            nativeOnly = false,
            messages = listOf(FounderAiChatMessage("user", "Inspect this safely.")),
            conversationId = "conversation-1",
        )

        val wire = json.encodeToString(request)

        assertTrue(wire.contains("\"mode\":\"teacher\""))
        assertTrue(wire.contains("\"nativeOnly\":false"))
        assertTrue(wire.contains("\"conversationId\":\"conversation-1\""))
        assertFalse(wire.contains("provider"))
    }

    @Test
    fun `response authority remains server-projected for distinct native and teacher responders`() {
        val native = json.decodeFromString(
            FounderAiChatResponse.serializer(),
            """{"succeeded":true,"mode":"legend","message":"Governed result.","responseAuthority":"LegendAi","stage":"realization"}""",
        )
        val teacher = json.decodeFromString(
            FounderAiChatResponse.serializer(),
            """{"succeeded":true,"mode":"teacher","message":"Teacher result.","responseAuthority":"OpenAITeacher","stage":"provider"}""",
        )

        assertEquals("LegendAi", native.responseAuthority)
        assertEquals("OpenAITeacher", teacher.responseAuthority)
        assertEquals("legend", native.mode)
        assertEquals("teacher", teacher.mode)
    }

    @Test
    fun `progress remains an advisory typed stream rather than a second response contract`() {
        val envelope = json.decodeFromString(
            FounderAiProgressEnvelope.serializer(),
            """{"type":"progress","elapsedSeconds":4,"progress":{"stage":"evidence","message":"Inspecting governed evidence","round":1}}""",
        )

        assertEquals("progress", envelope.type)
        assertEquals(4, envelope.elapsedSeconds)
        assertEquals("evidence", envelope.progress?.stage)
        assertEquals("Inspecting governed evidence", envelope.progress?.message)
    }
}
