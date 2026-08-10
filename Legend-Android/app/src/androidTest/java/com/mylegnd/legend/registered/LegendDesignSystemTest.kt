package com.mylegnd.legend.registered

import androidx.activity.ComponentActivity
import androidx.compose.ui.test.assertIsDisplayed
import androidx.compose.ui.test.junit4.createAndroidComposeRule
import androidx.compose.ui.test.onNodeWithText
import com.mylegnd.legend.registered.core.design.LegendTheme
import com.mylegnd.legend.registered.ui.LegendEmptyState
import org.junit.Rule
import org.junit.Test

class LegendDesignSystemTest {
    @get:Rule val compose = createAndroidComposeRule<ComponentActivity>()
    @Test fun emptyStateUsesLegendContent() { compose.setContent { LegendTheme { LegendEmptyState("No conversations", "Server-authorized data appears here.") } }; compose.onNodeWithText("No conversations").assertIsDisplayed() }
}
