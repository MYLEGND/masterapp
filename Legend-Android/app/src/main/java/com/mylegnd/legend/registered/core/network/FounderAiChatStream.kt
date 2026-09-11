package com.mylegnd.legend.registered.core.network

import com.mylegnd.legend.registered.core.model.*
import kotlinx.coroutines.suspendCancellableCoroutine
import kotlinx.serialization.json.Json
import okhttp3.*
import okhttp3.HttpUrl.Companion.toHttpUrl
import okhttp3.MediaType.Companion.toMediaType
import okhttp3.RequestBody.Companion.toRequestBody
import okio.BufferedSource
import java.io.IOException

/** One authenticated POST carries advisory frames and the authoritative terminal DTO. */
internal class FounderAiChatStream(private val client: OkHttpClient, private val baseUrl: String) {
    private val json = Json { ignoreUnknownKeys = true }

    suspend fun chat(
        role: String,
        operationId: String,
        request: FounderAiChatRequest,
        onProgress: (FounderAiProgressEnvelope) -> Unit,
    ): FounderAiChatResponse = suspendCancellableCoroutine { continuation ->
        val call = client.newCall(Request.Builder()
            .url(baseUrl.toHttpUrl().newBuilder().addPathSegments("api/v1/mobile/founder/legend-ai/chat").build())
            .header("Accept", "application/x-ndjson")
            .header("X-Legend-Participant-Type", role)
            .header("X-Legend-Ai-Operation-Id", operationId)
            .post(json.encodeToString(FounderAiChatRequest.serializer(), request)
                .toRequestBody("application/json".toMediaType()))
            .build())
        // Cancellation must interrupt headers AND a blocked body read immediately.
        continuation.invokeOnCancellation { call.cancel() }
        call.enqueue(object : Callback {
            override fun onFailure(call: Call, error: IOException) {
                if (continuation.isActive) continuation.resumeWith(Result.failure(error))
            }
            override fun onResponse(call: Call, response: Response) {
                try {
                    val result = response.use {
                        if (!it.isSuccessful) {
                            val problem = runCatching {
                                json.decodeFromString(MobileApiProblem.serializer(), it.body.string())
                            }.getOrNull()
                            throw LegendApiException(it.code, problem)
                        }
                        if (it.body.contentType()?.subtype != "x-ndjson") {
                            throw IOException("Founder chat did not return the requested stream.")
                        }
                        readResult(it.body.source(), onProgress) { continuation.isActive }
                    }
                    if (continuation.isActive) continuation.resumeWith(Result.success(result))
                } catch (error: Exception) {
                    if (continuation.isActive) continuation.resumeWith(Result.failure(error))
                }
            }
        })
    }

    internal fun readResult(
        source: BufferedSource,
        onProgress: (FounderAiProgressEnvelope) -> Unit,
        active: () -> Boolean = { true },
    ): FounderAiChatResponse {
        while (active()) {
            val line = source.readUtf8Line() ?: break
            if (line.isBlank()) continue
            val frame = json.decodeFromString(FounderAiProgressEnvelope.serializer(), line)
            when (frame.type) {
                "accepted", "heartbeat" -> Unit
                "progress" -> onProgress(frame)
                "result" -> {
                    val result = frame.result ?: throw IOException("Founder chat terminal result is missing.")
                    val status = frame.status ?: throw IOException("Founder chat terminal status is missing.")
                    if (status !in 200..599 || (status !in 200..299 && result.succeeded)) {
                        throw IOException("Founder chat terminal status contradicts its result.")
                    }
                    return result
                }
                else -> throw IOException("Unknown Founder chat stream frame.")
            }
        }
        throw IOException("Founder chat ended without a terminal result.")
    }
}
