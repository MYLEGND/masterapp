import Foundation

struct MobileConfiguration: Equatable, Sendable {
    let bundleIdentifier: String?
    let apiBaseURL: URL?
    let authorizationEndpoint: URL?
    let tokenEndpoint: URL?
    let clientID: String?
    let redirectScheme: String?
    let scope: String?
    let audience: String?

    static func fromBundle(_ bundle: Bundle = .main) -> MobileConfiguration {
        MobileConfiguration(
            bundleIdentifier: bundle.bundleIdentifier,
            apiBaseURL: bundle.urlValue(forInfoKey: "LegendAPIBaseURL"),
            authorizationEndpoint: bundle.urlValue(forInfoKey: "LegendAuthorizationEndpoint"),
            tokenEndpoint: bundle.urlValue(forInfoKey: "LegendTokenEndpoint"),
            clientID: bundle.stringValue(forInfoKey: "LegendAuthClientID"),
            redirectScheme: bundle.stringValue(forInfoKey: "LegendAuthRedirectScheme"),
            scope: bundle.stringValue(forInfoKey: "LegendAuthScope"),
            audience: bundle.stringValue(forInfoKey: "LegendAuthAudience")
        )
    }

    var validation: MobileConfigurationValidation {
        let required: [(String, String?)] = [
            ("LEGEND_BUNDLE_IDENTIFIER", bundleIdentifier),
            ("LEGEND_API_BASE_URL", apiBaseURL?.absoluteString),
            ("LEGEND_AUTHORIZATION_ENDPOINT", authorizationEndpoint?.absoluteString),
            ("LEGEND_TOKEN_ENDPOINT", tokenEndpoint?.absoluteString),
            ("LEGEND_AUTH_CLIENT_ID", clientID),
            ("LEGEND_AUTH_REDIRECT_SCHEME", redirectScheme),
            ("LEGEND_AUTH_SCOPE", scope),
            ("LEGEND_AUTH_AUDIENCE", audience)
        ]

        let missing = required.compactMap { name, value in
            Self.isConfigured(value) ? nil : name
        }
        return MobileConfigurationValidation(missingKeys: missing)
    }

    private static func isConfigured(_ value: String?) -> Bool {
        guard let value else { return false }
        let trimmed = value.trimmingCharacters(in: .whitespacesAndNewlines)
        return !trimmed.isEmpty && !trimmed.contains("$(")
    }
}

struct MobileConfigurationValidation: Equatable, Sendable {
    let missingKeys: [String]

    var isReady: Bool { missingKeys.isEmpty }
}

private extension Bundle {
    func stringValue(forInfoKey key: String) -> String? {
        guard let value = object(forInfoDictionaryKey: key) as? String else { return nil }
        let trimmed = value.trimmingCharacters(in: .whitespacesAndNewlines)
        return trimmed.isEmpty || trimmed.contains("$(") ? nil : trimmed
    }

    func urlValue(forInfoKey key: String) -> URL? {
        guard let string = stringValue(forInfoKey: key),
              let url = URL(string: string),
              let scheme = url.scheme?.lowercased(),
              scheme == "https",
              url.host != nil else {
            return nil
        }
        return url
    }
}
