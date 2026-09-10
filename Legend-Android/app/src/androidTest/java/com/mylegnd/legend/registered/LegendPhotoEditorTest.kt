package com.mylegnd.legend.registered

import android.graphics.Bitmap
import android.graphics.BitmapFactory
import android.graphics.Canvas
import android.graphics.Color
import android.net.Uri
import androidx.activity.ComponentActivity
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.test.*
import androidx.compose.ui.test.junit4.v2.createAndroidComposeRule
import com.mylegnd.legend.registered.core.design.LegendTheme
import com.mylegnd.legend.registered.ui.*
import org.junit.Assert.*
import org.junit.Rule
import org.junit.Test
import java.io.File
import java.util.concurrent.atomic.AtomicReference

class LegendPhotoEditorTest {
    @get:Rule val compose = createAndroidComposeRule<ComponentActivity>()

    @Test fun formatMenuIsCompactAndSelectsTheRequestedFormat() {
        val chosen = AtomicReference<com.mylegnd.legend.registered.core.model.LegendSocialContentType?>()
        compose.setContent { LegendTheme { LegendSocialCreationModeMenu({}, chosen::set) } }
        assertTrue(compose.onNodeWithTag("Legend creation formats").fetchSemanticsNode().boundsInRoot.height <= 440 * compose.activity.resources.displayMetrics.density)
        compose.onNodeWithText("Hac").performClick()
        assertEquals(com.mylegnd.legend.registered.core.model.LegendSocialContentType.HAC, chosen.get())
    }

    @Test fun rotationFillsTheExportAndZoomOutRevealsCanvas() {
        val source = Bitmap.createBitmap(40, 80, Bitmap.Config.ARGB_8888).apply { eraseColor(Color.RED) }
        val output = Bitmap.createBitmap(100, 100, Bitmap.Config.ARGB_8888)
        drawLegendPhoto(Canvas(output), 100f, 100f, source, LegendPhotoTransform(rotation = 90f))
        assertEquals(Color.RED, output.getPixel(1, 1))
        drawLegendPhoto(Canvas(output), 100f, 100f, source, LegendPhotoTransform(zoom = .25f))
        assertEquals(Color.BLACK, output.getPixel(1, 1))
        assertEquals(Color.RED, output.getPixel(50, 50))
        source.recycle(); output.recycle()
    }

    @Test fun pinchZoomOutIsAppliedToTheActualSavedPhoto() {
        val source = File.createTempFile("editor-test-", ".png", compose.activity.cacheDir)
        Bitmap.createBitmap(300, 300, Bitmap.Config.ARGB_8888).apply {
            eraseColor(Color.RED)
            source.outputStream().use { compress(Bitmap.CompressFormat.PNG, 100, it) }
            recycle()
        }
        val saved = AtomicReference<Uri?>()
        compose.setContent { LegendTheme { LegendSocialPhotoEditor(Uri.fromFile(source), listOf(1.0, .8), {}, saved::set) } }
        compose.waitUntil(10000) { runCatching { compose.onNodeWithText("Next").assertIsEnabled() }.isSuccess }
        compose.onNodeWithContentDescription("Photo crop preview").performTouchInput {
            pinch(start0 = Offset(center.x - width * .3f, center.y), end0 = Offset(center.x - width * .1f, center.y),
                start1 = Offset(center.x + width * .3f, center.y), end1 = Offset(center.x + width * .1f, center.y), durationMillis = 400)
        }
        compose.waitForIdle()
        compose.onNodeWithText("Next").performClick()
        compose.waitUntil(10000) { saved.get() != null }
        val output = File(requireNotNull(saved.get()?.path))
        val bitmap = BitmapFactory.decodeFile(output.path)
        assertEquals(Color.BLACK, bitmap.getPixel(2, 2))
        assertTrue(Color.red(bitmap.getPixel(bitmap.width / 2, bitmap.height / 2)) > 200)
        bitmap.recycle(); source.delete(); output.delete()
    }
    @Test fun movVideoIsPreparedAsPlayableH264Mp4() = kotlinx.coroutines.runBlocking {
        val context = androidx.test.platform.app.InstrumentationRegistry.getInstrumentation().targetContext
        val testContext = androidx.test.platform.app.InstrumentationRegistry.getInstrumentation().context
        val source = File.createTempFile("video-input-", ".mov", context.cacheDir)
        testContext.assets.open("creator-test.mov").use { input -> source.outputStream().use { input.copyTo(it) } }
        val output = com.mylegnd.legend.registered.core.media.SocialVideoPreparation.prepare(context, Uri.fromFile(source), com.mylegnd.legend.registered.core.model.SocialVideoEdit(startSeconds = .25, endSeconds = 1.25))
        try {
            assertTrue(output.length() > 0)
            assertEquals("mp4", output.extension)
            android.media.MediaMetadataRetriever().use {
                it.setDataSource(output.path)
                val duration = requireNotNull(it.extractMetadata(android.media.MediaMetadataRetriever.METADATA_KEY_DURATION)).toLong()
                assertTrue("The exported file must contain the selected one-second trim", duration in 900..1100)
            }
            val extractor = android.media.MediaExtractor()
            try {
                extractor.setDataSource(output.path)
                val video = (0 until extractor.trackCount).map(extractor::getTrackFormat).first { it.getString(android.media.MediaFormat.KEY_MIME)?.startsWith("video/") == true }
                assertEquals("video/avc", video.getString(android.media.MediaFormat.KEY_MIME))
            } finally { extractor.release() }
        } finally { source.delete(); output.delete() }
    }

}
