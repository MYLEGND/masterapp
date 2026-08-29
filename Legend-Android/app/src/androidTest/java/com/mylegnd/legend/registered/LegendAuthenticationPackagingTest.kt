package com.mylegnd.legend.registered

import android.content.pm.PackageManager
import androidx.test.ext.junit.runners.AndroidJUnit4
import androidx.test.platform.app.InstrumentationRegistry
import org.json.JSONObject
import org.junit.Assert.assertArrayEquals
import org.junit.Assert.assertTrue
import org.junit.Test
import org.junit.runner.RunWith
import java.net.URLDecoder
import java.nio.charset.StandardCharsets
import java.security.MessageDigest
import java.util.Base64

@RunWith(AndroidJUnit4::class)
class LegendAuthenticationPackagingTest {
    private val context get() = InstrumentationRegistry.getInstrumentation().targetContext

    @Test
    fun packagedMsalRedirectMatchesTheInstalledSigningCertificate() {
        val configuration = context.resources.openRawResource(R.raw.legend_msal_config)
            .bufferedReader()
            .use { JSONObject(it.readText()) }
        val redirectHash = URLDecoder.decode(
            configuration.getString("redirect_uri").substringAfterLast('/'),
            StandardCharsets.UTF_8,
        )
        val packageInfo = context.packageManager.getPackageInfo(
            context.packageName,
            PackageManager.PackageInfoFlags.of(PackageManager.GET_SIGNING_CERTIFICATES.toLong()),
        )
        val signerHashes = requireNotNull(packageInfo.signingInfo)
            .apkContentsSigners
            .map { signer ->
                Base64.getEncoder().encodeToString(
                    MessageDigest.getInstance("SHA-1").digest(signer.toByteArray()),
                )
            }

        assertTrue(
            "The packaged MSAL redirect must match an installed app signer.",
            redirectHash in signerHashes,
        )
    }

    @Test
    fun launcherResourceIsTheGeneratedPngArtworkRatherThanTheLegacyVector() {
        val pngSignature = context.resources.openRawResource(R.drawable.ic_legend_launcher)
            .use { it.readNBytes(8) }

        assertArrayEquals(
            byteArrayOf(-119, 80, 78, 71, 13, 10, 26, 10),
            pngSignature,
        )
    }
}
