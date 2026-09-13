package com.mylegnd.legend.registered

import com.mylegnd.legend.registered.core.model.*
import kotlinx.serialization.json.Json
import org.junit.Assert.*
import org.junit.Test

class HomeActivityProjectionTest {
    @Test fun `all day calendar entries do not leak into the preceding local day`() {
        val entry = com.mylegnd.legend.registered.ui.LegendCalendarEntry(1, "All day", java.time.Instant.parse("2026-09-09T00:00:00Z").toEpochMilli(), java.time.Instant.parse("2026-09-10T00:00:00Z").toEpochMilli(), true)
        val zone = java.time.ZoneId.of("America/Phoenix")
        assertFalse(entry.occursOn(java.time.LocalDate.parse("2026-09-08"), zone))
        assertTrue(entry.occursOn(java.time.LocalDate.parse("2026-09-09"), zone))
        assertFalse(entry.occursOn(java.time.LocalDate.parse("2026-09-10"), zone))
    }

    @Test fun `heart includes social summary and account activity in absolute time order`() {
        val social = Json.decodeFromString<SocialActivity>("""{"id":"same-id","kind":"Comment","summary":"commented on your Hac","actor":{"identity":{"userId":"member","participantType":"Client"},"profileId":"profile","displayName":"Member"},"postId":"post","occurredUtc":"2026-09-09T12:00:00-07:00"}""")
        val account = MessagingActivityNotification("same-id", "Account", "Account updated", "Details", "2026-09-09T18:00:00Z")
        val entries = LegendInAppActivityProjection.make(listOf(social, social), listOf(account))
        assertEquals(2, entries.size)
        assertEquals("network:same-id", entries.first().id)
        assertEquals("commented on your Hac", entries.first().detail)
        assertEquals("post", entries.first().postId)
        assertEquals(1, LegendInAppActivityProjection.unreadCount(entries, "2026-09-09T18:30:00Z"))
    }
}
