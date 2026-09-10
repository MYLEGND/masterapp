import AVFoundation
import Network
import ReplayKit
import UIKit
@preconcurrency import WebRTC

@MainActor
final class LegendRTCPeer: NSObject, RTCPeerConnectionDelegate {
    var onSignal: ((String, String, Int) async throws -> Void)?
    var onState: ((String) -> Void)?
    var onRemoteVideo: ((RTCVideoTrack) -> Void)?
    private(set) var localVideo: RTCVideoTrack?
    private let factory = RTCPeerConnectionFactory(encoderFactory: RTCDefaultVideoEncoderFactory(), decoderFactory: RTCDefaultVideoDecoderFactory())
    private var peer: RTCPeerConnection?
    private var capturer: RTCCameraVideoCapturer?
    private var videoSource: RTCVideoSource?
    private var sharingScreen = false
    private var cameraWasEnabled = true
    var onScreenSharingEnded: (() -> Void)?
    private var audio: RTCAudioTrack?
    private var remoteCandidates: [(Int, RTCIceCandidate)] = []
    private var localCandidates: [RTCIceCandidate] = []
    private var localSignalReady = false
    private var negotiationInProgress = false
    private var epoch = 0
    private var remoteEpoch = -1
    private var closed = false
    private let caller: Bool
    private let policy: LegendCallPolicy
    private let monitor = NWPathMonitor()
    private var cellular = false
    private var frontCamera = true
    private var recoveryTask: Task<Void, Never>?
    private var connected = false
    private var observers: [NSObjectProtocol] = []

    init(policy: LegendCallPolicy, video: Bool, caller: Bool) throws {
        self.policy = policy
        self.caller = caller
        super.init()
        let config = RTCConfiguration()
        config.iceServers = policy.stunUrls.map { RTCIceServer(urlStrings: [$0]) }
        config.sdpSemantics = .unifiedPlan
        config.bundlePolicy = .maxBundle
        config.continualGatheringPolicy = .gatherContinually
        guard let connection = factory.peerConnection(with: config, constraints: RTCMediaConstraints(mandatoryConstraints: nil, optionalConstraints: nil), delegate: self) else {
            throw LegendCallingError.unavailable("The call engine could not start.")
        }
        peer = connection
        let source = factory.audioSource(with: RTCMediaConstraints(mandatoryConstraints: nil, optionalConstraints: nil))
        audio = factory.audioTrack(with: source, trackId: "legend-audio")
        if let audio { connection.add(audio, streamIds: ["legend"]) }
        if video {
            let videoSource = factory.videoSource()
            self.videoSource = videoSource
            capturer = RTCCameraVideoCapturer(delegate: videoSource)
            localVideo = factory.videoTrack(with: videoSource, trackId: "legend-video")
            if let localVideo { connection.add(localVideo, streamIds: ["legend"]) }
            configureCamera()
        }
        applyBitrates()
        monitor.pathUpdateHandler = { [weak self] path in
            Task { @MainActor in
                guard let self, !self.closed else { return }
                let changed = self.cellular != path.usesInterfaceType(.cellular)
                self.cellular = path.usesInterfaceType(.cellular)
                self.applyBitrates()
                if changed { self.configureCamera(); if self.connected { self.recover() } }
            }
        }
        monitor.start(queue: DispatchQueue(label: "legend.call.network"))
        observers.append(NotificationCenter.default.addObserver(forName: UIApplication.didEnterBackgroundNotification, object: nil, queue: .main) { [weak self] _ in
            Task { @MainActor in self?.stopScreenSharing(); self?.capturer?.stopCapture() }
        })
        observers.append(NotificationCenter.default.addObserver(forName: UIApplication.didBecomeActiveNotification, object: nil, queue: .main) { [weak self] _ in
            Task { @MainActor in self?.configureCamera() }
        })
    }

    func offer(restart: Bool = false) async throws {
        guard let peer, !closed, !negotiationInProgress, caller else { return }
        negotiationInProgress = true
        defer { negotiationInProgress = false }
        epoch += 1
        localSignalReady = false
        localCandidates.removeAll()
        let constraints = RTCMediaConstraints(mandatoryConstraints: restart ? ["IceRestart": "true"] : nil, optionalConstraints: nil)
        let description: RTCSessionDescription = try await withCheckedThrowingContinuation { continuation in
            peer.offer(for: constraints) { value, error in
                if let value { continuation.resume(returning: value) }
                else { continuation.resume(throwing: error ?? LegendCallingError.unavailable("Could not negotiate the call.")) }
            }
        }
        try await setLocal(description)
        try await onSignal?("offer", description.sdp, epoch)
        localSignalReady = true
        try await flushLocalCandidates()
    }

    func receive(kind: String, data: String, epoch incomingEpoch: Int) async throws {
        guard let peer, !closed else { return }
        if kind == "restart" { if caller { recover() }; return }
        if kind == "candidate" {
            guard incomingEpoch >= epoch else { return }
            let candidate = try JSONDecoder().decode(Candidate.self, from: Data(data.utf8))
            let ice = RTCIceCandidate(sdp: candidate.candidate, sdpMLineIndex: candidate.sdpMLineIndex, sdpMid: candidate.sdpMid)
            if remoteEpoch == incomingEpoch { try await addCandidate(ice) }
            else { remoteCandidates.append((incomingEpoch, ice)) }
            return
        }
        guard incomingEpoch >= epoch else { return }
        epoch = incomingEpoch
        if kind == "offer" {
            guard !caller else { return }
            localSignalReady = false
            localCandidates.removeAll()
            try await setRemote(RTCSessionDescription(type: .offer, sdp: data))
            let answer: RTCSessionDescription = try await withCheckedThrowingContinuation { continuation in
                peer.answer(for: RTCMediaConstraints(mandatoryConstraints: nil, optionalConstraints: nil)) { value, error in
                    if let value { continuation.resume(returning: value) }
                    else { continuation.resume(throwing: error ?? LegendCallingError.unavailable("Could not answer the call.")) }
                }
            }
            try await setLocal(answer)
            try await onSignal?("answer", answer.sdp, epoch)
            localSignalReady = true
            try await flushLocalCandidates()
        } else if kind == "answer", caller {
            try await setRemote(RTCSessionDescription(type: .answer, sdp: data))
        }
    }

    func recover() {
        guard !closed, recoveryTask == nil else { return }
        onState?("Reconnecting")
        recoveryTask = Task { [weak self] in
            guard let self else { return }
            for attempt in 0..<self.policy.recoveryAttempts {
                do {
                    try await Task.sleep(for: .seconds(attempt == 0 ? 2 : 5))
                    guard !self.closed, !Task.isCancelled else { return }
                    if self.caller { try await self.offer(restart: true) }
                    else { try await self.onSignal?("restart", "", self.epoch) }
                    try await Task.sleep(for: .seconds(6))
                    if self.connected { self.recoveryTask = nil; return }
                } catch is CancellationError { return }
                catch { /* Retry only within the bounded recovery window. */ }
            }
            self.recoveryTask = nil
            self.onState?("Direct connection unavailable")
        }
    }

    func startScreenSharing() async throws {
        guard !closed, !sharingScreen, let source = videoSource else { return }
        sharingScreen = true
        cameraWasEnabled = localVideo?.isEnabled ?? true
        localVideo?.isEnabled = true
        source.adaptOutputFormat(toWidth: Int32(cellular ? policy.cellularWidth : policy.wifiWidth), height: Int32(cellular ? policy.cellularHeight : policy.wifiHeight), fps: 15)
        await capturer?.stopCapture()
        let capture = RTCVideoCapturer(delegate: source)
        do {
            try await withCheckedThrowingContinuation { (continuation: CheckedContinuation<Void, Error>) in
                RPScreenRecorder.shared().startCapture(handler: { [weak self] buffer, type, error in
                    if error != nil {
                        Task { @MainActor in self?.stopScreenSharing() }
                        return
                    }
                    guard type == .video, let pixels = CMSampleBufferGetImageBuffer(buffer) else { return }
                    let time = Int64(CMTimeGetSeconds(CMSampleBufferGetPresentationTimeStamp(buffer)) * 1_000_000_000)
                    let orientation = (CMGetAttachment(buffer, key: RPVideoSampleOrientationKey as CFString, attachmentModeOut: nil) as? NSNumber)?.uint32Value ?? 1
                    let rotation: RTCVideoRotation = switch CGImagePropertyOrientation(rawValue: orientation) {
                    case .right: ._90
                    case .down: ._180
                    case .left: ._270
                    default: ._0
                    }
                    source.capturer(capture, didCapture: RTCVideoFrame(buffer: RTCCVPixelBuffer(pixelBuffer: pixels), rotation: rotation, timeStampNs: time))
                }, completionHandler: { error in
                    if let error { continuation.resume(throwing: error) } else { continuation.resume() }
                })
            }
            if closed || !sharingScreen { RPScreenRecorder.shared().stopCapture { _ in } }
        } catch {
            sharingScreen = false
            localVideo?.isEnabled = cameraWasEnabled
            configureCamera()
            throw error
        }
    }
    func stopScreenSharing() {
        guard sharingScreen else { return }
        sharingScreen = false
        localVideo?.isEnabled = cameraWasEnabled
        RPScreenRecorder.shared().stopCapture { _ in }
        configureCamera()
        onScreenSharingEnded?()
    }
    func setMuted(_ muted: Bool) { audio?.isEnabled = !muted }
    func setCameraEnabled(_ enabled: Bool) {
        localVideo?.isEnabled = enabled
        if enabled { configureCamera() } else { capturer?.stopCapture() }
    }
    func switchCamera() { frontCamera.toggle(); configureCamera() }
    func close() {
        guard !closed else { return }
        closed = true
        stopScreenSharing()
        recoveryTask?.cancel(); recoveryTask = nil
        monitor.cancel()
        observers.forEach(NotificationCenter.default.removeObserver); observers.removeAll()
        audio?.isEnabled = false; localVideo?.isEnabled = false
        capturer?.stopCapture(); capturer = nil
        peer?.close(); peer = nil
        remoteCandidates.removeAll(); localCandidates.removeAll()
    }

    private func configureCamera() {
        guard !closed, !sharingScreen, let capturer, localVideo?.isEnabled == true, UIApplication.shared.applicationState != .background else { return }
        let position: AVCaptureDevice.Position = frontCamera ? .front : .back
        guard let device = RTCCameraVideoCapturer.captureDevices().first(where: { $0.position == position }) else { return }
        let width = cellular ? policy.cellularWidth : policy.wifiWidth
        let height = cellular ? policy.cellularHeight : policy.wifiHeight
        let fps = cellular ? policy.cellularFps : policy.wifiFps
        videoSource?.adaptOutputFormat(toWidth: Int32(width), height: Int32(height), fps: Int32(fps))
        let formats = RTCCameraVideoCapturer.supportedFormats(for: device)
        guard let format = formats.filter({
            let size = CMVideoFormatDescriptionGetDimensions($0.formatDescription)
            return size.width <= width && size.height <= height
        }).max(by: {
            let a = CMVideoFormatDescriptionGetDimensions($0.formatDescription)
            let b = CMVideoFormatDescriptionGetDimensions($1.formatDescription)
            return a.width * a.height < b.width * b.height
        }) else { return }
        let supportedFps = Int(format.videoSupportedFrameRateRanges.map(\.maxFrameRate).max() ?? Double(fps))
        capturer.startCapture(with: device, format: format, fps: min(fps, supportedFps))
    }

    private func applyBitrates() {
        for sender in peer?.senders ?? [] {
            let parameters = sender.parameters
            for encoding in parameters.encodings {
                encoding.maxBitrateBps = NSNumber(value: sender.track?.kind == "audio" ? policy.audioBitrate : policy.videoBitrate)
            }
            sender.parameters = parameters
        }
    }
    private func setLocal(_ description: RTCSessionDescription) async throws {
        guard let peer, !closed else { throw CancellationError() }
        try await withCheckedThrowingContinuation { (c: CheckedContinuation<Void, Error>) in
            peer.setLocalDescription(description) { error in if let error { c.resume(throwing: error) } else { c.resume() } }
        }
    }
    private func setRemote(_ description: RTCSessionDescription) async throws {
        guard let peer, !closed else { throw CancellationError() }
        try await withCheckedThrowingContinuation { (c: CheckedContinuation<Void, Error>) in
            peer.setRemoteDescription(description) { error in if let error { c.resume(throwing: error) } else { c.resume() } }
        }
        remoteEpoch = epoch
        let waiting = remoteCandidates.filter { $0.0 == epoch }
        remoteCandidates.removeAll { $0.0 <= epoch }
        for (_, candidate) in waiting { try await addCandidate(candidate) }
    }
    private func addCandidate(_ candidate: RTCIceCandidate) async throws {
        guard let peer, !closed else { throw CancellationError() }
        try await withCheckedThrowingContinuation { (c: CheckedContinuation<Void, Error>) in
            peer.add(candidate) { error in if let error { c.resume(throwing: error) } else { c.resume() } }
        }
    }
    private func flushLocalCandidates() async throws {
        let pending = localCandidates; localCandidates.removeAll()
        for candidate in pending { try await sendCandidate(candidate) }
    }
    private func sendCandidate(_ candidate: RTCIceCandidate) async throws {
        let data = try JSONEncoder().encode(Candidate(candidate: candidate.sdp, sdpMid: candidate.sdpMid, sdpMLineIndex: candidate.sdpMLineIndex))
        try await onSignal?("candidate", String(decoding: data, as: UTF8.self), epoch)
    }
    private struct Candidate: Codable { let candidate: String; let sdpMid: String?; let sdpMLineIndex: Int32 }

    nonisolated func peerConnection(_ peerConnection: RTCPeerConnection, didGenerate candidate: RTCIceCandidate) {
        Task { @MainActor in
            guard !self.closed else { return }
            if self.localSignalReady { do { try await self.sendCandidate(candidate) } catch { self.recover() } }
            else { self.localCandidates.append(candidate) }
        }
    }
    nonisolated func peerConnection(_ peerConnection: RTCPeerConnection, didChange newState: RTCIceConnectionState) {
        Task { @MainActor in
            guard !self.closed else { return }
            switch newState {
            case .connected, .completed:
                self.connected = true; self.recoveryTask?.cancel(); self.recoveryTask = nil; self.onState?("Connected")
            case .disconnected, .failed: self.connected = false; self.recover()
            case .closed: self.connected = false
            default: break
            }
        }
    }
    nonisolated func peerConnection(_ peerConnection: RTCPeerConnection, didAdd rtpReceiver: RTCRtpReceiver, streams: [RTCMediaStream]) {
        if let video = rtpReceiver.track as? RTCVideoTrack { Task { @MainActor in self.onRemoteVideo?(video) } }
    }
    nonisolated func peerConnection(_ peerConnection: RTCPeerConnection, didChange stateChanged: RTCSignalingState) {}
    nonisolated func peerConnection(_ peerConnection: RTCPeerConnection, didAdd stream: RTCMediaStream) {}
    nonisolated func peerConnection(_ peerConnection: RTCPeerConnection, didRemove stream: RTCMediaStream) {}
    nonisolated func peerConnectionShouldNegotiate(_ peerConnection: RTCPeerConnection) {}
    nonisolated func peerConnection(_ peerConnection: RTCPeerConnection, didChange newState: RTCIceGatheringState) {}
    nonisolated func peerConnection(_ peerConnection: RTCPeerConnection, didRemove candidates: [RTCIceCandidate]) {}
    nonisolated func peerConnection(_ peerConnection: RTCPeerConnection, didOpen dataChannel: RTCDataChannel) {}
}
