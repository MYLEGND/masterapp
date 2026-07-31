import AuthenticationServices
import Combine
import Foundation

struct MobileSession: Equatable, Sendable {
    let actor: MobileActor
    let capabilities: Set<String>
    let permittedParticipantTypes: [ParticipantType]

    init(
        actor: MobileActor,
        capabilities: Set<String>,
        permittedParticipantTypes: [ParticipantType]? = nil
    ) {
        self.actor = actor
        self.capabilities = capabilities

        var permitted: [ParticipantType] = []
        for participantType in (permittedParticipantTypes ?? []) + [actor.identity.participantType] {
            if !permitted.contains(participantType) {
                permitted.append(participantType)
            }
        }
        self.permittedParticipantTypes = permitted
    }

    var alternateParticipantTypes: [ParticipantType] {
        permittedParticipantTypes.filter {
            $0 != actor.identity.participantType
        }
    }
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
    let launchCache: any LegendLaunchCaching
    private var activeTokens: OAuthTokenSet?
    /// The single in-flight token refresh, shared by every concurrent caller.
    private var refreshTask: Task<OAuthTokenSet, Error>?
    private var authenticationRecoveryTask: Task<Void, Never>?
    private var authenticationRecoveryAttempts = 0
    private var lastCredentialRenewalUtc: Date?
    private static let maximumAuthenticationRecoveryAttempts = 2
    /// How long before expiry a token is proactively renewed.
    private static let refreshLeadTime: TimeInterval = 5 * 60
    /// The shortest gap between two silent renewals triggered by rejected requests.
    private static let minimumRenewalInterval: TimeInterval = 30

    init(
        configuration: MobileConfiguration = .current,
        tokenStore: (any SecureTokenStoring)? = nil,
        authorizer: (any OAuthAuthorizing)? = nil,
        tokenExchanger: (any OAuthTokenExchanging)? = nil,
        sessionService: (any MobileSessionServicing)? = nil,
        diagnostics: LegendDiagnostics? = nil,
        launchCache: (any LegendLaunchCaching)? = nil
    ) {
        self.configuration = configuration
        self.tokenStore = tokenStore ?? KeychainTokenStore(service: configuration.bundleIdentifier)
        self.authorizer = authorizer ?? SystemBrowserAuthorizer()
        self.tokenExchanger = tokenExchanger ?? URLSessionOAuthTokenExchanger()
        self.sessionService = sessionService
        self.diagnostics = diagnostics ?? LegendDiagnostics()
        self.launchCache = launchCache ?? LegendLaunchCache()
    }

    func restore() {
        guard configuration.validation.isReady else {
            transition(
                to: .contractUnavailable(configuration.validation),
                reason: "Configuration unavailable during restore")
            diagnostics.record(
                category: .configuration,
                summary: "Native mobile contract configuration is incomplete.")
            return
        }

        // Authentication restoration remains server-authoritative. Cached launch data
        // may improve presentation, but it must never open an authenticated shell before
        // the API confirms the bearer credential and current participant identity.
        switch state {
        case .loading, .failed:
            break
        case .contractUnavailable, .signedOut, .authenticating,
             .roleSelection, .authenticated:
            return
        }

        let storedTokens: OAuthTokenSet
        do {
            guard let tokens = try tokenStore.read() else {
                transition(to: .signedOut, reason: "No stored mobile credential")
                return
            }

            storedTokens = tokens
        } catch {
            endSessionForRejectedCredential(
                reason: "Stored credential could not be decoded")
            diagnostics.record(
                category: .authentication,
                summary: "Stored mobile credential could not be read and was cleared.")
            return
        }

        transition(to: .loading, reason: "Stored session validation started")

        Task {
            do {
                diagnostics.record(
                    category: .authentication,
                    summary: "Stored mobile credential found; server session validation started.")

                let tokens = try await usableTokens(from: storedTokens)
                try await establishSession(using: tokens)
            } catch {
                let apiError = error as? MobileAPIError

                if apiError?.provesInvalidBearerCredential == true ||
                    isRefreshCredentialRejected(error) {
                    endSessionForRejectedCredential(
                        reason: "Stored credential rejected during restore")
                    diagnostics.record(
                        category: .authentication,
                        summary: "Stored mobile credential was rejected; sign-in is required again.",
                        correlationID: apiError?.correlationID)
                    return
                }

                // Preserve the secure credential for an explicit retry, but never
                // manufacture an authenticated actor from cached identity data.
                transition(
                    to: .failed(failure(for: error)),
                    reason: "Stored session could not be validated")
                diagnostics.record(
                    category: .authentication,
                    summary: "Server session validation could not complete; the credential was retained for retry.",
                    correlationID: apiError?.correlationID)
            }
        }
    }


    /// Records the identity the server just confirmed, so the next launch can render
    /// the correct shell before the network answers.
    private func cacheSession(_ session: MobileSession) {
        launchCache.writeSession(MobileSessionCacheEntry(
            actor: session.actor,
            capabilities: Array(session.capabilities),
            permittedParticipantTypes: session.permittedParticipantTypes,
            cachedUtc: Date()))
    }

    /// What the failure screen's retry should do. A user who still holds a valid
    /// credential is retried into their session; only a signed-out user is sent to the
    /// provider.
    func retrySessionEntry() {
        if (try? tokenStore.read()) != nil {
            restore()
        } else {
            signIn()
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
                diagnostics.record(
                    category: .authentication,
                    summary: "OAuth token response decoded successfully.")

                // The identity-provider token remains provisional until the mobile API
                // confirms an authorized Legend actor or authorized role selection.
                activeTokens = tokens
                try await establishSession(using: tokens)
                try tokenStore.save(tokens)

                diagnostics.record(
                    category: .authentication,
                    summary: "Server-confirmed OAuth credential stored successfully.")
            } catch {
                activeTokens = nil

                if let authorizationError = error as? ASWebAuthenticationSessionError,
                   authorizationError.code == .canceledLogin {
                    transition(
                        to: .signedOut,
                        reason: "Authorization session cancelled")
                    return
                }

                if (error as? MobileAPIError)?.provesInvalidBearerCredential == true {
                    try? tokenStore.clear()
                    launchCache.clear()
                }

                transition(
                    to: .failed(failure(for: error)),
                    reason: "Native sign-in did not complete")
                diagnostics.record(category: .authentication, summary: "Native sign-in did not complete. Failure category: \(failureCategory(for: error)).", correlationID: (error as? MobileAPIError)?.correlationID)
            }
        }
    }

    func signOut() {
        try? tokenStore.clear()
        activeTokens = nil
        launchCache.clear()
        NativeUnreadBadge.clear()
        transition(to: configuration.validation.isReady ? .signedOut : .contractUnavailable(configuration.validation), reason: "User signed out")
    }

    /// A protected endpoint rejected the bearer token.
    ///
    /// This is almost always an expired access token, which is a routine event and
    /// must never cost the user their session. The stored refresh token is only
    /// destroyed when the identity provider itself refuses to renew it; until then
    /// Legend renews silently and the user stays signed in, the way they expect after
    /// signing in once.
    func handleAuthenticationFailure(_ failure: UserFacingFailure) {
        guard authenticationRecoveryTask == nil else { return }

        // A resource endpoint that keeps answering 401 even with a freshly minted
        // token is a server-side problem, not a credential problem. Renewing on every
        // one of those would hammer the identity provider, so hold a short floor.
        if let lastCredentialRenewalUtc,
           Date().timeIntervalSince(lastCredentialRenewalUtc) < Self.minimumRenewalInterval {
            diagnostics.record(
                category: .authentication,
                summary: "Ignored a repeat credential rejection; the token was just renewed.",
                correlationID: failure.correlationID)
            return
        }

        guard authenticationRecoveryAttempts < Self.maximumAuthenticationRecoveryAttempts else {
            diagnostics.record(
                category: .authentication,
                summary: "Silent credential renewal kept failing; returning to sign-in.",
                correlationID: failure.correlationID)
            endSessionForRejectedCredential(reason: "Silent renewal exhausted")
            return
        }

        authenticationRecoveryAttempts += 1
        authenticationRecoveryTask = Task { [weak self] in
            await self?.renewCredentialSilently(after: failure)
            self?.authenticationRecoveryTask = nil
        }
    }

    private func renewCredentialSilently(after failure: UserFacingFailure) async {
        guard let stored = try? tokenStore.read(),
              stored.refreshToken?.isEmpty == false else {
            // Nothing left to renew with. This is the one case that legitimately
            // requires signing in again.
            endSessionForRejectedCredential(reason: "No refresh credential available")
            return
        }

        do {
            let renewed = try await usableTokens(from: stored, forcingRefresh: true)
            lastCredentialRenewalUtc = Date()
            try await establishSession(using: renewed)
            authenticationRecoveryAttempts = 0
            diagnostics.record(
                category: .authentication,
                summary: "Renewed the Legend credential silently; the session continues.",
                correlationID: failure.correlationID)
        } catch {
            if isRefreshCredentialRejected(error) {
                endSessionForRejectedCredential(reason: "Identity provider rejected the refresh credential")
                return
            }

            // A transient problem. Keep the session; the surfaces show their own
            // offline state and the next request retries.
            diagnostics.record(
                category: .authentication,
                summary: "Credential renewal could not complete; keeping the session for retry.",
                correlationID: (error as? MobileAPIError)?.correlationID)
        }
    }

    /// Only the identity provider can end a session. A refused refresh grant is the
    /// signal; anything else is treated as recoverable.
    private func isRefreshCredentialRejected(_ error: Error) -> Bool {
        switch error as? MobileAPIError {
        case .server(let statusCode, _):
            return statusCode == 400 || statusCode == 401
        case .unauthorized, .apiUnauthorized:
            return true
        default:
            return false
        }
    }

    private func endSessionForRejectedCredential(reason: String) {
        try? tokenStore.clear()
        activeTokens = nil
        launchCache.clear()
        NativeUnreadBadge.clear()
        authenticationRecoveryAttempts = 0
        transition(
            to: configuration.validation.isReady ? .signedOut : .contractUnavailable(configuration.validation),
            reason: reason)
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
                let session = MobileSession(
                    actor: response.actor,
                    capabilities: ["messaging"],
                    permittedParticipantTypes: response.permittedParticipantTypes)
                // A role switch changes the acting identity, so the previous role's
                // cached shell and content must not survive it.
                launchCache.clear()
                cacheSession(session)
                transition(to: .authenticated(session), reason: "Mobile role selection completed")
            } catch {
                transition(to: .failed(failure(for: error, defaultTitle: "Role selection unavailable")), reason: "Mobile role selection did not complete")
                diagnostics.record(category: .authentication, summary: "Native mobile role selection did not complete. Failure category: \(failureCategory(for: error)).", correlationID: (error as? MobileAPIError)?.correlationID)
            }
        }
    }

    /// Switches only between typed roles already authorized for the current
    /// Entra bearer token. The server independently verifies the requested
    /// role before the application is rebuilt for the selected account.
    func switchToRole(_ participantType: ParticipantType) {
        guard case .authenticated(let currentSession) = state,
              currentSession.alternateParticipantTypes.contains(participantType) else {
            return
        }

        selectRole(participantType)
    }

    func makeMessagingStore() -> MessagingStore {
        guard let apiBaseURL = configuration.apiBaseURL,
              case .authenticated(let currentSession) = state else {
            return MessagingStore(
                api: MobileContractUnavailableMessagingAPI(),
                accessTokenProvider: { throw MobileMessagingContractError.unavailable },
                diagnostics: diagnostics,
                actorParticipantType: .client
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
            diagnostics: diagnostics,
            actorParticipantType: currentSession.actor.identity.participantType
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
            diagnostics: diagnostics,
            persistence: .cached(
                launchCache,
                kind: .home,
                actorKey: legendLaunchActorKey(currentSession.actor.identity)))
    }

    func makeFinancialStore() -> MobileFinancialStore {
        guard let apiBaseURL = configuration.apiBaseURL,
              case .authenticated(let currentSession) = state else {
            return MobileFinancialStore(
                api: MobileUnavailableFinancialAPI(),
                accessTokenProvider: {
                    throw MobileAPIError.unauthorized(correlationID: nil)
                },
                diagnostics: diagnostics
            )
        }

        return MobileFinancialStore(
            api: URLSessionMobileHomeAPI(
                client: MobileHTTPClient(baseURL: apiBaseURL),
                participantType: currentSession.actor.identity.participantType
            ),
            accessTokenProvider: { [weak self] in
                guard let self else {
                    throw MobileAPIError.unauthorized(correlationID: nil)
                }

                return try await self.accessTokenForRequest()
            },
            diagnostics: diagnostics
        )
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
            diagnostics: diagnostics,
            persistence: .cached(
                launchCache,
                kind: .socialFeed,
                actorKey: legendLaunchActorKey(currentSession.actor.identity)))
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

    /// Discover composes the social and Journey Circles stores so following and
    /// connection requests keep flowing through their existing owners.
    func makeDiscoveryStore(
        social: MobileSocialStore,
        journeyCircles: MobileJourneyCirclesStore
    ) -> MobileDiscoveryStore {
        guard let apiBaseURL = configuration.apiBaseURL,
              case .authenticated(let currentSession) = state else {
            return MobileDiscoveryStore(
                api: MobileUnavailableDiscoveryAPI(),
                social: social,
                journeyCircles: journeyCircles,
                accessTokenProvider: { throw MobileAPIError.unauthorized(correlationID: nil) },
                diagnostics: diagnostics)
        }

        return MobileDiscoveryStore(
            api: URLSessionMobileDiscoveryAPI(
                client: MobileHTTPClient(baseURL: apiBaseURL),
                participantType: currentSession.actor.identity.participantType),
            social: social,
            journeyCircles: journeyCircles,
            accessTokenProvider: { [weak self] in
                guard let self else { throw MobileAPIError.unauthorized(correlationID: nil) }
                return try await self.accessTokenForRequest()
            },
            diagnostics: diagnostics)
    }

    func makeAccountStore() -> MobileAccountStore {
        guard let apiBaseURL = configuration.apiBaseURL,
              case .authenticated(let currentSession) = state else {
            return MobileAccountStore(
                api: MobileUnavailableAccountAPI(),
                accessTokenProvider: { throw MobileAPIError.unauthorized(correlationID: nil) },
                diagnostics: diagnostics)
        }

        return MobileAccountStore(
            api: URLSessionMobileAccountAPI(
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
        let session = MobileSession(
            actor: actor,
            capabilities: response.capabilities.messaging ? ["messaging"] : [],
            permittedParticipantTypes: response.permittedParticipantTypes)
        cacheSession(session)
        authenticationRecoveryAttempts = 0
        transition(to: .authenticated(session), reason: "Authenticated mobile session decoded")
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

    private func usableTokens(
        from tokens: OAuthTokenSet,
        forcingRefresh: Bool = false
    ) async throws -> OAuthTokenSet {
        // Renew well before expiry. A token handed out with seconds left dies
        // mid-flight and surfaces as a 401, which used to cost the user their session.
        let refreshThreshold = Date().addingTimeInterval(Self.refreshLeadTime)
        if !forcingRefresh, tokens.expiresAt > refreshThreshold {
            activeTokens = tokens
            return tokens
        }

        // Startup fires several store requests at once. Without coalescing, each one
        // would run its own refresh against the identity provider, and with rotating
        // refresh tokens those concurrent refreshes can invalidate each other and sign
        // the user out. One refresh serves every waiter.
        if let refreshTask {
            return try await refreshTask.value
        }

        guard let refreshToken = tokens.refreshToken,
              !refreshToken.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else {
            throw MobileAPIError.unauthorized(correlationID: nil)
        }

        let task = Task { () throws -> OAuthTokenSet in
            let refreshed = try await self.tokenExchanger.refresh(
                refreshToken: refreshToken,
                configuration: self.configuration)
            try self.tokenStore.save(refreshed)
            return refreshed
        }
        refreshTask = task

        do {
            let refreshed = try await task.value
            refreshTask = nil
            activeTokens = refreshed
            return refreshed
        } catch {
            refreshTask = nil
            throw error
        }
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
