package com.mylegnd.legend.registered

import com.mylegnd.legend.registered.core.design.LegendLocalizationKey
import com.mylegnd.legend.registered.core.design.LegendLocalizationRuntime
import com.mylegnd.legend.registered.core.design.legendLocalized
import org.junit.Assert.assertEquals
import org.junit.Test
import java.util.Locale

class ApplicationLocalizationRuntimeTest {
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
