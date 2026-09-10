import AVFoundation
import CallKit
import Combine
import UIKit
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
    let deviceId: UUID
    private let identity: LogicalParticipantIdentity
    private let transport: MobileMessagingRealtimeClient
    private let provider: CXProvider
    private let controller = CXCallController()
    private var policy: LegendCallPolicy?
    private var peer: LegendRTCPeer?
    private var heartbeat: Task<Void, Never>?
    private var deadline: Task<Void, Never>?
    private var pendingOutgoing: (UUID, UUID, Bool)?
    private var reportedCalls = Set<UUID>()
    private var finishedCalls = Set<UUID>()
    private var starting = false
    private var stopped = false
    private var shutdownTask: Task<Void, Never>?
    private var audioObservers: [NSObjectProtocol] = []
    var isCaller: Bool { current.map { isCallerAccount($0) && $0.callerDeviceId == deviceId } ?? false }
    var incoming: Bool { current?.status == "ringing" && !isCaller }
    var name: String { isCaller ? current?.calleeName ?? "" : current?.callerName ?? "" }

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

    func start(conversationId: UUID, video: Bool) {
        guard !stopped, !starting, current == nil, pendingOutgoing == nil else { return }
        starting = true
        failure = nil
        Task {
            defer { starting = false }
            do {
                try await permissions(video: video)
                let synced = try await send(LegendCallCommand(action: "sync", deviceId: deviceId))
                policy = synced.policy
                let id = UUID()
                pendingOutgoing = (id, conversationId, video)
                let action = CXStartCallAction(call: id, handle: CXHandle(type: .generic, value: "Legend"))
                action.isVideo = video
                try await controller.request(CXTransaction(action: action))
            } catch { pendingOutgoing = nil; failure = error.localizedDescription }
        }
    }
    func answer() {
        guard let current else { return }
        Task { do { try await controller.request(CXTransaction(action: CXAnswerCallAction(call: current.id))) } catch { failure = error.localizedDescription } }
    }
    func end() {
        guard let current else { clear(); return }
        Task { do { try await controller.request(CXTransaction(action: CXEndCallAction(call: current.id))) } catch { await finish(current.id) } }
    }
    func systemTimedOut(_ id: UUID) {
        if current?.id == id { fail("The call could not start in time. Please try again.") }
        else if pendingOutgoing?.0 == id { pendingOutgoing = nil; failure = "The call could not start in time. Please try again." }
    }
    func dismissFailure() { failure = nil }
    func setMuted() {
        guard let current else { return }
        Task { try? await controller.request(CXTransaction(action: CXSetMutedCallAction(call: current.id, muted: !muted))) }
    }
    func toggleCamera() { cameraEnabled.toggle(); peer?.setCameraEnabled(cameraEnabled) }
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
        let result = try await transport.call(command)
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
            if current?.id == call.id {
                provider.reportCall(with: call.id, endedAt: Date(), reason: call.status == "declined" ? .declinedElsewhere : .remoteEnded)
                clear()
            }
            return
        }
        current = call
        if call.status == "ringing" {
            status = callerAccount ? "Calling" : "Incoming call"
            if !callerAccount && !reportedCalls.contains(call.id) {
                reportedCalls.insert(call.id)
                do { try await LegendCallSystem.shared.report(call) }
                catch { await finish(call.id) }
            }
            armDeadline(call.id, until: call.expiresUtc)
            return
        }
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
        peer = engine; localVideo = engine.localVideo
        updateProximity()
        armDeadline(call.id, until: Date().addingTimeInterval(Double(policy.connectSeconds)))
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
            self.fail(self.current?.status == "ringing" ? "The call was not answered." : "This network could not establish a direct call. Try another Wi-Fi or mobile connection.")
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
        if let current { finishedCalls.insert(current.id); LegendCallSystem.shared.finished(current.id) }
        peer?.close(); peer = nil
        heartbeat?.cancel(); heartbeat = nil; deadline?.cancel(); deadline = nil
        localVideo = nil; remoteVideo = nil; current = nil; pendingOutgoing = nil
        muted = false; cameraEnabled = true; speaker = false
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
            do {
                let result = try await self.send(LegendCallCommand(action: "invite", deviceId: self.deviceId, callId: pending.0, conversationId: pending.1, video: pending.2))
                guard self.pendingOutgoing?.0 == pending.0, !self.stopped else {
                    _ = try? await self.transport.call(LegendCallCommand(action: "end", deviceId: self.deviceId, callId: pending.0), existingConnectionOnly: true)
                    action.fail(); return
                }
                self.pendingOutgoing = nil
                guard let call = result.call else { action.fail(); return }
                self.current = call; self.status = "Calling"; self.reportedCalls.insert(call.id)
                let update = CXCallUpdate()
                update.localizedCallerName = call.calleeName
                update.remoteHandle = CXHandle(type: .generic, value: call.calleeName)
                update.hasVideo = call.video
                provider.reportCall(with: call.id, updated: update)
                provider.reportOutgoingCall(with: call.id, startedConnectingAt: Date())
                self.armDeadline(call.id, until: call.expiresUtc)
                action.fulfill()
            } catch { action.fail(); self.failure = error.localizedDescription; self.clear() }
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
        Task { @MainActor in action.fulfill(); await self.finish(action.callUUID) }
    }
    nonisolated func provider(_ provider: CXProvider, perform action: CXSetMutedCallAction) {
        Task { @MainActor in self.muted = action.isMuted; self.peer?.setMuted(action.isMuted); action.fulfill() }
    }
}
