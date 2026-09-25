import Combine
import Foundation
#if DEBUG
import os
#endif

enum DiagnosticCategory: String, Sendable {
    case configuration
    case authentication
    case networking
    case messaging
}

struct DiagnosticEvent: Equatable, Sendable, Identifiable {
    let id: UUID
    let timestamp: Date
    let category: DiagnosticCategory
    let summary: String
    let correlationID: String?

    init(category: DiagnosticCategory, summary: String, correlationID: String? = nil) {
        self.id = UUID()
        self.timestamp = Date()
        self.category = category
        self.summary = DiagnosticRedactor.redact(summary)
        self.correlationID = correlationID.map(DiagnosticRedactor.redact)
    }
}

enum DiagnosticRedactor {
    static func redact(_ value: String) -> String {
        value
            .replacingOccurrences(of: #"(?i)bearer\s+[A-Za-z0-9._\-]+"#, with: "Bearer [redacted]", options: .regularExpression)
            .replacingOccurrences(of: #"(?i)(access_token|refresh_token|id_token)=([^&\s]+)"#, with: "$1=[redacted]", options: .regularExpression)
    }
}

@MainActor
final class LegendDiagnostics: ObservableObject {
    @Published private(set) var events: [DiagnosticEvent] = []
    private let maximumEvents = 50

    func record(category: DiagnosticCategory, summary: String, correlationID: String? = nil) {
        let event = DiagnosticEvent(category: category, summary: summary, correlationID: correlationID)
        events.insert(event, at: 0)
        if events.count > maximumEvents {
            events.removeLast(events.count - maximumEvents)
        }
        MobileDebugDiagnostics.record(event.summary, correlationID: event.correlationID)
    }
}

enum MobileDebugDiagnostics {
    static func record(_ summary: String, correlationID: String? = nil) {
        #if DEBUG
        let suffix = correlationID.map { " correlation=\($0)" } ?? ""
        Logger(subsystem: "com.mylegnd.legend.registered", category: "mobile-auth")
            .debug("\(DiagnosticRedactor.redact(summary + suffix), privacy: .public)")
        #endif
    }
}

/// Untrusted technical observation. Server ingestion alone owns issue classification.
/// The constructor intentionally accepts no response body, error description or identity.
struct RuntimeDiagnosticEvent: Codable, Equatable, Sendable {
    let appIdentifier: String
    let platform: String
    let route: String
    let sourceFilePath: String
    let errorName: String
    let errorMessage: String
    let stackTrace: String
    let gitCommitHash: String
    let timestamp: String
    let operation: String
    let category: String
    let statusCode: Int?
    let appVersion: String

    enum Failure: String, Sendable { case http = "HttpFailure", transport = "TransportFailure", decoding = "DecodeFailure" }

    init(path: String, method: String, failure: Failure, statusCode: Int? = nil, bundle: Bundle = .main) {
        appIdentifier = "Legend-ios"
        platform = "ios"
        route = Self.routeTemplate(path)
        sourceFilePath = "Legend-ios/Legend/Core/MobileHTTPClient.swift"
        errorName = failure.rawValue
        errorMessage = "Native request did not complete."
        stackTrace = ""
        gitCommitHash = NativeBuildProvenance.commitHash(bundle: bundle)
        timestamp = ISO8601DateFormatter().string(from: Date())
        operation = ["GET", "POST", "PUT", "PATCH", "DELETE", "HEAD"].contains(method) ? method : "REQUEST"
        category = "observation"
        self.statusCode = statusCode.flatMap { (100...599).contains($0) ? $0 : nil }
        let version = bundle.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String ?? "unknown"
        let build = bundle.object(forInfoDictionaryKey: "CFBundleVersion") as? String ?? "unknown"
        appVersion = String("\(version) (\(build))".prefix(80))
    }

    static func routeTemplate(_ path: String) -> String {
        let clean = path.split(separator: "?", maxSplits: 1).first.map(String.init) ?? ""
        let segments = clean.split(separator: "/").map(String.init)
        let areas: Set<String> = ["account", "accounts", "auth", "session", "home", "social", "messaging", "notifications", "founder", "discovery", "journey", "journey-circles", "agent", "localization", "calls", "calling", "community", "scripture"]
        guard segments.count >= 4, Array(segments.prefix(3)) == ["api", "v1", "mobile"] else { return "/{route}" }
        let area = areas.contains(segments[3]) ? segments[3] : "{area}"
        return "/api/v1/mobile/" + area + String(repeating: "/{segment}", count: min(segments.count - 4, 6))
    }
}

/// Persists caught observations before upload so they can replay after relaunch.
/// This is not a Swift fatal-error or signal handler; those cannot safely run Swift/network code.
actor RuntimeDiagnosticReporter {
    static let shared = RuntimeDiagnosticReporter()
    static let endpoint = "/api/v1/mobile/runtime-diagnostics"
    private struct Pending: Codable { let id: UUID; let event: RuntimeDiagnosticEvent; var attempts: Int }
    private var pending: [Pending]
    private var flushing = false
    private var lastFlush = Date.distantPast
    private let defaults: UserDefaults
    private let key = "legend.runtime-diagnostics.v1"

    init(defaults: UserDefaults = .standard) {
        self.defaults = defaults
        let data = defaults.data(forKey: key)
        if let data, data.count <= 65_536, let saved = try? JSONDecoder().decode([Pending].self, from: data) {
            pending = Array(saved.filter { $0.attempts < 3 }.suffix(20))
        } else { pending = [] }
    }

    func record(_ event: RuntimeDiagnosticEvent) {
        if pending.contains(where: { $0.event.route == event.route && $0.event.operation == event.operation && $0.event.errorName == event.errorName && $0.event.statusCode == event.statusCode }) { return }
        pending.append(Pending(id: UUID(), event: event, attempts: 0))
        pending = Array(pending.suffix(20))
        persist()
    }

    func flush(client: MobileHTTPClient, accessToken: String, now: Date = Date()) async {
        guard !flushing, !pending.isEmpty, !accessToken.isEmpty, now.timeIntervalSince(lastFlush) >= 60 else { return }
        lastFlush = now
        flushing = true
        defer { flushing = false }
        // At most three sends per opportunity; no unbounded task or retry queue.
        for _ in 0..<3 {
            guard !Task.isCancelled, let first = pending.first else { return }
            pending[0].attempts += 1
            persist()
            do {
                try await client.post(Self.endpoint, body: first.event, accessToken: accessToken)
                pending.removeAll { $0.id == first.id }
                persist()
            } catch {
                if let index = pending.firstIndex(where: { $0.id == first.id }), pending[index].attempts >= 3 {
                    pending.remove(at: index)
                    persist()
                }
                // Authentication/policy failures wait for a subsequent authenticated request.
                // Diagnostics never refresh credentials or report their own failures.
                return
            }
        }
    }

    func pendingCount() -> Int { pending.count }

    private func persist() {
        if let data = try? JSONEncoder().encode(pending), data.count <= 65_536 { defaults.set(data, forKey: key) }
    }
}

/// The existing checkout guard is the sole producer. No runtime Git lookup or manual SHA override.
enum NativeBuildProvenance {
    private struct Receipt: Decodable { let schemaVersion: Int; let gitCommitHash: String }
    static func commitHash(bundle: Bundle = .main) -> String {
        guard let url = bundle.url(forResource: "LegendBuildProvenance", withExtension: "json"),
              let data = try? Data(contentsOf: url), data.count <= 1024,
              let receipt = try? JSONDecoder().decode(Receipt.self, from: data),
              receipt.schemaVersion == 1,
              receipt.gitCommitHash.range(of: "^[a-f0-9]{40}$", options: .regularExpression) != nil else {
            // Test bundles or corrupt external artifacts can lack the resource. Normal
            // Xcode application builds fail before producing a bundle without provenance.
            return "unknown"
        }
        return receipt.gitCommitHash
    }
}
