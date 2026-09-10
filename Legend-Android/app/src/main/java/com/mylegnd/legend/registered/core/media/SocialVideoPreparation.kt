package com.mylegnd.legend.registered.core.media

import android.content.Context
import android.media.MediaMetadataRetriever
import android.net.Uri
import com.mylegnd.legend.registered.core.model.SocialVideoEdit
import androidx.media3.common.MediaItem
import androidx.media3.common.MimeTypes
import androidx.media3.transformer.*
import androidx.media3.effect.Presentation
import com.mylegnd.legend.registered.core.design.LegendSocialFormats
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.suspendCancellableCoroutine
import kotlinx.coroutines.withContext
import java.io.File
import kotlin.coroutines.resume
import kotlin.coroutines.resumeWithException

internal class SocialMediaPreparationException(message: String, cause: Throwable? = null) : Exception(message, cause)

/** Produces the same H.264/AAC MP4 delivery format required of iOS before upload. */
@androidx.annotation.OptIn(androidx.media3.common.util.UnstableApi::class)
internal object SocialVideoPreparation {
    suspend fun prepare(context: Context, uri: Uri, edit: SocialVideoEdit = SocialVideoEdit()): File {
        withContext(Dispatchers.IO) {
            val metadata = MediaMetadataRetriever()
            try {
                metadata.setDataSource(context, uri)
                val duration = metadata.extractMetadata(MediaMetadataRetriever.METADATA_KEY_DURATION)?.toDoubleOrNull()
                    ?: throw SocialMediaPreparationException("This video's duration could not be read. Select it again.")
                val limit = requireNotNull(LegendSocialFormats.named("hac").maximumVideoDurationSeconds)
                val end = edit.endSeconds ?: (duration / 1000)
                if (!edit.startSeconds.isFinite() || !end.isFinite() || edit.startSeconds < 0 || end > duration / 1000 + 0.05 || end <= edit.startSeconds || end - edit.startSeconds > limit)
                    throw SocialMediaPreparationException("Select a video range of ${limit.toInt()} seconds or less.")
            } finally { metadata.release() }
        }
        val output = File.createTempFile("legend-video-", ".mp4", context.cacheDir)
        output.delete()
        try {
            return withContext(Dispatchers.Main.immediate) {
                suspendCancellableCoroutine { continuation ->
                    val encoder = DefaultEncoderFactory.Builder(context)
                        .setRequestedVideoEncoderSettings(VideoEncoderSettings.Builder().setBitrate(1_000_000).build())
                        .setRequestedAudioEncoderSettings(AudioEncoderSettings.Builder().setBitrate(128_000).build()).build()
                    val transformer = Transformer.Builder(context)
                        .setVideoMimeType(MimeTypes.VIDEO_H264).setAudioMimeType(MimeTypes.AUDIO_AAC)
                        .setEncoderFactory(object : Codec.EncoderFactory by encoder {
                            override fun videoNeedsEncoding() = true
                            override fun audioNeedsEncoding() = true
                        })
                        .addListener(object : Transformer.Listener {
                            override fun onCompleted(composition: Composition, result: ExportResult) {
                                if (continuation.isActive) continuation.resume(output)
                            }
                            override fun onError(composition: Composition, result: ExportResult, exception: ExportException) {
                                if (continuation.isActive) continuation.resumeWithException(SocialMediaPreparationException("The video could not be prepared. Your original is unchanged; try selecting it again.", exception))
                            }
                        }).build()
                    continuation.invokeOnCancellation { android.os.Handler(android.os.Looper.getMainLooper()).post { transformer.cancel(); output.delete() } }
                    val clip = MediaItem.ClippingConfiguration.Builder().setStartPositionMs((edit.startSeconds * 1000).toLong())
                    edit.endSeconds?.let { clip.setEndPositionMs((it * 1000).toLong()) }
                    val item = MediaItem.Builder().setUri(uri).setClippingConfiguration(clip.build()).build()
                    val edited = EditedMediaItem.Builder(item).setRemoveAudio(edit.muted)
                        .setEffects(Effects(emptyList(), listOf(Presentation.createForHeight(720)))).build()
                    val composition = Composition.Builder(EditedMediaItemSequence.Builder(edited).build())
                        .setHdrMode(Composition.HDR_MODE_TONE_MAP_HDR_TO_SDR_USING_OPEN_GL).build()
                    transformer.start(composition, output.absolutePath)
                }
            }
        } catch (error: Throwable) { output.delete(); throw error }
    }
}
