import CallKit
import PushKit
import UIKit
@preconcurrency import WebRTC

// PushKit must report to CallKit immediately, before session restoration or a
// network request. The authenticated account store then validates/owns the call.
@MainActor
final class LegendCallSystem: NSObject, PKPushRegistryDelegate, CXProviderDelegate {
    static let shared = LegendCallSystem()
    let provider: CXProvider
    private var registry: PKPushRegistry?
    private(set) var token: String?
    private weak var owner: LegendCallStore?
    private var pending: [UUID: LegendCallSnapshot] = [:]
    private var reported = Set<UUID>()
    private var reportCompletions: [UUID: [(Error?) -> Void]] = [:]
    private override init() {
        let configuration = CXProviderConfiguration()
        configuration.supportsVideo = true
        configuration.ringtoneSound = "legend_incoming.wav"
        configuration.maximumCallsPerCallGroup = 1
        configuration.maximumCallGroups = 1
        configuration.supportedHandleTypes = [.generic]
        configuration.includesCallsInRecents = false
        provider = CXProvider(configuration: configuration)
        super.init()
        provider.setDelegate(self, queue: .main)
    }
    func start() {
        guard registry == nil else { return }
        let registry = PKPushRegistry(queue: .main)
        registry.delegate = self
        registry.desiredPushTypes = [.voIP]
        self.registry = registry
    }
    func attach(_ store: LegendCallStore) {
        owner = store
        start()
        store.registerVoipToken(token)
        for call in pending.values where store.owns(call) {
            Task { await store.receive(LegendCallEvent(call: call, signalKind: nil, signalData: nil, fromDeviceId: nil, toDeviceId: nil)) }
        }
    }
    func detach(_ store: LegendCallStore) { if owner === store { owner = nil } }
    func report(_ call: LegendCallSnapshot) async throws {
        try await withCheckedThrowingContinuation { (continuation: CheckedContinuation<Void, Error>) in
            reportIncoming(call) { error in
                if let error { continuation.resume(throwing: error) }
                else { continuation.resume() }
            }
        }
    }
    private func reportIncoming(_ call: LegendCallSnapshot, completion: @escaping (Error?) -> Void) {
        if reported.contains(call.id) { completion(nil); return }
        if reportCompletions[call.id] != nil { reportCompletions[call.id]?.append(completion); return }
        reportCompletions[call.id] = [completion]
        // Called synchronously from PushKit, before authentication or network I/O.
        provider.reportNewIncomingCall(with: call.id, update: Self.update(call)) { error in
            Task { @MainActor in
                guard let callbacks = self.reportCompletions.removeValue(forKey: call.id) else {
                    if error == nil { self.provider.reportCall(with: call.id, endedAt: Date(), reason: .failed) }
                    return
                }
                if error == nil { self.reported.insert(call.id) }
                callbacks.forEach { $0(error) }
            }
        }
        Task { [weak self] in
            try? await Task.sleep(for: .seconds(5))
            guard let self, let callbacks = self.reportCompletions.removeValue(forKey: call.id) else { return }
            self.provider.reportCall(with: call.id, endedAt: Date(), reason: .failed)
            callbacks.forEach { $0(LegendCallingError.unavailable("Incoming call presentation timed out.")) }
        }
    }
    func finished(_ id: UUID) { pending.removeValue(forKey: id) }
    private static func update(_ call: LegendCallSnapshot) -> CXCallUpdate {
        let update = CXCallUpdate()
        update.remoteHandle = CXHandle(type: .generic, value: call.callerName)
        update.localizedCallerName = call.callerName
        update.hasVideo = call.video
        update.supportsGrouping = false; update.supportsUngrouping = false; update.supportsDTMF = false
        return update
    }
    nonisolated func pushRegistry(_ registry: PKPushRegistry, didUpdate pushCredentials: PKPushCredentials, for type: PKPushType) {
        let token = pushCredentials.token.map { String(format: "%02x", $0) }.joined()
        Task { @MainActor in self.token = token; self.owner?.registerVoipToken(token) }
    }
    nonisolated func pushRegistry(_ registry: PKPushRegistry, didInvalidatePushTokenFor type: PKPushType) {
        Task { @MainActor in self.owner?.unregisterVoipToken(self.token); self.token = nil }
    }
    nonisolated func pushRegistry(_ registry: PKPushRegistry, didReceiveIncomingPushWith payload: PKPushPayload, for type: PKPushType, completion: @escaping () -> Void) {
        // PushKit was created on the main queue. Do not perform I/O before the report.
        MainActor.assumeIsolated {
            guard let object = payload.dictionaryPayload["legendCall"],
                  let data = try? JSONSerialization.data(withJSONObject: object),
                  let call = try? JSONDecoder.mobile.decode(LegendCallSnapshot.self, from: data) else {
                let id = UUID()
                provider.reportNewIncomingCall(with: id, update: CXCallUpdate()) { _ in
                    Task { @MainActor in self.provider.reportCall(with: id, endedAt: Date(), reason: .failed); completion() }
                }
                return
            }
            pending[call.id] = call
            reportIncoming(call) { error in
                completion()
                Task { @MainActor in
                    if error != nil || call.expiresUtc <= Date() {
                        self.provider.reportCall(with: call.id, endedAt: Date(), reason: .unanswered); self.finished(call.id); return
                    }
                    if let owner = self.owner, owner.owns(call) {
                        await owner.receive(LegendCallEvent(call: call, signalKind: nil, signalData: nil, fromDeviceId: nil, toDeviceId: nil))
                    }
                }
            }
            Task { [weak self] in
                try? await Task.sleep(for: .seconds(max(1, call.expiresUtc.timeIntervalSinceNow)))
                guard let self, self.pending[call.id] != nil else { return }
                self.provider.reportCall(with: call.id, endedAt: Date(), reason: .unanswered); self.finished(call.id)
            }
        }
    }
    nonisolated func providerDidReset(_ provider: CXProvider) { Task { @MainActor in self.owner?.providerDidReset(provider); self.pending.removeAll() } }
    nonisolated func provider(_ provider: CXProvider, perform action: CXStartCallAction) {
        Task { @MainActor in guard let owner = self.owner else { action.fail(); return }; owner.provider(provider, perform: action) }
    }
    nonisolated func provider(_ provider: CXProvider, perform action: CXAnswerCallAction) {
        Task { @MainActor in
            for _ in 0..<30 {
                if let owner = self.owner, owner.current?.id == action.callUUID { self.finished(action.callUUID); owner.provider(provider, perform: action); return }
                try? await Task.sleep(for: .milliseconds(100))
            }
            action.fail(); provider.reportCall(with: action.callUUID, endedAt: Date(), reason: .failed); self.finished(action.callUUID)
        }
    }
    nonisolated func provider(_ provider: CXProvider, perform action: CXEndCallAction) {
        Task { @MainActor in
            self.finished(action.callUUID)
            if let owner = self.owner, owner.current?.id == action.callUUID { owner.provider(provider, perform: action) }
            else { action.fulfill() }
        }
    }
    nonisolated func provider(_ provider: CXProvider, perform action: CXSetMutedCallAction) {
        Task { @MainActor in guard let owner = self.owner else { action.fail(); return }; owner.provider(provider, perform: action) }
    }
    nonisolated func provider(_ provider: CXProvider, timedOutPerforming action: CXAction) {
        guard let action = action as? CXCallAction else { return }
        Task { @MainActor in self.owner?.systemTimedOut(action.callUUID); self.finished(action.callUUID) }
    }
    nonisolated func provider(_ provider: CXProvider, didActivate audioSession: AVAudioSession) {
        RTCAudioSession.sharedInstance().audioSessionDidActivate(audioSession)
        RTCAudioSession.sharedInstance().isAudioEnabled = true
        Task { @MainActor in self.owner?.audioActivated(true) }
    }
    nonisolated func provider(_ provider: CXProvider, didDeactivate audioSession: AVAudioSession) {
        RTCAudioSession.sharedInstance().isAudioEnabled = false
        Task { @MainActor in self.owner?.audioActivated(false) }
        RTCAudioSession.sharedInstance().audioSessionDidDeactivate(audioSession)
    }
}
