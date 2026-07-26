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

protocol MobileSessionServicing: Sendable {
    func bootstrap(accessToken: String) async throws -> MobileBootstrapResponse
    func selectRole(_ participantType: ParticipantType, accessToken: String) async throws -> MobileRoleSelectionResponse
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
    private let sessionService: (any MobileSessionServicing)?
    private var activeTokens: OAuthTokenSet?

    init(
        configuration: MobileConfiguration = .current,
        tokenStore: (any SecureTokenStoring)? = nil,
        authorizer: (any OAuthAuthorizing)? = nil,
        tokenExchanger: (any OAuthTokenExchanging)? = nil,
        sessionService: (any MobileSessionServicing)? = nil,
        diagnostics: LegendDiagnostics? = nil
    ) {
        self.configuration = configuration
        self.tokenStore = tokenStore ?? KeychainTokenStore(service: configuration.bundleIdentifier)
        self.authorizer = authorizer ?? SystemBrowserAuthorizer()
        self.tokenExchanger = tokenExchanger ?? URLSessionOAuthTokenExchanger()
        self.sessionService = sessionService
        self.diagnostics = diagnostics ?? LegendDiagnostics()
    }

    func restore() {
        guard configuration.validation.isReady else {
            transition(to: .contractUnavailable(configuration.validation), reason: "Configuration unavailable during restore")
            diagnostics.record(category: .configuration, summary: "Native mobile contract configuration is incomplete.")
            return
        }

        Task {
            do {
                guard let storedTokens = try tokenStore.read() else {
                    transition(to: .signedOut, reason: "No stored mobile credential")
                    return
                }
                diagnostics.record(category: .authentication, summary: "Stored mobile credential found; session bootstrap started.")
                let tokens = try await usableTokens(from: storedTokens)
                try await establishSession(using: tokens)
            } catch {
                if (error as? MobileAPIError)?.provesInvalidBearerCredential == true {
                    try? tokenStore.clear()
                    activeTokens = nil
                    transition(to: .signedOut, reason: "Stored bearer credential rejected by mobile API")
                    diagnostics.record(category: .authentication, summary: "Stored mobile bearer credential was rejected by the mobile API.", correlationID: (error as? MobileAPIError)?.correlationID)
                } else {
                    transition(to: .failed(failure(for: error)), reason: "Stored session bootstrap did not complete")
                    diagnostics.record(category: .authentication, summary: "Stored native session could not be restored.", correlationID: (error as? MobileAPIError)?.correlationID)
                }
            }
        }
    }

    func signIn() {
        guard configuration.validation.isReady else {
            transition(to: .contractUnavailable(configuration.validation), reason: "Configuration unavailable during sign-in")
            return
        }

        Task {
            transition(to: .authenticating, reason: "Authorization session started")
            diagnostics.record(category: .authentication, summary: "Native authorization session started.")
            do {
                let pkce = try PKCEChallenge.create()
                let stateValue = UUID().uuidString
                let request = try authorizationRequest(state: stateValue, pkce: pkce)
                let callbackURL = try await authorizer.authorize(request)
                diagnostics.record(category: .authentication, summary: "Native authorization callback received.")
                let authorizationCode = try validate(callbackURL: callbackURL, expectedState: stateValue)
                diagnostics.record(category: .authentication, summary: "Native authorization callback state matched.")
                diagnostics.record(category: .authentication, summary: "OAuth token exchange started.")
                let tokens = try await tokenExchanger.exchange(
                    code: authorizationCode,
                    pkceVerifier: pkce.verifier,
                    configuration: configuration
                )
                diagnostics.record(category: .authentication, summary: "OAuth token response decoded successfully.")
                try tokenStore.save(tokens)
                activeTokens = tokens
                diagnostics.record(category: .authentication, summary: "OAuth access token stored successfully.")
                try await establishSession(using: tokens)
            } catch {
                if let authorizationError = error as? ASWebAuthenticationSessionError,
                   authorizationError.code == .canceledLogin {
                    transition(to: .signedOut, reason: "Authorization session cancelled")
                    return
                }
                transition(to: .failed(failure(for: error)), reason: "Native sign-in did not complete")
                diagnostics.record(category: .authentication, summary: "Native sign-in did not complete. Failure category: \(failureCategory(for: error)).", correlationID: (error as? MobileAPIError)?.correlationID)
            }
        }
    }

    func signOut() {
        try? tokenStore.clear()
        activeTokens = nil
        NativeUnreadBadge.clear()
        transition(to: configuration.validation.isReady ? .signedOut : .contractUnavailable(configuration.validation), reason: "User signed out")
    }

    func selectRole(_ participantType: ParticipantType) {
        guard configuration.validation.isReady,
              let apiBaseURL = configuration.apiBaseURL else {
            transition(to: .contractUnavailable(configuration.validation), reason: "Configuration unavailable during role selection")
            return
        }

        Task {
            transition(to: .authenticating, reason: "Mobile role selection started")
            do {
                guard let storedTokens = try tokenStore.read() else {
                    try? tokenStore.clear()
                    activeTokens = nil
                    transition(to: .signedOut, reason: "No stored credential for role selection")
                    return
                }
                let tokens = try await usableTokens(from: storedTokens)
                let response = try await mobileSessionService(apiBaseURL: apiBaseURL)
                    .selectRole(participantType, accessToken: tokens.accessToken)
                transition(to: .authenticated(MobileSession(
                    actor: response.actor,
                    capabilities: ["messaging"]
                )), reason: "Mobile role selection completed")
            } catch {
                transition(to: .failed(failure(for: error, defaultTitle: "Role selection unavailable")), reason: "Mobile role selection did not complete")
                diagnostics.record(category: .authentication, summary: "Native mobile role selection did not complete. Failure category: \(failureCategory(for: error)).", correlationID: (error as? MobileAPIError)?.correlationID)
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

    func makeSocialStore() -> MobileSocialStore {
        guard let apiBaseURL = configuration.apiBaseURL,
              case .authenticated(let currentSession) = state else {
            return MobileSocialStore(
                api: MobileUnavailableSocialAPI(),
                accessTokenProvider: { throw MobileAPIError.unauthorized(correlationID: nil) },
                diagnostics: diagnostics)
        }

        return MobileSocialStore(
            api: URLSessionMobileSocialAPI(
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
        diagnostics.record(category: .authentication, summary: "Mobile session request started. Authorization header present: true.")
        let response = try await mobileSessionService(apiBaseURL: apiBaseURL)
            .bootstrap(accessToken: tokens.accessToken)
        diagnostics.record(category: .authentication, summary: "Mobile session response decoded successfully.", correlationID: response.correlationID)
        guard response.authenticated else {
            throw MobileAPIError.unauthorized(correlationID: response.correlationID)
        }
        if response.requiresParticipantSelection {
            guard !response.permittedParticipantTypes.isEmpty else {
                throw MobileAPIError.forbidden(correlationID: response.correlationID)
            }
            transition(to: .roleSelection(MobileRoleSelection(
                permittedParticipantTypes: response.permittedParticipantTypes,
                correlationID: response.correlationID))
                , reason: "Mobile role selection required")
            return
        }
        guard let actor = response.actor else {
            throw MobileAPIError.forbidden(correlationID: response.correlationID)
        }
        transition(to: .authenticated(MobileSession(
            actor: actor,
            capabilities: response.capabilities.messaging ? ["messaging"] : []
        )), reason: "Authenticated mobile session decoded")
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

    func makeAgentWorkspaceStore() -> MobileAgentWorkspaceStore {
        guard let apiBaseURL = configuration.apiBaseURL,
              case .authenticated(let currentSession) = state,
              currentSession.actor.identity.participantType == .agent else {
            return MobileAgentWorkspaceStore(
                api: MobileUnavailableAgentWorkspaceAPI(),
                accessTokenProvider: { throw MobileAPIError.forbidden(correlationID: nil) },
                diagnostics: diagnostics)
        }

        return MobileAgentWorkspaceStore(
            api: URLSessionMobileAgentWorkspaceAPI(client: MobileHTTPClient(baseURL: apiBaseURL)),
            accessTokenProvider: { [weak self] in
                guard let self else { throw MobileAPIError.unauthorized(correlationID: nil) }
                return try await self.accessTokenForRequest()
            },
            diagnostics: diagnostics)
    }

    private func validate(callbackURL: URL, expectedState: String) throws -> String {
        try OAuthCallbackValidator.authorizationCode(
            from: callbackURL,
            redirectScheme: configuration.redirectScheme,
            expectedState: expectedState)
    }

    private func mobileSessionService(apiBaseURL: URL) -> any MobileSessionServicing {
        sessionService ?? MobileSessionAPI(client: MobileHTTPClient(baseURL: apiBaseURL))
    }

    private func transition(to newState: MobileSessionState, reason: String) {
        #if DEBUG
        diagnostics.record(
            category: .authentication,
            summary: "Coordinator state transition \(state.diagnosticName) -> \(newState.diagnosticName). Reason: \(reason).")
        #endif
        state = newState
    }

    private func failure(for error: Error, defaultTitle: String = "Sign-in unavailable") -> UserFacingFailure {
        guard let apiError = error as? MobileAPIError else {
            return UserFacingFailure(title: defaultTitle, message: error.localizedDescription, correlationID: nil)
        }

        switch apiError {
        case .apiUnauthorized:
            return UserFacingFailure(
                title: "Secure sign-in required",
                message: "The mobile API rejected the current bearer credential. Please sign in again.",
                correlationID: apiError.correlationID)
        case .apiForbidden:
            return UserFacingFailure(
                title: "Mobile access unavailable",
                message: "Your Entra sign-in succeeded, but the server could not resolve an authorized mobile role.",
                correlationID: apiError.correlationID)
        case .networkUnavailable:
            return UserFacingFailure(
                title: "Connection unavailable",
                message: "Your secure session is still stored. Check your connection and try again.",
                correlationID: nil)
        case .apiConflict(let code, _):
            return UserFacingFailure(
                title: "Role selection required",
                message: code == "mobile_role_selection_required"
                    ? "Choose an authorized mobile role before continuing."
                    : apiError.localizedDescription,
                correlationID: apiError.correlationID)
        default:
            return UserFacingFailure(title: defaultTitle, message: apiError.localizedDescription, correlationID: apiError.correlationID)
        }
    }

    private func failureCategory(for error: Error) -> String {
        if let apiError = error as? MobileAPIError {
            return apiError.safeCode ?? String(describing: apiError)
        }
        if error is OAuthCallbackError { return "oauth_callback" }
        return String(describing: type(of: error))
    }
}

extension MobileSessionState {
    var diagnosticName: String {
        switch self {
        case .loading: "loading"
        case .contractUnavailable: "contractUnavailable"
        case .signedOut: "signedOut"
        case .authenticating: "authenticating"
        case .roleSelection: "roleSelection"
        case .authenticated: "authenticated"
        case .failed: "failed"
        }
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

        MobileDebugDiagnostics.record("OAuth token endpoint request started.")
        let (data, response) = try await URLSession.shared.data(for: request)
        guard let http = response as? HTTPURLResponse else { throw MobileAPIError.invalidServerResponse }
        let correlationID = http.value(forHTTPHeaderField: "X-Correlation-ID")
        MobileDebugDiagnostics.record("OAuth token endpoint response status \(http.statusCode).", correlationID: correlationID)
        guard (200 ... 299).contains(http.statusCode) else {
            throw MobileAPIError.server(statusCode: http.statusCode, correlationID: correlationID)
        }

        let tokenResponse: OAuthTokenResponse
        do {
            tokenResponse = try JSONDecoder.mobile.decode(OAuthTokenResponse.self, from: data)
        } catch {
            throw MobileAPIError.decodingFailed(correlationID: correlationID)
        }
        MobileDebugDiagnostics.record("OAuth token endpoint response decoded successfully.", correlationID: correlationID)

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

private struct MobileSessionAPI: MobileSessionServicing {
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
