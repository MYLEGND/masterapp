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
}
