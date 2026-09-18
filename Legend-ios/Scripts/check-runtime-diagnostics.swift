// Runs the actual Foundation transport and diagnostic queue on macOS, without signing,
// installing the app, production networking, or requiring the UI/WebRTC dependency.
// swiftc -parse-as-library Legend/Core/LegendDiagnostics.swift Legend/Core/MobileHTTPClient.swift \
//   Scripts/check-runtime-diagnostics.swift -o /tmp/legend-diagnostics-check
import Foundation

// UI-only dependencies of MobileHTTPClient; the transport and queue are compiled unchanged.
struct MobileSocialMediaStream: Sendable { let url: URL; let headers: [String: String] }
func LegendLocalized(_ value: String) -> String { value }

private final class DiagnosticProtocol: URLProtocol, @unchecked Sendable {
    private static let lock = NSLock()
    private static var requests: [URLRequest] = []
    static func captured() -> [URLRequest] { lock.lock(); defer { lock.unlock() }; return requests }
    override class func canInit(with request: URLRequest) -> Bool { true }
    override class func canonicalRequest(for request: URLRequest) -> URLRequest { request }
    override func startLoading() {
        Self.lock.lock(); Self.requests.append(request); Self.lock.unlock()
        let response = HTTPURLResponse(url: request.url!, statusCode: 503, httpVersion: nil, headerFields: nil)!
        client?.urlProtocol(self, didReceive: response, cacheStoragePolicy: .notAllowed)
        client?.urlProtocol(self, didLoad: Data("{\"message\":\"private-server-body\"}".utf8))
        client?.urlProtocolDidFinishLoading(self)
    }
    override func stopLoading() {}
}

@main private enum DiagnosticCheck {
    static func main() async throws {
        if CommandLine.arguments.count > 1 {
            guard let appBundle = Bundle(path: CommandLine.arguments[1]) else { fatalError("Expected built application bundle") }
            let hash = NativeBuildProvenance.commitHash(bundle: appBundle)
            precondition(hash.range(of: "^[a-f0-9]{40}$", options: .regularExpression) != nil)
            print("PASS: built application resource provides checkout SHA \(hash)")
        }
        let suite = "legend.diagnostics.check." + UUID().uuidString
        let defaults = UserDefaults(suiteName: suite)!
        defer { defaults.removePersistentDomain(forName: suite) }
        let reporter = RuntimeDiagnosticReporter(defaults: defaults)
        let event = RuntimeDiagnosticEvent(path: "/api/v1/mobile/social/private-id?token=secret", method: "GET", failure: .http, statusCode: 500)
        precondition(event.route == "/api/v1/mobile/social/{segment}")
        precondition(event.category == "observation")
        let encoded = String(decoding: try JSONEncoder().encode(event), as: UTF8.self)
        precondition(!encoded.contains("private-id") && !encoded.contains("secret"))
        await reporter.record(event)
        await reporter.record(event)
        let deduplicated = await reporter.pendingCount()
        precondition(deduplicated == 1)
        let restored = RuntimeDiagnosticReporter(defaults: defaults)
        let count = await restored.pendingCount()
        precondition(count == 1)

        let config = URLSessionConfiguration.ephemeral
        config.protocolClasses = [DiagnosticProtocol.self]
        let session = URLSession(configuration: config)
        defer { session.invalidateAndCancel() }
        let client = MobileHTTPClient(baseURL: URL(string: "https://diagnostics.invalid")!, session: session)
        let now = Date()
        await restored.flush(client: client, accessToken: "", now: now)
        precondition(DiagnosticProtocol.captured().isEmpty)
        await restored.flush(client: client, accessToken: "test-token", now: now)
        await restored.flush(client: client, accessToken: "test-token", now: now.addingTimeInterval(1))
        precondition(DiagnosticProtocol.captured().count == 1)
        await restored.flush(client: client, accessToken: "test-token", now: now.addingTimeInterval(61))
        await restored.flush(client: client, accessToken: "test-token", now: now.addingTimeInterval(122))
        let exhausted = await restored.pendingCount()
        precondition(exhausted == 0)
        let requests = DiagnosticProtocol.captured()
        precondition(requests.count == 3, "Failed uploads must not recursively capture diagnostics")
        for request in requests {
            precondition(request.url?.path == RuntimeDiagnosticReporter.endpoint)
            precondition(request.value(forHTTPHeaderField: "Authorization") == "Bearer test-token")
        }
        for code in 400..<430 {
            await restored.record(RuntimeDiagnosticEvent(path: "/api/v1/mobile/social/private-id", method: "GET", failure: .http, statusCode: code))
        }
        let bounded = await restored.pendingCount()
        precondition(bounded == 20)
        let persisted = defaults.data(forKey: "legend.runtime-diagnostics.v1")!
        precondition(persisted.count <= 65_536)
        let text = String(decoding: persisted, as: UTF8.self)
        precondition(!text.contains("test-token") && !text.contains("private-server-body"))
        print("PASS: native payload privacy, deduplication, persisted replay, authenticated transport, retry bound, cooldown, recursion suppression, queue bound")
    }
}
