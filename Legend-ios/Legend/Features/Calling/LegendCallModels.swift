import Foundation

struct LegendCallCommand: Encodable {
    var action: String
    var deviceId: UUID
    var callId: UUID? = nil
    var conversationId: UUID? = nil
    var video: Bool = false
    var signalKind: String? = nil
    var signalData: String? = nil
    var epoch: Int = 0
    var pushToken: String? = nil
    var pushEnvironment: String? = nil
}
struct LegendCallPolicy: Decodable {
    let stunUrls: [String]
    let wifiWidth, wifiHeight, wifiFps, cellularWidth, cellularHeight, cellularFps: Int
    let videoBitrate, audioBitrate, ringSeconds, connectSeconds, recoveryAttempts: Int
    var adaptation: LegendCallAdaptationPolicy? = nil
    var relay: LegendCallRelay? = nil
}
struct LegendCallSnapshot: Decodable, Identifiable {
    let id, conversationId: UUID
    let callerUserId, callerType, calleeUserId, calleeType: String
    let callerDeviceId: UUID
    let calleeDeviceId: UUID?
    let callerName, calleeName: String
    let video: Bool
    let status: String
    let createdUtc, expiresUtc: Date
    let epoch: Int
    var callerUserIds: [String]? = nil
    var calleeUserIds: [String]? = nil
    var receivedUtc: Date? = nil
    var failureMessage: String? = nil
    var callerImagePath: String? = nil
    var terminal: Bool { ["ended", "declined", "missed"].contains(status) }
}
struct LegendCallEvent: Decodable {
    let call: LegendCallSnapshot
    let signalKind, signalData: String?
    let fromDeviceId, toDeviceId: UUID?
}
struct LegendCallResult: Decodable {
    let succeeded: Bool
    let error: String?
    let call: LegendCallSnapshot?
    let activeCalls: [LegendCallSnapshot]?
    let policy: LegendCallPolicy?
}
enum LegendCallingError: LocalizedError {
    case unavailable(String)
    var errorDescription: String? {
        switch self { case .unavailable(let message): return message }
    }
}

struct LegendCallAdaptationPolicy: Decodable {
    let sampleSeconds, recoverySamples, lowBandwidth, highBandwidth: Int
    let highLatencySeconds, audioPriority: Double
    let lowWidth, lowHeight, lowFps, lowBitrate, mediumWidth, mediumHeight, mediumFps, mediumBitrate: Int
}

struct LegendCallRelay: Decodable { let urls: [String]; let username, credential: String; let expiresUtc: Date }

extension LegendCallAdaptationPolicy {
    /// Missing bandwidth never proves recovery. High RTT can still prove congestion.
    func targetQuality(bandwidth: Double?, latency: Double?) -> Int? {
        if let latency, latency.isFinite, latency > highLatencySeconds { return 0 }
        guard let bandwidth, bandwidth.isFinite, bandwidth >= 0 else { return nil }
        return bandwidth < Double(lowBandwidth) ? 0 : bandwidth < Double(highBandwidth) ? 1 : 2
    }
}
