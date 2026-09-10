package com.mylegnd.legend.registered.feature.calling

import android.Manifest
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
import org.webrtc.SurfaceViewRenderer
import org.webrtc.VideoTrack

val LocalLegendCalling = staticCompositionLocalOf<LegendCallViewModel?> { null }

@Composable
fun LegendCallOverlay(store: LegendCallViewModel) {
    val state by store.state.collectAsStateWithLifecycle()
    val answerPermissions = rememberLauncherForActivityResult(ActivityResultContracts.RequestMultiplePermissions()) { grants ->
        if (grants.values.all { it }) store.answer() else store.end()
    }
    fun answer() { answerPermissions.launch(if (state.call?.video == true) arrayOf(Manifest.permission.RECORD_AUDIO, Manifest.permission.CAMERA) else arrayOf(Manifest.permission.RECORD_AUDIO)) }
    LaunchedEffect(state.systemAnswerRequested) { if (state.systemAnswerRequested) answer() }
    if (state.call == null && state.failure == null) return
    Dialog(onDismissRequest = {}, properties = DialogProperties(usePlatformDefaultWidth = false, dismissOnBackPress = false, dismissOnClickOutside = false)) {
        Box(Modifier.fillMaxSize().background(Brush.linearGradient(listOf(LegendColors.Navy, LegendColors.Midnight)))) {
            state.remoteVideo?.let { video -> store.peer?.let { engine -> LegendVideoSurface(video, engine, Modifier.fillMaxSize()) } }
            Column(Modifier.fillMaxSize().safeDrawingPadding().padding(24.dp), horizontalAlignment = Alignment.CenterHorizontally, verticalArrangement = Arrangement.spacedBy(24.dp)) {
                Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.SpaceBetween) {
                    Text(legendLocalized("LEGEND®"), style = LegendTypography.Section, color = LegendColors.Gold)
                    state.localVideo?.let { video -> store.peer?.let { engine -> LegendVideoSurface(video, engine, Modifier.size(105.dp, 145.dp).clip(RoundedCornerShape(22.dp))) } }
                }
                Spacer(Modifier.weight(1f))
                if (state.failure != null) {
                    Text(legendLocalized(state.failure!!), color = Color.White)
                    Button(onClick = { store.end(); store.dismissFailure() }) { Text(legendLocalized("Close")) }
                } else {
                    Text(state.name, style = LegendTypography.Section, color = Color.White)
                    Text(callStatusLabel(state.status), color = LegendColors.Gold)
                    if (state.incoming) {
                        Row(horizontalArrangement = Arrangement.spacedBy(30.dp)) {
                            Button(onClick = { store.end() }, colors = ButtonDefaults.buttonColors(containerColor = LegendColors.Error)) { Text(legendLocalized("Decline")) }
                            Button(onClick = { answer() }, colors = ButtonDefaults.buttonColors(containerColor = LegendColors.Gold, contentColor = LegendColors.Midnight)) { Text(legendLocalized("Answer")) }
                        }
                    } else {
                        Row(horizontalArrangement = Arrangement.spacedBy(16.dp)) {
                            IconButton(onClick = store::toggleMute, modifier = Modifier.background(Color.White.copy(alpha = .15f), CircleShape)) { Icon(if (state.muted) Icons.Default.MicOff else Icons.Default.Mic, legendLocalized("Mute"), tint = Color.White) }
                            IconButton(onClick = store::toggleSpeaker, modifier = Modifier.background(Color.White.copy(alpha = .15f), CircleShape)) { Icon(Icons.Default.VolumeUp, legendLocalized("Speaker"), tint = Color.White) }
                            if (state.call?.video == true) {
                                IconButton(onClick = store::toggleCamera) { Icon(if (state.camera) Icons.Default.Videocam else Icons.Default.VideocamOff, legendLocalized("Camera"), tint = Color.White) }
                                IconButton(onClick = store::flipCamera) { Icon(Icons.Default.Cameraswitch, legendLocalized("Flip"), tint = Color.White) }
                            }
                        }
                        Button(onClick = { store.end() }, colors = ButtonDefaults.buttonColors(containerColor = LegendColors.Error)) { Icon(Icons.Default.CallEnd, null); Spacer(Modifier.width(8.dp)); Text(legendLocalized("End call")) }
                    }
                }
                Spacer(Modifier.height(28.dp))
            }
        }
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
private fun LegendVideoSurface(track: VideoTrack, peer: LegendRTCPeer, modifier: Modifier) {
    key(track) {
        AndroidView(modifier = modifier, factory = { context -> SurfaceViewRenderer(context).apply { init(peer.egl.eglBaseContext, null); setEnableHardwareScaler(true); track.addSink(this) } }, onRelease = { view -> track.removeSink(view); view.release() }, update = {})
    }
}
