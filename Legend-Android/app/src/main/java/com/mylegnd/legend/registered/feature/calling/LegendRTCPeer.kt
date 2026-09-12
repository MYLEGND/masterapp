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
import org.webrtc.audio.JavaAudioDeviceModule
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
    private val remoteScreenSharing: (Boolean) -> Unit = {},
    private val audioObservation: (String) -> Unit = {},
) {
    val egl: EglBase = EglBase.create()
    private val app = context.applicationContext
    private val audioDeviceModule: JavaAudioDeviceModule
    private val factory: PeerConnectionFactory
    private val peer: PeerConnection
    private val audioSource: AudioSource
    private val audioTrack: AudioTrack
    private var videoSource: VideoSource? = null
    private var surfaceHelper: SurfaceTextureHelper? = null
    private var capturer: CameraVideoCapturer? = null
    private var screenCapturer: ScreenCapturerAndroid? = null
    private var screenHelper: SurfaceTextureHelper? = null
    private var screenSource: VideoSource? = null
    private var screenTrack: VideoTrack? = null
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
    private var qualityMonitor: Job? = null
    private var quality = 2
    private var availableBandwidth: Double? = null
    private var healthySamples = 0
    private var qualitySample = 0L
    private var completedQualitySample = 0L
    private var screenContentSize: Pair<Int, Int>? = null
    private var screenGeneration = 0L
    private var audioObservationCount = 0
    private var previousAudioCounters: Map<String, Long>? = null
    private val candidates = mutableListOf<Pair<Int, IceCandidate>>()
    private val outgoing = mutableListOf<IceCandidate>()
    private val json = Json { ignoreUnknownKeys = true }
    private val network = object : ConnectivityManager.NetworkCallback() {
        override fun onCapabilitiesChanged(network: Network, capabilities: NetworkCapabilities) {
            scope.launch {
                if (closed) return@launch
                qualitySample++; healthySamples = 0
                val next = capabilities.hasTransport(NetworkCapabilities.TRANSPORT_CELLULAR)
                if (next != cellular) { cellular = next; configureCapture(); applyBitrates(); if (connected) recover() }
            }
        }
        override fun onLost(network: Network) { scope.launch { if (!closed) { connected = false; recover() } } }
    }
    init {
        PeerConnectionFactory.initialize(PeerConnectionFactory.InitializationOptions.builder(app).createInitializationOptions())
        audioDeviceModule = JavaAudioDeviceModule.builder(app)
            .setAudioRecordErrorCallback(object : JavaAudioDeviceModule.AudioRecordErrorCallback {
                override fun onWebRtcAudioRecordInitError(error: String) = audioFailure("Microphone unavailable")
                override fun onWebRtcAudioRecordStartError(code: JavaAudioDeviceModule.AudioRecordStartErrorCode, error: String) = audioFailure("Microphone unavailable")
                override fun onWebRtcAudioRecordError(error: String) = audioFailure("Microphone unavailable")
            })
            .setAudioTrackErrorCallback(object : JavaAudioDeviceModule.AudioTrackErrorCallback {
                override fun onWebRtcAudioTrackInitError(error: String) = audioFailure("Call playback unavailable")
                override fun onWebRtcAudioTrackStartError(code: JavaAudioDeviceModule.AudioTrackStartErrorCode, error: String) = audioFailure("Call playback unavailable")
                override fun onWebRtcAudioTrackError(error: String) = audioFailure("Call playback unavailable")
            }).createAudioDeviceModule()
        factory = PeerConnectionFactory.builder()
            .setAudioDeviceModule(audioDeviceModule)
            .setVideoEncoderFactory(DefaultVideoEncoderFactory(egl.eglBaseContext, true, true))
            .setVideoDecoderFactory(DefaultVideoDecoderFactory(egl.eglBaseContext)).createPeerConnectionFactory()
        val servers = policy.stunUrls.map { PeerConnection.IceServer.builder(it).createIceServer() }.toMutableList()
        policy.relay?.let { relay -> servers.add(PeerConnection.IceServer.builder(relay.urls).setUsername(relay.username).setPassword(relay.credential).createIceServer()) }
        val configuration = PeerConnection.RTCConfiguration(servers).apply {
            sdpSemantics = PeerConnection.SdpSemantics.UNIFIED_PLAN
            bundlePolicy = PeerConnection.BundlePolicy.MAXBUNDLE
            continualGatheringPolicy = PeerConnection.ContinualGatheringPolicy.GATHER_CONTINUALLY
        }
        peer = requireNotNull(factory.createPeerConnection(configuration, object : PeerConnection.Observer {
            override fun onIceCandidate(candidate: IceCandidate) { scope.launch { if (!closed) { if (readyToSignal) sendCandidate(candidate) else outgoing.add(candidate) } } }
            override fun onIceConnectionChange(value: PeerConnection.IceConnectionState) { scope.launch {
                if (closed) return@launch
                when (value) {
                    PeerConnection.IceConnectionState.CONNECTED, PeerConnection.IceConnectionState.COMPLETED -> {
                        val newlyConnected = !connected
                        connected = true; recovery?.cancel(); recovery = null; state("Connected")
                        if (newlyConnected) sendMediaState(request = true)
                    }
                    PeerConnection.IceConnectionState.DISCONNECTED, PeerConnection.IceConnectionState.FAILED -> { connected = false; recover() }
                    else -> Unit
                }
            } }
            override fun onAddTrack(receiver: RtpReceiver, streams: Array<out MediaStream>) { (receiver.track() as? VideoTrack)?.let { scope.launch { if (!closed) remoteVideo(it) } } }
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
        policy.adaptation?.let { tuning ->
            qualityMonitor = scope.launch {
                while (!closed) {
                    delay(tuning.sampleSeconds.coerceAtLeast(1) * 1000L)
                    if (completedQualitySample != qualitySample) healthySamples = 0
                    val sample = ++qualitySample
                    if (connected) peer.getStats { report ->
                        val selected = report.statsMap.values.firstOrNull { it.type == "transport" && it.members["selectedCandidatePairId"] != null }?.members?.get("selectedCandidatePairId") as? String
                        val pair = selected?.let { report.statsMap[it] }
                        val bandwidth = (pair?.members?.get("availableOutgoingBitrate") as? Number)?.toDouble()
                        val latency = (pair?.members?.get("currentRoundTripTime") as? Number)?.toDouble()
                        scope.launch sampleResult@{
                            if (closed || !connected || sample != qualitySample) return@sampleResult
                            completedQualitySample = sample
                            observeAudio(report)
                            if (bandwidth != null && bandwidth.isFinite() && bandwidth >= 0) {
                                availableBandwidth = bandwidth
                                applyBitrates()
                            }
                            val target = tuning.targetQuality(bandwidth, latency)
                            if (target == null) { healthySamples = 0; return@sampleResult }
                            if (target < quality) { quality = target; healthySamples = 0; configureCapture(); applyBitrates() }
                            else if (target > quality) { healthySamples++; if (healthySamples >= tuning.recoverySamples) { quality++; healthySamples = 0; configureCapture(); applyBitrates() } }
                            else healthySamples = 0
                        }
                    }
                }
            }
        }
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
            applyBitrates()
            signal("offer", description.description, epoch)
            readyToSignal = true
            flushCandidates()
        } finally { negotiating = false }
    }
    suspend fun receive(kind: String, data: String, incomingEpoch: Int) {
        if (closed) return
        if (kind == "restart") { if (caller) recover(); return }
        if (incomingEpoch < epoch) return
        if (kind == "media-state") {
            val media = json.callMediaState(data) ?: return
            remoteScreenSharing(media.screenSharing)
            if (media.request) sendMediaState()
            return
        }
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
            applyBitrates()
            signal("answer", answer.description, epoch)
            readyToSignal = true; flushCandidates()
        } else if (kind == "answer" && caller) {
            setDescription(SessionDescription(SessionDescription.Type.ANSWER, data), false)
            applyBitrates()
            drainRemote()
        }
    }
    fun recover() {
        if (closed || recovery != null) return
        qualitySample++; healthySamples = 0
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
        val generation = ++screenGeneration
        cameraWasEnabled = localVideo?.enabled() ?: true
        val screen = ScreenCapturerAndroid(permission, object : android.media.projection.MediaProjection.Callback() {
            override fun onStop() { scope.launch { if (generation == screenGeneration) stopScreenSharing() } }
            override fun onCapturedContentResize(width: Int, height: Int) {
                scope.launch {
                    if (!closed && generation == screenGeneration && width > 0 && height > 0) {
                        screenContentSize = width to height
                        val size = screenSize(width, height)
                        if (size != screenDimensions && screenCapturer != null) {
                            screenDimensions = size
                            screenCapturer?.changeCaptureFormat(size.first, size.second, screenLimits.fps)
                        }
                    }
                }
            }
        })
        try {
            screenCapturer = screen
            capturer?.stopCapture(); captureStarted = false
            screenSource = factory.createVideoSource(true)
            screenTrack = factory.createVideoTrack("legend-screen", screenSource)
            screenHelper = SurfaceTextureHelper.create("LegendCallScreen", egl.eglBaseContext)
            screen.initialize(screenHelper, app, screenSource?.capturerObserver)
            check(peer.senders.firstOrNull { it.track()?.kind() == "video" }?.setTrack(screenTrack, false) == true) {
                "Screen sharing could not replace the camera track."
            }
            applyBitrates()
            val display = app.resources.displayMetrics
            screenContentSize = display.widthPixels to display.heightPixels
            val size = screenSize(display.widthPixels, display.heightPixels)
            screenDimensions = size
            screen.startCapture(size.first, size.second, screenLimits.fps)
            scope.launch { sendMediaState() }
        } catch (error: Exception) { stopScreenSharing(); throw error }
    }
    private val screenLimits: LegendScreenCaptureLimits get() =
        policy.screenShare?.limits(quality, cellular) ?: captureLimits.let {
            // Older servers retain their advertised camera limits; no client-only quota policy.
            LegendScreenCaptureLimits(it.first, it.second, minOf(15, it.third), videoBitrate)
        }
    private fun screenSize(width: Int, height: Int): Pair<Int, Int> = screenLimits.dimensions(width, height)
    fun stopScreenSharing() {
        val screen = screenCapturer ?: return
        screenCapturer = null
        screenGeneration++
        screenDimensions = null
        screenContentSize = null
        localVideo?.setEnabled(cameraWasEnabled)
        runCatching { screen.stopCapture() }; runCatching { screen.dispose() }
        val sender = peer.senders.firstOrNull { it.track()?.kind() == "video" }
        val restored = sender?.setTrack(localVideo, false) == true
        if (!restored) sender?.setTrack(null, false)
        screenTrack?.dispose(); screenTrack = null
        screenSource?.dispose(); screenSource = null
        screenHelper?.dispose(); screenHelper = null
        if (!closed && !restored) state("Video capture unavailable")
        configureCapture()
        applyBitrates()
        onScreenSharingEnded?.invoke()
        if (!closed) scope.launch { sendMediaState() }
    }
    fun flipCamera() { capturer?.switchCamera(null) }
    private val captureLimits: Triple<Int, Int, Int> get() {
        val tuning = policy.adaptation
        val width = if (tuning != null && quality == 0) tuning.lowWidth else if (tuning != null && quality == 1) tuning.mediumWidth else if (cellular) policy.cellularWidth else policy.wifiWidth
        val height = if (tuning != null && quality == 0) tuning.lowHeight else if (tuning != null && quality == 1) tuning.mediumHeight else if (cellular) policy.cellularHeight else policy.wifiHeight
        val fps = if (tuning != null && quality == 0) tuning.lowFps else if (tuning != null && quality == 1) tuning.mediumFps else if (cellular) policy.cellularFps else policy.wifiFps
        return Triple(minOf(width, if (cellular) policy.cellularWidth else policy.wifiWidth), minOf(height, if (cellular) policy.cellularHeight else policy.wifiHeight), minOf(fps, if (cellular) policy.cellularFps else policy.wifiFps))
    }
    private fun configureCapture() {
        if (closed) return
        if (screenCapturer != null) {
            screenContentSize?.let { (width, height) ->
                val size = screenSize(width, height)
                screenDimensions = size
                screenCapturer?.changeCaptureFormat(size.first, size.second, screenLimits.fps)
            }
            return
        }
        if (background || !video || localVideo?.enabled() != true) return
        val (width, height, fps) = captureLimits
        if (captureStarted) capturer?.changeCaptureFormat(width, height, fps)
        else { capturer?.startCapture(width, height, fps); captureStarted = capturer != null }
    }
    private val videoBitrate: Int get() = when (quality) {
        0 -> policy.adaptation?.lowBitrate ?: policy.videoBitrate
        1 -> policy.adaptation?.mediumBitrate ?: policy.videoBitrate
        else -> policy.videoBitrate
    }
    private fun applyBitrates() {
        peer.senders.forEach { sender ->
            val audio = sender.track()?.kind() == "audio"
            val parameters = sender.parameters
            if (!audio) parameters.degradationPreference = if (screenCapturer != null)
                RtpParameters.DegradationPreference.MAINTAIN_RESOLUTION else RtpParameters.DegradationPreference.BALANCED
            parameters.encodings.forEach {
                it.bitratePriority = if (audio) policy.adaptation?.audioPriority ?: 1.0 else 1.0
                val sharing = screenCapturer != null
                val videoBudget = if (sharing) policy.screenShare?.videoBudget(screenLimits.bitrate, availableBandwidth, policy.audioBitrate)
                    ?: screenLimits.bitrate else videoBitrate
                it.active = audio || videoBudget > 0
                it.maxBitrateBps = if (audio) policy.audioBitrate else videoBudget.coerceAtLeast(1)
                if (!audio) it.maxFramerate = if (sharing) screenLimits.fps else captureLimits.third
            }
            sender.parameters = parameters
        }
    }
    private fun audioFailure(message: String) { scope.launch { if (!closed) state(message) } }
    private suspend fun sendMediaState(request: Boolean = false) {
        if (closed || !connected || policy.screenShare == null) return
        runCatching { signal("media-state", json.encodeToString(LegendCallMediaState(screenCapturer != null, request)), epoch) }
    }
    private fun observeAudio(report: RTCStatsReport) {
        if (audioObservationCount >= 6) return
        val counters = mutableMapOf<String, Long>()
        for (stat in report.statsMap.values) {
            if (stat.members["kind"] != "audio" && stat.members["mediaType"] != "audio") continue
            val incoming = stat.type == "inbound-rtp"
            if (!incoming && stat.type != "outbound-rtp") continue
            for ((field, label) in if (incoming) listOf("bytesReceived" to "audioInboundBytesDelta", "packetsReceived" to "audioInboundPacketsDelta")
                else listOf("bytesSent" to "audioOutboundBytesDelta", "packetsSent" to "audioOutboundPacketsDelta")) {
                (stat.members[field] as? Number)?.toLong()?.let { counters[label] = (counters[label] ?: 0L) + it }
            }
        }
        val previous = previousAudioCounters
        previousAudioCounters = counters
        if (previous == null) return
        audioObservationCount++
        val deltas = listOf("audioInboundBytesDelta", "audioOutboundBytesDelta", "audioInboundPacketsDelta", "audioOutboundPacketsDelta").joinToString(" ") { name ->
            val current = counters[name]; val old = previous[name]
            "$name=${if (current != null && old != null && current >= old) (current - old).toString() else "unknown"}"
        }
        audioObservation("$deltas localAudioTrackEnabled=${audioTrack.enabled()}")
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
        closed = true
        qualityMonitor?.cancel(); qualityMonitor = null; recovery?.cancel(); recovery = null
        stopScreenSharing()
        runCatching { connectivity.unregisterNetworkCallback(network) }
        audioTrack.setEnabled(false); localVideo?.setEnabled(false)
        runCatching { capturer?.stopCapture() }; capturer?.dispose(); capturer = null
        peer.close(); peer.dispose()
        audioTrack.dispose(); audioSource.dispose(); localVideo?.dispose(); videoSource?.dispose(); surfaceHelper?.dispose()
        factory.dispose(); audioDeviceModule.release(); egl.release()
        candidates.clear(); outgoing.clear()
    }
    @Serializable private data class Candidate(val candidate: String, val sdpMid: String? = null, val sdpMLineIndex: Int)
}
