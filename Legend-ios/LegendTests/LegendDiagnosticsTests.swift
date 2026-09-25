import XCTest
@testable import Legend

final class LegendDiagnosticsTests: XCTestCase {
    func testRedactorRemovesBearerAndTokenQueryValues() {
        let input = "Bearer abc.def-123 access_token=secret-value&refresh_token=another-secret"

        let output = DiagnosticRedactor.redact(input)

        XCTAssertFalse(output.contains("abc.def-123"))
        XCTAssertFalse(output.contains("secret-value"))
        XCTAssertFalse(output.contains("another-secret"))
        XCTAssertTrue(output.contains("[redacted]"))
    }

    func testDiagnosticEventDoesNotRetainRawAuthorizationValue() {
        let event = DiagnosticEvent(category: .networking, summary: "Bearer test-token")

        XCTAssertFalse(event.summary.contains("test-token"))
    }
    func testRuntimePayloadDropsDynamicPathQueryAndUnsupportedMethod() throws {
        let event = RuntimeDiagnosticEvent(path: "/api/v1/mobile/messaging/member@example.com/private-id?token=secret", method: "private-value", failure: .http, statusCode: 503)
        XCTAssertEqual(event.route, "/api/v1/mobile/messaging/{segment}/{segment}")
        XCTAssertEqual(event.operation, "REQUEST")
        XCTAssertEqual(event.category, "observation")
        XCTAssertEqual(event.stackTrace, "")
        let payload = String(decoding: try JSONEncoder().encode(event), as: UTF8.self)
        for secret in ["member@example.com", "private-id", "secret", "private-value"] { XCTAssertFalse(payload.contains(secret)) }
        XCTAssertEqual(RuntimeDiagnosticEvent.routeTemplate("https://private.example.com/secret"), "/{route}")
    }

    func testRuntimeQueueIsBoundedDeduplicatedAndReplayedAfterRecreation() async {
        let suite = "legend.diagnostics.test." + UUID().uuidString
        let defaults = UserDefaults(suiteName: suite)!
        defer { defaults.removePersistentDomain(forName: suite) }
        let reporter = RuntimeDiagnosticReporter(defaults: defaults)
        for code in 400..<430 {
            await reporter.record(RuntimeDiagnosticEvent(path: "/api/v1/mobile/social/private-id", method: "GET", failure: .http, statusCode: code))
        }
        await reporter.record(RuntimeDiagnosticEvent(path: "/api/v1/mobile/social/another-id", method: "GET", failure: .http, statusCode: 429))
        let count = await reporter.pendingCount()
        XCTAssertEqual(count, 20)
        let reloaded = RuntimeDiagnosticReporter(defaults: defaults)
        let restored = await reloaded.pendingCount()
        XCTAssertEqual(restored, 20)
        let data = defaults.data(forKey: "legend.runtime-diagnostics.v1")!
        XCTAssertLessThanOrEqual(data.count, 65_536)
        XCTAssertFalse(String(decoding: data, as: UTF8.self).contains("private-id"))
    }
    func testApplicationBundleContainsVerifiedCheckoutProvenance() {
        let hash = NativeBuildProvenance.commitHash()
        XCTAssertNotNil(hash.range(of: "^[a-f0-9]{40}$", options: .regularExpression))
        XCTAssertNil(Bundle.main.object(forInfoDictionaryKey: "LegendGitCommitHash"), "Manual build-setting provenance must not compete with the checkout authority")
    }
}
