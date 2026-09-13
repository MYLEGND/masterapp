package com.mylegnd.legend.registered

import com.mylegnd.legend.registered.ui.LegendReactionEmoji
import com.mylegnd.legend.registered.ui.legendReactionSearch
import org.junit.Assert.*
import org.junit.Test

class LegendReactionToneTest {
    @Test fun savedToneUsesOnlyMappedVariantAndDefaultRemovesPriorTone() {
        val entry = LegendReactionEmoji("👍🏿", "thumbs up", emptyList(), "👍",
            mapOf("default" to "👍", "mediumLight" to "👍🏼", "dark" to "👍🏿"))
        assertEquals("👍🏼", entry.variant(2))
        assertEquals("👍", entry.variant(0))
        assertEquals("👍🏿", entry.variant(5))
    }
    @Test fun unsupportedToneLeavesBaseUntouchedWithoutSynthesizingModifier() {
        val entry = LegendReactionEmoji("❤️", "heart", emptyList(), "❤️", mapOf("default" to "❤️"))
        assertEquals("❤️", entry.variant(4))
        assertEquals("❤️", entry.variant(99))
    }
    @Test fun mixedToneSequenceUsesCanonicalMappedSequence() {
        val entry = LegendReactionEmoji("🫱🏿‍🫲🏻", "handshake", emptyList(), "🤝",
            mapOf("default" to "🤝", "dark" to "🤝🏿"))
        assertEquals("🤝🏿", entry.variant(5))
        assertFalse(entry.variant(5).contains("🏻"))
    }
    @Test fun exactMixedToneSearchFindsItsSingleBaseEntry() {
        val base = LegendReactionEmoji("🤝", "handshake", listOf("handshake"), "🤝", mapOf("default" to "🤝", "dark" to "🤝🏿"))
        val mixed = base.copy(emoji = "🫱🏿‍🫲🏻")
        assertEquals(listOf(base), legendReactionSearch(listOf(base, mixed), "🫱🏿‍🫲🏻"))
    }
    @Test fun keywordSearchNormalizesAccentsAndRequiresEveryWord() {
        val happy = LegendReactionEmoji("😀", "grin", listOf("happy", "face"), "😀", mapOf("default" to "😀"))
        val sad = LegendReactionEmoji("😢", "cry", listOf("sad", "face"), "😢", mapOf("default" to "😢"))
        assertEquals(listOf(happy), legendReactionSearch(listOf(happy, sad), "HÁPPY face"))
        assertTrue(legendReactionSearch(listOf(happy, sad), "happy cat").isEmpty())
    }

}
