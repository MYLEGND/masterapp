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

private val Context.legendSecureDataStore by preferencesDataStore("legend_secure_session")
private val cachedSessionKey = stringPreferencesKey("encrypted_session")
@Serializable data class CachedLegendSession(val actorId: String, val participantType: String, val displayName: String, val cachedUtc: String)

/** Encrypted local presentation cache only. OAuth tokens stay inside MSAL/Android credential storage. */
class SecureSessionStore(private val context: Context) {
    private val json = Json
    suspend fun read(): CachedLegendSession? = context.legendSecureDataStore.data.first()[cachedSessionKey]?.let(::decrypt)?.let { json.decodeFromString(CachedLegendSession.serializer(), it) }
    suspend fun write(value: CachedLegendSession) { context.legendSecureDataStore.edit { it[cachedSessionKey] = encrypt(json.encodeToString(CachedLegendSession.serializer(), value)) } }
    suspend fun clear() { context.legendSecureDataStore.edit { it.remove(cachedSessionKey) } }
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
