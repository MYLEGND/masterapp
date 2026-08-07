import AuthenticationServices
import Combine
import Foundation
import LocalAuthentication

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
    func bootstrap(
        accessToken: String,
        preferredParticipantType: ParticipantType?
    ) async throws -> MobileBootstrapResponse
    func selectRole(_ participantType: ParticipantType, accessToken: String) async throws -> MobileRoleSelectionResponse
}

extension MobileSessionServicing {
    /// Existing test and contract implementations that only need the role-neutral
    /// session endpoint stay valid. The concrete API provides its own implementation
    /// and receives dynamic dispatch through this protocol requirement.
    func bootstrap(
        accessToken: String,
        preferredParticipantType: ParticipantType?
    ) async throws -> MobileBootstrapResponse {
        try await bootstrap(accessToken: accessToken)
    }
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
    @Published private(set) var isOfferingBiometricSignIn = false

    private let configuration: MobileConfiguration
    private let tokenStore: any SecureTokenStoring
    private let authorizer: any OAuthAuthorizing
    private let tokenExchanger: any OAuthTokenExchanging
    private let diagnostics: LegendDiagnostics
    private let sessionService: (any MobileSessionServicing)?
    private let biometricSecurity: any MobileBiometricSessionSecuring
    let launchCache: any LegendLaunchCaching
    private var activeTokens: OAuthTokenSet?

    /// Presentation cache for protected avatar resources. The resource URL
    /// contains a server-generated content version, so a changed image gets a
    /// new key instead of overwriting or competing with authoritative state.
    private let protectedImageCache: NSCache<NSString, NSData> = {
        let cache = NSCache<NSString, NSData>()
        cache.countLimit = 256
        cache.totalCostLimit = 32 * 1024 * 1024
        return cache
    }()

    /// One protected resource path owns at most one active network request.
    /// Multiple visible views awaiting the same avatar share that request
    /// instead of racing identical GETs before NSCache has been populated.
    private var protectedImageLoadTasks: [
        String: Task<Data?, Never>
    ] = [:]
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
        launchCache: (any LegendLaunchCaching)? = nil,
        biometricSecurity: (any MobileBiometricSessionSecuring)? = nil
    ) {
        self.configuration = configuration
        self.tokenStore = tokenStore ?? KeychainTokenStore(service: configuration.bundleIdentifier)
        self.authorizer = authorizer ?? SystemBrowserAuthorizer()
        self.tokenExchanger = tokenExchanger ?? URLSessionOAuthTokenExchanger()
        self.sessionService = sessionService
        self.diagnostics = diagnostics ?? LegendDiagnostics()
        self.launchCache = launchCache ?? LegendLaunchCache()
        self.biometricSecurity = biometricSecurity ?? MobileBiometricSessionSecurity()
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

        guard !storedTokens.requiresInteractiveSignIn else {
            endSessionForRejectedCredential(reason: "90-day interactive sign-in checkpoint reached")
            diagnostics.record(
                category: .authentication,
                summary: "The 90-day mobile security checkpoint requires a fresh interactive sign-in.")
            return
        }

        let cachedSession = launchCache.readSession(
            matchingCredentialFingerprint: LegendSessionCredentialFingerprint.make(
                from: storedTokens))?.session
        if let cachedSession,
           biometricSecurity.isEnabled(for: cachedSession.actor.identity) {
            transition(to: .loading, reason: "Face ID required before cached session restoration")
            Task { [weak self] in
                guard let self else { return }
                guard await self.biometricSecurity.authenticate() else {
                    self.transition(
                        to: .failed(UserFacingFailure(
                            title: "Face ID required",
                            message: "Use Face ID to reopen your protected Legend session.",
                            correlationID: nil)),
                        reason: "Face ID did not authenticate the cached session")
                    return
                }
                self.restoreStoredSession(storedTokens, cachedSession: cachedSession)
            }
            return
        }

        restoreStoredSession(storedTokens, cachedSession: cachedSession)
    }

    /// A returning member sees the exact last authorized account immediately. The
    /// cached identity is only a presentation hint; the API still validates the token
    /// and selected participant type in the background before it can persist.
    private func restoreStoredSession(
        _ storedTokens: OAuthTokenSet,
        cachedSession: MobileSession?
    ) {
        if let cachedSession {
            transition(to: .authenticated(cachedSession), reason: "Cached last account opened immediately")
        } else {
            transition(to: .loading, reason: "Stored session validation started")
        }

        Task { [weak self] in
            guard let self else { return }
            do {
                self.diagnostics.record(
                    category: .authentication,
                    summary: "Stored mobile credential found; server session validation started.")

                let tokens = try await self.usableTokens(from: storedTokens)
                try await self.establishSession(
                    using: tokens,
                    preferredParticipantType: cachedSession?.actor.identity.participantType)
            } catch {
                let apiError = error as? MobileAPIError

                if apiError?.provesInvalidBearerCredential == true ||
                    self.isRefreshCredentialRejected(error) {
                    self.endSessionForRejectedCredential(
                        reason: "Stored credential rejected during restore")
                    self.diagnostics.record(
                        category: .authentication,
                        summary: "Stored mobile credential was rejected; sign-in is required again.",
                        correlationID: apiError?.correlationID)
                    return
                }

                if cachedSession != nil {
                    // A temporary outage must not hide the already-authorized shell.
                    // The credential is retained and the next foreground refresh
                    // validates it again.
                    self.diagnostics.record(
                        category: .authentication,
                        summary: "Cached Legend session remains visible while server validation retries.",
                        correlationID: apiError?.correlationID)
                    return
                }

                self.transition(
                    to: .failed(self.failure(for: error)),
                    reason: "Stored session could not be validated")
                self.diagnostics.record(
                    category: .authentication,
                    summary: "Server session validation could not complete; the credential was retained for retry.",
                    correlationID: apiError?.correlationID)
            }
        }
    }


    /// Records the identity the server just confirmed, so the next launch can render
    /// the correct shell before the network answers.
    private func cacheSession(_ session: MobileSession) {
        guard let activeTokens,
              let credentialFingerprint = LegendSessionCredentialFingerprint.make(
                from: activeTokens) else {
            // There is no safe credential binding for this presentation cache.
            // Removing it is preferable to risking another account seeing it.
            launchCache.clear()
            return
        }

        launchCache.writeSession(MobileSessionCacheEntry(
            actor: session.actor,
            capabilities: Array(session.capabilities),
            permittedParticipantTypes: session.permittedParticipantTypes,
            cachedUtc: Date(),
            credentialFingerprint: credentialFingerprint))
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

                if case .authenticated(let session) = state {
                    offerBiometricSignInIfNeeded(for: session)
                }

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
                let session = try activateSelectedRole(
                    response,
                    expectedParticipantType: participantType,
                    clearCachedLaunch: true,
                    reason: "Mobile role selection completed")
                offerBiometricSignInIfNeeded(for: session)
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
                actorParticipantType: .client,
                isFounder: false
            )
        }

        let accessTokenProvider: () async throws -> String = { [weak self] in
            guard let self else { throw MobileAPIError.unauthorized(correlationID: nil) }
            return try await self.accessTokenForRequest()
        }

        return MessagingStore(
            api: URLSessionMessagingAPI(
                client: MobileHTTPClient(baseURL: apiBaseURL),
                participantType: currentSession.actor.identity.participantType),
            accessTokenProvider: accessTokenProvider,
            diagnostics: diagnostics,
            actorParticipantType: currentSession.actor.identity.participantType,
            isFounder: currentSession.capabilities.contains("founder"),
            realtime: MobileMessagingRealtimeClient(
                apiBaseURL: apiBaseURL,
                participantType: currentSession.actor.identity.participantType,
                accessTokenProvider: accessTokenProvider)
        )
    }

    func makeNotificationStore() -> MobileNotificationStore {
        guard let apiBaseURL = configuration.apiBaseURL,
              case .authenticated(let currentSession) = state else {
            return MobileNotificationStore(
                api: MobileUnavailableNotificationAPI(),
                accessTokenProvider: { throw MobileAPIError.unauthorized(correlationID: nil) },
                diagnostics: diagnostics)
        }

        let accessTokenProvider: () async throws -> String = { [weak self] in
            guard let self else { throw MobileAPIError.unauthorized(correlationID: nil) }
            return try await self.accessTokenForRequest()
        }
        return MobileNotificationStore(
            api: URLSessionMobileNotificationAPI(
                client: MobileHTTPClient(baseURL: apiBaseURL),
                participantType: currentSession.actor.identity.participantType),
            accessTokenProvider: accessTokenProvider,
            diagnostics: diagnostics)
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

    func makeDailyScriptureManagementStore() -> MobileDailyScriptureManagementStore {
        guard let apiBaseURL = configuration.apiBaseURL,
              case .authenticated(let currentSession) = state else {
            return MobileDailyScriptureManagementStore(
                api: MobileUnavailableDailyScriptureManagementAPI(),
                accessTokenProvider: { throw MobileAPIError.unauthorized(correlationID: nil) },
                diagnostics: diagnostics)
        }

        return MobileDailyScriptureManagementStore(
            api: URLSessionMobileDailyScriptureManagementAPI(
                client: MobileHTTPClient(baseURL: apiBaseURL),
                participantType: currentSession.actor.identity.participantType),
            accessTokenProvider: { [weak self] in
                guard let self else { throw MobileAPIError.unauthorized(correlationID: nil) }
                return try await self.accessTokenForRequest()
            },
            diagnostics: diagnostics)
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

    /// Discover is read-only; relationship changes are handled by the opened
    /// public profile through the social authority.
    func makeDiscoveryStore() -> MobileDiscoveryStore {
        guard let apiBaseURL = configuration.apiBaseURL,
              case .authenticated(let currentSession) = state else {
            return MobileDiscoveryStore(
                api: MobileUnavailableDiscoveryAPI(),
                accessTokenProvider: { throw MobileAPIError.unauthorized(correlationID: nil) },
                diagnostics: diagnostics)
        }

        return MobileDiscoveryStore(
            api: URLSessionMobileDiscoveryAPI(
                client: MobileHTTPClient(baseURL: apiBaseURL),
                participantType: currentSession.actor.identity.participantType),
            accessTokenProvider: { [weak self] in
                guard let self else { throw MobileAPIError.unauthorized(correlationID: nil) }
                return try await self.accessTokenForRequest()
            },
            diagnostics: diagnostics,
            actorParticipantType: currentSession.actor.identity.participantType)
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

    private func establishSession(
        using tokens: OAuthTokenSet,
        preferredParticipantType: ParticipantType? = nil
    ) async throws {
        guard let apiBaseURL = configuration.apiBaseURL else { throw MobileAPIError.invalidBaseURL }
        diagnostics.record(category: .authentication, summary: "Mobile session request started. Authorization header present: true.")
        let response = try await mobileSessionService(apiBaseURL: apiBaseURL)
            .bootstrap(
                accessToken: tokens.accessToken,
                preferredParticipantType: preferredParticipantType)
        diagnostics.record(category: .authentication, summary: "Mobile session response decoded successfully.", correlationID: response.correlationID)
        guard response.authenticated else {
            throw MobileAPIError.unauthorized(correlationID: response.correlationID)
        }
        if response.requiresParticipantSelection {
            guard !response.permittedParticipantTypes.isEmpty else {
                throw MobileAPIError.forbidden(correlationID: response.correlationID)
            }

            // A returning member has already made this choice. Some session
            // responses intentionally remain role-neutral, so resolve the saved
            // typed role through the same server-authorized selection endpoint
            // instead of ever returning them to the chooser.
            if let preferredParticipantType,
               response.permittedParticipantTypes.contains(preferredParticipantType) {
                let selected = try await mobileSessionService(apiBaseURL: apiBaseURL)
                    .selectRole(preferredParticipantType, accessToken: tokens.accessToken)
                _ = try activateSelectedRole(
                    selected,
                    expectedParticipantType: preferredParticipantType,
                    clearCachedLaunch: false,
                    reason: "Restored last selected mobile account")
                return
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
            capabilities: response.capabilities.sessionCapabilities,
            permittedParticipantTypes: response.permittedParticipantTypes)
        commitConfirmedSession(session, reason: "Authenticated mobile session decoded")
    }

    /// Applies the single authoritative role-selection response. Manual switches
    /// replace cached content; returning to the same stored role preserves it.
    private func activateSelectedRole(
        _ response: MobileRoleSelectionResponse,
        expectedParticipantType: ParticipantType,
        clearCachedLaunch: Bool,
        reason: String
    ) throws -> MobileSession {
        guard response.actor.identity.participantType == expectedParticipantType else {
            throw MobileAPIError.forbidden(correlationID: response.correlationID)
        }

        let session = MobileSession(
            actor: response.actor,
            capabilities: response.capabilities?.sessionCapabilities ?? ["messaging"],
            permittedParticipantTypes: response.permittedParticipantTypes)
        if clearCachedLaunch {
            launchCache.clear()
        }
        commitConfirmedSession(session, reason: reason)
        return session
    }

    /// Revalidation refreshes the persisted session without remounting an already
    /// visible shell for the same typed account. A different Agent/Client identity
    /// is a real account switch and remains the sole reason to rebuild the shell.
    private func commitConfirmedSession(_ session: MobileSession, reason: String) {
        cacheSession(session)
        authenticationRecoveryAttempts = 0

        if case .authenticated(let activeSession) = state,
           activeSession.actor.identity == session.actor.identity {
            diagnostics.record(
                category: .authentication,
                summary: "Validated the active mobile identity without remounting its application shell.")
            return
        }

        transition(to: .authenticated(session), reason: reason)
    }

    func cachedProtectedImageData(
        resourcePath: String
    ) -> Data? {
        let path = resourcePath.trimmingCharacters(
            in: .whitespacesAndNewlines)
        guard path.hasPrefix("/") else { return nil }
        return protectedImageCache.object(forKey: path as NSString) as Data?
    }

    func protectedImageData(
        resourcePath: String
    ) async -> Data? {
        let path = resourcePath.trimmingCharacters(
            in: .whitespacesAndNewlines)

        guard path.hasPrefix("/"),
              let apiBaseURL = configuration.apiBaseURL else {
            return nil
        }

        let key = path as NSString

        if let cached = protectedImageCache.object(forKey: key) {
            return cached as Data
        }

        if let existing = protectedImageLoadTasks[path] {
            return await existing.value
        }

        let participantHeaders = Self.protectedImageHeaders(for: state)
        let task = Task { [weak self] () -> Data? in
            guard let self else { return nil }

            do {
                let accessToken = try await self.accessTokenForRequest()

                return try await MobileHTTPClient(
                    baseURL: apiBaseURL
                ).getData(
                    path,
                    accessToken: accessToken,
                    headers: participantHeaders)
            } catch {
                return nil
            }
        }

        protectedImageLoadTasks[path] = task

        let data = await task.value

        protectedImageLoadTasks.removeValue(
            forKey: path)

        if let data {
            protectedImageCache.setObject(
                data as NSData,
                forKey: key,
                cost: data.count)
        }

        return data
    }

    static func protectedImageHeaders(
        for state: MobileSessionState
    ) -> [String: String] {
        guard case .authenticated(let session) = state else {
            return [:]
        }

        return [
            "X-Legend-Participant-Type": session.actor.identity.participantType.rawValue
        ]
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
            let refreshedResponse = try await self.tokenExchanger.refresh(
                refreshToken: refreshToken,
                configuration: self.configuration)
            let refreshed = tokens.refreshed(
                accessToken: refreshedResponse.accessToken,
                refreshToken: refreshedResponse.refreshToken,
                expiresAt: refreshedResponse.expiresAt)
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

    var isBiometricSignInAvailable: Bool {
        biometricSecurity.isAvailable
    }

    var isBiometricSignInEnabled: Bool {
        guard case .authenticated(let session) = state else { return false }
        return biometricSecurity.isEnabled(for: session.actor.identity)
    }

    func setBiometricSignInEnabled(_ isEnabled: Bool) {
        guard case .authenticated(let session) = state else { return }
        let identity = session.actor.identity
        if !isEnabled {
            biometricSecurity.disable(for: identity)
            return
        }

        Task { [weak self] in
            guard let self else { return }
            _ = await self.biometricSecurity.enable(for: identity)
        }
    }

    func enableBiometricSignInFromEnrollment() {
        guard case .authenticated(let session) = state else { return }
        isOfferingBiometricSignIn = false
        let identity = session.actor.identity
        Task { [weak self] in
            guard let self else { return }
            _ = await self.biometricSecurity.enable(for: identity)
        }
    }

    func declineBiometricSignInEnrollment() {
        guard case .authenticated(let session) = state else { return }
        biometricSecurity.markPrompted(for: session.actor.identity)
        isOfferingBiometricSignIn = false
    }

    private func offerBiometricSignInIfNeeded(for session: MobileSession) {
        guard biometricSecurity.isAvailable,
              !biometricSecurity.hasPrompted(for: session.actor.identity) else {
            return
        }
        isOfferingBiometricSignIn = true
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

/// Device-local protection for an already-authorized Legend session. The bearer
/// credential remains in Keychain; this only decides whether the device should ask
/// for Face ID before reopening that member's cached shell.
@MainActor
protocol MobileBiometricSessionSecuring: AnyObject {
    var isAvailable: Bool { get }
    func hasPrompted(for identity: LogicalParticipantIdentity) -> Bool
    func isEnabled(for identity: LogicalParticipantIdentity) -> Bool
    func markPrompted(for identity: LogicalParticipantIdentity)
    func disable(for identity: LogicalParticipantIdentity)
    func enable(for identity: LogicalParticipantIdentity) async -> Bool
    func authenticate() async -> Bool
}

@MainActor
final class MobileBiometricSessionSecurity: MobileBiometricSessionSecuring {
    private let defaults: UserDefaults
    private let contextFactory: () -> LAContext

    init(
        defaults: UserDefaults = .standard,
        contextFactory: @escaping () -> LAContext = { LAContext() }
    ) {
        self.defaults = defaults
        self.contextFactory = contextFactory
    }

    var isAvailable: Bool {
        let context = contextFactory()
        var error: NSError?
        return context.canEvaluatePolicy(
            .deviceOwnerAuthenticationWithBiometrics,
            error: &error) && context.biometryType == .faceID
    }

    func hasPrompted(for identity: LogicalParticipantIdentity) -> Bool {
        defaults.bool(forKey: key(for: identity, suffix: "prompted"))
    }

    func isEnabled(for identity: LogicalParticipantIdentity) -> Bool {
        defaults.bool(forKey: key(for: identity, suffix: "enabled"))
    }

    func markPrompted(for identity: LogicalParticipantIdentity) {
        defaults.set(true, forKey: key(for: identity, suffix: "prompted"))
    }

    func disable(for identity: LogicalParticipantIdentity) {
        markPrompted(for: identity)
        defaults.set(false, forKey: key(for: identity, suffix: "enabled"))
    }

    func enable(for identity: LogicalParticipantIdentity) async -> Bool {
        markPrompted(for: identity)
        let authenticated = await evaluateFaceID()
        defaults.set(authenticated, forKey: key(for: identity, suffix: "enabled"))
        return authenticated
    }

    func authenticate() async -> Bool {
        await evaluateFaceID()
    }

    private func evaluateFaceID() async -> Bool {
        let context = contextFactory()
        var error: NSError?
        guard context.canEvaluatePolicy(
            .deviceOwnerAuthenticationWithBiometrics,
            error: &error),
            context.biometryType == .faceID else {
            return false
        }

        return await withCheckedContinuation { continuation in
            context.evaluatePolicy(
                .deviceOwnerAuthenticationWithBiometrics,
                localizedReason: "Use Face ID to access your Legend account.") { success, _ in
                    continuation.resume(returning: success)
                }
        }
    }

    private func key(
        for identity: LogicalParticipantIdentity,
        suffix: String
    ) -> String {
        "com.mylegnd.mobile.face-id.\(identity.participantType.rawValue).\(identity.userID).\(suffix)"
    }
}

private struct MobileSessionAPI: MobileSessionServicing {
    let client: MobileHTTPClient

    func bootstrap(accessToken: String) async throws -> MobileBootstrapResponse {
        try await client.get("/api/v1/mobile/session", accessToken: accessToken, response: MobileBootstrapResponse.self)
    }

    func bootstrap(
        accessToken: String,
        preferredParticipantType: ParticipantType?
    ) async throws -> MobileBootstrapResponse {
        let headers = preferredParticipantType.map {
            ["X-Legend-Participant-Type": $0.rawValue]
        } ?? [:]
        return try await client.get(
            "/api/v1/mobile/session",
            accessToken: accessToken,
            headers: headers,
            response: MobileBootstrapResponse.self)
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
    let isFounder: Bool
    let canManageScripture: Bool

    private enum CodingKeys: String, CodingKey {
        case messaging, isFounder, canManageScripture
    }

    init(
        messaging: Bool,
        isFounder: Bool = false,
        canManageScripture: Bool = false
    ) {
        self.messaging = messaging
        self.isFounder = isFounder
        self.canManageScripture = canManageScripture
    }

    init(from decoder: Decoder) throws {
        let container = try decoder.container(keyedBy: CodingKeys.self)
        messaging = try container.decodeIfPresent(Bool.self, forKey: .messaging) ?? false
        isFounder = try container.decodeIfPresent(Bool.self, forKey: .isFounder) ?? false
        canManageScripture = try container.decodeIfPresent(Bool.self, forKey: .canManageScripture) ?? false
    }

    var sessionCapabilities: Set<String> {
        var capabilities: Set<String> = messaging ? ["messaging"] : []
        if isFounder {
            capabilities.insert("founder")
        }
        if canManageScripture {
            capabilities.insert("scripture-management")
        }
        return capabilities
    }
}

struct MobileRoleSelectionRequest: Encodable {
    let participantType: String
}

struct MobileRoleSelectionResponse: Decodable {
    let actor: MobileActor
    let permittedParticipantTypes: [ParticipantType]
    let correlationID: String
    let capabilities: MobileCapabilities?

    init(
        actor: MobileActor,
        permittedParticipantTypes: [ParticipantType],
        correlationID: String,
        capabilities: MobileCapabilities? = nil
    ) {
        self.actor = actor
        self.permittedParticipantTypes = permittedParticipantTypes
        self.correlationID = correlationID
        self.capabilities = capabilities
    }

    private enum CodingKeys: String, CodingKey {
        case actor
        case permittedParticipantTypes
        case correlationID = "correlationId"
        case capabilities
    }
}
