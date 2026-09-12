package com.mylegnd.legend.registered.feature.calling

import android.content.Context
import android.net.ConnectivityManager
import android.net.Network
import android.net.NetworkCapabilities
import kotlinx.coroutines.*
import kotlinx.serialization.Serializable
import kotlinx.serialization.encodeToString
import kotlinx.serialization.json.Json
import org.webrtc.*
import kotlin.coroutines.resume
import kotlin.coroutines.resumeWithException
import kotlin.coroutines.suspendCoroutine

/** Native media only. Signaling, identity and call policy remain server-owned. */
class LegendRTCPeer(
    context: Context, private val policy: LegendCallPolicy, private val video: Boolean,
    private val caller: Boolean, private val scope: CoroutineScope,
    private val signal: suspend (String, String, Int) -> Unit,
    private val state: (String) -> Unit,
    private val remoteVideo: (VideoTrack) -> Unit,
) {
    val egl: EglBase = EglBase.create()
    private val app = context.applicationContext
    private val factory: PeerConnectionFactory
    private val peer: PeerConnection
    private val audioSource: AudioSource
    private val audioTrack: AudioTrack
    private var videoSource: VideoSource? = null
    private var surfaceHelper: SurfaceTextureHelper? = null
    private var capturer: CameraVideoCapturer? = null
    private var screenCapturer: ScreenCapturerAndroid? = null
    private var screenHelper: SurfaceTextureHelper? = null
    private var screenDimensions: Pair<Int, Int>? = null
    private var cameraWasEnabled = true
    var onScreenSharingEnded: (() -> Unit)? = null
    var localVideo: VideoTrack? = null; private set
    private val connectivity = app.getSystemService(ConnectivityManager::class.java)
    private var cellular = false
    private var background = false
    private var captureStarted = false
    private var closed = false
    private var epoch = 0
    private var remoteEpoch = -1
    private var readyToSignal = false
    private var negotiating = false
    private var connected = false
    private var recovery: Job? = null
    private val candidates = mutableListOf<Pair<Int, IceCandidate>>()
    private val outgoing = mutableListOf<IceCandidate>()
    private val json = Json { ignoreUnknownKeys = true }
    private val network = object : ConnectivityManager.NetworkCallback() {
        override fun onCapabilitiesChanged(network: Network, capabilities: NetworkCapabilities) {
            scope.launch {
                val next = capabilities.hasTransport(NetworkCapabilities.TRANSPORT_CELLULAR)
                if (next != cellular) { cellular = next; configureCapture(); applyBitrates(); if (connected) recover() }
            }
        }
        override fun onLost(network: Network) { scope.launch { connected = false; recover() } }
    }
    init {
        PeerConnectionFactory.initialize(PeerConnectionFactory.InitializationOptions.builder(app).createInitializationOptions())
        factory = PeerConnectionFactory.builder()
            .setVideoEncoderFactory(DefaultVideoEncoderFactory(egl.eglBaseContext, true, true))
            .setVideoDecoderFactory(DefaultVideoDecoderFactory(egl.eglBaseContext)).createPeerConnectionFactory()
        val configuration = PeerConnection.RTCConfiguration(policy.stunUrls.map { PeerConnection.IceServer.builder(it).createIceServer() }).apply {
            sdpSemantics = PeerConnection.SdpSemantics.UNIFIED_PLAN
            bundlePolicy = PeerConnection.BundlePolicy.MAXBUNDLE
            continualGatheringPolicy = PeerConnection.ContinualGatheringPolicy.GATHER_CONTINUALLY
        }
        peer = requireNotNull(factory.createPeerConnection(configuration, object : PeerConnection.Observer {
            override fun onIceCandidate(candidate: IceCandidate) { scope.launch { if (!closed) { if (readyToSignal) sendCandidate(candidate) else outgoing.add(candidate) } } }
            override fun onIceConnectionChange(value: PeerConnection.IceConnectionState) { scope.launch {
                if (closed) return@launch
                when (value) {
                    PeerConnection.IceConnectionState.CONNECTED, PeerConnection.IceConnectionState.COMPLETED -> { connected = true; recovery?.cancel(); recovery = null; state("Connected") }
                    PeerConnection.IceConnectionState.DISCONNECTED, PeerConnection.IceConnectionState.FAILED -> { connected = false; recover() }
                    else -> Unit
                }
            } }
            override fun onAddTrack(receiver: RtpReceiver, streams: Array<out MediaStream>) { (receiver.track() as? VideoTrack)?.let { scope.launch { remoteVideo(it) } } }
            override fun onSignalingChange(value: PeerConnection.SignalingState) {}
            override fun onIceConnectionReceivingChange(receiving: Boolean) {}
            override fun onIceGatheringChange(value: PeerConnection.IceGatheringState) {}
            override fun onIceCandidatesRemoved(candidates: Array<out IceCandidate>) {}
            override fun onAddStream(stream: MediaStream) {}
            override fun onRemoveStream(stream: MediaStream) {}
            override fun onDataChannel(channel: DataChannel) {}
            override fun onRenegotiationNeeded() {}
        }))
        audioSource = factory.createAudioSource(MediaConstraints())
        audioTrack = factory.createAudioTrack("legend-audio", audioSource)
        peer.addTrack(audioTrack, listOf("legend"))
        if (video) {
            videoSource = factory.createVideoSource(false)
            localVideo = factory.createVideoTrack("legend-video", videoSource)
            peer.addTrack(localVideo, listOf("legend"))
            val enumerator = Camera2Enumerator(app)
            val name = enumerator.deviceNames.firstOrNull { enumerator.isFrontFacing(it) } ?: enumerator.deviceNames.firstOrNull()
            capturer = name?.let { enumerator.createCapturer(it, null) }
            surfaceHelper = SurfaceTextureHelper.create("LegendCallCamera", egl.eglBaseContext)
            capturer?.initialize(surfaceHelper, app, videoSource?.capturerObserver)
            configureCapture()
        }
        applyBitrates()
        connectivity.registerDefaultNetworkCallback(network)
    }
    suspend fun offer(restart: Boolean = false) {
        if (closed || negotiating || !caller) return
        negotiating = true
        try {
            epoch += 1; readyToSignal = false; outgoing.clear()
            val constraints = MediaConstraints().apply { if (restart) mandatory.add(MediaConstraints.KeyValuePair("IceRestart", "true")) }
            val description = createDescription(true, constraints)
            setDescription(description, true)
            signal("offer", description.description, epoch)
            readyToSignal = true
            flushCandidates()
        } finally { negotiating = false }
    }
    suspend fun receive(kind: String, data: String, incomingEpoch: Int) {
        if (closed) return
        if (kind == "restart") { if (caller) recover(); return }
        if (incomingEpoch < epoch) return
        if (kind == "candidate") {
            val value = json.decodeFromString<Candidate>(data)
            val candidate = IceCandidate(value.sdpMid, value.sdpMLineIndex, value.candidate)
            if (remoteEpoch == incomingEpoch) peer.addIceCandidate(candidate) else candidates.add(incomingEpoch to candidate)
            return
        }
        epoch = incomingEpoch
        if (kind == "offer" && !caller) {
            readyToSignal = false; outgoing.clear()
            setDescription(SessionDescription(SessionDescription.Type.OFFER, data), false)
            drainRemote()
            val answer = createDescription(false, MediaConstraints())
            setDescription(answer, true)
            signal("answer", answer.description, epoch)
            readyToSignal = true; flushCandidates()
        } else if (kind == "answer" && caller) {
            setDescription(SessionDescription(SessionDescription.Type.ANSWER, data), false)
            drainRemote()
        }
    }
    fun recover() {
        if (closed || recovery != null) return
        state("Reconnecting")
        recovery = scope.launch {
            repeat(policy.recoveryAttempts) { attempt ->
                delay(if (attempt == 0) 2_000 else 5_000)
                if (closed) return@launch
                runCatching { if (caller) offer(true) else signal("restart", "", epoch) }
                delay(6_000)
                if (connected) { recovery = null; return@launch }
            }
            recovery = null; state("Direct connection unavailable")
        }
    }
    fun muted(value: Boolean) { audioTrack.setEnabled(!value) }
    fun camera(value: Boolean) { localVideo?.setEnabled(value); if (value) configureCapture() else runCatching { capturer?.stopCapture(); captureStarted = false } }
    fun background(value: Boolean) { background = value; if (value) runCatching { capturer?.stopCapture(); captureStarted = false } else configureCapture() }
    fun startScreenSharing(permission: android.content.Intent) {
        check(!closed && video && screenCapturer == null)
        val source = requireNotNull(videoSource)
        cameraWasEnabled = localVideo?.enabled() ?: true
        localVideo?.setEnabled(true)
        val screen = ScreenCapturerAndroid(permission, object : android.media.projection.MediaProjection.Callback() {
            override fun onStop() { scope.launch { stopScreenSharing() } }
            override fun onCapturedContentResize(width: Int, height: Int) {
                scope.launch {
                    if (width > 0 && height > 0) {
                        val size = screenSize(width, height)
                        if (size != screenDimensions && screenCapturer != null) {
                            screenDimensions = size
                            screenCapturer?.changeCaptureFormat(size.first, size.second, 15)
                        }
                    }
                }
            }
        })
        try {
            capturer?.stopCapture(); captureStarted = false
            screenHelper = SurfaceTextureHelper.create("LegendCallScreen", egl.eglBaseContext)
            screenCapturer = screen
            screen.initialize(screenHelper, app, source.capturerObserver)
            val display = app.resources.displayMetrics
            val size = screenSize(display.widthPixels, display.heightPixels)
            screenDimensions = size
            screen.startCapture(size.first, size.second, 15)
        } catch (error: Exception) { stopScreenSharing(); throw error }
    }
    private fun screenSize(width: Int, height: Int): Pair<Int, Int> {
        val maxEdge = if (cellular) maxOf(policy.cellularWidth, policy.cellularHeight) else maxOf(policy.wifiWidth, policy.wifiHeight)
        val scale = minOf(1.0, maxEdge.toDouble() / maxOf(width, height))
        return ((width * scale).toInt() / 2 * 2).coerceAtLeast(2) to ((height * scale).toInt() / 2 * 2).coerceAtLeast(2)
    }
    fun stopScreenSharing() {
        val screen = screenCapturer ?: return
        screenCapturer = null
        screenDimensions = null
        localVideo?.setEnabled(cameraWasEnabled)
        runCatching { screen.stopCapture() }; screen.dispose()
        screenHelper?.dispose(); screenHelper = null
        configureCapture()
        onScreenSharingEnded?.invoke()
    }
    fun flipCamera() { capturer?.switchCamera(null) }
    private fun configureCapture() {
        if (closed || screenCapturer != null || background || !video || localVideo?.enabled() != true) return
        val width = if (cellular) policy.cellularWidth else policy.wifiWidth
        val height = if (cellular) policy.cellularHeight else policy.wifiHeight
        val fps = if (cellular) policy.cellularFps else policy.wifiFps
        if (captureStarted) capturer?.changeCaptureFormat(width, height, fps)
        else { capturer?.startCapture(width, height, fps); captureStarted = capturer != null }
    }
    private fun applyBitrates() {
        peer.senders.forEach { sender ->
            val parameters = sender.parameters
            parameters.encodings.forEach { it.maxBitrateBps = if (sender.track()?.kind() == "audio") policy.audioBitrate else policy.videoBitrate }
            sender.parameters = parameters
        }
    }
    private suspend fun flushCandidates() { val batch = outgoing.toList(); outgoing.clear(); batch.forEach { sendCandidate(it) } }
    private suspend fun sendCandidate(candidate: IceCandidate) {
        runCatching { signal("candidate", json.encodeToString(Candidate(candidate.sdp, candidate.sdpMid, candidate.sdpMLineIndex)), epoch) }.onFailure { recover() }
    }
    private fun drainRemote() { remoteEpoch = epoch; candidates.filter { it.first == epoch }.forEach { peer.addIceCandidate(it.second) }; candidates.removeAll { it.first <= epoch } }
    private suspend fun createDescription(offer: Boolean, constraints: MediaConstraints): SessionDescription = suspendCoroutine { continuation ->
        val observer = object : SdpObserver {
            override fun onCreateSuccess(value: SessionDescription) { continuation.resume(value) }
            override fun onCreateFailure(error: String) { continuation.resumeWithException(IllegalStateException("Call negotiation failed.")) }
            override fun onSetSuccess() {}
            override fun onSetFailure(error: String) {}
        }
        if (offer) peer.createOffer(observer, constraints) else peer.createAnswer(observer, constraints)
    }
    private suspend fun setDescription(description: SessionDescription, local: Boolean): Unit = suspendCoroutine { continuation ->
        val observer = object : SdpObserver {
            override fun onSetSuccess() { continuation.resume(Unit) }
            override fun onSetFailure(error: String) { continuation.resumeWithException(IllegalStateException("Call negotiation failed.")) }
            override fun onCreateSuccess(value: SessionDescription) {}
            override fun onCreateFailure(error: String) {}
        }
        if (local) peer.setLocalDescription(observer, description) else peer.setRemoteDescription(observer, description)
    }
    fun close() {
        if (closed) return
        closed = true; recovery?.cancel(); recovery = null
        stopScreenSharing()
        runCatching { connectivity.unregisterNetworkCallback(network) }
        audioTrack.setEnabled(false); localVideo?.setEnabled(false)
        runCatching { capturer?.stopCapture() }; capturer?.dispose(); capturer = null
        peer.close(); peer.dispose()
        audioTrack.dispose(); audioSource.dispose(); localVideo?.dispose(); videoSource?.dispose(); surfaceHelper?.dispose()
        factory.dispose(); egl.release()
        candidates.clear(); outgoing.clear()
    }
    @Serializable private data class Candidate(val candidate: String, val sdpMid: String? = null, val sdpMLineIndex: Int)
}
