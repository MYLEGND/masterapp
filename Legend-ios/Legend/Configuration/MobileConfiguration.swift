import Foundation

enum MobileConfigurationKey: String, CaseIterable, Identifiable, Sendable {
    case apiBaseURL
    case authorizationEndpoint
    case tokenEndpoint
    case clientID
    case redirectScheme
    case scope
    case audience

    var id: String { buildSetting }

    var buildSetting: String {
        switch self {
        case .apiBaseURL: "LEGEND_API_BASE_URL"
        case .authorizationEndpoint: "LEGEND_AUTHORIZATION_ENDPOINT"
        case .tokenEndpoint: "LEGEND_TOKEN_ENDPOINT"
        case .clientID: "LEGEND_AUTH_CLIENT_ID"
        case .redirectScheme: "LEGEND_AUTH_REDIRECT_SCHEME"
        case .scope: "LEGEND_AUTH_SCOPE"
        case .audience: "LEGEND_AUTH_AUDIENCE"
        }
    }

    var infoDictionaryKey: String {
        switch self {
        case .apiBaseURL: "LegendAPIBaseURL"
        case .authorizationEndpoint: "LegendAuthorizationEndpoint"
        case .tokenEndpoint: "LegendTokenEndpoint"
        case .clientID: "LegendAuthClientID"
        case .redirectScheme: "LegendAuthRedirectScheme"
        case .scope: "LegendAuthScope"
        case .audience: "LegendAuthAudience"
        }
    }

    var requiresHTTPSURL: Bool {
        switch self {
        case .apiBaseURL, .authorizationEndpoint, .tokenEndpoint:
            true
        case .clientID, .redirectScheme, .scope, .audience:
            false
        }
    }
}

struct MobileConfiguration: Equatable, Sendable {
    /// Bundle.main is the sole bundle identity authority. It is intentionally
    /// not a runtime build setting and is not part of configuration validation.
    let bundleIdentifier: String
    let apiBaseURL: URL?
    let authorizationEndpoint: URL?
    let tokenEndpoint: URL?
    let clientID: String?
    let redirectScheme: String?
    let scope: String?
    let audience: String?
    let agentOnlineAccountURL: URL?
    let privacyPolicyURL: URL?

    init(
        bundleIdentifier: String,
        apiBaseURL: URL?,
        authorizationEndpoint: URL?,
        tokenEndpoint: URL?,
        clientID: String?,
        redirectScheme: String?,
        scope: String?,
        audience: String?,
        agentOnlineAccountURL: URL? = nil,
        privacyPolicyURL: URL? = nil
    ) {
        self.bundleIdentifier = bundleIdentifier
        self.apiBaseURL = apiBaseURL
        self.authorizationEndpoint = authorizationEndpoint
        self.tokenEndpoint = tokenEndpoint
        self.clientID = clientID
        self.redirectScheme = redirectScheme
        self.scope = scope
        self.audience = audience
        self.agentOnlineAccountURL = agentOnlineAccountURL
        self.privacyPolicyURL = privacyPolicyURL
    }

    static var current: MobileConfiguration {
        MobileConfigurationProvider().load()
    }

    var validation: MobileConfigurationValidation {
        MobileConfigurationValidation(
            missingKeys: MobileConfigurationKey.allCases.filter { !isConfigured($0) })
    }

    private func isConfigured(_ key: MobileConfigurationKey) -> Bool {
        switch key {
        case .apiBaseURL: apiBaseURL != nil
        case .authorizationEndpoint: authorizationEndpoint != nil
        case .tokenEndpoint: tokenEndpoint != nil
        case .clientID: Self.isConfigured(clientID)
        case .redirectScheme: Self.isConfigured(redirectScheme)
        case .scope: Self.isConfigured(scope)
        case .audience: Self.isConfigured(audience)
        }
    }

    fileprivate static func isConfigured(_ value: String?) -> Bool {
        guard let value else { return false }
        let trimmed = value.trimmingCharacters(in: .whitespacesAndNewlines)
        return !trimmed.isEmpty && !trimmed.contains("$(")
    }
}

struct MobileConfigurationProvider: Sendable {
    private let bundle: Bundle

    init(bundle: Bundle = .main) {
        self.bundle = bundle
    }

    func load() -> MobileConfiguration {
        MobileConfiguration(
            bundleIdentifier: bundle.bundleIdentifier ?? "",
            apiBaseURL: urlValue(for: .apiBaseURL),
            authorizationEndpoint: urlValue(for: .authorizationEndpoint),
            tokenEndpoint: urlValue(for: .tokenEndpoint),
            clientID: stringValue(for: .clientID),
            redirectScheme: stringValue(for: .redirectScheme),
            scope: stringValue(for: .scope),
            audience: stringValue(for: .audience),
            agentOnlineAccountURL: urlValue(
                forInfoDictionaryKey: "LegendAgentOnlineAccountURL"),
            privacyPolicyURL: urlValue(
                forInfoDictionaryKey: "LegendPrivacyPolicyURL")
        )
    }

    private func stringValue(for key: MobileConfigurationKey) -> String? {
        guard let value = bundle.object(forInfoDictionaryKey: key.infoDictionaryKey) as? String else {
            return nil
        }

        let trimmed = value.trimmingCharacters(in: .whitespacesAndNewlines)
        return MobileConfiguration.isConfigured(trimmed) ? trimmed : nil
    }

    private func urlValue(for key: MobileConfigurationKey) -> URL? {
        guard key.requiresHTTPSURL,
              let value = stringValue(for: key) else {
            return nil
        }
        return httpsURL(from: value)
    }

    private func urlValue(forInfoDictionaryKey key: String) -> URL? {
        guard let value = bundle.object(forInfoDictionaryKey: key) as? String else {
            return nil
        }
        return httpsURL(from: value)
    }

    private func httpsURL(from value: String) -> URL? {
        let trimmed = value.trimmingCharacters(in: .whitespacesAndNewlines)
        guard MobileConfiguration.isConfigured(trimmed),
              let url = URL(string: trimmed),
              let scheme = url.scheme?.lowercased(),
              scheme == "https",
              url.host != nil else {
            return nil
        }
        return url
    }
}

struct MobileConfigurationValidation: Equatable, Sendable {
    let missingKeys: [MobileConfigurationKey]

    var isReady: Bool { missingKeys.isEmpty }

    var summary: String {
        if isReady {
            return "All required native configuration values are present."
        }
        return "Secure sign-in is unavailable until an administrator provides \(missingKeys.count) required value\(missingKeys.count == 1 ? "" : "s")."
    }

    var missingBuildSettings: [String] {
        missingKeys.map(\.buildSetting)
    }
}
