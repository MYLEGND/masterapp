package com.mylegnd.legend.registered

import com.mylegnd.legend.registered.core.diagnostics.RuntimeDiagnosticEvent
import com.mylegnd.legend.registered.core.diagnostics.RuntimeDiagnosticQueue
import com.mylegnd.legend.registered.core.diagnostics.RuntimeDiagnostics
import com.mylegnd.legend.registered.core.network.AccessTokenProvider
import com.mylegnd.legend.registered.core.network.LegendApiClient
import kotlinx.serialization.encodeToString
import kotlinx.serialization.json.Json
import okhttp3.Request
import org.junit.Assert.*
import org.junit.Test
import java.nio.file.Files

class RuntimeDiagnosticsTest {
    @Test fun payloadDropsUserPathQueryAndNonconstantMethod() {
        val event = RuntimeDiagnosticEvent.network("/api/v1/mobile/messaging/member@example.com/private-id?token=secret", "private-value", 503)
        val payload = Json { encodeDefaults = true }.encodeToString(event)
        assertEquals("/api/v1/mobile/messaging/{segment}/{segment}", event.route)
        assertEquals("REQUEST", event.operation)
        listOf("member@example.com", "private-id", "secret", "private-value").forEach { assertFalse(payload.contains(it)) }
        assertEquals("observation", event.category)
        assertEquals("", event.stackTrace)
        assertEquals("/{route}", RuntimeDiagnosticEvent.routeTemplate("https://private.example.com/secret"))
    }

    @Test fun pendingQueueIsBoundedDeduplicatedAndReplaysAfterRecreation() {
        val dir = Files.createTempDirectory("legend-diagnostics").toFile()
        try {
            val file = dir.resolve("pending.json")
            val queue = RuntimeDiagnosticQueue(file)
            repeat(30) { queue.record(RuntimeDiagnosticEvent.network("/api/v1/mobile/social/private-id", "GET", 400 + it)) }
            assertEquals(20, queue.count())
            queue.record(RuntimeDiagnosticEvent.network("/api/v1/mobile/social/another-private-id", "GET", 429))
            assertEquals(20, queue.count())
            assertTrue(file.length() <= 65_536)
            assertFalse(file.readText().contains("private-id"))
            val reloaded = RuntimeDiagnosticQueue(file)
            assertEquals(20, reloaded.count())
            val (id, event) = reloaded.next()!!
            assertEquals(410, event.statusCode)
            reloaded.finish(id, true)
            assertEquals(19, RuntimeDiagnosticQueue(file).count())
        } finally { dir.deleteRecursively() }
    }

    @Test fun thirdFailedAttemptDropsEventAndCorruptFileIsIgnored() {
        val queue = RuntimeDiagnosticQueue(null)
        queue.record(RuntimeDiagnosticEvent.crash())
        repeat(3) {
            val (id, _) = queue.next()!!
            queue.finish(id, false)
        }
        assertEquals(0, queue.count())
        val file = Files.createTempFile("legend-diagnostics", ".json").toFile()
        try {
            file.writeText("malformed")
            assertEquals(0, RuntimeDiagnosticQueue(file).count())
            file.writeText("x".repeat(65_537))
            assertEquals(0, RuntimeDiagnosticQueue(file).count())
        } finally { file.delete() }
    }

    @Test fun unauthenticatedReplayNeverReachesNetwork() {
        val client = LegendApiClient.create("http://127.0.0.1:1", object : AccessTokenProvider {
            override suspend fun accessToken(): String? = null
        })
        val failure = runCatching {
            client.httpClient.newCall(Request.Builder().url("http://127.0.0.1:1" + RuntimeDiagnostics.endpoint).build()).execute()
        }.exceptionOrNull()
        assertEquals("Diagnostic replay requires the existing authenticated session.", failure?.message)
    }
    @Test fun crashSnapshotAlwaysDelegatesAndOmitsExceptionText() {
        val directory = Files.createTempDirectory("legend-crash").toFile()
        try {
            val original = IllegalStateException("private message token=secret")
            var delegated = 0
            val previous = Thread.UncaughtExceptionHandler { thread, error ->
                assertSame(Thread.currentThread(), thread)
                assertSame(original, error)
                delegated++
            }
            val file = directory.resolve("crash.json")
            RuntimeDiagnostics.crashHandler(file, previous).uncaughtException(Thread.currentThread(), original)
            assertEquals(1, delegated)
            assertTrue(file.length() <= 4096)
            assertFalse(file.readText().contains("private message"))
            assertFalse(file.readText().contains("secret"))
            RuntimeDiagnostics.crashHandler(directory.resolve("missing/crash.json"), previous)
                .uncaughtException(Thread.currentThread(), original)
            assertEquals(2, delegated)
        } finally { directory.deleteRecursively() }
    }

    @Test fun operationFailureKeepsCompilerLocationWithoutExceptionMessage() {
        val error = IllegalStateException("private message Bearer secret")
        error.stackTrace = arrayOf(StackTraceElement(
            "com.mylegnd.legend.registered.data.HomeRepository", "load", "LegendRepositories.kt", 42,
        ))
        val event = RuntimeDiagnosticEvent.operationFailure(error)
        val payload = Json { encodeDefaults = true }.encodeToString(event)
        assertEquals("load", event.operation)
        assertEquals("LegendRepositories.kt:42 load", event.stackTrace)
        assertFalse(payload.contains("private message"))
        assertFalse(payload.contains("secret"))
    }
}
