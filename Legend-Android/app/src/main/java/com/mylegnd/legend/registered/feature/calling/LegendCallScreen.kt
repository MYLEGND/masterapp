package com.mylegnd.legend.registered.feature.calling

import android.Manifest
import android.os.Build
import android.media.projection.MediaProjectionConfig
import android.media.projection.MediaProjectionManager
import androidx.activity.compose.LocalActivity
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.foundation.background
import androidx.compose.foundation.Image
import androidx.compose.ui.graphics.asImageBitmap
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.*
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.runtime.saveable.listSaver
import androidx.compose.runtime.saveable.rememberSaveable
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
import com.mylegnd.legend.registered.ui.LegendHomeBrandBar
import com.mylegnd.legend.registered.ui.legendPressClickable
import org.webrtc.SurfaceViewRenderer
import org.webrtc.VideoTrack

val LocalLegendCalling = staticCompositionLocalOf<LegendCallViewModel?> { null }

@Composable
private fun rememberCallPermissionGate(): LegendCallPermissionGate = rememberSaveable(
    saver = listSaver<LegendCallPermissionGate, String>(
        save = { it.pendingScope()?.let { scope -> listOf(scope.ownerId, scope.deviceId, scope.callId) }.orEmpty() },
        restore = { LegendCallPermissionGate(if (it.size == 3) LegendCallPermissionScope(it[0], it[1], it[2]) else null) },
    ),
) { LegendCallPermissionGate() }

@Composable
fun LegendCallOverlay(store: LegendCallViewModel) {
    val state by store.state.collectAsStateWithLifecycle()
    val presentingIdentity = state.starting || state.call?.status == "ringing"
    val context = androidx.compose.ui.platform.LocalContext.current
    val scope = rememberCoroutineScope()
    var snapshotRequest by remember { mutableIntStateOf(0) }
    var minimized by remember { mutableStateOf(false) }
    LaunchedEffect(state.sharingScreen) { minimized = state.sharingScreen }
    var snapshotError by remember { mutableStateOf<String?>(null) }
    val peerAvatar by produceState<android.graphics.Bitmap?>(null, state.call?.id, state.imagePath) {
        value = null
        val path = state.imagePath
        if (path != null) value = withContext(Dispatchers.IO) {
            com.mylegnd.legend.registered.core.push.loadLegendSenderAvatar(context, path)
        }
    }
    // Keep the original pending scope across recreation and owner changes.
    // A newer account must never inherit an earlier account's OS consent.
    val screenPermissionGate = rememberCallPermissionGate()
    val answerPermissionGate = rememberCallPermissionGate()
    val screenPermission = rememberLauncherForActivityResult(ActivityResultContracts.StartActivityForResult()) { result ->
        val requested = screenPermissionGate.consume(store.permissionScope())
        if (requested != null && result.resultCode == android.app.Activity.RESULT_OK)
            result.data?.let { store.startScreenSharing(it, requested) }
    }
    val answerPermissions = rememberLauncherForActivityResult(ActivityResultContracts.RequestMultiplePermissions()) { grants ->
        if (answerPermissionGate.consume(store.permissionScope()) != null) {
            val required = if (store.state.value.call?.video == true) listOf(Manifest.permission.RECORD_AUDIO, Manifest.permission.CAMERA)
                else listOf(Manifest.permission.RECORD_AUDIO)
            if (required.all { grants[it] == true }) store.answer() else store.end()
        }
    }
    fun answer() {
        val requested = store.permissionScope() ?: return
        if (!answerPermissionGate.begin(requested)) return
        try { answerPermissions.launch(if (state.call?.video == true) arrayOf(Manifest.permission.RECORD_AUDIO, Manifest.permission.CAMERA) else arrayOf(Manifest.permission.RECORD_AUDIO)) }
        catch (failure: RuntimeException) { answerPermissionGate.consume(null); throw failure }
    }
    LaunchedEffect(state.systemAnswerRequested) { if (state.systemAnswerRequested) answer() }
    val activity = LocalActivity.current
    val showOnLockScreen = state.call != null && !(state.sharingScreen && minimized)
    DisposableEffect(activity, showOnLockScreen, state.incoming) {
        // Only the authenticated call overlay may appear above the keyguard.
        // Never dismiss the keyguard or expose the rest of the account.
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O_MR1) {
            activity?.setShowWhenLocked(showOnLockScreen)
            activity?.setTurnScreenOn(showOnLockScreen && state.incoming)
        } else if (showOnLockScreen) {
            @Suppress("DEPRECATION")
            activity?.window?.addFlags(android.view.WindowManager.LayoutParams.FLAG_SHOW_WHEN_LOCKED)
        }
        onDispose {
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O_MR1) {
                activity?.setShowWhenLocked(false); activity?.setTurnScreenOn(false)
            } else {
                @Suppress("DEPRECATION")
                activity?.window?.clearFlags(android.view.WindowManager.LayoutParams.FLAG_SHOW_WHEN_LOCKED)
            }
        }
    }
    if (state.call == null && !state.starting && state.failure == null) return
    if (state.sharingScreen && minimized) {
        Row(Modifier.fillMaxWidth().statusBarsPadding().padding(12.dp).background(LegendColors.Navy, RoundedCornerShape(28.dp)).padding(8.dp),
            horizontalArrangement = Arrangement.SpaceBetween, verticalAlignment = Alignment.CenterVertically) {
            IconButton(onClick = { minimized = false }) { Icon(Icons.Default.Phone, legendLocalized("Return to call"), tint = Color.White) }
            Text(legendLocalized("Share screen"), color = Color.White)
            IconButton(onClick = store::stopScreenSharing) { Icon(Icons.Default.StopScreenShare, legendLocalized("Stop sharing"), tint = LegendColors.Gold) }
        }
        return
    }
    Dialog(onDismissRequest = {}, properties = DialogProperties(usePlatformDefaultWidth = false, dismissOnBackPress = false, dismissOnClickOutside = false)) {
        BoxWithConstraints(Modifier.fillMaxSize().background(Brush.linearGradient(listOf(LegendColors.Navy, LegendColors.Midnight)))) {
            val portraitSize = minOf(LegendDesignAuthority.size("callPortrait"), maxWidth * .64f, maxHeight * .36f)
            if (state.wallpaperMode == "profile" && peerAvatar != null && state.remoteVideo == null) {
                Image(peerAvatar!!.asImageBitmap(), contentDescription = null, modifier = Modifier.fillMaxSize(),
                    contentScale = androidx.compose.ui.layout.ContentScale.Crop)
                Box(Modifier.fillMaxSize().background(Color.Black.copy(alpha = .55f)))
            }
            state.remoteVideo?.let { video -> store.peer?.let { engine -> LegendVideoSurface(video, engine, Modifier.fillMaxSize(), snapshotRequest, fitContent = state.remoteScreenSharing) { bitmap ->
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
            if (!presentingIdentity && !state.sharingScreen) state.localVideo?.let { video -> store.peer?.let { engine ->
                LegendVideoSurface(video, engine, Modifier.align(Alignment.TopStart).statusBarsPadding().padding(start = 20.dp, top = 110.dp).size(96.dp, 132.dp).clip(RoundedCornerShape(20.dp)))
            } }
            Column(Modifier.fillMaxSize().navigationBarsPadding().padding(horizontal = 24.dp).padding(bottom = 12.dp),
                horizontalAlignment = Alignment.CenterHorizontally, verticalArrangement = Arrangement.spacedBy(12.dp)) {
                LegendHomeBrandBar(openFounderAi = null, create = null, showsHomeActions = false, usesDarkSurface = true)
                if (state.failure == null) Text(callStatusLabel(state.status), color = Color.White,
                    modifier = Modifier.background(Color.Black.copy(alpha = .3f), RoundedCornerShape(24.dp)).padding(horizontal = 14.dp, vertical = 6.dp))
                // Identity and secondary controls can scroll on compact/accessibility
                // layouts; the answer/cancel/end action always retains its own row.
                Column(Modifier.weight(1f).fillMaxWidth().verticalScroll(rememberScrollState()),
                    horizontalAlignment = Alignment.CenterHorizontally, verticalArrangement = Arrangement.spacedBy(16.dp, Alignment.CenterVertically)) {
                if (state.name.isNotBlank() && presentingIdentity) {
                    Box(Modifier.size(portraitSize).clip(CircleShape)
                        .background(LegendColors.Gold.copy(alpha = .18f)), contentAlignment = Alignment.Center) {
                        val avatar = peerAvatar
                        if (avatar != null) Image(avatar.asImageBitmap(), contentDescription = null,
                            modifier = Modifier.fillMaxSize(), contentScale = androidx.compose.ui.layout.ContentScale.Crop)
                        else Icon(Icons.Default.Person, contentDescription = null, tint = LegendColors.Gold)
                    }
                }
                if (state.name.isNotBlank() && presentingIdentity) {
                    Text(state.name, style = LegendTypography.Title, color = Color.White,
                        textAlign = androidx.compose.ui.text.style.TextAlign.Center)
                }
                    if (state.failure != null) Text(legendLocalized(state.failure!!), color = Color.White)
                    if (presentingIdentity && state.status == "Calling") Text(legendLocalized("Waiting for the recipient’s device to confirm receipt"), color = Color.White)
                    if (presentingIdentity && state.status == "Ringing") Text(legendLocalized("The recipient’s device received your call"), color = Color.White)
                    if (!presentingIdentity && state.failure == null) {
                        Column(Modifier.align(Alignment.End), verticalArrangement = Arrangement.spacedBy(16.dp)) {
                            LegendCallControl(if (state.muted) "Unmute" else "Mute", if (state.muted) Icons.Default.MicOff else Icons.Default.Mic, selected = state.muted, action = store::toggleMute)
                            LegendCallControl("Speaker", Icons.Default.VolumeUp, selected = state.speaker, action = store::toggleSpeaker)
                            if (state.call?.video == true && !state.sharingScreen) {
                                LegendCallControl("Camera", if (state.camera) Icons.Default.Videocam else Icons.Default.VideocamOff, action = store::toggleCamera)
                                LegendCallControl("Flip", Icons.Default.Cameraswitch, action = store::flipCamera)
                            }
                        if (state.remoteVideo != null && state.status == "Connected") {
                            LegendCallControl("Take snapshot", Icons.Default.PhotoCamera) { snapshotRequest++ }
                        }
                        if (state.call?.video == true && state.status == "Connected") {
                            LegendCallControl(if (state.sharingScreen) "Stop sharing" else "Share screen", Icons.Default.ScreenShare, selected = state.sharingScreen) {
                                if (state.sharingScreen) store.stopScreenSharing()
                                else {
                                    val projection = context.getSystemService(MediaProjectionManager::class.java)
                                    val capture = if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.UPSIDE_DOWN_CAKE)
                                        projection.createScreenCaptureIntent(MediaProjectionConfig.createConfigForDefaultDisplay())
                                    else projection.createScreenCaptureIntent()
                                    val requested = store.permissionScope()
                                    if (requested != null && screenPermissionGate.begin(requested)) {
                                        try { screenPermission.launch(capture) }
                                        catch (failure: RuntimeException) { screenPermissionGate.consume(null); throw failure }
                                    }
                                }
                            }
                        }
                        }
                    }
                }
                if (state.failure != null) {
                    Button(onClick = { store.end(); store.dismissFailure() }) { Text(legendLocalized("Close")) }
                } else if (state.incoming) {
                    Row(horizontalArrangement = Arrangement.spacedBy(30.dp)) {
                        LegendCallControl("Decline", Icons.Default.CallEnd, LegendColors.Error) { store.end() }
                        LegendCallControl("Answer", Icons.Default.Phone, LegendColors.Gold) { answer() }
                    }
                } else {
                    LegendCallControl(if (presentingIdentity) "Cancel" else "End call", Icons.Default.CallEnd, LegendColors.Error) { store.end() }
                }
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
private fun LegendVideoSurface(track: VideoTrack, peer: LegendRTCPeer, modifier: Modifier, snapshotRequest: Int = 0, fitContent: Boolean = false, snapshot: (android.graphics.Bitmap) -> Unit = {}) {
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
            init(peer.egl.eglBaseContext, null); setScalingType(if (fitContent) org.webrtc.RendererCommon.ScalingType.SCALE_ASPECT_FIT else org.webrtc.RendererCommon.ScalingType.SCALE_ASPECT_FILL); setEnableHardwareScaler(true); track.addSink(this); renderer = this
        } }, onRelease = { view -> track.removeSink(view); view.release(); renderer = null }, update = { view ->
            view.setScalingType(if (fitContent) org.webrtc.RendererCommon.ScalingType.SCALE_ASPECT_FIT else org.webrtc.RendererCommon.ScalingType.SCALE_ASPECT_FILL)
        })
    }
}

@Composable
private fun LegendCallControl(title: String, icon: androidx.compose.ui.graphics.vector.ImageVector,
    color: Color = Color.White.copy(alpha = LegendDesignAuthority.opacity("callControlSurface")),
    selected: Boolean = false, action: () -> Unit) {
    Column(Modifier.legendPressClickable(action), horizontalAlignment = Alignment.CenterHorizontally,
        verticalArrangement = Arrangement.spacedBy(LegendSpacing.Xs)) {
        Box(Modifier.size(LegendDesignAuthority.size("callControl")).background(if (selected) LegendColors.Gold else color, CircleShape), contentAlignment = Alignment.Center) {
            Icon(icon, legendLocalized(title), tint = Color.White)
        }
        Text(legendLocalized(title), style = LegendTypography.Caption, color = Color.White)
    }
}

@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun LegendCallingProfileSheet(store: LegendCallViewModel, dismiss: () -> Unit, editProfilePhoto: () -> Unit) {
    val scope = rememberCoroutineScope()
    var choices by remember(store) { mutableStateOf<LegendCallResult?>(null) }
    var selection by remember(store) { mutableStateOf<LegendCallPreferences?>(null) }
    var busy by remember { mutableStateOf(true) }
    var error by remember { mutableStateOf<String?>(null) }
    LaunchedEffect(store) {
        try {
            val result = store.preferences()
            check(result.preferences != null && result.ringtones != null && result.wallpapers != null) { "Calling preferences are unavailable." }
            choices = result; selection = result.preferences
        } catch (failure: Exception) { error = failure.message ?: "Calling preferences are unavailable." }
        finally { busy = false }
    }
    ModalBottomSheet(onDismissRequest = dismiss, containerColor = LegendColors.Canvas) {
        Column(Modifier.fillMaxWidth().padding(LegendSpacing.PageHorizontal), verticalArrangement = Arrangement.spacedBy(LegendSpacing.Md)) {
            Text(legendLocalized("Calling profile"), style = LegendTypography.Section)
            TextButton(onClick = { dismiss(); editProfilePhoto() }) {
                Icon(Icons.Default.AccountCircle, null); Spacer(Modifier.width(LegendSpacing.Xs))
                Text(legendLocalized("Change profile photo"))
            }
            if (busy) CircularProgressIndicator(color = LegendColors.Gold)
            error?.let { Text(legendLocalized(it), color = LegendColors.Error) }
            val current = selection
            if (current != null) {
                Text(legendLocalized("Ringtone"), style = LegendTypography.Label)
                choices?.ringtones.orEmpty().forEach { option ->
                    Row(verticalAlignment = Alignment.CenterVertically) {
                        RadioButton(selected = current.ringtoneId == option.id, enabled = !busy,
                            onClick = { selection = current.copy(ringtoneId = option.id) })
                        Text(legendLocalized(option.label))
                    }
                }
                Text(legendLocalized("Calling wallpaper"), style = LegendTypography.Label)
                choices?.wallpapers.orEmpty().forEach { option ->
                    Row(verticalAlignment = Alignment.CenterVertically) {
                        RadioButton(selected = current.wallpaperMode == option.id, enabled = !busy,
                            onClick = { selection = current.copy(wallpaperMode = option.id) })
                        Text(legendLocalized(option.label))
                    }
                }
                Button(enabled = !busy, onClick = {
                    busy = true; error = null
                    scope.launch {
                        try { store.preferences(current); dismiss() }
                        catch (failure: Exception) { error = failure.message ?: "Calling preferences could not be saved." }
                        finally { busy = false }
                    }
                }) { Text(legendLocalized("Save")) }
            }
            Spacer(Modifier.height(LegendSpacing.Lg))
        }
    }
}
