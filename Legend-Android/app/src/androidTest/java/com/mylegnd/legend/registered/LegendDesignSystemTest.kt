package com.mylegnd.legend.registered

import android.content.Intent
import androidx.activity.ComponentActivity
import androidx.compose.ui.test.assertIsDisplayed
import androidx.compose.ui.test.junit4.v2.createAndroidComposeRule
import androidx.compose.ui.test.onNodeWithText
import com.mylegnd.legend.registered.core.design.LegendTheme
import com.mylegnd.legend.registered.core.design.LegendSocialFormats
import com.mylegnd.legend.registered.core.design.LegendNavigationPolicy
import com.mylegnd.legend.registered.core.navigation.LegendNotificationNavigation
import com.mylegnd.legend.registered.ui.LegendEmptyState
import com.mylegnd.legend.registered.ui.LegendTab
import org.junit.Assert.assertEquals
import org.junit.Rule
import org.junit.Test

class LegendDesignSystemTest {
    @get:Rule val compose = createAndroidComposeRule<ComponentActivity>()
    @Test fun emptyStateUsesLegendContent() { compose.setContent { LegendTheme { LegendEmptyState("No conversations", "Server-authorized data appears here.") } }; compose.onNodeWithText("No conversations").assertIsDisplayed() }

    @Test fun notificationRouteAcceptsSystemFcmDataPayload() {
        val navigation = LegendNotificationNavigation()

        navigation.capture(Intent().putExtra("conversationId", " conversation-42 "))

        assertEquals("conversation-42", navigation.destination.value?.conversationId)
    }

    @Test fun socialCanvasRulesComeFromTheSharedLegendSpecification() {
        compose.setContent { LegendTheme { LegendEmptyState("Legend", "Shared authority") } }

        assertEquals(10, LegendSocialFormats.named("post").maximumMediaItems)
        assertEquals(0.8, LegendSocialFormats.named("post").mediaAspectRatio, 0.0)
        assertEquals(9.0 / 16.0, LegendSocialFormats.named("story").mediaAspectRatio, 0.0)
        assertEquals(1, LegendSocialFormats.named("hac").maximumMediaItems)
    }

    @Test fun primaryNavigationOrderAndRoleVisibilityComeFromTheSharedLegendSpecification() {
        compose.setContent { LegendTheme { LegendEmptyState("Legend", "Shared authority") } }

        assertEquals(
            listOf("Home", "Clients", "Discover", "For You", "Messages", "Account"),
            LegendNavigationPolicy.Tabs,
        )
        assertEquals("Clients", LegendNavigationPolicy.AgentOnlyTab)
        assertEquals(
            LegendNavigationPolicy.Tabs,
            LegendTab.available("Agent").map(LegendTab::label),
        )
        assertEquals(
            listOf("Home", "Discover", "For You", "Messages", "Account"),
            LegendTab.available("Client").map(LegendTab::label),
        )
    }
}
