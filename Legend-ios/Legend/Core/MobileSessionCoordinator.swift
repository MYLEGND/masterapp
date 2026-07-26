import AuthenticationServices
import Combine
import Foundation

struct MobileSession: Equatable, Sendable {
    let actor: MobileActor
    let capabilities: Set<String>
}

struct MobileRoleSelection: Equatable, Sendable {
    let permittedParticipantTypes: [ParticipantType]
    let correlationID: String?
}

enum MobileSessionState: Equatable {
    case loading
    case contractUnavailable(MobileConfigurationValidation)
    case signedOut
    case authenticating
    case roleSelection(MobileRoleSelection)
    case authenticated(MobileSession)
    case failed(UserFacingFailure)
}

struct UserFacingFailure: Equatable {
    let title: String
    let message: String
    let correlationID: String?
}

@MainActor
final class MobileSessionCoordinator: ObservableObject {
    @Published private(set) var state: MobileSessionState = .loading

    private let configuration: MobileConfiguration
    private let tokenStore: any SecureTokenStoring
    private let authorizer: any OAuthAuthorizing
    private let tokenExchanger: any OAuthTokenExchanging
    private let diagnostics: LegendDiagnostics
    private var activeTokens: OAuthTokenSet?

    init(
        configuration: MobileConfiguration = .current,
        tokenStore: (any SecureTokenStoring)? = nil,
        authorizer: (any OAuthAuthorizing)? = nil,
        tokenExchanger: (any OAuthTokenExchanging)? = nil,
        diagnostics: LegendDiagnostics? = nil
    ) {
        self.configuration = configuration
        self.tokenStore = tokenStore ?? KeychainTokenStore(service: configuration.bundleIdentifier)
        self.authorizer = authorizer ?? SystemBrowserAuthorizer()
        self.tokenExchanger = tokenExchanger ?? URLSessionOAuthTokenExchanger()
        self.diagnostics = diagnostics ?? LegendDiagnostics()
    }

    func restore() {
        guard configuration.validation.isReady else {
            state = .contractUnavailable(configuration.validation)
            diagnostics.record(category: .configuration, summary: "Native mobile contract configuration is incomplete.")
            return
        }

        Task {
            do {
                guard let storedTokens = try tokenStore.read() else {
                    state = .signedOut
                    return
                }
                let tokens = try await usableTokens(from: storedTokens)
                try await establishSession(using: tokens)
            } catch {
                try? tokenStore.clear()
                activeTokens = nil
                state = .signedOut
                diagnostics.record(category: .authentication, summary: "Stored native session could not be restored.")
            }
        }
    }

    func signIn() {
        guard configuration.validation.isReady else {
            state = .contractUnavailable(configuration.validation)
            return
        }

        Task {
            state = .authenticating
            do {
                let pkce = try PKCEChallenge.create()
                let stateValue = UUID().uuidString
                let request = try authorizationRequest(state: stateValue, pkce: pkce)
                let callbackURL = try await authorizer.authorize(request)
                let authorizationCode = try validate(callbackURL: callbackURL, expectedState: stateValue)
                let tokens = try await tokenExchanger.exchange(
                    code: authorizationCode,
                    pkceVerifier: pkce.verifier,
                    configuration: configuration
                )
                try tokenStore.save(tokens)
                activeTokens = tokens
                try await establishSession(using: tokens)
            } catch {
                if let authorizationError = error as? ASWebAuthenticationSessionError,
                   authorizationError.code == .canceledLogin {
                    state = .signedOut
                    return
                }
                state = .failed(UserFacingFailure(
                    title: "Sign-in unavailable",
                    message: error.localizedDescription,
                    correlationID: (error as? MobileAPIError)?.correlationID
                ))
                diagnostics.record(category: .authentication, summary: "Native sign-in did not complete.")
            }
        }
    }

    func signOut() {
        try? tokenStore.clear()
        activeTokens = nil
        state = configuration.validation.isReady ? .signedOut : .contractUnavailable(configuration.validation)
    }

    func selectRole(_ participantType: ParticipantType) {
        guard configuration.validation.isReady,
              let apiBaseURL = configuration.apiBaseURL else {
            state = .contractUnavailable(configuration.validation)
            return
        }

        Task {
            state = .authenticating
            do {
                guard let storedTokens = try tokenStore.read() else {
                    try? tokenStore.clear()
                    activeTokens = nil
                    state = .signedOut
                    return
                }
                let tokens = try await usableTokens(from: storedTokens)
                let response = try await MobileSessionAPI(client: MobileHTTPClient(baseURL: apiBaseURL))
                    .selectRole(participantType, accessToken: tokens.accessToken)
                state = .authenticated(MobileSession(
                    actor: response.actor,
                    capabilities: ["messaging"]
                ))
            } catch {
                state = .failed(UserFacingFailure(
                    title: "Role selection unavailable",
                    message: error.localizedDescription,
                    correlationID: (error as? MobileAPIError)?.correlationID
                ))
                diagnostics.record(category: .authentication, summary: "Native mobile role selection did not complete.")
            }
        }
    }

    func makeMessagingStore() -> MessagingStore {
        guard let apiBaseURL = configuration.apiBaseURL,
              case .authenticated(let currentSession) = state else {
            return MessagingStore(
                api: MobileContractUnavailableMessagingAPI(),
                accessTokenProvider: { throw MobileMessagingContractError.unavailable },
                diagnostics: diagnostics
            )
        }

        return MessagingStore(
            api: URLSessionMessagingAPI(
                client: MobileHTTPClient(baseURL: apiBaseURL),
                participantType: currentSession.actor.identity.participantType),
            accessTokenProvider: { [weak self] in
                guard let self else { throw MobileAPIError.unauthorized(correlationID: nil) }
                return try await self.accessTokenForRequest()
            },
            diagnostics: diagnostics
        )
    }

    func makeHomeStore() -> MobileHomeStore {
        guard let apiBaseURL = configuration.apiBaseURL,
              case .authenticated(let currentSession) = state else {
            return MobileHomeStore(
                api: MobileUnavailableHomeAPI(),
                accessTokenProvider: { throw MobileAPIError.unauthorized(correlationID: nil) },
                diagnostics: diagnostics)
        }

        return MobileHomeStore(
            api: URLSessionMobileHomeAPI(
                client: MobileHTTPClient(baseURL: apiBaseURL),
                participantType: currentSession.actor.identity.participantType),
            accessTokenProvider: { [weak self] in
                guard let self else { throw MobileAPIError.unauthorized(correlationID: nil) }
                return try await self.accessTokenForRequest()
            },
            diagnostics: diagnostics)
    }

    func makeJourneyCirclesStore() -> MobileJourneyCirclesStore {
        guard let apiBaseURL = configuration.apiBaseURL,
              case .authenticated(let currentSession) = state else {
            return MobileJourneyCirclesStore(
                api: MobileUnavailableJourneyCirclesAPI(),
                accessTokenProvider: { throw MobileAPIError.unauthorized(correlationID: nil) },
                diagnostics: diagnostics)
        }

        return MobileJourneyCirclesStore(
            api: URLSessionMobileJourneyCirclesAPI(
                client: MobileHTTPClient(baseURL: apiBaseURL),
                participantType: currentSession.actor.identity.participantType),
            accessTokenProvider: { [weak self] in
                guard let self else { throw MobileAPIError.unauthorized(correlationID: nil) }
                return try await self.accessTokenForRequest()
            },
            diagnostics: diagnostics)
    }

    private func establishSession(using tokens: OAuthTokenSet) async throws {
        guard let apiBaseURL = configuration.apiBaseURL else { throw MobileAPIError.invalidBaseURL }
        let response = try await MobileSessionAPI(client: MobileHTTPClient(baseURL: apiBaseURL))
            .bootstrap(accessToken: tokens.accessToken)
        guard response.authenticated else {
            throw MobileAPIError.unauthorized(correlationID: response.correlationID)
        }
        if response.requiresParticipantSelection {
            guard !response.permittedParticipantTypes.isEmpty else {
                throw MobileAPIError.forbidden(correlationID: response.correlationID)
            }
            state = .roleSelection(MobileRoleSelection(
                permittedParticipantTypes: response.permittedParticipantTypes,
                correlationID: response.correlationID))
            return
        }
        guard let actor = response.actor else {
            throw MobileAPIError.forbidden(correlationID: response.correlationID)
        }
        state = .authenticated(MobileSession(
            actor: actor,
            capabilities: response.capabilities.messaging ? ["messaging"] : []
        ))
    }

    private func accessTokenForRequest() async throws -> String {
        guard configuration.validation.isReady else { throw MobileAPIError.invalidBaseURL }
        if let activeTokens {
            let tokens = try await usableTokens(from: activeTokens)
            return tokens.accessToken
        }
        guard let storedTokens = try tokenStore.read() else {
            throw MobileAPIError.unauthorized(correlationID: nil)
        }
        let tokens = try await usableTokens(from: storedTokens)
        return tokens.accessToken
    }

    private func usableTokens(from tokens: OAuthTokenSet) async throws -> OAuthTokenSet {
        let refreshThreshold = Date().addingTimeInterval(60)
        guard tokens.expiresAt <= refreshThreshold else {
            activeTokens = tokens
            return tokens
        }
        guard let refreshToken = tokens.refreshToken,
              !refreshToken.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else {
            throw MobileAPIError.unauthorized(correlationID: nil)
        }
        let refreshed = try await tokenExchanger.refresh(
            refreshToken: refreshToken,
            configuration: configuration)
        try tokenStore.save(refreshed)
        activeTokens = refreshed
        return refreshed
    }

    private func authorizationRequest(state: String, pkce: PKCEChallenge) throws -> OAuthAuthorizationRequest {
        guard let authorizationEndpoint = configuration.authorizationEndpoint,
              let clientID = configuration.clientID,
              let redirectScheme = configuration.redirectScheme,
              let scope = configuration.scope else {
            throw MobileAPIError.invalidBaseURL
        }
        return OAuthAuthorizationRequest(
            authorizationEndpoint: authorizationEndpoint,
            clientID: clientID,
            redirectScheme: redirectScheme,
            scope: scope,
            state: state,
            pkce: pkce
        )
    }

    private func validate(callbackURL: URL, expectedState: String) throws -> String {
        try OAuthCallbackValidator.authorizationCode(
            from: callbackURL,
            redirectScheme: configuration.redirectScheme,
            expectedState: expectedState)
    }
}

enum OAuthCallbackValidator {
    static func authorizationCode(
        from callbackURL: URL,
        redirectScheme: String?,
        expectedState: String) throws -> String
    {
        guard callbackURL.scheme?.caseInsensitiveCompare(redirectScheme ?? "") == .orderedSame,
              callbackURL.host?.caseInsensitiveCompare("oauth") == .orderedSame,
              callbackURL.path == "/callback",
              let components = URLComponents(url: callbackURL, resolvingAgainstBaseURL: false),
              let returnedState = components.queryItems?.first(where: { $0.name == "state" })?.value,
              returnedState == expectedState,
              let code = components.queryItems?.first(where: { $0.name == "code" })?.value,
              !code.isEmpty else {
            throw OAuthCallbackError.invalidCallback
        }
        return code
    }
}

enum OAuthCallbackError: LocalizedError, Equatable {
    case invalidCallback

    var errorDescription: String? {
        "The sign-in callback could not be verified."
    }
}

protocol OAuthTokenExchanging: Sendable {
    func exchange(code: String, pkceVerifier: String, configuration: MobileConfiguration) async throws -> OAuthTokenSet
    func refresh(refreshToken: String, configuration: MobileConfiguration) async throws -> OAuthTokenSet
}

struct URLSessionOAuthTokenExchanger: OAuthTokenExchanging {
    func exchange(code: String, pkceVerifier: String, configuration: MobileConfiguration) async throws -> OAuthTokenSet {
        guard let tokenEndpoint = configuration.tokenEndpoint,
              let clientID = configuration.clientID,
              let redirectScheme = configuration.redirectScheme,
              let scope = configuration.scope else {
            throw MobileAPIError.invalidBaseURL
        }

        return try await requestTokens(tokenEndpoint, parameters: [
            "grant_type": "authorization_code",
            "code": code,
            "client_id": clientID,
            "redirect_uri": "\(redirectScheme)://oauth/callback",
            "scope": scope,
            "code_verifier": pkceVerifier
        ])
    }

    func refresh(refreshToken: String, configuration: MobileConfiguration) async throws -> OAuthTokenSet {
        guard let tokenEndpoint = configuration.tokenEndpoint,
              let clientID = configuration.clientID,
              let scope = configuration.scope else {
            throw MobileAPIError.invalidBaseURL
        }

        let response = try await requestTokens(tokenEndpoint, parameters: [
            "grant_type": "refresh_token",
            "refresh_token": refreshToken,
            "client_id": clientID,
            "scope": scope
        ])
        return OAuthTokenSet(
            accessToken: response.accessToken,
            refreshToken: response.refreshToken ?? refreshToken,
            expiresAt: response.expiresAt)
    }

    private func requestTokens(_ tokenEndpoint: URL, parameters: [String: String]) async throws -> OAuthTokenSet {
        var request = URLRequest(url: tokenEndpoint)
        request.httpMethod = "POST"
        request.setValue("application/x-www-form-urlencoded", forHTTPHeaderField: "Content-Type")
        request.setValue("application/json", forHTTPHeaderField: "Accept")
        request.httpBody = FormURLEncoder.encode(parameters)

        let (data, response) = try await URLSession.shared.data(for: request)
        guard let http = response as? HTTPURLResponse else { throw MobileAPIError.invalidServerResponse }
        let correlationID = http.value(forHTTPHeaderField: "X-Correlation-ID")
        guard (200 ... 299).contains(http.statusCode) else {
            throw MobileAPIError.server(statusCode: http.statusCode, correlationID: correlationID)
        }

        let tokenResponse: OAuthTokenResponse
        do {
            tokenResponse = try JSONDecoder.mobile.decode(OAuthTokenResponse.self, from: data)
        } catch {
            throw MobileAPIError.decodingFailed(correlationID: correlationID)
        }

        return OAuthTokenSet(
            accessToken: tokenResponse.accessToken,
            refreshToken: tokenResponse.refreshToken,
            expiresAt: Date().addingTimeInterval(TimeInterval(tokenResponse.expiresIn))
        )
    }
}

private struct OAuthTokenResponse: Decodable {
    let accessToken: String
    let refreshToken: String?
    let expiresIn: Int

    enum CodingKeys: String, CodingKey {
        case accessToken = "access_token"
        case refreshToken = "refresh_token"
        case expiresIn = "expires_in"
    }
}

private enum FormURLEncoder {
    static func encode(_ values: [String: String]) -> Data? {
        values
            .sorted { $0.key < $1.key }
            .map { "\($0.key.urlFormEncoded)=\($0.value.urlFormEncoded)" }
            .joined(separator: "&")
            .data(using: .utf8)
    }
}

private extension String {
    var urlFormEncoded: String {
        addingPercentEncoding(withAllowedCharacters: .urlQueryAllowed.subtracting(CharacterSet(charactersIn: "+&="))) ?? self
    }
}

private struct MobileSessionAPI: Sendable {
    let client: MobileHTTPClient

    func bootstrap(accessToken: String) async throws -> MobileBootstrapResponse {
        try await client.get("/api/v1/mobile/session", accessToken: accessToken, response: MobileBootstrapResponse.self)
    }

    func selectRole(_ participantType: ParticipantType, accessToken: String) async throws -> MobileRoleSelectionResponse {
        try await client.post(
            "/api/v1/mobile/session/select-role",
            body: MobileRoleSelectionRequest(participantType: participantType.rawValue),
            accessToken: accessToken,
            response: MobileRoleSelectionResponse.self)
    }
}

struct MobileBootstrapResponse: Decodable {
    let authenticated: Bool
    let actor: MobileActor?
    let permittedParticipantTypes: [ParticipantType]
    let requiresParticipantSelection: Bool
    let capabilities: MobileCapabilities
    let correlationID: String

    private enum CodingKeys: String, CodingKey {
        case authenticated
        case actor
        case permittedParticipantTypes
        case requiresParticipantSelection
        case capabilities
        case correlationID = "correlationId"
    }
}

struct MobileCapabilities: Decodable {
    let messaging: Bool
}

struct MobileRoleSelectionRequest: Encodable {
    let participantType: String
}

struct MobileRoleSelectionResponse: Decodable {
    let actor: MobileActor
    let permittedParticipantTypes: [ParticipantType]
    let correlationID: String

    private enum CodingKeys: String, CodingKey {
        case actor
        case permittedParticipantTypes
        case correlationID = "correlationId"
    }
}
