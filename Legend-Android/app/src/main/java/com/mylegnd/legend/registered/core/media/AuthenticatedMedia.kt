package com.mylegnd.legend.registered.core.media

import android.content.Context
import android.net.Uri
import androidx.compose.runtime.*
import androidx.compose.ui.Modifier
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.ui.draw.clip
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.size
import androidx.compose.ui.unit.dp
import androidx.compose.ui.viewinterop.AndroidView
import androidx.media3.common.MediaItem
import androidx.media3.exoplayer.ExoPlayer
import androidx.media3.ui.PlayerView
import com.mylegnd.legend.registered.core.network.LegendApiClient
import com.mylegnd.legend.registered.core.model.MobileAvatar
import com.mylegnd.legend.registered.ui.LegendAvatar
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

    /** Profile images use the actor-scoped resource path issued by the server. */
    suspend fun profileAvatarFile(avatar: MobileAvatar, participantType: String): File = withContext(Dispatchers.IO) {
        val target = File(context.cacheDir, "legend-avatar-${avatar.resourcePath.hashCode().toUInt().toString(16)}")
        if (target.exists() && target.length() > 0) return@withContext target
        val url = avatar.resourcePath.takeIf { it.startsWith("http://") || it.startsWith("https://") }
            ?: "${client.baseUrl}/${avatar.resourcePath.trimStart('/')}"
        val request = Request.Builder().url(url).header("X-Legend-Participant-Type", participantType).build()
        client.httpClient.newCall(request).execute().use { response ->
            check(response.isSuccessful) { "Protected avatar is unavailable." }
            response.body.byteStream().use { input -> target.outputStream().use { input.copyTo(it) } }
        }
        target
    }
}
@Composable fun LegendVideoPlayer(file: File, modifier: Modifier = Modifier) {
    val context = androidx.compose.ui.platform.LocalContext.current
    val player = remember(file) { ExoPlayer.Builder(context).build().apply { setMediaItem(MediaItem.fromUri(Uri.fromFile(file))); prepare() } }
    DisposableEffect(player) { onDispose { player.release() } }
    AndroidView(factory = { PlayerView(it).apply { this.player = player } }, modifier = modifier)
}

/** Native, temporary creator preview. The URI stays on-device until the user publishes through the existing social API. */
@Composable fun LegendLocalVideoPreview(uri: Uri, modifier: Modifier = Modifier) {
    val context = androidx.compose.ui.platform.LocalContext.current
    val player = remember(uri) { ExoPlayer.Builder(context).build().apply { setMediaItem(MediaItem.fromUri(uri)); prepare() } }
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

/**
 * Draws the same server-authorized profile image projection used by iOS. The
 * initials fallback is only a loading/error presentation; it never converts a
 * protected image into a public URL.
 */
@Composable
fun LegendProtectedAvatar(
    avatar: MobileAvatar?,
    displayName: String,
    participantType: String,
    repository: AuthenticatedMediaRepository,
    modifier: Modifier = Modifier,
    size: androidx.compose.ui.unit.Dp = 40.dp,
) {
    var file by remember(avatar?.resourcePath) { mutableStateOf<File?>(null) }
    LaunchedEffect(avatar?.resourcePath, participantType) {
        if (avatar != null) runCatching { repository.profileAvatarFile(avatar, participantType) }.onSuccess { file = it }
    }
    if (file == null) {
        LegendAvatar(displayName, modifier = modifier, size = size)
    } else {
        AsyncImage(
            model = file,
            contentDescription = "$displayName profile image",
            modifier = modifier.size(size).clip(CircleShape),
        )
    }
}
