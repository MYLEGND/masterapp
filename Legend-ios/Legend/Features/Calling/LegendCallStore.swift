import AVFoundation
import CallKit
import Combine
import UIKit
import os
@preconcurrency import WebRTC

@MainActor
final class LegendCallStore: NSObject, ObservableObject, CXProviderDelegate {
    @Published private(set) var current: LegendCallSnapshot?
    @Published private(set) var status = ""
    @Published private(set) var failure: String?
    @Published private(set) var localVideo: RTCVideoTrack?
    @Published private(set) var remoteVideo: RTCVideoTrack?
    @Published private(set) var muted = false
    @Published private(set) var cameraEnabled = true
    @Published private(set) var speaker = false
    @Published private(set) var sharingScreen = false
    @Published var minimized = false
    @Published var controlError: String?
    let deviceId: UUID
    private let identity: LogicalParticipantIdentity
    private let transport: MobileMessagingRealtimeClient
    private let provider: CXProvider
    private let controller = CXCallController()
    private var policy: LegendCallPolicy?
    private var peer: LegendRTCPeer?
    private var ringback: AVAudioPlayer?
    private var audioActive = false
    private let callLog = Logger(subsystem: "com.mylegnd.legend.registered", category: "calling")
    private var reconciliation: Task<Void, Never>?
    private var startupDeadline: Task<Void, Never>?
    private var heartbeat: Task<Void, Never>?
    private var deadline: Task<Void, Never>?
    @Published private var pendingOutgoing: (UUID, UUID, Bool)?
    private var outgoingName = ""
    var isStarting: Bool { pendingOutgoing != nil && current == nil }
    private var reportedCalls = Set<UUID>()
    private var finishedCalls = Set<UUID>()
    private var requestingScreenShare = false
    private var stopped = false
    private var shutdownTask: Task<Void, Never>?
    private var audioObservers: [NSObjectProtocol] = []
    var isCaller: Bool { current.map { isCallerAccount($0) && $0.callerDeviceId == deviceId } ?? false }
    var incoming: Bool { current?.status == "ringing" && !isCaller }
    var name: String { if isStarting { return outgoingName }; return isCaller ? current?.calleeName ?? "" : current?.callerName ?? "" }

    init(transport: MobileMessagingRealtimeClient, identity: LogicalParticipantIdentity) {
        self.transport = transport
        self.identity = identity
        let defaults = UserDefaults.standard
        let saved = defaults.string(forKey: "legend.call.device")
        deviceId = saved.flatMap(UUID.init(uuidString:)) ?? UUID()
        defaults.set(deviceId.uuidString, forKey: "legend.call.device")
        provider = LegendCallSystem.shared.provider
        super.init()
        LegendCallSystem.shared.attach(self)
        RTCAudioSession.sharedInstance().useManualAudio = true
        transport.onCall = { [weak self] event in Task { await self?.receive(event) } }
        transport.onCallReconnect = { [weak self] in Task { await self?.sync() } }
        audioObservers.append(NotificationCenter.default.addObserver(forName: AVAudioSession.routeChangeNotification, object: nil, queue: .main) { [weak self] _ in
            Task { @MainActor in self?.updateProximity() }
        })
        audioObservers.append(NotificationCenter.default.addObserver(forName: AVAudioSession.interruptionNotification, object: nil, queue: .main) { [weak self] note in
            Task { @MainActor in
                guard let self, self.current != nil else { return }
                if (note.userInfo?[AVAudioSessionInterruptionTypeKey] as? UInt) == AVAudioSession.InterruptionType.began.rawValue {
                    self.status = "Audio interrupted"
                } else { self.status = "Reconnecting"; self.peer?.recover() }
            }
        })
    }

    func start(conversationId: UUID, video: Bool, recipientName: String = "") {
        guard !stopped, current == nil, pendingOutgoing == nil else { return }
        let id = UUID()
        failure = nil
        minimized = false
        outgoingName = recipientName
        status = "Calling"
        // Publish before any permission prompt, network request, or CallKit work.
        pendingOutgoing = (id, conversationId, video)
        Task {
            do {
                guard pendingOutgoing?.0 == id, !stopped else { return }
                try await permissions(video: video)
                guard pendingOutgoing?.0 == id, !stopped else { return }
                try AVAudioSession.sharedInstance().setCategory(.playAndRecord, mode: video ? .videoChat : .voiceChat,
                    options: video ? [.allowBluetoothHFP, .defaultToSpeaker] : [.allowBluetoothHFP])
                startupDeadline = Task { [weak self] in
                    try? await Task.sleep(for: .seconds(25))
                    guard !Task.isCancelled, let self, self.pendingOutgoing?.0 == id else { return }
                    self.failure = "The call could not start in time. Please try again."
                    self.provider.reportCall(with: id, endedAt: Date(), reason: .failed)
                    self.clear()
                }
                let action = CXStartCallAction(call: id, handle: CXHandle(type: .generic, value: recipientName.isEmpty ? "Legend" : recipientName))
                action.isVideo = video
                try await controller.request(CXTransaction(action: action))
            } catch {
                guard pendingOutgoing?.0 == id else { return }
                failure = error.localizedDescription
                clear()
            }
        }
    }
    func answer() {
        guard let current else { return }
        Task { do { try await controller.request(CXTransaction(action: CXAnswerCallAction(call: current.id))) } catch { failure = error.localizedDescription } }
    }
    func end() {
        if let pending = pendingOutgoing {
            provider.reportCall(with: pending.0, endedAt: Date(), reason: .remoteEnded)
            clear()
            Task { _ = try? await transport.call(LegendCallCommand(action: "end", deviceId: deviceId, callId: pending.0), existingConnectionOnly: true) }
            return
        }
        guard let current else { clear(); return }
        Task { do { try await controller.request(CXTransaction(action: CXEndCallAction(call: current.id))) } catch { await finish(current.id) } }
    }
    func systemTimedOut(_ id: UUID) {
        if current?.id == id { fail("The call could not start in time. Please try again.") }
        else if pendingOutgoing?.0 == id { failure = "The call could not start in time. Please try again."; clear() }
    }
    func dismissFailure() { failure = nil }
    func setMuted() {
        guard let current else { return }
        Task { try? await controller.request(CXTransaction(action: CXSetMutedCallAction(call: current.id, muted: !muted))) }
    }
    func toggleCamera() { cameraEnabled.toggle(); peer?.setCameraEnabled(cameraEnabled) }
    func toggleScreenSharing() {
        guard let peer, current?.video == true, !requestingScreenShare else { return }
        if sharingScreen { peer.stopScreenSharing(); sharingScreen = false; minimized = false; return }
        requestingScreenShare = true
        Task {
            defer { requestingScreenShare = false }
            do {
                try await peer.startScreenSharing()
                guard current != nil else { peer.stopScreenSharing(); return }
                sharingScreen = true; minimized = true
            } catch { controlError = LegendLocalized("Screen sharing could not start. Please try again.") }
        }
    }
    func switchCamera() { peer?.switchCamera() }
    func toggleSpeaker() {
        do {
            speaker.toggle()
            try AVAudioSession.sharedInstance().overrideOutputAudioPort(speaker ? .speaker : .none)
            updateProximity()
        } catch { failure = "The audio route could not be changed." }
    }

    private func permissions(video: Bool) async throws {
        let audio = await withCheckedContinuation { continuation in AVAudioApplication.requestRecordPermission { continuation.resume(returning: $0) } }
        guard audio else { throw LegendCallingError.unavailable("Allow microphone access in Settings to join a call.") }
        if video {
            let camera = await AVCaptureDevice.requestAccess(for: .video)
            guard camera else { throw LegendCallingError.unavailable("Allow camera access in Settings to join a video call.") }
        }
    }
    private func send(_ command: LegendCallCommand) async throws -> LegendCallResult {
        guard !stopped else { throw CancellationError() }
        callLog.info("Call command: \(command.action, privacy: .public)")
        let result = try await transport.call(command)
        callLog.info("Call result: \(command.action, privacy: .public), success=\(result.succeeded)")
        guard !stopped else { throw CancellationError() }
        guard result.succeeded else { throw LegendCallingError.unavailable(result.error ?? "Call unavailable.") }
        if let policy = result.policy { self.policy = policy }
        return result
    }
    private func isCallerAccount(_ call: LegendCallSnapshot) -> Bool {
        call.callerType == identity.participantType.rawValue && (call.callerUserIds ?? [call.callerUserId]).contains { $0.caseInsensitiveCompare(identity.userID) == .orderedSame }
    }
    private func isCalleeAccount(_ call: LegendCallSnapshot) -> Bool {
        call.calleeType == identity.participantType.rawValue && (call.calleeUserIds ?? [call.calleeUserId]).contains { $0.caseInsensitiveCompare(identity.userID) == .orderedSame }
    }
    func owns(_ call: LegendCallSnapshot) -> Bool { isCallerAccount(call) || isCalleeAccount(call) }
    func registerVoipToken(_ token: String?) {
        guard !stopped, let token, let environment = LegendAPNSEnvironment.fromSignedEntitlement(Bundle.main.object(forInfoDictionaryKey: "LegendAPNSEnvironment") as? String) else { return }
        Task { _ = try? await send(LegendCallCommand(action: "register-voip", deviceId: deviceId, pushToken: token, pushEnvironment: environment.rawValue)) }
    }
    private func sync() async {
        registerVoipToken(LegendCallSystem.shared.token)
        do {
            let result = try await send(LegendCallCommand(action: "sync", deviceId: deviceId))
            if let active = current {
                if let fresh = result.activeCalls?.first(where: { $0.id == active.id }) {
                    current = fresh
                    if fresh.status != "ringing" { peer?.recover() }
                } else { provider.reportCall(with: active.id, endedAt: Date(), reason: .remoteEnded); clear() }
            } else if let incoming = result.activeCalls?.first(where: {
                isCalleeAccount($0) && $0.status == "ringing"
            }) { await receive(LegendCallEvent(call: incoming, signalKind: nil, signalData: nil, fromDeviceId: nil, toDeviceId: nil)) }
        } catch { if current != nil { status = "Reconnecting" } }
    }

    func receive(_ event: LegendCallEvent) async {
        let call = event.call
        guard !stopped, owns(call), !finishedCalls.contains(call.id) else { return }
        if let pendingOutgoing, pendingOutgoing.0 != call.id { return }
        if current == nil && call.status != "ringing" && pendingOutgoing?.0 != call.id { return }
        if current?.id == call.id && current?.status != "ringing" && call.status == "ringing" { return }
        guard event.toDeviceId == nil || event.toDeviceId == deviceId else { return }
        if let current, current.id != call.id { return }
        let callerAccount = isCallerAccount(call)
        if callerAccount && call.callerDeviceId != deviceId { return }
        if let answered = call.calleeDeviceId, !callerAccount, answered != deviceId {
            if current?.id == call.id { provider.reportCall(with: call.id, endedAt: Date(), reason: .answeredElsewhere); clear() }
            return
        }
        if call.terminal {
            if current?.id == call.id || pendingOutgoing?.0 == call.id {
                if callerAccount { failure = call.failureMessage }
                provider.reportCall(with: call.id, endedAt: Date(), reason: call.status == "declined" ? .declinedElsewhere : .remoteEnded)
                clear()
            }
            return
        }
        if current?.id == call.id && current?.receivedUtc != nil && call.receivedUtc == nil && call.status == "ringing" { return }
        current = call
        callLog.info("Call event: \(call.status, privacy: .public), received=\(call.receivedUtc != nil)")
        startReconciliation(call.id)
        if call.status == "ringing" {
            status = callerAccount ? (call.receivedUtc == nil ? "Calling" : "Ringing") : "Incoming call"
            updateRingback()
            if !callerAccount && !reportedCalls.contains(call.id) {
                reportedCalls.insert(call.id)
                do {
                    try await LegendCallSystem.shared.report(call)
                    guard current?.id == call.id, current?.status == "ringing" else { return }
                    _ = try await send(LegendCallCommand(action: "received", deviceId: deviceId, callId: call.id))
                } catch {
                    callLog.error("Incoming presentation/receipt failed: \(String(describing: error), privacy: .private)")
                    failure = "The incoming call could not be presented or confirmed. Please try again."
                    provider.reportCall(with: call.id, endedAt: Date(), reason: .failed)
                    clear() // A failure on this device must not decline another receiving device.
                }
            }
            armDeadline(call.id, until: call.expiresUtc)
            return
        }
        updateRingback()
        if let kind = event.signalKind, let data = event.signalData {
            do { try await ensurePeer(); try await peer?.receive(kind: kind, data: data, epoch: call.epoch) }
            catch { fail("The direct connection could not be established.") }
        } else if callerAccount && peer == nil {
            do { try await ensurePeer(); try await peer?.offer() }
            catch { fail(error.localizedDescription) }
        }
    }

    private func ensurePeer() async throws {
        guard peer == nil, let call = current else { return }
        if policy == nil { _ = try await send(LegendCallCommand(action: "get", deviceId: deviceId, callId: call.id)) }
        guard let policy, current?.id == call.id else { throw CancellationError() }
        ringback?.stop(); ringback = nil
        status = "Connecting"
        speaker = call.video
        let audioSession = AVAudioSession.sharedInstance()
        try audioSession.setCategory(.playAndRecord, mode: call.video ? .videoChat : .voiceChat, options: call.video ? [.allowBluetoothHFP, .defaultToSpeaker] : [.allowBluetoothHFP])
        let engine = try LegendRTCPeer(policy: policy, video: call.video, caller: isCaller)
        engine.onSignal = { [weak self] kind, data, epoch in
            guard let self, self.current?.id == call.id else { throw CancellationError() }
            _ = try await self.send(LegendCallCommand(action: "signal", deviceId: self.deviceId, callId: call.id, signalKind: kind, signalData: data, epoch: epoch))
        }
        engine.onRemoteVideo = { [weak self] track in self?.remoteVideo = track }
        engine.onState = { [weak self] state in
            guard let self, self.current?.id == call.id else { return }
            self.status = state
            if state == "Connected" {
                self.deadline?.cancel()
                if self.isCaller { self.provider.reportOutgoingCall(with: call.id, connectedAt: Date()) }
                Task { _ = try? await self.send(LegendCallCommand(action: "connected", deviceId: self.deviceId, callId: call.id)) }
                self.startHeartbeat(call.id)
            } else if state == "Direct connection unavailable" {
                self.fail("This network could not establish a direct call. Try another Wi-Fi or mobile connection.")
            }
        }
        engine.onScreenSharingEnded = { [weak self] in self?.sharingScreen = false; self?.minimized = false }
        peer = engine; localVideo = engine.localVideo
        updateProximity()
        armDeadline(call.id, until: Date().addingTimeInterval(Double(policy.connectSeconds)))
    }
    func audioActivated(_ active: Bool) {
        audioActive = active
        updateRingback()
    }
    private func updateRingback() {
        guard audioActive, isCaller, current?.status == "ringing", current?.receivedUtc != nil else {
            ringback?.stop(); ringback = nil; return
        }
        guard ringback == nil else { return }
        do {
            guard let url = Bundle.main.url(forResource: "legend_ringback", withExtension: "wav") else {
                throw LegendCallingError.unavailable("The calling sound is unavailable.")
            }
            let player = try AVAudioPlayer(contentsOf: url)
            player.numberOfLoops = -1
            guard player.play() else { throw LegendCallingError.unavailable("The calling sound could not play.") }
            ringback = player
        } catch { controlError = "The recipient received the call, but the ringing sound could not play." }
    }
    private func startReconciliation(_ id: UUID) {
        guard reconciliation == nil else { return }
        reconciliation = Task { [weak self] in
            while !Task.isCancelled {
                try? await Task.sleep(for: .seconds(3))
                guard !Task.isCancelled, let self, self.current?.id == id else { return }
                do {
                    let result = try await self.send(LegendCallCommand(action: "get", deviceId: self.deviceId, callId: id))
                    guard !Task.isCancelled, self.current?.id == id, let call = result.call else { return }
                    await self.receive(LegendCallEvent(call: call, signalKind: nil, signalData: nil, fromDeviceId: nil, toDeviceId: nil))
                } catch {
                    guard !Task.isCancelled, self.current?.id == id else { return }
                    self.fail("The call status could not be confirmed. Please try again.")
                }
            }
        }
    }
    private func startHeartbeat(_ id: UUID) {
        guard heartbeat == nil else { return }
        heartbeat = Task { [weak self] in
            var failures = 0
            while !Task.isCancelled {
                try? await Task.sleep(for: .seconds(25))
                guard !Task.isCancelled, let self, self.current?.id == id else { return }
                do { _ = try await self.send(LegendCallCommand(action: "heartbeat", deviceId: self.deviceId, callId: id)); failures = 0 }
                catch { failures += 1; if failures >= 2 { self.fail("The call session could not be verified. Please call again."); return }; self.peer?.recover() }
            }
        }
    }
    private func armDeadline(_ id: UUID, until date: Date) {
        deadline?.cancel()
        deadline = Task { [weak self] in
            try? await Task.sleep(for: .seconds(max(1, date.timeIntervalSinceNow)))
            guard !Task.isCancelled, let self, self.current?.id == id else { return }
            self.fail(self.current?.status == "ringing" ? (self.current?.receivedUtc == nil && self.isCaller ? "The recipient could not be reached. Their device did not confirm receiving the call." : "The call was not answered.") : "This network could not establish a direct call. Try another Wi-Fi or mobile connection.")
        }
    }
    private func updateProximity() {
        UIDevice.current.isProximityMonitoringEnabled = current != nil && !speaker && AVAudioSession.sharedInstance().currentRoute.outputs.contains { $0.portType == .builtInReceiver }
    }
    private func fail(_ message: String) {
        failure = message
        guard let id = current?.id else { clear(); return }
        provider.reportCall(with: id, endedAt: Date(), reason: .failed)
        Task { await finish(id) }
    }
    private func finish(_ id: UUID) async {
        let decline = current?.status == "ringing" && !isCaller
        clear()
        _ = try? await send(LegendCallCommand(action: decline ? "decline" : "end", deviceId: deviceId, callId: id))
    }
    private func clear() {
        if let pendingOutgoing { finishedCalls.insert(pendingOutgoing.0); LegendCallSystem.shared.finished(pendingOutgoing.0) }
        if let current { finishedCalls.insert(current.id); LegendCallSystem.shared.finished(current.id) }
        ringback?.stop(); ringback = nil
        reconciliation?.cancel(); reconciliation = nil
        startupDeadline?.cancel(); startupDeadline = nil
        peer?.close(); peer = nil
        heartbeat?.cancel(); heartbeat = nil; deadline?.cancel(); deadline = nil
        localVideo = nil; remoteVideo = nil; current = nil; pendingOutgoing = nil
        muted = false; cameraEnabled = true; speaker = false
        sharingScreen = false; minimized = false; controlError = nil
        UIDevice.current.isProximityMonitoringEnabled = false
        RTCAudioSession.sharedInstance().isAudioEnabled = false
    }
    func unregisterVoipToken(_ token: String?) {
        guard let token, let environment = LegendAPNSEnvironment.fromSignedEntitlement(Bundle.main.object(forInfoDictionaryKey: "LegendAPNSEnvironment") as? String) else { return }
        Task { _ = try? await transport.call(LegendCallCommand(action: "unregister-voip", deviceId: deviceId, pushToken: token, pushEnvironment: environment.rawValue), existingConnectionOnly: true) }
    }
    func shutdown() {
        guard !stopped else { return }
        stopped = true
        transport.retireAccountConnection()
        let call = current
        let callId = call?.id ?? pendingOutgoing?.0
        let token = LegendCallSystem.shared.token
        let environment = LegendAPNSEnvironment.fromSignedEntitlement(Bundle.main.object(forInfoDictionaryKey: "LegendAPNSEnvironment") as? String)
        if let call { provider.reportCall(with: call.id, endedAt: Date(), reason: .remoteEnded) }
        clear(); LegendCallSystem.shared.detach(self)
        audioObservers.forEach(NotificationCenter.default.removeObserver); audioObservers.removeAll()
        shutdownTask = Task { [transport, deviceId] in
            // Never reconnect with another account's token during teardown.
            if let callId {
                _ = try? await transport.call(LegendCallCommand(action: call?.status == "ringing" && call?.callerDeviceId != deviceId ? "decline" : "end", deviceId: deviceId, callId: callId), existingConnectionOnly: true)
            }
            if let token, let environment {
                _ = try? await transport.call(LegendCallCommand(action: "unregister-voip", deviceId: deviceId, pushToken: token, pushEnvironment: environment.rawValue), existingConnectionOnly: true)
            }
            transport.stop()
        }
    }
    func awaitShutdown() async { await shutdownTask?.value }

    nonisolated func providerDidReset(_ provider: CXProvider) { Task { @MainActor in if let id = self.current?.id { await self.finish(id) } else { self.clear() } } }
    nonisolated func provider(_ provider: CXProvider, perform action: CXStartCallAction) {
        Task { @MainActor in
            guard let pending = self.pendingOutgoing, pending.0 == action.callUUID else { action.fail(); return }
            // CallKit's start action confirms local readiness, not a remote network round trip.
            action.fulfill()
            provider.reportOutgoingCall(with: pending.0, startedConnectingAt: Date())
            do {
                let result = try await self.send(LegendCallCommand(action: "invite", deviceId: self.deviceId, callId: pending.0, conversationId: pending.1, video: pending.2))
                guard self.pendingOutgoing?.0 == pending.0, !self.stopped else {
                    _ = try? await self.transport.call(LegendCallCommand(action: "end", deviceId: self.deviceId, callId: pending.0), existingConnectionOnly: true)
                    return
                }
                guard let call = result.call else { throw LegendCallingError.unavailable("The call could not start. Please try again.") }
                self.pendingOutgoing = nil
                self.startupDeadline?.cancel(); self.startupDeadline = nil
                self.reportedCalls.insert(call.id)
                await self.receive(LegendCallEvent(call: call, signalKind: nil, signalData: nil, fromDeviceId: nil, toDeviceId: nil))
                let update = CXCallUpdate()
                update.localizedCallerName = call.calleeName
                update.remoteHandle = CXHandle(type: .generic, value: call.calleeName)
                update.hasVideo = call.video
                provider.reportCall(with: call.id, updated: update)

            } catch {
                guard self.pendingOutgoing?.0 == pending.0 else { return }
                provider.reportCall(with: pending.0, endedAt: Date(), reason: .failed)
                self.failure = error.localizedDescription; self.clear()
            }
        }
    }
    nonisolated func provider(_ provider: CXProvider, perform action: CXAnswerCallAction) {
        Task { @MainActor in
            guard let call = self.current, call.id == action.callUUID else { action.fail(); return }
            do {
                try await self.permissions(video: call.video)
                guard self.current?.id == call.id else { action.fail(); return }
                try await self.ensurePeer()
                let result = try await self.send(LegendCallCommand(action: "accept", deviceId: self.deviceId, callId: call.id))
                self.current = result.call; action.fulfill()
            } catch { action.fail(); self.fail(error.localizedDescription) }
        }
    }
    nonisolated func provider(_ provider: CXProvider, perform action: CXEndCallAction) {
        Task { @MainActor in
            action.fulfill()
            guard self.current?.id == action.callUUID || self.pendingOutgoing?.0 == action.callUUID else { return }
            await self.finish(action.callUUID)
        }
    }
    nonisolated func provider(_ provider: CXProvider, perform action: CXSetMutedCallAction) {
        Task { @MainActor in self.muted = action.isMuted; self.peer?.setMuted(action.isMuted); action.fulfill() }
    }
}
