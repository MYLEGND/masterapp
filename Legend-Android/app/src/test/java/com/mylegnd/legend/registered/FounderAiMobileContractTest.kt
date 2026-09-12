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
    fun `independent answering request preserves research permission separately from strict native only`() {
        val request = FounderAiChatRequest(mode = "legend", nativeOnly = false, externalAnsweringBlocked = true,
            messages = listOf(FounderAiChatMessage("user", "Use approved evidence.")), conversationId = "independent-1")
        val restored = json.decodeFromString(FounderAiChatRequest.serializer(), json.encodeToString(request))
        assertTrue(restored.externalAnsweringBlocked)
        assertFalse(restored.nativeOnly)
        val legacy = json.decodeFromString(FounderAiChatRequest.serializer(),
            """{"mode":"legend","nativeOnly":true,"messages":[],"conversationId":"old"}""")
        assertFalse(legacy.externalAnsweringBlocked)
        assertTrue(legacy.nativeOnly)
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
    fun `foundation capability metadata survives response decoding and serialization`() {
        val response = json.decodeFromString(
            FounderAiChatResponse.serializer(),
            """{"succeeded":true,"mode":"legend","message":"A partial answer.","responseAuthority":"HostedFoundation","foundationModel":"configured-model","foundationHosting":"external","externalAnsweringUsed":true,"escalationUsed":false,"researchState":"InsufficientEvidence","learningState":"AwaitingCritic","escalationDisposition":"Restricted","stage":"response_partial","reason":"provider_output_incomplete"}""",
        )
        val restored = json.decodeFromString(FounderAiChatResponse.serializer(), json.encodeToString(response))
        assertEquals(response, restored)
        assertEquals("configured-model", restored.foundationModel)
        assertEquals("external", restored.foundationHosting)
        assertEquals(true, restored.externalAnsweringUsed)
        assertEquals(false, restored.escalationUsed)
        assertEquals("InsufficientEvidence", restored.researchState)
        assertEquals("AwaitingCritic", restored.learningState)
        assertEquals("Restricted", restored.escalationDisposition)
        assertEquals("response_partial", restored.stage)
        assertEquals("provider_output_incomplete", restored.reason)
        val legacy = json.decodeFromString(FounderAiChatResponse.serializer(),
            """{"succeeded":true,"mode":"legend","message":"Older response."}""")
        assertEquals(null, legacy.externalAnsweringUsed)
        assertEquals(null, legacy.researchState)
    }

    @Test
    fun `local foundation retains model lineage without inventing training or escalation`() {
        val local = json.decodeFromString(FounderAiChatResponse.serializer(),
            """{"succeeded":true,"mode":"legend","message":"Local answer.","responseAuthority":"LocalFoundation","foundationHosting":"LegendControlled","foundationModel":"local-pinned-model","externalAnsweringUsed":false,"escalationUsed":false}""")
        assertEquals("LocalFoundation", local.responseAuthority)
        assertEquals("LegendControlled", local.foundationHosting)
        assertEquals(false, local.externalAnsweringUsed)
        assertEquals(false, local.escalationUsed)
        assertEquals(null, local.modelTrainingRunId)
        assertEquals(null, local.modelAssistanceState)
        val promoted = local.copy(modelAssistanceState = "Applied", modelVersion = "promoted-version",
            modelTrainingRunId = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee", modelProvenance = "GovernedPromotedModel")
        assertEquals(promoted, json.decodeFromString(FounderAiChatResponse.serializer(), json.encodeToString(promoted)))
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
