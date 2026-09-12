package com.mylegnd.legend.registered

import com.mylegnd.legend.registered.feature.calling.LegendCallAdaptationPolicy
import com.mylegnd.legend.registered.feature.calling.targetQuality
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Test

class LegendCallAdaptationTest {
    @Test fun weakWifiAndUnknownSamplesDoNotPromiseHighQuality() {
        val tuning = LegendCallAdaptationPolicy(3, 4, 350_000, 900_000, 0.6,
            320, 180, 15, 250_000, 640, 360, 24, 600_000, 4.0)
        assertEquals(0, tuning.targetQuality(0.0, null))
        assertEquals(0, tuning.targetQuality(349_999.0, 0.1))
        assertEquals(1, tuning.targetQuality(350_000.0, 0.1))
        assertEquals(2, tuning.targetQuality(900_000.0, 0.1))
        assertEquals(0, tuning.targetQuality(2_000_000.0, 0.7))
        assertEquals(0, tuning.targetQuality(null, 0.7))
        assertNull(tuning.targetQuality(null, 0.1))
        assertNull(tuning.targetQuality(Double.NaN, null))
        assertNull(tuning.targetQuality(Double.POSITIVE_INFINITY, null))
        assertNull(tuning.targetQuality(-1.0, null))
    }
}
