package com.mylegnd.legend.registered

import com.mylegnd.legend.registered.core.model.FinancialDetailDestination
import com.mylegnd.legend.registered.core.model.FinancialPresentationOrder
import com.mylegnd.legend.registered.core.model.FinancialPrioritySection
import com.mylegnd.legend.registered.core.model.FinancialSummaryMetric
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Test

class FinancialPresentationContractTest {
    @Test
    fun `every iOS-supported server detail key resolves to one Android native destination`() {
        val expected = listOf(
            "assets",
            "liabilities",
            "cash-flow",
            "protection",
            "tax-profile",
            "current-outlook",
            "monthly-outlook",
            "debt-obligations",
            "financial-position",
            "upcoming-activity",
            "protection-discussion",
            "data-attention",
        )

        assertEquals(expected, FinancialDetailDestination.entries.map { it.key })
        assertNull(FinancialDetailDestination.fromServerKey("unverified-section"))
    }

    @Test
    fun `cash-flow landing owns outlook cards while dashboard keeps server priorities intact`() {
        val sections = listOf(
            priority("current-outlook", 1),
            priority("debt-obligations", 2),
            priority("data-attention", 3),
            priority("financial-position", 4),
            priority("protection-discussion", 5),
            priority("monthly-outlook", 6),
        )

        assertEquals(
            listOf(
                "financial-position",
                "data-attention",
                "debt-obligations",
                "protection-discussion",
            ),
            FinancialPresentationOrder.dashboardSections(sections).map { it.key },
        )
    }

    private fun priority(key: String, priority: Int) = FinancialPrioritySection(
        key = key,
        eyebrow = "Server priority",
        title = key,
        systemImage = "server-owned",
        priority = priority,
        status = "Current",
        reason = "Server reason",
        primaryMetric = FinancialSummaryMetric(
            label = "Value",
            semantic = "informational",
        ),
    )
}
