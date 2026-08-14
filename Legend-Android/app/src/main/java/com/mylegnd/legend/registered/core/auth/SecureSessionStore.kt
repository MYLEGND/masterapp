package com.mylegnd.legend.registered.core.auth

import android.content.Context
import android.security.keystore.KeyGenParameterSpec
import android.security.keystore.KeyProperties
import android.util.Base64
import androidx.datastore.preferences.core.edit
import androidx.datastore.preferences.core.stringPreferencesKey
import androidx.datastore.preferences.preferencesDataStore
import kotlinx.coroutines.flow.first
import kotlinx.serialization.Serializable
import kotlinx.serialization.json.Json
import java.security.KeyStore
import javax.crypto.Cipher
import javax.crypto.KeyGenerator
import javax.crypto.SecretKey
import javax.crypto.spec.GCMParameterSpec
import java.time.Instant

private val Context.legendSecureDataStore by preferencesDataStore("legend_secure_session")
private val cachedSessionKey = stringPreferencesKey("encrypted_session")

/** Presentation-only metadata for an MSAL-held credential. Tokens never enter DataStore. */
@Serializable data class CachedLegendSession(
    val actorId: String,
    val participantType: String,
    val displayName: String,
    val cachedUtc: String,
    val accountId: String? = null,
    val interactiveSignInUtc: String? = null,
) {
    fun requiresInteractiveSignIn(retentionDays: Int, now: Instant = Instant.now()): Boolean {
        val authenticatedAt = interactiveSignInUtc?.let { value -> runCatching { Instant.parse(value) }.getOrNull() }
            ?: return true
        return authenticatedAt.plusSeconds(retentionDays.toLong() * 24L * 60L * 60L) <= now
    }
}

@Serializable private data class CachedLegendSessionCatalog(
    val selectedAccountId: String? = null,
    val accounts: List<CachedLegendSession> = emptyList(),
)

/** Encrypted local presentation cache only. OAuth tokens stay inside MSAL/Android credential storage. */
class SecureSessionStore(private val context: Context) {
    private val json = Json
    suspend fun read(): CachedLegendSession? {
        val catalog = readCatalog()
        return catalog.selectedAccountId?.let { selected -> catalog.accounts.firstOrNull { it.accountId == selected } }
            ?: catalog.accounts.maxByOrNull { it.cachedUtc }
    }

    suspend fun accounts(): List<CachedLegendSession> = readCatalog().accounts
        .sortedByDescending { it.cachedUtc }

    suspend fun write(value: CachedLegendSession) {
        val accountId = value.accountId ?: value.actorId
        val normalized = value.copy(accountId = accountId)
        val catalog = readCatalog()
        val accounts = catalog.accounts.filterNot { it.accountId == accountId } + normalized
        writeCatalog(CachedLegendSessionCatalog(selectedAccountId = accountId, accounts = accounts))
    }

    suspend fun selectAccount(accountId: String): CachedLegendSession? {
        val catalog = readCatalog()
        val selected = catalog.accounts.firstOrNull { it.accountId == accountId } ?: return null
        writeCatalog(catalog.copy(selectedAccountId = accountId))
        return selected
    }

    suspend fun removeAccount(accountId: String) {
        val catalog = readCatalog()
        val remaining = catalog.accounts.filterNot { it.accountId == accountId }
        writeCatalog(
            CachedLegendSessionCatalog(
                selectedAccountId = catalog.selectedAccountId.takeUnless { it == accountId }
                    ?: remaining.maxByOrNull { it.cachedUtc }?.accountId,
                accounts = remaining,
            )
        )
    }

    suspend fun clear() { context.legendSecureDataStore.edit { it.remove(cachedSessionKey) } }

    private suspend fun readCatalog(): CachedLegendSessionCatalog {
        val decrypted = context.legendSecureDataStore.data.first()[cachedSessionKey]?.let(::decrypt)
            ?: return CachedLegendSessionCatalog()
        return runCatching { json.decodeFromString(CachedLegendSessionCatalog.serializer(), decrypted) }
            .getOrElse {
                // Pre-catalog metadata did not carry an interactive timestamp,
                // so it correctly reaches the shared 90-day checkpoint now.
                val legacy = json.decodeFromString(CachedLegendSession.serializer(), decrypted)
                CachedLegendSessionCatalog(
                    selectedAccountId = legacy.accountId ?: legacy.actorId,
                    accounts = listOf(legacy.copy(accountId = legacy.accountId ?: legacy.actorId)),
                )
            }
    }

    private suspend fun writeCatalog(value: CachedLegendSessionCatalog) {
        context.legendSecureDataStore.edit {
            it[cachedSessionKey] = encrypt(json.encodeToString(CachedLegendSessionCatalog.serializer(), value))
        }
    }
    private fun key(): SecretKey {
        val store = KeyStore.getInstance("AndroidKeyStore").apply { load(null) }
        (store.getKey(ALIAS, null) as? SecretKey)?.let { return it }
        return KeyGenerator.getInstance(KeyProperties.KEY_ALGORITHM_AES, "AndroidKeyStore").apply {
            init(KeyGenParameterSpec.Builder(ALIAS, KeyProperties.PURPOSE_ENCRYPT or KeyProperties.PURPOSE_DECRYPT).setBlockModes(KeyProperties.BLOCK_MODE_GCM).setEncryptionPaddings(KeyProperties.ENCRYPTION_PADDING_NONE).setKeySize(256).build())
        }.generateKey()
    }
    private fun encrypt(value: String): String { val cipher = Cipher.getInstance("AES/GCM/NoPadding").apply { init(Cipher.ENCRYPT_MODE, key()) }; return Base64.encodeToString(cipher.iv + cipher.doFinal(value.encodeToByteArray()), Base64.NO_WRAP) }
    private fun decrypt(value: String): String { val raw = Base64.decode(value, Base64.NO_WRAP); val cipher = Cipher.getInstance("AES/GCM/NoPadding"); cipher.init(Cipher.DECRYPT_MODE, key(), GCMParameterSpec(128, raw.copyOfRange(0, 12))); return cipher.doFinal(raw.copyOfRange(12, raw.size)).decodeToString() }
    private companion object { const val ALIAS = "legend_session_v1" }
}
