package com.mylegnd.legend.registered.feature.calling

import android.Manifest
import android.app.Application
import android.content.Context
import android.content.Intent
import android.content.pm.PackageManager
import android.media.*
import android.os.PowerManager
import android.os.Build
import android.telecom.CallAudioState
import androidx.core.content.ContextCompat
import androidx.lifecycle.ViewModel
import androidx.lifecycle.viewModelScope
import com.mylegnd.legend.registered.R
import com.mylegnd.legend.registered.MainActivity
import com.mylegnd.legend.registered.LegendContainer
import com.mylegnd.legend.registered.core.model.MobileIdentity
import com.mylegnd.legend.registered.core.realtime.MobileMessagingRealtimeClient
import kotlinx.coroutines.*
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.asStateFlow
import org.webrtc.VideoTrack
import java.time.Instant
import java.util.UUID

// One activity owner retains media across rotation, but never reuses a retired
// account store when switching A -> B -> A or rebuilding localized UI.
class LegendCallingCoordinator : ViewModel() {
    private var identityKey: String? = null
    private var current: LegendCallViewModel? = null
    fun activate(app: Application, container: LegendContainer, identity: MobileIdentity): LegendCallViewModel {
        val key = "${identity.participantType}:${identity.userId}"
        if (identityKey != key || current == null) {
            current?.shutdown()
            identityKey = key
            current = LegendCallViewModel(app, container.messagingRealtime(identity.participantType), identity)
        }
        return requireNotNull(current)
    }
    fun deactivate(store: LegendCallViewModel) {
        if (current === store) { store.shutdown(); current = null; identityKey = null }
    }
    override fun onCleared() { current?.shutdown(); current = null }
}

// This survives Activity recreation and shares the existing messaging socket.
data class LegendCallUiState(val call: LegendCallSnapshot? = null, val status: String = "", val failure: String? = null, val name: String = "", val incoming: Boolean = false, val muted: Boolean = false, val camera: Boolean = true, val speaker: Boolean = false, val localVideo: VideoTrack? = null, val remoteVideo: VideoTrack? = null, val systemAnswerRequested: Boolean = false, val sharingScreen: Boolean = false, val controlError: String? = null, val starting: Boolean = false)
class LegendCallViewModel(private val app: Application, val transport: MobileMessagingRealtimeClient, private val identity: MobileIdentity) : ViewModel() {
    private val mutableState = MutableStateFlow(LegendCallUiState())
    val state = mutableState.asStateFlow()
    val deviceId = app.getSharedPreferences("legend_calls", Context.MODE_PRIVATE).let { preferences -> preferences.getString("device", null) ?: UUID.randomUUID().toString().also { preferences.edit().putString("device", it).apply() } }
    private var policy: LegendCallPolicy? = null
    var peer: LegendRTCPeer? = null; private set
    private var ringback: MediaPlayer? = null
    private var reconciliation: Job? = null
    private var startupDeadline: Job? = null
    private var stopped = false
    private var pending: Triple<String, String, Boolean>? = null
    private var deadline: Job? = null
    private var heartbeat: Job? = null
    private val reported = mutableSetOf<String>()
    private val finished = mutableSetOf<String>()
    private val audio = app.getSystemService(AudioManager::class.java)
    private var proximity: PowerManager.WakeLock? = null
    private val focus = AudioFocusRequest.Builder(AudioManager.AUDIOFOCUS_GAIN).setAudioAttributes(AudioAttributes.Builder().setUsage(AudioAttributes.USAGE_VOICE_COMMUNICATION).setContentType(AudioAttributes.CONTENT_TYPE_SPEECH).build()).setOnAudioFocusChangeListener { change ->
        viewModelScope.launch { if (state.value.call != null) { if (change < 0) update { it.copy(status = "Audio interrupted") } else peer?.recover() } }
    }.build()
    val pendingCallId get() = pending?.first
    val inCall get() = state.value.call != null || pending != null
    private val caller get() = state.value.call?.let { isCallerAccount(it) && it.callerDeviceId == deviceId } == true
    private fun isCallerAccount(call: LegendCallSnapshot) = call.callerType.equals(identity.participantType, true) && (call.callerUserIds ?: listOf(call.callerUserId)).any { it.equals(identity.userId, true) }
    private fun isCalleeAccount(call: LegendCallSnapshot) = call.calleeType.equals(identity.participantType, true) && (call.calleeUserIds ?: listOf(call.calleeUserId)).any { it.equals(identity.userId, true) }
    init {
        LegendCallPlatform.store = this
        LegendCallPlatform.register(app)
        transport.onCall = { event -> viewModelScope.launch { receive(event) } }
        transport.onCallReconnect = { viewModelScope.launch { sync() } }
    }
    private fun update(transform: (LegendCallUiState) -> LegendCallUiState) { mutableState.value = transform(mutableState.value) }
    private suspend fun command(value: LegendCallCommand): LegendCallResult {
        check(!stopped) { "This account session has ended." }
        val result = transport.call(value)
        check(!stopped) { "This account session has ended." }
        check(result.succeeded) { result.error ?: "Call unavailable." }
        result.policy?.let { policy = it }
        return result
    }
    fun start(conversationId: String, video: Boolean, recipientName: String = "") {
        if (inCall || stopped) return
        val request = Triple(UUID.randomUUID().toString(), conversationId, video)
        pending = request
        update { it.copy(starting = true, status = "Calling", name = recipientName, failure = null) }
        viewModelScope.launch {
            try {
                checkPermissions(video)
                if (pending?.first != request.first || stopped) return@launch
                startupDeadline = viewModelScope.launch {
                    delay(25_000)
                    if (pending?.first == request.first) fail("The call could not start in time. Please try again.")
                }
                LegendCallPlatform.outgoing(app, request.first)
            } catch (error: Exception) {
                if (pending?.first == request.first) fail(error.message ?: "Calling unavailable.")
            }
        }
    }
    fun placePendingCall() = viewModelScope.launch {
        val request = pending ?: return@launch
        try {
            val result = command(LegendCallCommand("invite", deviceId, request.first, request.second, request.third))
            if (pending?.first != request.first || stopped) {
                runCatching { transport.call(LegendCallCommand("cancel", deviceId, request.first, request.second), existingConnectionOnly = true) }
                return@launch
            }
            val call = result.call ?: error("The call could not start. Please try again.")
            pending = null
            startupDeadline?.cancel(); startupDeadline = null
            receive(LegendCallEvent(call))
        } catch (error: Exception) { if (pending?.first == request.first) fail(error.message ?: "Calling unavailable.") }
    }
    fun answer() = viewModelScope.launch {
        val call = state.value.call ?: return@launch
        update { it.copy(systemAnswerRequested = false) }
        try {
            checkPermissions(call.video)
            ensurePeer()
            command(LegendCallCommand("accept", deviceId, call.id)).call?.let(::show)
            LegendCallPlatform.connection?.setActive()
        } catch (error: Exception) { fail(error.message ?: "The call could not be answered.") }
    }
    fun requestSystemAnswer() {
        update { it.copy(systemAnswerRequested = true) }
        app.startActivity(Intent(app, MainActivity::class.java).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK or Intent.FLAG_ACTIVITY_SINGLE_TOP))
    }
    fun end() = viewModelScope.launch {
        val outgoing = pending
        val id = state.value.call?.id ?: outgoing?.first
        val decline = state.value.incoming
        clear()
        if (id != null) runCatching { command(LegendCallCommand(if (outgoing != null) "cancel" else if (decline) "decline" else "end", deviceId, id, outgoing?.second)) }
    }
    fun background(value: Boolean) { peer?.background(value) }
    fun dismissFailure() { update { it.copy(failure = null) } }
    fun toggleMute() { update { it.copy(muted = !it.muted) }; peer?.muted(state.value.muted) }
    fun toggleCamera() { update { it.copy(camera = !it.camera) }; peer?.camera(state.value.camera) }
    fun startScreenSharing(permission: android.content.Intent) {
        if (state.value.status != "Connected" || state.value.call?.video != true) return
        runCatching { LegendCallPlatform.startScreenSharing(app, permission) }
            .onFailure { update { it.copy(controlError = "Screen sharing could not start. Please try again.") } }
    }
    fun captureScreen(permission: android.content.Intent) {
        val engine = peer ?: return
        engine.onScreenSharingEnded = { update { it.copy(sharingScreen = false) } }
        runCatching { engine.startScreenSharing(permission) }
            .onSuccess { update { it.copy(sharingScreen = true) } }
            .onFailure { update { it.copy(controlError = "Screen sharing could not start. Please try again.") } }
    }
    fun stopScreenSharing() { peer?.stopScreenSharing(); update { it.copy(sharingScreen = false) } }
    fun dismissControlError() { update { it.copy(controlError = null) } }
    fun flipCamera() { peer?.flipCamera() }
    fun toggleSpeaker() {
        val enabled = !state.value.speaker
        routeSpeaker(enabled)
        update { it.copy(speaker = enabled) }; updateProximity()
    }
    fun audioRouteChanged(route: Int) { update { it.copy(speaker = route == CallAudioState.ROUTE_SPEAKER) }; updateProximity() }
    fun platformFailed() { viewModelScope.launch {
        if (state.value.incoming) {
            update { it.copy(failure = "Android could not present this call. Check calling permissions and notifications.") }
            clear() // Do not decline other devices on the receiving account.
        } else fail("Android could not start this call. Check calling permissions and try again.")
    } }
    fun receivePush(call: LegendCallSnapshot) { viewModelScope.launch { receive(LegendCallEvent(call)) } }
    @Suppress("DEPRECATION")
    private fun routeSpeaker(enabled: Boolean) {
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.S) {
            val target = if (enabled) AudioDeviceInfo.TYPE_BUILTIN_SPEAKER else AudioDeviceInfo.TYPE_BUILTIN_EARPIECE
            audio.availableCommunicationDevices.firstOrNull { it.type == target }?.let { audio.setCommunicationDevice(it) }
        } else audio.isSpeakerphoneOn = enabled
    }
    private fun checkPermissions(video: Boolean) {
        check(ContextCompat.checkSelfPermission(app, Manifest.permission.RECORD_AUDIO) == PackageManager.PERMISSION_GRANTED) { "Allow microphone access to join a call." }
        if (video) check(ContextCompat.checkSelfPermission(app, Manifest.permission.CAMERA) == PackageManager.PERMISSION_GRANTED) { "Allow camera access to join a video call." }
    }
    private fun show(call: LegendCallSnapshot) {
        LegendCallPlatform.connection?.setCallerDisplayName(if (isCallerAccount(call)) call.calleeName else call.callerName, android.telecom.TelecomManager.PRESENTATION_ALLOWED)
        update { it.copy(call = call, starting = false, name = if (call.callerDeviceId == deviceId) call.calleeName else call.callerName,
            incoming = call.status == "ringing" && call.callerDeviceId != deviceId,
            status = if (call.status == "ringing") { if (call.callerDeviceId == deviceId) { if (call.receivedUtc == null) "Calling" else "Ringing" } else "Incoming call" } else it.status) }
        updateRingback()
        startReconciliation(call.id)
    }
    private suspend fun sync() {
        runCatching {
            val result = command(LegendCallCommand("sync", deviceId))
            val current = state.value.call
            if (current != null) {
                val found = result.activeCalls?.firstOrNull { it.id == current.id }
                if (found == null) clear() else { show(found); if (found.status != "ringing") peer?.recover() }
            } else result.activeCalls?.firstOrNull { isCalleeAccount(it) && it.status == "ringing" }?.let { receive(LegendCallEvent(it)) }
        }
    }
    private suspend fun receive(event: LegendCallEvent) {
        val call = event.call
        val owns = isCallerAccount(call) || isCalleeAccount(call)
        if (stopped || !owns || call.id in finished) return
        if (state.value.call == null && call.status != "ringing" && pending?.first != call.id) return
        if (state.value.call?.id == call.id && state.value.call?.status != "ringing" && call.status == "ringing") return
        if (event.toDeviceId != null && event.toDeviceId != deviceId) return
        if (state.value.call != null && state.value.call?.id != call.id) return
        val callerAccount = isCallerAccount(call)
        if (callerAccount && call.callerDeviceId != deviceId) return
        if (!callerAccount && call.calleeDeviceId != null && call.calleeDeviceId != deviceId) { if (state.value.call?.id == call.id) clear(); return }
        if (pending != null && pending?.first != call.id) return
        if (call.terminal) {
            if (state.value.call?.id == call.id || pending?.first == call.id) {
                if (callerAccount) update { it.copy(failure = call.failureMessage) }
                clear()
            }
            return
        }
        if (state.value.call?.id == call.id && state.value.call?.receivedUtc != null && call.receivedUtc == null && call.status == "ringing") return
        show(call)
        if (call.status == "ringing") {
            if (!callerAccount && reported.add(call.id)) { runCatching { LegendCallPlatform.incoming(app, call) }.onFailure { platformFailed() } }
            armDeadline(call.id, Instant.parse(call.expiresUtc).toEpochMilli())
        } else if (event.signalKind != null && event.signalData != null) {
            runCatching { ensurePeer(); peer?.receive(event.signalKind, event.signalData, call.epoch) }.onFailure { fail("The direct connection could not be established.") }
        } else if (callerAccount && peer == null) {
            runCatching { ensurePeer(); peer?.offer() }.onFailure { fail(it.message ?: "Calling unavailable.") }
        }
    }
    fun confirmIncomingPresentation(id: String) = viewModelScope.launch {
        if (state.value.call?.id != id || !state.value.incoming) return@launch
        runCatching { command(LegendCallCommand("received", deviceId, id)) }
            .onFailure {
                if (state.value.call?.id == id) {
                    update { it.copy(failure = "The incoming call could not be confirmed. Please try again.") }
                    clear()
                }
            }
    }
    private fun updateRingback() {
        val call = state.value.call
        if (!caller || call?.status != "ringing" || call.receivedUtc == null) {
            ringback?.release(); ringback = null; return
        }
        if (ringback != null) return
        runCatching {
            audio.mode = AudioManager.MODE_IN_COMMUNICATION
            check(audio.requestAudioFocus(focus) == AudioManager.AUDIOFOCUS_REQUEST_GRANTED)
            val player = MediaPlayer()
            ringback = player
            player.setAudioAttributes(AudioAttributes.Builder().setUsage(AudioAttributes.USAGE_VOICE_COMMUNICATION_SIGNALLING).setContentType(AudioAttributes.CONTENT_TYPE_MUSIC).build())
            app.resources.openRawResourceFd(R.raw.legend_ringback).use { descriptor ->
                player.setDataSource(descriptor.fileDescriptor, descriptor.startOffset, descriptor.length)
            }
            player.isLooping = true; player.prepare(); player.start()
        }.onFailure {
            ringback?.release(); ringback = null
            update { it.copy(controlError = "The recipient received the call, but the ringing sound could not play.") }
        }
    }
    private fun startReconciliation(id: String) {
        if (reconciliation != null) return
        reconciliation = viewModelScope.launch {
            while (isActive && state.value.call?.id == id) {
                delay(3_000)
                if (state.value.call?.id != id) return@launch
                try {
                    val result = command(LegendCallCommand("get", deviceId, id))
                    if (!isActive || state.value.call?.id != id) return@launch
                    result.call?.let { receive(LegendCallEvent(it)) }
                } catch (error: Exception) {
                    if (isActive && state.value.call?.id == id) fail("The call status could not be confirmed. Please try again.")
                }
            }
        }
    }
    private suspend fun ensurePeer() {
        if (peer != null) return
        val call = state.value.call ?: return
        if (policy == null) command(LegendCallCommand("get", deviceId, call.id))
        val settings = policy ?: error("Call settings unavailable.")
        checkPermissions(call.video)
        ringback?.release(); ringback = null
        audio.mode = AudioManager.MODE_IN_COMMUNICATION
        audio.requestAudioFocus(focus)
        update { it.copy(speaker = call.video, status = "Connecting") }
        if (call.video) routeSpeaker(true)
        LegendCallPlatform.foreground(app, call.video)
        val engine = LegendRTCPeer(app, settings, call.video, caller, viewModelScope,
            signal = { kind, data, epoch -> command(LegendCallCommand("signal", deviceId, call.id, signalKind = kind, signalData = data, epoch = epoch)); Unit },
            state = { value ->
                if (state.value.call?.id == call.id) {
                    update { it.copy(status = value) }
                    if (value == "Connected") {
                        deadline?.cancel(); LegendCallPlatform.connection?.setActive()
                        viewModelScope.launch { runCatching { command(LegendCallCommand("connected", deviceId, call.id)) } }
                        startHeartbeat(call.id)
                    } else if (value == "Direct connection unavailable") fail("This network could not establish a direct call. Try another Wi-Fi or mobile connection.")
                }
            }, remoteVideo = { track -> update { it.copy(remoteVideo = track) } })
        peer = engine; update { it.copy(localVideo = engine.localVideo) }; updateProximity()
        armDeadline(call.id, System.currentTimeMillis() + settings.connectSeconds * 1000L)
    }
    private fun startHeartbeat(id: String) {
        if (heartbeat != null) return
        heartbeat = viewModelScope.launch {
            var failures = 0
            while (isActive && state.value.call?.id == id) {
                delay(25_000)
                if (state.value.call?.id != id) return@launch
                updateProximity()
                runCatching { command(LegendCallCommand("heartbeat", deviceId, id)) }
                    .onSuccess { failures = 0 }
                    .onFailure { failures++; if (failures >= 2) fail("The call session could not be verified. Please call again.") else peer?.recover() }
            }
        }
    }
    private fun armDeadline(id: String, at: Long) {
        deadline?.cancel()
        deadline = viewModelScope.launch { delay((at - System.currentTimeMillis()).coerceAtLeast(1_000)); if (state.value.call?.id == id) fail(if (state.value.call?.status == "ringing") { if (caller && state.value.call?.receivedUtc == null) "The recipient could not be reached. Their device did not confirm receiving the call." else "The call was not answered." } else "This network could not establish a direct call. Try another Wi-Fi or mobile connection.") }
    }
    private fun updateProximity() {
        val power = app.getSystemService(PowerManager::class.java)
        val receiver = if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.S) audio.communicationDevice?.type == AudioDeviceInfo.TYPE_BUILTIN_EARPIECE else !state.value.speaker
        if (state.value.call != null && !state.value.speaker && receiver && power.isWakeLockLevelSupported(PowerManager.PROXIMITY_SCREEN_OFF_WAKE_LOCK)) {
            if (proximity == null) proximity = power.newWakeLock(PowerManager.PROXIMITY_SCREEN_OFF_WAKE_LOCK, "legend:call-proximity").apply { setReferenceCounted(false) }
            proximity?.acquire(90_000)
        } else { if (proximity?.isHeld == true) proximity?.release(); proximity = null }
    }
    private fun fail(message: String) { update { it.copy(failure = message) }; end() }
    private fun clear() {
        pending?.first?.let { finished.add(it) }
        ringback?.release(); ringback = null
        reconciliation?.cancel(); reconciliation = null
        startupDeadline?.cancel(); startupDeadline = null
        state.value.call?.id?.let { finished.add(it) }
        deadline?.cancel(); deadline = null; heartbeat?.cancel(); heartbeat = null; pending = null
        val oldPeer = peer; peer = null; update { LegendCallUiState(failure = it.failure) }; oldPeer?.close()
        if (proximity?.isHeld == true) proximity?.release(); proximity = null
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.S) audio.clearCommunicationDevice() else routeSpeaker(false)
        audio.abandonAudioFocusRequest(focus); audio.mode = AudioManager.MODE_NORMAL
        LegendCallPlatform.ended(app)
    }
    fun shutdown() {
        if (stopped) return
        stopped = true
        transport.retireAccountConnection()
        val outgoing = pending
        val id = state.value.call?.id ?: outgoing?.first
        val decline = state.value.incoming
        clear()
        if (LegendCallPlatform.store === this) LegendCallPlatform.store = null
        viewModelScope.cancel()
        // A bounded teardown owns only the old authenticated connection. It may
        // outlive ViewModel cancellation but cannot reconnect as the next account.
        CoroutineScope(Dispatchers.Main.immediate).launch {
            try {
                if (id != null) runCatching { transport.call(LegendCallCommand(if (outgoing != null) "cancel" else if (decline) "decline" else "end", deviceId, id, outgoing?.second), existingConnectionOnly = true) }
            } finally { transport.close(); cancel() }
        }
    }
    override fun onCleared() { shutdown() }
}
