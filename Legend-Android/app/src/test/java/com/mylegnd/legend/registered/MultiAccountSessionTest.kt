package com.mylegnd.legend.registered

import android.app.Activity
import com.mylegnd.legend.registered.core.auth.*
import com.mylegnd.legend.registered.core.config.LegendRuntimeConfiguration
import com.mylegnd.legend.registered.core.design.LegendDesignAuthority
import com.mylegnd.legend.registered.core.network.LegendApiClient
import com.mylegnd.legend.registered.core.session.*
import java.io.File
import java.net.ServerSocket
import java.time.Instant
import java.util.concurrent.atomic.AtomicBoolean
import kotlin.concurrent.thread
import kotlinx.coroutines.runBlocking
import org.junit.Assert.*
import org.junit.Before
import org.junit.Test

class MultiAccountSessionTest {
    @Before fun loadCanonicalPolicy() {
        val source = listOf(File("../../Legend-Design/legend-design.tokens.json"), File("../Legend-Design/legend-design.tokens.json")).first { it.exists() }
        LegendDesignAuthority.loadSpecification(source.readText())
    }

    @Test fun addRoleSelectSwitchAndFailureKeepIndependentAccounts() = runBlocking {
        SessionServer().use { server ->
            val auth = TestAuth()
            val authority = LegendBearerTokenAuthority(auth)
            val cache = MemorySessions()
            val initialDate = Instant.now().minusSeconds(30L * 86400).toString()
            cache.write(saved("a", initialDate))
            val client = LegendApiClient.create(server.url, authority)
            val repository = SessionRepository(LegendRuntimeConfiguration("https://api.example.test", "client", "https://identity.example.test", "scope", "msauth://test"), auth, authority, { client }, cache)
            assertTrue(repository.restore() is SessionState.Authenticated)
            assertTrue(repository.authenticateAccount(true) { LegendAuthenticatedAccount("b", "B") } is SessionState.RoleSelection)
            assertEquals("a", cache.read()?.accountId)
            assertEquals(initialDate, cache.read()?.interactiveSignInUtc)
            val added = repository.selectRole("Client") as SessionState.Authenticated
            assertEquals(setOf("a", "b"), added.session.signedInAccounts.map { it.accountId }.toSet())
            repository.switchSignedInAccount("a")
            assertEquals("a", cache.read()?.accountId)
            assertEquals(initialDate, cache.read()?.interactiveSignInUtc)
            server.rejectNext.set(true)
            assertTrue(runCatching { repository.switchSignedInAccount("b") }.isFailure)
            assertEquals("a", cache.read()?.accountId)
            assertEquals("a", authority.accessToken())
            assertTrue(runCatching { repository.authenticateAccount(true) { throw AuthenticationCancelledException() } }.isFailure)
            assertEquals("a", authority.accessToken())
            assertEquals(2, cache.accounts().size)
            repository.authenticateAccount(true) { LegendAuthenticatedAccount("b", "B") }
            repository.signOut()
            assertNull(auth.removed)
            assertEquals(2, cache.accounts().size)
            assertEquals("a", cache.read()?.accountId)
            assertTrue(repository.restore() is SessionState.Authenticated)
            repository.authenticateAccount(true) { LegendAuthenticatedAccount("c", "C") }
            repository.signOut()
            assertEquals("c", auth.removed)
            assertEquals(2, cache.accounts().size)
            assertEquals("a", cache.read()?.accountId)
        }
    }

    @Test fun expiredAccountCannotSilentlyRenewButAnotherAccountCan() = runBlocking {
        val auth = TestAuth()
        val authority = LegendBearerTokenAuthority(auth)
        authority.activateAccount(saved("expired", Instant.now().minusSeconds(91L * 86400).toString()))
        assertTrue(authority.requiresInteractiveSignIn())
        assertTrue(runCatching { authority.accessToken() }.exceptionOrNull() is AuthenticationReauthenticationRequiredException)
        assertNull(auth.lastRequested)
        authority.activateAccount(saved("current", Instant.now().minusSeconds(89L * 86400).toString()))
        assertEquals("current", authority.accessToken())
        assertEquals("current", auth.lastRequested)
    }

    private fun saved(id: String, date: String) = CachedLegendSession(id, "Client", id, Instant.now().toString(), id, date)

    private class TestAuth : LegendAuthClient {
        var lastRequested: String? = null
        var removed: String? = null
        override suspend fun restoreAccessToken(accountId: String?): String? { lastRequested = accountId; return accountId }
        override suspend fun signIn(activity: Activity, forceReauthentication: Boolean) = error("Interactive UI is outside this test")
        override suspend fun signedInAccounts() = listOf(LegendAuthenticatedAccount("a", "A"), LegendAuthenticatedAccount("b", "B"))
        override suspend fun signOut(accountId: String?) { removed = accountId }
    }
    private class MemorySessions : LegendSessionStoring {
        private val values = linkedMapOf<String, CachedLegendSession>()
        private var selected: String? = null
        override suspend fun read() = values[selected]
        override suspend fun accounts() = values.values.toList()
        override suspend fun write(value: CachedLegendSession) { selected = value.accountId!!; values[selected!!] = value }
        override suspend fun selectAccount(accountId: String): CachedLegendSession? { selected = accountId; return values[accountId] }
        override suspend fun removeAccount(accountId: String) { values.remove(accountId) }
        override suspend fun clear() { values.clear(); selected = null }
    }
    private class SessionServer : AutoCloseable {
        private val socket = ServerSocket(0)
        val url = "http://127.0.0.1:${socket.localPort}/"
        val rejectNext = AtomicBoolean(false)
        private val worker = thread(isDaemon = true) {
            while (!socket.isClosed) {
                val peer = try { socket.accept() } catch (_: java.net.SocketException) { break }
                peer.use {
                    it.soTimeout = 5_000
                    val input = it.getInputStream().bufferedReader()
                    val first = input.readLine()
                    val headers = mutableMapOf<String, String>()
                    while (true) {
                        val line = input.readLine() ?: break
                        if (line.isEmpty()) break
                        headers[line.substringBefore(':').lowercase()] = line.substringAfter(':').trim()
                    }
                    repeat(headers["content-length"]?.toIntOrNull() ?: 0) { input.read() }
                    val id = headers["authorization"]?.removePrefix("Bearer ") ?: "missing"
                    val roleSelection = id != "a" && first.startsWith("GET")
                    val actor = """{"identity":{"userId":"$id","participantType":"Client"},"profileId":"profile-$id","displayName":"$id"}"""
                    val body = """{"authenticated":true,"actor":${if (roleSelection) "null" else actor},"permittedParticipantTypes":["Client"],"requiresParticipantSelection":$roleSelection,"capabilities":{},"correlationId":"test"}""".toByteArray()
                    val status = if (rejectNext.getAndSet(false)) "503 Unavailable" else "200 OK"
                    it.getOutputStream().apply {
                        write("HTTP/1.1 $status\r\nContent-Type: application/json\r\nContent-Length: ${body.size}\r\nConnection: close\r\n\r\n".toByteArray())
                        write(body); flush()
                    }
                }
            }
        }
        override fun close() { socket.close(); worker.join(5_000) }
    }
}
