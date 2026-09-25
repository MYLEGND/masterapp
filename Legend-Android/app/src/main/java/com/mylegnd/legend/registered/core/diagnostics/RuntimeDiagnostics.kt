package com.mylegnd.legend.registered.core.diagnostics

import android.app.Application
import com.mylegnd.legend.registered.BuildConfig
import java.io.File
import java.io.FileOutputStream
import java.time.Instant
import java.util.UUID
import java.util.concurrent.atomic.AtomicBoolean
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.launch
import kotlinx.serialization.Serializable
import kotlinx.serialization.encodeToString
import kotlinx.serialization.json.Json
import okhttp3.MediaType.Companion.toMediaType
import okhttp3.OkHttpClient
import okhttp3.Request
import okhttp3.RequestBody.Companion.toRequestBody

/** A bounded technical observation, never a client-side declaration of a confirmed defect. */
@Serializable
internal data class RuntimeDiagnosticEvent(
    val appIdentifier: String = "Legend-Android",
    val platform: String = "android",
    val route: String,
    val sourceFilePath: String = "Legend-Android/app/src/main/java/com/mylegnd/legend/registered/core/network/LegendApi.kt",
    val errorName: String,
    val errorMessage: String = "Native operation did not complete.",
    val stackTrace: String = "",
    val gitCommitHash: String = BuildConfig.GIT_COMMIT_HASH,
    val timestamp: String = Instant.now().toString(),
    val operation: String,
    val category: String = "observation",
    val statusCode: Int? = null,
    val appVersion: String = "${BuildConfig.VERSION_NAME} (${BuildConfig.VERSION_CODE})",
) {
    companion object {
        // Only known static route areas survive. All dynamic path segments and queries are discarded.
        fun routeTemplate(path: String): String {
            val parts = path.substringBefore('?').split('/').filter { it.isNotEmpty() }
            val areas = setOf("account", "accounts", "auth", "session", "home", "social", "messaging", "notifications", "founder", "discovery", "journey", "journey-circles", "agent", "localization", "calls", "calling", "community", "scripture")
            if (parts.size < 4 || parts.take(3) != listOf("api", "v1", "mobile")) return "/{route}"
            val area = parts[3].takeIf { it in areas } ?: "{area}"
            return "/api/v1/mobile/$area" + "/{segment}".repeat((parts.size - 4).coerceAtMost(6))
        }
        fun network(path: String, method: String, status: Int? = null) = RuntimeDiagnosticEvent(
            route = routeTemplate(path),
            operation = method.takeIf { it in setOf("GET", "POST", "PUT", "PATCH", "DELETE", "HEAD") } ?: "REQUEST",
            errorName = if (status == null) "TransportFailure" else "HttpFailure",
            statusCode = status?.takeIf { it in 100..599 },
        )
        fun operationFailure(error: Exception): RuntimeDiagnosticEvent {
            // Compiler-owned frame symbols only; Throwable.message/toString and arguments are forbidden.
            val frame = error.stackTrace.firstOrNull {
                it.className.startsWith("com.mylegnd.legend.registered.") &&
                    it.className.matches(Regex("[A-Za-z0-9_.$]{1,300}")) &&
                    it.fileName?.matches(Regex("[A-Za-z0-9_]+\\.(kt|java)")) == true &&
                    it.methodName.matches(Regex("[A-Za-z0-9_$<>-]{1,100}"))
            }
            val source = frame?.let {
                "Legend-Android/app/src/main/java/" + it.className.substringBeforeLast('.').replace('.', '/') + "/" + it.fileName
            } ?: "Legend-Android/app/src/main/java/com/mylegnd/legend/registered/data/LegendRepositories.kt"
            return RuntimeDiagnosticEvent(
                route = "/native",
                operation = frame?.methodName ?: "repositoryRequest",
                sourceFilePath = source.take(512),
                errorName = if (error is kotlinx.serialization.SerializationException) "DecodeFailure" else "OperationFailure",
                stackTrace = frame?.let { "${it.fileName}:${it.lineNumber.coerceAtLeast(0)} ${it.methodName}" } ?: "",
            )
        }

        fun crash() = RuntimeDiagnosticEvent(
            route = "/native",
            operation = "uncaughtException",
            sourceFilePath = "Legend-Android/app/src/main/java/com/mylegnd/legend/registered/LegendApplication.kt",
            errorName = "UnhandledException",
        )
    }
}

/** The file contains only events built above, never tokens, exception messages or HTTP payloads. */
internal class RuntimeDiagnosticQueue(private val file: File?) {
    @Serializable private data class Pending(val id: String, val event: RuntimeDiagnosticEvent, var attempts: Int = 0)
    private val json = Json { ignoreUnknownKeys = true; encodeDefaults = true }
    private val pending = runCatching {
        if (file != null && file.isFile && file.length() <= 65_536) {
            json.decodeFromString<List<Pending>>(file.readText()).filter { it.attempts in 0..2 }.takeLast(20).toMutableList()
        } else mutableListOf()
    }.getOrElse { mutableListOf() }

    @Synchronized fun record(event: RuntimeDiagnosticEvent) {
        if (pending.any { it.event.route == event.route && it.event.operation == event.operation && it.event.errorName == event.errorName && it.event.statusCode == event.statusCode }) return
        pending.add(Pending(UUID.randomUUID().toString(), event))
        while (pending.size > 20) pending.removeAt(0)
        persist()
    }
    @Synchronized fun next(): Pair<String, RuntimeDiagnosticEvent>? {
        val first = pending.firstOrNull() ?: return null
        first.attempts++
        persist()
        return first.id to first.event
    }
    @Synchronized fun finish(id: String, success: Boolean) {
        pending.removeAll { it.id == id && (success || it.attempts >= 3) }
        persist()
    }
    @Synchronized internal fun count(): Int = pending.size
    private fun persist() {
        val storage = file ?: return
        runCatching {
            val bytes = json.encodeToString(pending).toByteArray()
            if (bytes.size > 65_536) return
            val temporary = File(storage.parentFile, "${storage.name}.tmp")
            FileOutputStream(temporary).use { it.write(bytes) }
            temporary.renameTo(storage)
        }
    }
}

internal object RuntimeDiagnostics {
    const val endpoint = "/api/v1/mobile/runtime-diagnostics"
    private val json = Json { encodeDefaults = true }
    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.IO)
    private val flushing = AtomicBoolean(false)
    @Volatile private var lastReplayNanos: Long? = null
    @Volatile private var queue = RuntimeDiagnosticQueue(null)
    private val initialized = AtomicBoolean(false)

    fun initialize(application: Application) {
        if (!initialized.compareAndSet(false, true)) return
        val directory = File(application.noBackupFilesDir, "runtime-diagnostics").apply { mkdirs() }
        queue = RuntimeDiagnosticQueue(File(directory, "pending.json"))
        val crashFile = File(directory, "crash.json")
        runCatching {
            if (crashFile.isFile && crashFile.length() <= 4096) {
                queue.record(json.decodeFromString<RuntimeDiagnosticEvent>(crashFile.readText()))
            }
            crashFile.delete()
        }
        val previous = Thread.getDefaultUncaughtExceptionHandler()
        Thread.setDefaultUncaughtExceptionHandler(crashHandler(crashFile, previous))
    }

    internal fun crashHandler(crashFile: File, previous: Thread.UncaughtExceptionHandler?) =
        Thread.UncaughtExceptionHandler { thread, throwable ->
            // Best effort only: bounded local write, no network, locks, exception text or credentials.
            // Android's prior crash handler remains authoritative for process termination/reporting.
            try {
                val bytes = json.encodeToString(RuntimeDiagnosticEvent.crash()).toByteArray()
                if (bytes.size <= 4096) FileOutputStream(crashFile).use { it.write(bytes); it.fd.sync() }
            } catch (_: Throwable) {
                // A fatal/OOM state may prevent even this small write; never mask the original crash.
            } finally {
                if (previous != null) previous.uncaughtException(thread, throwable)
                else { android.os.Process.killProcess(android.os.Process.myPid()); kotlin.system.exitProcess(10) }
            }
        }

    fun recordNetwork(path: String, method: String, status: Int? = null) {
        if (path.endsWith(endpoint)) return
        runCatching { queue.record(RuntimeDiagnosticEvent.network(path, method, status)) }
    }

    fun recordOperationFailure(error: Exception) {
        runCatching { queue.record(RuntimeDiagnosticEvent.operationFailure(error)) }
    }

    fun recordAuthenticationFailure() {
        runCatching { queue.record(RuntimeDiagnosticEvent(
            route = "/native/auth", operation = "authenticate", errorName = "AuthenticationFailure",
            sourceFilePath = "Legend-Android/app/src/main/java/com/mylegnd/legend/registered/core/logging/LegendLogger.kt",
        )) }
    }

    fun replay(client: OkHttpClient, baseUrl: String) {
        if (!initialized.get() || queue.count() == 0 || !flushing.compareAndSet(false, true)) return
        val now = System.nanoTime()
        if (lastReplayNanos?.let { now - it < 60_000_000_000L } == true) { flushing.set(false); return }
        lastReplayNanos = now
        scope.launch {
            try {
                // Every attempt obtains credentials from the existing auth interceptor.
                // At most three sends per authenticated request opportunity, three attempts/event.
                repeat(3) {
                    val (id, event) = queue.next() ?: return@launch
                    val success = runCatching {
                        val request = Request.Builder().url(baseUrl.trimEnd('/') + endpoint)
                            .post(json.encodeToString(event).toRequestBody("application/json".toMediaType())).build()
                        client.newCall(request).execute().use { it.isSuccessful }
                    }.getOrDefault(false)
                    queue.finish(id, success)
                    if (!success) return@launch
                }
            } finally { flushing.set(false) }
        }
    }
}
