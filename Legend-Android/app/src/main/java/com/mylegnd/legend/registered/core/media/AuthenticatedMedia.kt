package com.mylegnd.legend.registered.core.media

import android.content.Context
import android.net.Uri
import androidx.compose.runtime.*
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.border
import androidx.compose.ui.draw.clip
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.size
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.Dp
import androidx.compose.ui.layout.ContentScale
import androidx.compose.ui.viewinterop.AndroidView
import androidx.compose.material3.LinearProgressIndicator
import androidx.media3.common.MediaItem
import androidx.media3.common.Player
import androidx.media3.exoplayer.ExoPlayer
import androidx.media3.ui.PlayerView
import com.mylegnd.legend.registered.core.network.LegendApiClient
import com.mylegnd.legend.registered.core.model.MobileAvatar
import com.mylegnd.legend.registered.ui.LegendAvatar
import com.mylegnd.legend.registered.core.design.LegendColors
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import okhttp3.Request
import java.io.File
import java.util.concurrent.ConcurrentHashMap
import coil3.compose.AsyncImage

/** Protected social assets are cached as an authenticated performance layer, never made public for Android. */
class AuthenticatedMediaRepository(private val context: Context, private val client: LegendApiClient) {
    /**
     * Profile avatar routes are stable after an account owner replaces an image.
     * Revalidating once per process prevents the former path-only disk cache
     * from showing a different, stale identity than iOS after an avatar update.
     * Immutable social assets remain disk cached by their server-issued asset ID.
     */
    private val validatedAvatarPaths = ConcurrentHashMap.newKeySet<String>()
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
        if (target.exists() && target.length() > 0 && avatar.resourcePath in validatedAvatarPaths) {
            return@withContext target
        }
        val url = avatar.resourcePath.takeIf { it.startsWith("http://") || it.startsWith("https://") }
            ?: "${client.baseUrl}/${avatar.resourcePath.trimStart('/')}"
        val request = Request.Builder().url(url).header("X-Legend-Participant-Type", participantType).build()
        val fetched = runCatching {
            val temporary = File(target.parentFile, "${target.name}.refresh")
            client.httpClient.newCall(request).execute().use { response ->
                check(response.isSuccessful) { "Protected avatar is unavailable." }
                response.body.byteStream().use { input -> temporary.outputStream().use { input.copyTo(it) } }
            }
            check(temporary.length() > 0) { "Protected avatar is unavailable." }
            temporary.copyTo(target, overwrite = true)
            temporary.delete()
        }
        if (fetched.isFailure && (!target.exists() || target.length() == 0L)) {
            throw fetched.exceptionOrNull() ?: IllegalStateException("Protected avatar is unavailable.")
        }
        validatedAvatarPaths += avatar.resourcePath
        target
    }
}
/**
 * The one Media3 renderer for protected LEGEND video. Feed-specific composition
 * controls its chrome and playback intent through parameters; it never creates
 * another media pipeline or exposes a public media URL.
 */
@Composable fun LegendVideoPlayer(
    file: File,
    modifier: Modifier = Modifier,
    autoPlay: Boolean = false,
    showControls: Boolean = true,
    loop: Boolean = false,
) {
    val context = LocalContext.current
    val player = remember(file) {
        ExoPlayer.Builder(context).build().apply {
            setMediaItem(MediaItem.fromUri(Uri.fromFile(file)))
            repeatMode = if (loop) Player.REPEAT_MODE_ONE else Player.REPEAT_MODE_OFF
            prepare()
        }
    }
    LaunchedEffect(autoPlay) { player.playWhenReady = autoPlay }
    DisposableEffect(player) { onDispose { player.release() } }
    AndroidView(
        factory = {
            PlayerView(it).apply {
                this.player = player
                useController = showControls
            }
        },
        update = {
            it.player = player
            it.useController = showControls
        },
        modifier = modifier,
    )
}

/** Native, temporary creator preview. The URI stays on-device until the user publishes through the existing social API. */
@Composable fun LegendLocalVideoPreview(uri: Uri, modifier: Modifier = Modifier) {
    val context = LocalContext.current
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
    contentScale: ContentScale = ContentScale.Fit,
    videoHeight: Dp? = 220.dp,
    autoPlayVideo: Boolean = false,
    showVideoControls: Boolean = true,
    loopVideo: Boolean = false,
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
            LegendVideoPlayer(
                file!!,
                modifier.fillMaxWidth().then(
                    videoHeight?.let { Modifier.height(it) } ?: Modifier,
                ),
                autoPlay = autoPlayVideo,
                showControls = showVideoControls,
                loop = loopVideo,
            )
        file != null -> AsyncImage(
            model = file,
            contentDescription = contentDescription,
            contentScale = contentScale,
            modifier = modifier.fillMaxWidth(),
        )
        unavailable -> Unit
        else -> LinearProgressIndicator(modifier = modifier.fillMaxWidth())
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
    size: Dp = 40.dp,
) {
    var file by remember(avatar?.resourcePath) { mutableStateOf<File?>(null) }
    LaunchedEffect(avatar?.resourcePath, participantType) {
        file = null
        if (avatar != null) {
            runCatching { repository.profileAvatarFile(avatar, participantType) }
                .onSuccess { file = it }
        }
    }
    if (file == null) {
        LegendAvatar(displayName, modifier = modifier, size = size)
    } else {
        AsyncImage(
            model = file,
            contentDescription = "$displayName profile image",
            modifier = modifier
                .size(size)
                .clip(CircleShape)
                .border(1.dp, LegendColors.Gold.copy(alpha = 0.7f), CircleShape),
            contentScale = ContentScale.Crop,
        )
    }
}
