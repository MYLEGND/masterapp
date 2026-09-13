package com.mylegnd.legend.registered

import com.mylegnd.legend.registered.feature.calling.*
import kotlinx.serialization.json.Json
import org.junit.Assert.*
import org.junit.Test

class LegendScreenSharePolicyTest {
    private val screen = LegendCallScreenSharePolicy(
        1920, 1080, 15, 2_500_000,
        1280, 720, 10, 1_200_000,
        960, 540, 5, 250_000, 0.15,
    )

    @Test fun screenTiersPreserveReadableResolutionAndBoundCellularCost() {
        assertEquals(LegendScreenCaptureLimits(1920, 1080, 15, 2_500_000), screen.limits(2, false))
        assertEquals(LegendScreenCaptureLimits(1280, 720, 10, 1_200_000), screen.limits(2, true))
        assertEquals(screen.limits(1, false), screen.limits(1, true))
        assertEquals(LegendScreenCaptureLimits(960, 540, 5, 250_000), screen.limits(0, true))
    }

    @Test fun capturePreservesPortraitLandscapeAndSquareContentWithinBothPolicyBounds() {
        val high = screen.limits(2, false)
        assertEquals(1080 to 1920, high.dimensions(1440, 2560))
        assertEquals(1920 to 1080, high.dimensions(2560, 1440))
        assertEquals(1080 to 1080, high.dimensions(2000, 2000))
        assertEquals(640 to 360, high.dimensions(640, 360)) // No invented/upscaled detail.
        val low = screen.limits(0, false)
        assertEquals(540 to 960, low.dimensions(1080, 1920))
        val ultrawide = high.dimensions(3440, 1440)
        assertEquals(1920, ultrawide.first)
        assertTrue(ultrawide.second in 802..804)
        assertTrue(ultrawide.first % 2 == 0 && ultrawide.second % 2 == 0)
    }

    @Test fun measuredVideoBudgetPreservesAudioAndTransportWithoutInventingMissingBandwidth() {
        assertEquals(233_500, screen.videoBudget(250_000, 350_000.0, 64_000))
        assertEquals(701_000, screen.videoBudget(2_500_000, 900_000.0, 64_000))
        assertEquals(0, screen.videoBudget(250_000, 64_000.0, 64_000))
        assertEquals(0, screen.videoBudget(250_000, 0.0, 64_000))
        assertEquals(2_500_000, screen.videoBudget(2_500_000, 10_000_000.0, 64_000))
        assertEquals(250_000, screen.videoBudget(250_000, null, 64_000))
        assertEquals(250_000, screen.videoBudget(250_000, Double.NaN, 64_000))
    }

    @Test fun malformedPresentationMetadataDoesNotBecomeACallFailure() {
        val json = Json { ignoreUnknownKeys = true }
        assertNull(json.callMediaState("not json"))
        assertNull(json.callMediaState("{}"))
        assertNull(json.callMediaState("""{"screenSharing":"invalid"}"""))
        assertEquals(LegendCallMediaState(true, false), json.callMediaState("""{"screenSharing":true,"future":1}"""))
        assertEquals(LegendCallMediaState(false, true), json.callMediaState("""{"screenSharing":false,"request":true}"""))
    }

    @Test fun additiveScreenContractKeepsOldServersCompatibleAndUsesServerValues() {
        val old = """{"stunUrls":[],"wifiWidth":1280,"wifiHeight":720,"wifiFps":30,"cellularWidth":640,"cellularHeight":480,"cellularFps":24,"videoBitrate":1000000,"audioBitrate":64000,"ringSeconds":45,"connectSeconds":20,"recoveryAttempts":3}"""
        assertNull(Json.decodeFromString<LegendCallPolicy>(old).screenShare)
        val current = old.dropLast(1) + """, "screenShare":{"highWidth":1600,"highHeight":900,"highFps":12,"highBitrate":1900000,"mediumWidth":1200,"mediumHeight":674,"mediumFps":8,"mediumBitrate":900000,"lowWidth":800,"lowHeight":450,"lowFps":4,"lowBitrate":300000,"transportHeadroomFraction":0.15}}"""
        val received = requireNotNull(Json.decodeFromString<LegendCallPolicy>(current).screenShare)
        assertEquals(LegendScreenCaptureLimits(1600, 900, 12, 1_900_000), received.limits(2, false))
        assertEquals(LegendScreenCaptureLimits(800, 450, 4, 300_000), received.limits(0, false))
    }
}
