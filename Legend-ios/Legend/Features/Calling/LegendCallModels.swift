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
    var screenShare: LegendCallScreenSharePolicy? = nil
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

struct LegendCallScreenSharePolicy: Decodable {
    let highWidth, highHeight, highFps, highBitrate: Int
    let mediumWidth, mediumHeight, mediumFps, mediumBitrate: Int
    let lowWidth, lowHeight, lowFps, lowBitrate: Int
    let transportHeadroomFraction: Double

    func bitrate(quality: Int, availableBandwidth: Double?, audioBitrate: Int) -> Int {
        let ceiling = profile(quality: quality).bitrate
        guard let availableBandwidth, availableBandwidth.isFinite, availableBandwidth >= 0 else { return ceiling }
        let budget = max(0, availableBandwidth * (1 - min(1, max(0, transportHeadroomFraction))) - Double(audioBitrate))
        return Int(min(Double(ceiling), budget))
    }

    func profile(quality: Int) -> (width: Int, height: Int, fps: Int, bitrate: Int) {
        switch quality {
        case 0: return (lowWidth, lowHeight, lowFps, lowBitrate)
        case 1: return (mediumWidth, mediumHeight, mediumFps, mediumBitrate)
        default: return (highWidth, highHeight, highFps, highBitrate)
        }
    }
    func dimensions(width: Int, height: Int, quality: Int) -> (width: Int, height: Int) {
        let target = profile(quality: quality)
        return Self.fit(width: width, height: height, targetWidth: target.width, targetHeight: target.height)
    }
    static func fit(width: Int, height: Int, targetWidth: Int, targetHeight: Int) -> (width: Int, height: Int) {
        let portrait = height > width
        let maxWidth = portrait ? min(targetWidth, targetHeight) : max(targetWidth, targetHeight)
        let maxHeight = portrait ? max(targetWidth, targetHeight) : min(targetWidth, targetHeight)
        let scale = min(1, Double(maxWidth) / Double(max(1, width)), Double(maxHeight) / Double(max(1, height)))
        return (max(2, min(maxWidth, Int((Double(width) * scale).rounded())) / 2 * 2), max(2, min(maxHeight, Int((Double(height) * scale).rounded())) / 2 * 2))
    }
}
