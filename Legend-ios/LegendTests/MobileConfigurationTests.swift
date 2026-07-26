import XCTest
@testable import Legend

final class MobileConfigurationTests: XCTestCase {
    func testMissingValuesKeepTheAppInContractUnavailableState() {
        let configuration = MobileConfiguration(
            bundleIdentifier: "com.example.legend",
            apiBaseURL: nil,
            authorizationEndpoint: nil,
            tokenEndpoint: nil,
            clientID: nil,
            redirectScheme: nil,
            scope: nil,
            audience: nil
        )

        XCTAssertFalse(configuration.validation.isReady)
        XCTAssertEqual(configuration.validation.missingKeys.count, MobileConfigurationKey.allCases.count)
        XCTAssertFalse(configuration.validation.missingBuildSettings.contains("LEGEND_BUNDLE_IDENTIFIER"))
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
        let configuration = MobileConfiguration.current

        XCTAssertEqual(configuration.bundleIdentifier, Bundle.main.bundleIdentifier)
    }

    func testValidationReportUsesTheSingleStronglyTypedConfigurationKeySet() {
        let configuration = MobileConfiguration(
            bundleIdentifier: "com.example.legend",
            apiBaseURL: nil,
            authorizationEndpoint: URL(string: "https://identity.example.test/authorize"),
            tokenEndpoint: nil,
            clientID: "example-client-id",
            redirectScheme: "com-example-legend",
            scope: nil,
            audience: "api://example-audience"
        )

        XCTAssertEqual(
            configuration.validation.missingBuildSettings,
            ["LEGEND_API_BASE_URL", "LEGEND_TOKEN_ENDPOINT", "LEGEND_AUTH_SCOPE"])
        XCTAssertEqual(
            configuration.validation.summary,
            "Secure sign-in is unavailable until an administrator provides 3 required values.")
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

    func testXcodeGenProjectSourceExposesEveryMobileConfigurationValueToInfoPlist() throws {
        let projectFile = URL(fileURLWithPath: #filePath)
            .deletingLastPathComponent()
            .deletingLastPathComponent()
            .appendingPathComponent("project.yml")
        let project = try String(contentsOf: projectFile, encoding: .utf8)

        [
            "LegendAPIBaseURL",
            "LegendAuthorizationEndpoint",
            "LegendTokenEndpoint",
            "LegendAuthClientID",
            "LegendAuthRedirectScheme",
            "LegendAuthScope",
            "LegendAuthAudience",
            "CFBundleURLTypes"
        ].forEach { key in
            XCTAssertTrue(project.contains(key), "Missing generated Info.plist configuration key: \(key)")
        }
    }
}
