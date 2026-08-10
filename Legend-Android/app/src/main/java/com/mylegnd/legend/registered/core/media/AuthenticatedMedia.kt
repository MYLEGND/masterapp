package com.mylegnd.legend.registered.core.media

import android.content.Context
import android.net.Uri
import androidx.compose.runtime.*
import androidx.compose.ui.Modifier
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.ui.unit.dp
import androidx.compose.ui.viewinterop.AndroidView
import androidx.media3.common.MediaItem
import androidx.media3.exoplayer.ExoPlayer
import androidx.media3.ui.PlayerView
import com.mylegnd.legend.registered.core.network.LegendApiClient
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import okhttp3.Request
import java.io.File
import coil3.compose.AsyncImage

/** Protected social assets are cached as an authenticated performance layer, never made public for Android. */
class AuthenticatedMediaRepository(private val context: Context, private val client: LegendApiClient) {
    suspend fun socialMediaFile(assetId: String, participantType: String): File = withContext(Dispatchers.IO) {
        val target = File(context.cacheDir, "legend-social-$assetId")
        if (target.exists() && target.length() > 0) return@withContext target
        val request = Request.Builder().url("${client.baseUrl}/api/v1/mobile/social/media/$assetId").header("X-Legend-Participant-Type", participantType).build()
        client.httpClient.newCall(request).execute().use { response -> check(response.isSuccessful) { "Protected media is unavailable." }; response.body.byteStream().use { input -> target.outputStream().use { input.copyTo(it) } } }
        target
    }
}
@Composable fun LegendVideoPlayer(file: File, modifier: Modifier = Modifier) {
    val context = androidx.compose.ui.platform.LocalContext.current
    val player = remember(file) { ExoPlayer.Builder(context).build().apply { setMediaItem(MediaItem.fromUri(Uri.fromFile(file))); prepare() } }
    DisposableEffect(player) { onDispose { player.release() } }
    AndroidView(factory = { PlayerView(it).apply { this.player = player } }, modifier = modifier)
}

@Composable
fun LegendProtectedSocialMedia(
    assetId: String,
    mediaKind: String,
    participantType: String,
    repository: AuthenticatedMediaRepository,
    contentDescription: String?,
    modifier: Modifier = Modifier,
) {
    var file by remember(assetId) { mutableStateOf<File?>(null) }
    var unavailable by remember(assetId) { mutableStateOf(false) }
    LaunchedEffect(assetId, participantType) {
        runCatching { repository.socialMediaFile(assetId, participantType) }
            .onSuccess { file = it }
            .onFailure { unavailable = true }
    }
    when {
        file != null && mediaKind.equals("video", ignoreCase = true) ->
            LegendVideoPlayer(file!!, modifier.fillMaxWidth().height(220.dp))
        file != null -> AsyncImage(
            model = file,
            contentDescription = contentDescription,
            modifier = modifier.fillMaxWidth(),
        )
        unavailable -> Unit
        else -> androidx.compose.material3.LinearProgressIndicator(modifier = modifier.fillMaxWidth())
    }
}
