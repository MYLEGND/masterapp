package com.mylegnd.legend.registered

import com.mylegnd.legend.registered.core.model.LegendInAppActivityProjection
import com.mylegnd.legend.registered.core.model.MessagingActivityNotification
import com.mylegnd.legend.registered.core.network.MobileWorkOrder
import kotlinx.serialization.json.Json
import org.junit.Assert.*
import org.junit.Test

class WorkNotificationsContractTest {
    @Test fun `channel unread totals do not depend on the visible notification limit`() {
        val snapshot = Json.decodeFromString<com.mylegnd.legend.registered.core.network.NotificationSnapshot>(
            """{"badge":{"unreadCount":134,"revision":1,"updatedUtc":"2026-11-03T10:00:00Z"},"notifications":[],"workChannelsVersion":1,"engineeringChannelsVersion":1,"channelUnreadCounts":{"work":120,"engineering":12,"personal":2}}""")
        assertEquals(120, snapshot.channelUnreadCounts?.get("work"))
        assertEquals(12, snapshot.channelUnreadCounts?.get("engineering"))
        assertTrue(snapshot.notifications.isEmpty())
    }

    @Test fun `work ledger entries do not duplicate into personal activity`() {
        val work = MessagingActivityNotification("work", "StoreOrder", "Order", "Ready", "2026-11-03T10:20:30Z", channel = "work")
        val personal = MessagingActivityNotification("personal", "Account", "Account", "Updated", "2026-11-03T10:20:30Z")
        val entries = LegendInAppActivityProjection.make(emptyList(), listOf(work, personal, MessagingActivityNotification("engineering", "Engineering", "Engineering", "Notice", "2026-11-03T10:20:30Z", channel = "engineering")))
        assertEquals(listOf("account:personal"), entries.map { it.id })
    }

    @Test fun `order concurrency token roundtrips with full server precision`() {
        val json = """{"id":"fixture","businessId":"business","profileId":"profile","orderNumber":"QA","fulfillmentStatus":"Packed","allowedStatuses":["Packed","Shipped"],"updatedUtc":"2026-11-03T10:20:30.1234567Z","firstName":"Fixture","lastName":"Only","addressLine1":"Fixture","city":"Fixture","state":"AZ","postalCode":"00000","items":[]}"""
        val order = Json.decodeFromString<MobileWorkOrder>(json)
        assertEquals("2026-11-03T10:20:30.1234567Z", order.updatedUtc)
    }
}
