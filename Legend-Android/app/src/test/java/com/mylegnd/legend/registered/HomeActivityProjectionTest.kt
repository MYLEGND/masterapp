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


    @Test fun `raw engineering activity is not duplicated beside Founder action cards`() {
        val engineering = MessagingActivityNotification(
            "engineering-id",
            "Engineering",
            "LEGEND Engineering release approval required",
            "machine detail",
            "2026-10-02T20:00:00Z",
        )
        val account = MessagingActivityNotification(
            "account-id",
            "Account",
            "Account updated",
            "Details",
            "2026-10-02T19:00:00Z",
        )

        val entries = LegendInAppActivityProjection.make(emptyList(), listOf(engineering, account))

        assertEquals(1, entries.size)
        assertEquals("account:account-id", entries.single().id)
    }

    @Test fun `Founder engineering action contract carries explicit approve and deny steps`() {
        val item = Json { ignoreUnknownKeys = true }.decodeFromString<FounderEngineeringActionItem>(
            """{"workItemId":"00000000-0000-0000-0000-000000000001","attentionKind":"action_required","title":"Release decision needed","summary":"Validation passed.","actionStep":"Approve or deny this release.","requiresFounderAction":true,"primaryAction":"approve_release","primaryActionLabel":"Approve release","secondaryAction":"deny_release","secondaryActionLabel":"Deny release","technicalSummary":"Work item details","updatedUtc":"2026-10-02T20:00:00Z"}"""
        )

        assertTrue(item.requiresFounderAction)
        assertEquals("approve_release", item.primaryAction)
        assertEquals("deny_release", item.secondaryAction)
        assertEquals("Approve release", item.primaryActionLabel)
        assertEquals("Deny release", item.secondaryActionLabel)
    }
}
