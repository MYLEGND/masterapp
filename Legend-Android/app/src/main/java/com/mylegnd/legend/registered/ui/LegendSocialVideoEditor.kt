package com.mylegnd.legend.registered.ui

import android.media.MediaMetadataRetriever
import android.net.Uri
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.*
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.unit.dp
import androidx.compose.ui.viewinterop.AndroidView
import androidx.media3.common.MediaItem
import androidx.media3.exoplayer.ExoPlayer
import androidx.media3.ui.PlayerView
import com.mylegnd.legend.registered.core.design.*
import com.mylegnd.legend.registered.core.model.SocialVideoEdit
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext

@Composable
internal fun LegendSocialVideoEditor(uri: Uri, initial: SocialVideoEdit, back: () -> Unit, done: (SocialVideoEdit) -> Unit) {
    val context = LocalContext.current
    var duration by remember(uri) { mutableStateOf(0f) }
    var range by remember(uri) { mutableStateOf(0f..1f) }
    var muted by remember(uri) { mutableStateOf(initial.muted) }
    var failure by remember(uri) { mutableStateOf<String?>(null) }
    val maximum = requireNotNull(LegendSocialFormats.named("hac").maximumVideoDurationSeconds).toFloat()
    val player = remember(uri) { ExoPlayer.Builder(context).build() }
    DisposableEffect(player) { onDispose { player.release() } }
    LaunchedEffect(uri) {
        try {
            duration = withContext(Dispatchers.IO) {
                val metadata = MediaMetadataRetriever()
                try {
                    metadata.setDataSource(context, uri)
                    (metadata.extractMetadata(MediaMetadataRetriever.METADATA_KEY_DURATION)?.toFloatOrNull() ?: error("Duration unavailable")) / 1000
                } finally { metadata.release() }
            }
            check(duration > 0)
            range = initial.startSeconds.toFloat()..(initial.endSeconds?.toFloat() ?: minOf(duration, maximum))
        } catch (cancelled: kotlinx.coroutines.CancellationException) { throw cancelled }
        catch (_: Exception) { failure = "This video could not be opened. Select it again." }
    }
    fun preview() {
        val clip = MediaItem.ClippingConfiguration.Builder().setStartPositionMs((range.start * 1000).toLong()).setEndPositionMs((range.endInclusive * 1000).toLong()).build()
        player.setMediaItem(MediaItem.Builder().setUri(uri).setClippingConfiguration(clip).build())
        player.prepare()
    }
    LaunchedEffect(duration) { if (duration > 0) preview() }
    LaunchedEffect(muted) { player.volume = if (muted) 0f else 1f }
    Column(Modifier.fillMaxWidth().fillMaxHeight(.94f).background(LegendColors.Midnight).padding(16.dp), verticalArrangement = Arrangement.spacedBy(12.dp)) {
        Row {
            TextButton(onClick = back) { Text(legendLocalized("Back"), color = LegendColors.OnNavy) }
            Text(legendLocalized("Edit video"), Modifier.weight(1f), style = LegendTypography.Title, color = LegendColors.OnNavy)
            TextButton(enabled = duration > 0 && range.endInclusive > range.start && range.endInclusive - range.start <= maximum, onClick = { done(SocialVideoEdit(range.start.toDouble(), range.endInclusive.toDouble(), muted)) }) { Text(legendLocalized("Next"), color = LegendColors.GoldBright) }
        }
        AndroidView(factory = { PlayerView(it).apply { this.player = player; useController = true } }, modifier = Modifier.fillMaxWidth().weight(1f))
        if (duration > 0) {
            Text(legendLocalized("Trim"), color = LegendColors.GoldBright)
            Text("${range.start.toInt()}s – ${range.endInclusive.toInt()}s · ${duration.toInt()}s", color = LegendColors.OnNavy)
            RangeSlider(value = range, onValueChange = { range = it }, valueRange = 0f..duration, onValueChangeFinished = { if (range.endInclusive > range.start) preview() })
            if (range.endInclusive - range.start > maximum) Text(legendLocalized("Select up to 600 seconds (10 minutes)."), color = LegendColors.GoldBright)
        }
        Row { Text(legendLocalized("Mute original audio"), Modifier.weight(1f), color = LegendColors.OnNavy); Switch(checked = muted, onCheckedChange = { muted = it }) }
        TextButton(onClick = { range = 0f..minOf(duration, maximum); muted = false; if (duration > 0) preview() }) { Text(legendLocalized("Reset"), color = LegendColors.GoldBright) }
        failure?.let { Text(legendLocalized(it), color = LegendColors.GoldBright) }
    }
}
