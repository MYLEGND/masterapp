package com.mylegnd.legend.registered.core.model

import java.time.Instant

internal data class LegendHomeNotification(val id: String, val title: String, val detail: String, val occurredUtc: String, val author: SocialAuthor? = null, val postId: String? = null) {
    val occurredAt: Instant get() = runCatching { Instant.parse(occurredUtc) }.getOrDefault(Instant.MIN)
}

/** The same two server projections used by the iOS heart: social feed activity and messaging activity. */
internal object LegendInAppActivityProjection {
    fun make(network: List<SocialActivity>, account: List<MessagingActivityNotification>): List<LegendHomeNotification> =
        (network.map { LegendHomeNotification("network:${it.id}", it.actor.displayName, it.summary.ifBlank { it.kind }, it.occurredUtc, it.actor, it.postId) } +
            account.map { LegendHomeNotification("account:${it.id}", it.title, it.detail, it.occurredUtc) })
            .distinctBy { it.id }.sortedByDescending { it.occurredAt }

    fun unreadCount(entries: List<LegendHomeNotification>, viewedAt: String): Int {
        val viewed = runCatching { Instant.parse(viewedAt) }.getOrDefault(Instant.MIN)
        return entries.count { it.occurredAt > viewed }
    }
}
