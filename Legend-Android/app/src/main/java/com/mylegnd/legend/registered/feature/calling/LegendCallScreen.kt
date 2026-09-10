package com.mylegnd.legend.registered.feature.calling

import android.Manifest
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.*
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Brush
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.unit.dp
import androidx.compose.ui.viewinterop.AndroidView
import androidx.compose.ui.window.Dialog
import androidx.compose.ui.window.DialogProperties
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import com.mylegnd.legend.registered.core.design.*
import com.mylegnd.legend.registered.ui.legendPressClickable
import org.webrtc.SurfaceViewRenderer
import org.webrtc.VideoTrack

val LocalLegendCalling = staticCompositionLocalOf<LegendCallViewModel?> { null }

@Composable
fun LegendCallOverlay(store: LegendCallViewModel) {
    val state by store.state.collectAsStateWithLifecycle()
    val context = androidx.compose.ui.platform.LocalContext.current
    val scope = rememberCoroutineScope()
    var snapshotRequest by remember { mutableIntStateOf(0) }
    var snapshotError by remember { mutableStateOf<String?>(null) }
    val screenPermission = rememberLauncherForActivityResult(ActivityResultContracts.StartActivityForResult()) { result ->
        if (result.resultCode == android.app.Activity.RESULT_OK) result.data?.let(store::startScreenSharing)
    }
    val answerPermissions = rememberLauncherForActivityResult(ActivityResultContracts.RequestMultiplePermissions()) { grants ->
        if (grants.values.all { it }) store.answer() else store.end()
    }
    fun answer() { answerPermissions.launch(if (state.call?.video == true) arrayOf(Manifest.permission.RECORD_AUDIO, Manifest.permission.CAMERA) else arrayOf(Manifest.permission.RECORD_AUDIO)) }
    LaunchedEffect(state.systemAnswerRequested) { if (state.systemAnswerRequested) answer() }
    if (state.call == null && !state.starting && state.failure == null) return
    Dialog(onDismissRequest = {}, properties = DialogProperties(usePlatformDefaultWidth = false, dismissOnBackPress = false, dismissOnClickOutside = false)) {
        Box(Modifier.fillMaxSize().background(Brush.linearGradient(listOf(LegendColors.Navy, LegendColors.Midnight)))) {
            state.remoteVideo?.let { video -> store.peer?.let { engine -> LegendVideoSurface(video, engine, Modifier.fillMaxSize(), snapshotRequest) { bitmap ->
                scope.launch {
                    runCatching {
                        val uri = withContext(Dispatchers.IO) {
                            val folder = java.io.File(context.cacheDir, "call-snapshots").apply { mkdirs() }
                            folder.listFiles()?.filter { System.currentTimeMillis() - it.lastModified() > 86_400_000 }?.forEach { it.delete() }
                            val file = java.io.File(folder, "Legend-${java.util.UUID.randomUUID()}.png")
                            file.outputStream().use { bitmap.compress(android.graphics.Bitmap.CompressFormat.PNG, 100, it) }
                            androidx.core.content.FileProvider.getUriForFile(context, "${context.packageName}.call-snapshots", file)
                        }
                        context.startActivity(android.content.Intent.createChooser(android.content.Intent(android.content.Intent.ACTION_SEND).apply {
                            type = "image/png"; putExtra(android.content.Intent.EXTRA_STREAM, uri)
                            addFlags(android.content.Intent.FLAG_GRANT_READ_URI_PERMISSION)
                        }, null))
                    }.onFailure { snapshotError = "The snapshot could not be shared. Please try again." }
                }
            } } }
            Column(Modifier.fillMaxSize().safeDrawingPadding().padding(24.dp), horizontalAlignment = Alignment.CenterHorizontally, verticalArrangement = Arrangement.spacedBy(24.dp)) {
                Text(legendLocalized("LEGEND®"), style = LegendTypography.Wordmark, color = Color.White)
                Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.End) {
                    state.localVideo?.let { video -> store.peer?.let { engine -> LegendVideoSurface(video, engine, Modifier.size(105.dp, 145.dp).clip(RoundedCornerShape(22.dp))) } }
                }
                Spacer(Modifier.weight(1f))
                if (state.failure != null) {
                    Text(legendLocalized(state.failure!!), color = Color.White)
                    Button(onClick = { store.end(); store.dismissFailure() }) { Text(legendLocalized("Close")) }
                } else {
                    Text(state.name, style = LegendTypography.Section, color = Color.White)
                    Text(callStatusLabel(state.status), color = LegendColors.Gold)
                    if (state.status == "Calling") Text(legendLocalized("Waiting for the recipient’s device to confirm receipt"), color = Color.White)
                    if (state.status == "Ringing") Text(legendLocalized("The recipient’s device received your call"), color = Color.White)
                    if (state.starting) {
                        CircularProgressIndicator(color = LegendColors.Gold)
                        LegendCallControl("Cancel", Icons.Default.CallEnd, LegendColors.Error) { store.end() }
                    } else if (state.incoming) {
                        Row(horizontalArrangement = Arrangement.spacedBy(30.dp)) {
                        LegendCallControl("Decline", Icons.Default.CallEnd, LegendColors.Error) { store.end() }
                            LegendCallControl("Answer", Icons.Default.Phone, LegendColors.Gold) { answer() }
                        }
                    } else {
                        Row(horizontalArrangement = Arrangement.spacedBy(16.dp)) {
                            LegendCallControl(if (state.muted) "Unmute" else "Mute", if (state.muted) Icons.Default.MicOff else Icons.Default.Mic, selected = state.muted, action = store::toggleMute)
                            LegendCallControl("Speaker", Icons.Default.VolumeUp, selected = state.speaker, action = store::toggleSpeaker)
                            if (state.call?.video == true && !state.sharingScreen) {
                                LegendCallControl("Camera", if (state.camera) Icons.Default.Videocam else Icons.Default.VideocamOff, action = store::toggleCamera)
                                LegendCallControl("Flip", Icons.Default.Cameraswitch, action = store::flipCamera)
                            }
                        }
                        if (state.remoteVideo != null && state.status == "Connected") {
                            TextButton(onClick = { snapshotRequest++ }) {
                                Icon(Icons.Default.PhotoCamera, null, tint = LegendColors.GoldBright)
                                Spacer(Modifier.width(8.dp)); Text(legendLocalized("Take snapshot"), color = Color.White)
                            }
                        }
                        if (state.call?.video == true && state.status == "Connected") {
                            OutlinedButton(onClick = {
                                if (state.sharingScreen) store.stopScreenSharing()
                                else screenPermission.launch(context.getSystemService(android.media.projection.MediaProjectionManager::class.java).createScreenCaptureIntent())
                            }) {
                                Icon(Icons.Default.ScreenShare, null, tint = LegendColors.GoldBright)
                                Spacer(Modifier.width(8.dp))
                                Text(if (state.sharingScreen) legendLocalized("Stop sharing") else legendLocalized("Share screen"), color = Color.White)
                            }
                        }
                        LegendCallControl("End call", Icons.Default.CallEnd, LegendColors.Error) { store.end() }
                    }
                }
                Spacer(Modifier.height(28.dp))
            }
        }
    }
    snapshotError?.let { message ->
        AlertDialog(onDismissRequest = { snapshotError = null }, text = { Text(legendLocalized(message)) }, confirmButton = { TextButton(onClick = { snapshotError = null }) { Text(legendLocalized("OK")) } })
    }
    state.controlError?.let { message ->
        AlertDialog(onDismissRequest = store::dismissControlError, title = { Text(legendLocalized("Call controls")) }, text = { Text(legendLocalized(message)) }, confirmButton = { TextButton(onClick = store::dismissControlError) { Text(legendLocalized("OK")) } })
    }
}
@Composable
private fun callStatusLabel(value: String): String = when (value) {
    "Calling" -> legendLocalized("Calling")
    "Incoming call" -> legendLocalized("Incoming call")
    "Connecting" -> legendLocalized("Connecting")
    "Connected" -> legendLocalized("Connected")
    "Reconnecting" -> legendLocalized("Reconnecting")
    "Audio interrupted" -> legendLocalized("Audio interrupted")
    else -> legendLocalized(value)
}
@Composable
private fun LegendVideoSurface(track: VideoTrack, peer: LegendRTCPeer, modifier: Modifier, snapshotRequest: Int = 0, snapshot: (android.graphics.Bitmap) -> Unit = {}) {
    var renderer by remember(track) { mutableStateOf<SurfaceViewRenderer?>(null) }
    val onSnapshot by rememberUpdatedState(snapshot)
    DisposableEffect(renderer, snapshotRequest) {
        val view = renderer
        val consumed = java.util.concurrent.atomic.AtomicBoolean(false)
        val disposed = java.util.concurrent.atomic.AtomicBoolean(false)
        lateinit var listener: org.webrtc.EglRenderer.FrameListener
        listener = org.webrtc.EglRenderer.FrameListener { bitmap ->
            if (consumed.compareAndSet(false, true)) view?.post { view.removeFrameListener(listener); if (!disposed.get()) onSnapshot(bitmap) }
        }
        if (snapshotRequest > 0) view?.addFrameListener(listener, 1f)
        onDispose { disposed.set(true); consumed.set(true); if (snapshotRequest > 0) view?.removeFrameListener(listener) }
    }
    key(track) {
        AndroidView(modifier = modifier, factory = { context -> SurfaceViewRenderer(context).apply {
            init(peer.egl.eglBaseContext, null); setEnableHardwareScaler(true); track.addSink(this); renderer = this
        } }, onRelease = { view -> track.removeSink(view); view.release(); renderer = null }, update = {})
    }
}

@Composable
private fun LegendCallControl(title: String, icon: androidx.compose.ui.graphics.vector.ImageVector,
    color: Color = Color.White.copy(alpha = LegendDesignAuthority.opacity("callControlSurface")),
    selected: Boolean = false, action: () -> Unit) {
    Column(Modifier.legendPressClickable(action), horizontalAlignment = Alignment.CenterHorizontally,
        verticalArrangement = Arrangement.spacedBy(LegendSpacing.Xs)) {
        Box(Modifier.size(LegendDesignAuthority.size("callControl")).background(if (selected) LegendColors.Gold else color, CircleShape), contentAlignment = Alignment.Center) {
            Icon(icon, null, tint = Color.White)
        }
        Text(legendLocalized(title), style = LegendTypography.Caption, color = Color.White)
    }
}
