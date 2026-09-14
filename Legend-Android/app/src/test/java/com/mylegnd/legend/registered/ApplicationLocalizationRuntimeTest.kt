package com.mylegnd.legend.registered

import com.mylegnd.legend.registered.core.design.LegendLocalizationKey
import com.mylegnd.legend.registered.core.design.LegendLocalizationRuntime
import com.mylegnd.legend.registered.core.design.legendLocalized
import com.mylegnd.legend.registered.core.model.ApplicationLocalizedCopy
import com.mylegnd.legend.registered.core.design.validatedText
import org.junit.Assert.assertNull
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test
import java.util.Locale
import com.mylegnd.legend.registered.data.request
import com.mylegnd.legend.registered.data.LoadState
import com.mylegnd.legend.registered.core.network.LegendApiException
import kotlinx.coroutines.runBlocking
import kotlinx.coroutines.CancellationException
import kotlinx.serialization.SerializationException
import com.mylegnd.legend.registered.core.model.ApplicationLocalizationCatalog
import com.mylegnd.legend.registered.core.model.ApplicationLocalizationContinuation
import com.mylegnd.legend.registered.core.auth.CachedLegendSession

class ApplicationLocalizationRuntimeTest {
    @Test
    fun `existing request transport distinguishes offline from malformed data and permanent HTTP status`() = runBlocking {
        for (status in listOf(400, 401, 403, 404, 408, 429, 503)) {
            val result = request<String> { throw LegendApiException(status, null) } as LoadState.Error
            assertEquals(status == 408 || status == 429 || status == 503, result.transportRetryable)
            assertEquals(status, result.status)
        }
        assertTrue((request<String> { throw java.io.IOException("offline") } as LoadState.Error).transportRetryable)
        assertFalse((request<String> { throw SerializationException("malformed") } as LoadState.Error).transportRetryable)
        var cancelled = false
        try { request<String> { throw CancellationException("closed") } } catch (_: CancellationException) { cancelled = true }
        assertTrue(cancelled)
    }

    @Test
    fun `cached languages retain exact account role catalog identity and authoritative withdrawal`() {
        val copy = ApplicationLocalizedCopy("entry", "Settings", "Anviwònman", "visual interface copy", "r1", emptyList(), "AzureTranslator", "ProviderDerived", "Observation", "now", true)
        fun catalog(language: String) = ApplicationLocalizationCatalog("v1", "en", language, language, "now", true, listOf(copy))
        val original = CachedLegendSession("actor-a", "Agent", "A", "now", accountId = "a", preferredLanguageCode = "ht")
        val saved = original.retainingLocalization("Agent", catalog("ht")).retainingLocalization("Agent", catalog("es"))
        assertEquals(listOf("ht", "es"), saved.localizationCatalogsFor("Agent").map { it.languageCode })
        assertTrue(saved.localizationCatalogsFor("Client").isEmpty())
        assertTrue(original.localizationCatalogsFor("Agent").isEmpty())
        assertEquals("ht", saved.retainingLocalization("Agent", catalog("en")).preferredLanguageCode)
        assertEquals("now", saved.cachedUtc)
        val revoked = saved.retainingLocalization("Agent", catalog("ht").copy(entries = listOf(copy.copy(text="Settings", failureCode="approved_translation_unavailable"))))
        assertNull(revoked.localizationCatalogsFor("Agent").last().entries.single().validatedText("Settings", "visual interface copy", "r1", emptyList()))
        var bounded = saved
        repeat(12) { bounded = bounded.retainingLocalization("Agent", catalog("language-$it")) }
        assertEquals(8, bounded.localizationCatalogs.size)
    }

    @Test
    fun `continuation follows bounded server instructions including monthly delay not provider error names`() {
        val next = ApplicationLocalizationContinuation("RetryableFailure", 1, 2_678_400)
        assertTrue(next.isResumable())
        assertFalse(next.copy(disposition="Blocked").isResumable())
        assertFalse(next.copy(disposition="AwaitingApproval").isResumable())
        assertFalse(next.copy(retryAfterSeconds=0).isResumable())
        assertFalse(next.copy(maximumRequestsPerPass=999).isResumable())
    }

    @Test
    fun `catalog entries require matching source contract and isolate individual failures`() {
        val valid = ApplicationLocalizedCopy("entry", "Settings", "Anviwònman", "visual interface copy", "revision1", emptyList(), "AzureTranslator", "ProviderDerived", "Observation", "2026-09-10T00:00:00Z", true)
        assertEquals("Anviwònman", valid.validatedText("Settings", "visual interface copy", "revision1", emptyList()))
        assertNull(valid.copy(sourceRevision = "revision2").validatedText("Settings", "visual interface copy", "revision1", emptyList()))
        assertNull(valid.copy(failureCode = "translation_pending").validatedText("Settings", "visual interface copy", "revision1", emptyList()))
        assertNull(valid.copy(source = "Other").validatedText("Settings", "visual interface copy", "revision1", emptyList()))
        assertNull(valid.copy(placeholders = listOf("name")).validatedText("Settings", "visual interface copy", "revision1", emptyList()))
    }

    @Test
    fun `identical refresh preserves presentation but revised copy or locale updates it`() {
        val key = LegendLocalizationKey("Settings", "visual interface copy")
        val copy = mapOf(key to "Paramètres")
        LegendLocalizationRuntime.install(copy, Locale.FRENCH)
        assertFalse(LegendLocalizationRuntime.install(copy.toMap(), Locale.FRENCH))
        assertTrue(LegendLocalizationRuntime.install(mapOf(key to "Réglages"), Locale.FRENCH))
        assertEquals("Réglages", legendLocalized("Settings"))
        assertTrue(LegendLocalizationRuntime.install(mapOf(key to "Réglages"), Locale.CANADA_FRENCH))
        assertFalse(LegendLocalizationRuntime.install(mapOf(key to "Réglages"), Locale.CANADA_FRENCH))
    }

    @Test
    fun `one installed catalog renders visual and accessibility copy in Haitian Creole`() {
        LegendLocalizationRuntime.install(
            mapOf(
                LegendLocalizationKey("Secure sign in", "visual interface copy") to "Konekte an sekirite",
                LegendLocalizationKey("Sign-in action", "accessibility copy") to "Aksyon koneksyon",
                LegendLocalizationKey("Welcome, {name}.", "visual interface copy") to "Byenveni, {name}."),
            Locale.forLanguageTag("ht"),
        )

        assertEquals("Konekte an sekirite", legendLocalized("Secure sign in"))
        assertEquals(
            "Aksyon koneksyon",
            legendLocalized("Sign-in action", "accessibility copy"),
        )
        assertEquals(
            "Byenveni, Zac.",
            legendLocalized("Welcome, {name}.", mapOf("name" to "Zac")),
        )
        assertEquals("ht", LegendLocalizationRuntime.locale().language)
    }

    @Test
    fun `missing retained copy falls back to source without a provider call`() {
        LegendLocalizationRuntime.install(emptyMap(), Locale.forLanguageTag("ht"))

        assertEquals("Offline source copy", legendLocalized("Offline source copy"))
    }

    @Test
    fun `installing a changed language atomically updates active presentation`() {
        val key = LegendLocalizationKey("Settings", "visual interface copy")
        LegendLocalizationRuntime.install(mapOf(key to "Paramètres"), Locale.FRENCH)
        assertEquals("Paramètres", legendLocalized("Settings"))

        LegendLocalizationRuntime.install(mapOf(key to "Anviwònman"), Locale.forLanguageTag("ht"))
        assertEquals("Anviwònman", legendLocalized("Settings"))
        assertEquals("ht", LegendLocalizationRuntime.locale().language)
    }
}
