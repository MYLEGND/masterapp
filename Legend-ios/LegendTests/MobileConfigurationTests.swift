import XCTest
@testable import Legend

final class MobileConfigurationTests: XCTestCase {
    func testMissingValuesKeepTheAppInContractUnavailableState() {
        let configuration = MobileConfiguration(
            bundleIdentifier: nil,
            apiBaseURL: nil,
            authorizationEndpoint: nil,
            tokenEndpoint: nil,
            clientID: nil,
            redirectScheme: nil,
            scope: nil,
            audience: nil
        )

        XCTAssertFalse(configuration.validation.isReady)
        XCTAssertEqual(configuration.validation.missingKeys.count, 8)
    }

    func testCompleteConfigurationIsReadyForAuthorization() {
        let configuration = MobileConfiguration(
            bundleIdentifier: "com.example.legend",
            apiBaseURL: URL(string: "https://staging.example.test"),
            authorizationEndpoint: URL(string: "https://identity.example.test/authorize"),
            tokenEndpoint: URL(string: "https://identity.example.test/token"),
            clientID: "example-client-id",
            redirectScheme: "com-example-legend",
            scope: "openid profile",
            audience: "api://example-audience"
        )

        XCTAssertTrue(configuration.validation.isReady)
    }

    func testBundleIdentifierComesFromTheBundleRatherThanACustomPlistKey() {
        let configuration = MobileConfiguration.fromBundle(.main)

        XCTAssertEqual(configuration.bundleIdentifier, Bundle.main.bundleIdentifier)
    }

    func testProductionConfigurationIncludesTheRequiredDelegatedScopeSetting() throws {
        let configurationDirectory = URL(fileURLWithPath: #filePath)
            .deletingLastPathComponent()
            .deletingLastPathComponent()
            .appendingPathComponent("Configuration")
        let productionConfiguration = try String(
            contentsOf: configurationDirectory.appendingPathComponent("Legend-Production.xcconfig"),
            encoding: .utf8)

        XCTAssertTrue(productionConfiguration.contains("LEGEND_AUTH_SCOPE ="))
    }
}
