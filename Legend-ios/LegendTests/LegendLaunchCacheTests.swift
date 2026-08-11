import Foundation
import XCTest
@testable import Legend

@MainActor
final class LegendLaunchCacheTests: XCTestCase {
    /// A returning user opens straight into their own shell with no network involved.
    func testCachedSessionOpensTheShellWithoutWaitingOnTheNetwork() async throws {
        let cache = InMemoryLaunchCache()
        cache.writeSession(MobileSessionCacheEntry(
            actor: try Self.cachedActor(),
            capabilities: ["messaging"],
            permittedParticipantTypes: [.client],
            cachedUtc: Date(),
            credentialFingerprint: LegendSessionCredentialFingerprint.make(
                from: Self.validTokens())))

        let coordinator = MobileSessionCoordinator(
            configuration: Self.readyConfiguration(),
            tokenStore: StubTokenStore(tokens: Self.validTokens()),
            authorizer: NeverAuthorizer(),
            tokenExchanger: NeverExchanger(),
            sessionService: HangingSessionService(),
            diagnostics: LegendDiagnostics(),
            launchCache: cache)

        coordinator.restore()

        // No awaiting: the shell is authenticated on the very first turn of the loop.
        guard case .authenticated(let session) = coordinator.state else {
            return XCTFail("Expected the cached session to open the shell immediately")
        }
        XCTAssertEqual(session.actor.displayName, "Client One")
    }

    func testConfirmingTheCachedIdentityDoesNotRemountTheVisibleShell() async throws {
        let cache = InMemoryLaunchCache()
        cache.writeSession(MobileSessionCacheEntry(
            actor: try Self.cachedActor(),
            capabilities: ["messaging"],
            permittedParticipantTypes: [.client],
            cachedUtc: Date(),
            credentialFingerprint: LegendSessionCredentialFingerprint.make(
                from: Self.validTokens())))
        let confirmedActor = try MobileActor(
            identity: LogicalParticipantIdentity(
                userID: "client-one",
                participantType: .client),
            profileID: "00000000-0000-0000-0000-000000000002",
            displayName: "Client One Updated",
            avatar: nil)
        let coordinator = MobileSessionCoordinator(
            configuration: Self.readyConfiguration(),
            tokenStore: StubTokenStore(tokens: Self.validTokens()),
            authorizer: NeverAuthorizer(),
            tokenExchanger: NeverExchanger(),
            sessionService: AcceptingSessionService(actor: confirmedActor),
            diagnostics: LegendDiagnostics(),
            launchCache: cache)

        coordinator.restore()
        try await Task.sleep(for: .milliseconds(200))

        guard case .authenticated(let visibleSession) = coordinator.state else {
            return XCTFail("Expected the cached shell to remain authenticated")
        }
        // The root is keyed by logical identity, so a refreshed projection for
        // the same account updates in place instead of remounting the shell.
        XCTAssertEqual(visibleSession.actor.displayName, "Client One Updated")
        XCTAssertEqual(cache.readSession()?.actor.displayName, "Client One Updated")
    }

    /// Cached identity is a rendering hint, never an authority. A rejected credential
    /// still signs the user out and wipes the cache.
    func testRejectedCredentialClearsTheCachedSession() async throws {
        let cache = InMemoryLaunchCache()
        cache.writeSession(MobileSessionCacheEntry(
            actor: try Self.cachedActor(),
            capabilities: [],
            permittedParticipantTypes: [.client],
            cachedUtc: Date()))

        let coordinator = MobileSessionCoordinator(
            configuration: Self.readyConfiguration(),
            tokenStore: StubTokenStore(tokens: Self.validTokens()),
            authorizer: NeverAuthorizer(),
            tokenExchanger: NeverExchanger(),
            sessionService: RejectingSessionService(),
            diagnostics: LegendDiagnostics(),
            launchCache: cache)

        coordinator.restore()
        try await Task.sleep(for: .milliseconds(200))

        guard case .signedOut = coordinator.state else {
            return XCTFail("Expected a rejected credential to sign the user out")
        }
        XCTAssertNil(cache.readSession(), "The cached identity must not survive a rejection")
    }

    func testDifferentCredentialNeverPresentsAnotherAccountFromLaunchCache() async throws {
        let cache = InMemoryLaunchCache()
        let cachedTokens = OAuthTokenSet(
            accessToken: "cached-access-token",
            refreshToken: "cached-refresh-token",
            expiresAt: .distantFuture)
        let currentTokens = OAuthTokenSet(
            accessToken: "current-access-token",
            refreshToken: "current-refresh-token",
            expiresAt: .distantFuture)
        cache.writeSession(MobileSessionCacheEntry(
            actor: try Self.cachedActor(),
            capabilities: ["messaging"],
            permittedParticipantTypes: [.client],
            cachedUtc: Date(),
            credentialFingerprint: LegendSessionCredentialFingerprint.make(
                from: cachedTokens)))

        let coordinator = MobileSessionCoordinator(
            configuration: Self.readyConfiguration(),
            tokenStore: StubTokenStore(tokens: currentTokens),
            authorizer: NeverAuthorizer(),
            tokenExchanger: NeverExchanger(),
            sessionService: HangingSessionService(),
            diagnostics: LegendDiagnostics(),
            launchCache: cache)

        coordinator.restore()

        XCTAssertEqual(coordinator.state, .loading)
    }

    func testNinetyDayCheckpointClearsTheStoredSessionAndRequiresInteractiveSignIn() throws {
        let store = MutableTokenStore(tokens: OAuthTokenSet(
            accessToken: "access",
            refreshToken: "refresh",
            expiresAt: .distantFuture,
            interactiveSignInAt: Date().addingTimeInterval(-60 * 60 * 24 * 90)))
        let cache = InMemoryLaunchCache()
        cache.writeSession(MobileSessionCacheEntry(
            actor: try Self.cachedActor(),
            capabilities: ["messaging"],
            permittedParticipantTypes: [.client],
            cachedUtc: Date()))
        let coordinator = MobileSessionCoordinator(
            configuration: Self.readyConfiguration(),
            tokenStore: store,
            authorizer: NeverAuthorizer(),
            tokenExchanger: NeverExchanger(),
            sessionService: HangingSessionService(),
            diagnostics: LegendDiagnostics(),
            launchCache: cache)

        coordinator.restore()

        XCTAssertEqual(coordinator.state, .signedOut)
        XCTAssertNil(try store.read())
        XCTAssertNil(cache.readSession())
    }

    /// An expired cache is not trusted; the app resolves the session over the network
    /// before showing a shell.
    func testStaleCachedSessionIsIgnored() throws {
        let cache = InMemoryLaunchCache()
        cache.writeSession(MobileSessionCacheEntry(
            actor: try Self.cachedActor(),
            capabilities: [],
            permittedParticipantTypes: [.client],
            cachedUtc: Date().addingTimeInterval(-60 * 60 * 24 * 91)))

        XCTAssertNil(cache.readSession())
    }

    func testProtectedImageCachePersistsAcrossLaunchAndRefreshesByVersion() {
        let directoryName = "LegendAvatarCacheTests-\(UUID().uuidString)"
        let originalPath = "/api/v1/mobile/profile-images/Client/00000000-0000-0000-0000-000000000001?v=before"
        let updatedPath = "/api/v1/mobile/profile-images/Client/00000000-0000-0000-0000-000000000001?v=after"
        let originalData = Data("last authorized avatar".utf8)

        let writer = LegendLaunchCache(directoryName: directoryName)
        defer { writer.clear() }
        writer.writeProtectedImage(originalData, resourcePath: originalPath)

        // A fresh cache instance models a cold application launch.
        let reader = LegendLaunchCache(directoryName: directoryName)
        XCTAssertEqual(reader.readProtectedImage(resourcePath: originalPath), originalData)
        XCTAssertEqual(
            reader.readLastKnownProtectedImage(resourcePath: updatedPath),
            originalData,
            "The previous authorized photo remains visible while the new immutable version revalidates.")
        XCTAssertNil(
            reader.readProtectedImage(resourcePath: updatedPath),
            "A changed version must still make a network request for the replacement image.")
    }

    /// Home renders last-known content on its first frame instead of a loading state.
    func testHomeStoreHydratesFromCacheBeforeAnyRequest() throws {
        let box = PayloadBox()
        let persistence = LegendStorePersistence<MobileHomeResponse>(
            read: { box.home },
            write: { box.home = $0 })
        box.home = try Self.cachedHome()

        let store = MobileHomeStore(
            api: MobileUnavailableHomeAPI(),
            accessTokenProvider: { "token" },
            diagnostics: LegendDiagnostics(),
            persistence: persistence)

        guard case .loaded = store.state else {
            return XCTFail("Expected Home to open on cached content, not a loading state")
        }
    }

    /// A failed refresh must never replace content that is already on screen.
    func testCachedHomeSurvivesAFailedRefresh() async throws {
        let box = PayloadBox()
        box.home = try Self.cachedHome()

        let store = MobileHomeStore(
            api: MobileUnavailableHomeAPI(),
            accessTokenProvider: { "token" },
            diagnostics: LegendDiagnostics(),
            persistence: LegendStorePersistence<MobileHomeResponse>(
                read: { box.home },
                write: { box.home = $0 }))

        _ = await store.loadIfNeeded()

        guard case .loaded = store.state else {
            return XCTFail("Cached home content must remain visible after a failed load")
        }
    }


    /// The whole point: an expired access token is routine. It must be renewed
    /// silently, never charged to the user as another trip through the login provider.
    func testExpiredAccessTokenIsRenewedSilentlyAndKeepsTheSession() async throws {
        let store = MutableTokenStore(tokens: Self.expiringTokens())
        let exchanger = RecordingExchanger(refreshed: Self.freshTokens())
        let coordinator = MobileSessionCoordinator(
            configuration: Self.readyConfiguration(),
            tokenStore: store,
            authorizer: NeverAuthorizer(),
            tokenExchanger: exchanger,
            sessionService: AcceptingSessionService(actor: try Self.cachedActor()),
            diagnostics: LegendDiagnostics(),
            launchCache: InMemoryLaunchCache())

        coordinator.restore()
        try await Task.sleep(for: .milliseconds(250))

        guard case .authenticated = coordinator.state else {
            return XCTFail("An expired access token must renew silently, not sign the user out")
        }
        let refreshes = await exchanger.refreshCount()
        XCTAssertEqual(refreshes, 1)
        XCTAssertNotNil(try store.read(), "The credential must survive a routine renewal")
    }

    /// A protected endpoint returning 401 is an expired token, not a revoked account.
    /// It must renew, not destroy the stored credential.
    func testProtectedEndpointRejectionRenewsInsteadOfSigningOut() async throws {
        let store = MutableTokenStore(tokens: Self.freshTokens())
        let exchanger = RecordingExchanger(refreshed: Self.freshTokens())
        let coordinator = MobileSessionCoordinator(
            configuration: Self.readyConfiguration(),
            tokenStore: store,
            authorizer: NeverAuthorizer(),
            tokenExchanger: exchanger,
            sessionService: AcceptingSessionService(actor: try Self.cachedActor()),
            diagnostics: LegendDiagnostics(),
            launchCache: InMemoryLaunchCache())

        coordinator.restore()
        try await Task.sleep(for: .milliseconds(200))

        coordinator.handleAuthenticationFailure(UserFacingFailure(
            title: "Home unavailable",
            message: "The bearer credential was rejected.",
            correlationID: "expired-access-token"))
        try await Task.sleep(for: .milliseconds(300))

        guard case .authenticated = coordinator.state else {
            return XCTFail("A 401 from a protected endpoint must not end the session")
        }
        XCTAssertNotNil(try store.read(), "The refresh credential must not be destroyed by a 401")
    }

    /// The one case that legitimately requires signing in again: the identity provider
    /// refuses to renew.
    func testRejectedRefreshGrantSignsTheUserOut() async throws {
        let store = MutableTokenStore(tokens: Self.freshTokens())
        let coordinator = MobileSessionCoordinator(
            configuration: Self.readyConfiguration(),
            tokenStore: store,
            authorizer: NeverAuthorizer(),
            tokenExchanger: RejectingExchanger(),
            sessionService: AcceptingSessionService(actor: try Self.cachedActor()),
            diagnostics: LegendDiagnostics(),
            launchCache: InMemoryLaunchCache())

        coordinator.restore()
        try await Task.sleep(for: .milliseconds(200))

        coordinator.handleAuthenticationFailure(UserFacingFailure(
            title: "Home unavailable",
            message: "Rejected.",
            correlationID: nil))
        try await Task.sleep(for: .milliseconds(400))

        guard case .signedOut = coordinator.state else {
            return XCTFail("A refused refresh grant must return the user to sign-in")
        }
        XCTAssertNil(try store.read())
    }

    /// Losing connectivity is not losing your account.
    func testOfflineLaunchKeepsTheStoredCredential() async throws {
        let store = MutableTokenStore(tokens: Self.freshTokens())
        let coordinator = MobileSessionCoordinator(
            configuration: Self.readyConfiguration(),
            tokenStore: store,
            authorizer: NeverAuthorizer(),
            tokenExchanger: RecordingExchanger(refreshed: Self.freshTokens()),
            sessionService: OfflineSessionService(),
            diagnostics: LegendDiagnostics(),
            launchCache: InMemoryLaunchCache())

        coordinator.restore()
        try await Task.sleep(for: .milliseconds(250))

        XCTAssertNotNil(
            try store.read(),
            "An offline launch must never discard the credential and force a new login")
    }


    /// Cache must be stale-WHILE-revalidate. Hydrating from disk shows content
    /// immediately, but it must never suppress the network refresh, or the app would
    /// serve yesterday's data until the user manually pulls.
    func testHydratedStoreStillRevalidatesOverTheNetwork() async throws {
        let box = PayloadBox()
        box.home = try Self.cachedHome()
        let api = CountingHomeAPI(response: try Self.cachedHome())

        let store = MobileHomeStore(
            api: api,
            accessTokenProvider: { "token" },
            diagnostics: LegendDiagnostics(),
            persistence: LegendStorePersistence<MobileHomeResponse>(
                read: { box.home },
                write: { box.home = $0 }))

        // Content is on screen straight away.
        guard case .loaded = store.state else {
            return XCTFail("Expected hydrated content on the first frame")
        }

        _ = await store.loadIfNeeded()

        let calls = await api.calls()
        XCTAssertEqual(calls, 1, "A hydrated store must still revalidate on launch")
    }

    private static func cachedHome() throws -> MobileHomeResponse {
        let identity = try LogicalParticipantIdentity(
            userID: "client-one",
            participantType: .client)
        return MobileHomeResponse(
            identity: MobileHomeIdentity(
                userID: identity.userID,
                participantType: .client,
                profileID: UUID(),
                displayName: "Client One"),
            messaging: MobileMessagingSummary(unreadCount: 0, conversationCount: 0),
            journey: nil,
            upcomingAppointments: [],
            actions: [],
            dailyScripture: MobileDailyScripture(
                date: "2026-07-31",
                reference: "Psalm 1:1",
                translation: "KJV",
                verses: ["Psalm 1:1"],
                text: "Blessed is the man that walketh not in the counsel of the ungodly."),
            activeClientCount: 0)
    }

    private static func readyConfiguration() -> MobileConfiguration {
        MobileConfiguration(
            bundleIdentifier: "com.example.legend",
            apiBaseURL: URL(string: "https://staging.example.test"),
            authorizationEndpoint: URL(string: "https://identity.example.test/authorize"),
            tokenEndpoint: URL(string: "https://identity.example.test/token"),
            clientID: "example-client-id",
            redirectScheme: "legend",
            scope: "openid profile offline_access",
            audience: "api://legend")
    }

    private static func cachedActor() throws -> MobileActor {
        try MobileActor(
            identity: try LogicalParticipantIdentity(userID: "client-one", participantType: .client),
            profileID: "00000000-0000-0000-0000-000000000002",
            displayName: "Client One",
            avatar: nil)
    }


    private static func expiringTokens() -> OAuthTokenSet {
        OAuthTokenSet(
            accessToken: "stale-access",
            refreshToken: "refresh",
            expiresAt: Date().addingTimeInterval(-60))
    }

    private static func freshTokens() -> OAuthTokenSet {
        OAuthTokenSet(
            accessToken: "access",
            refreshToken: "refresh",
            expiresAt: Date().addingTimeInterval(3_600))
    }
    private static func validTokens() -> OAuthTokenSet {
        OAuthTokenSet(
            accessToken: "access",
            refreshToken: "refresh",
            expiresAt: Date().addingTimeInterval(3_600))
    }
}

private final class PayloadBox: @unchecked Sendable {
    var home: MobileHomeResponse?
}

private final class InMemoryLaunchCache: LegendLaunchCaching, @unchecked Sendable {
    private var session: MobileSessionCacheEntry?
    private var payloads: [String: Data] = [:]

    func readSession() -> MobileSessionCacheEntry? {
        guard let session, session.isFresh else { return nil }
        return session
    }

    func writeSession(_ entry: MobileSessionCacheEntry) { session = entry }

    func readPayload(_ kind: LegendLaunchPayloadKind, actorKey: String) -> Data? {
        payloads["\(kind.rawValue)-\(actorKey)"]
    }

    func writePayload(_ data: Data, kind: LegendLaunchPayloadKind, actorKey: String) {
        payloads["\(kind.rawValue)-\(actorKey)"] = data
    }

    func clear() {
        session = nil
        payloads.removeAll()
    }
}

private struct StubTokenStore: SecureTokenStoring {
    let tokens: OAuthTokenSet?
    func read() throws -> OAuthTokenSet? { tokens }
    func save(_ tokens: OAuthTokenSet) throws {}
    func clear() throws {}
}

private final class NeverAuthorizer: OAuthAuthorizing {
    func authorize(_ request: OAuthAuthorizationRequest) async throws -> URL {
        throw MobileAPIError.unauthorized(correlationID: nil)
    }
}

private struct NeverExchanger: OAuthTokenExchanging {
    func exchange(code: String, pkceVerifier: String, configuration: MobileConfiguration) async throws -> OAuthTokenSet {
        throw MobileAPIError.unauthorized(correlationID: nil)
    }

    func refresh(refreshToken: String, configuration: MobileConfiguration) async throws -> OAuthTokenSet {
        throw MobileAPIError.unauthorized(correlationID: nil)
    }
}

/// Never answers, proving the shell does not wait on session resolution.
private struct HangingSessionService: MobileSessionServicing {
    func bootstrap(accessToken: String) async throws -> MobileBootstrapResponse {
        try await Task.sleep(for: .seconds(30))
        throw MobileAPIError.networkUnavailable
    }

    func selectRole(_ participantType: ParticipantType, accessToken: String) async throws -> MobileRoleSelectionResponse {
        throw MobileAPIError.networkUnavailable
    }
}

private struct RejectingSessionService: MobileSessionServicing {
    func bootstrap(accessToken: String) async throws -> MobileBootstrapResponse {
        throw MobileAPIError.apiUnauthorized(
            code: "mobile_authentication_required",
            correlationID: "launch-cache-test")
    }

    func selectRole(_ participantType: ParticipantType, accessToken: String) async throws -> MobileRoleSelectionResponse {
        throw MobileAPIError.apiUnauthorized(
            code: "mobile_authentication_required",
            correlationID: "launch-cache-test")
    }
}

private final class MutableTokenStore: SecureTokenStoring, @unchecked Sendable {
    private var tokens: OAuthTokenSet?
    init(tokens: OAuthTokenSet?) { self.tokens = tokens }
    func read() throws -> OAuthTokenSet? { tokens }
    func save(_ tokens: OAuthTokenSet) throws { self.tokens = tokens }
    func clear() throws { tokens = nil }
}

private actor RecordingExchanger: OAuthTokenExchanging {
    private let refreshed: OAuthTokenSet
    private var refreshes = 0

    init(refreshed: OAuthTokenSet) { self.refreshed = refreshed }

    func refreshCount() -> Int { refreshes }

    func exchange(code: String, pkceVerifier: String, configuration: MobileConfiguration) async throws -> OAuthTokenSet {
        throw MobileAPIError.unauthorized(correlationID: nil)
    }

    func refresh(refreshToken: String, configuration: MobileConfiguration) async throws -> OAuthTokenSet {
        refreshes += 1
        return refreshed
    }
}

/// Stands in for an identity provider answering `invalid_grant`.
private struct RejectingExchanger: OAuthTokenExchanging {
    func exchange(code: String, pkceVerifier: String, configuration: MobileConfiguration) async throws -> OAuthTokenSet {
        throw MobileAPIError.server(statusCode: 400, correlationID: nil)
    }

    func refresh(refreshToken: String, configuration: MobileConfiguration) async throws -> OAuthTokenSet {
        throw MobileAPIError.server(statusCode: 400, correlationID: nil)
    }
}

private struct AcceptingSessionService: MobileSessionServicing {
    let actor: MobileActor

    func bootstrap(accessToken: String) async throws -> MobileBootstrapResponse {
        MobileBootstrapResponse(
            authenticated: true,
            actor: actor,
            permittedParticipantTypes: [.client],
            requiresParticipantSelection: false,
            capabilities: MobileCapabilities(messaging: true),
            correlationID: "accepting")
    }

    func selectRole(_ participantType: ParticipantType, accessToken: String) async throws -> MobileRoleSelectionResponse {
        throw MobileAPIError.networkUnavailable
    }
}

private struct OfflineSessionService: MobileSessionServicing {
    func bootstrap(accessToken: String) async throws -> MobileBootstrapResponse {
        throw MobileAPIError.networkUnavailable
    }

    func selectRole(_ participantType: ParticipantType, accessToken: String) async throws -> MobileRoleSelectionResponse {
        throw MobileAPIError.networkUnavailable
    }
}

private actor CountingHomeAPI: MobileHomeAPI {
    private let response: MobileHomeResponse
    private var callCount = 0

    init(response: MobileHomeResponse) { self.response = response }

    func calls() -> Int { callCount }

    func home(accessToken: String) async throws -> MobileHomeResponse {
        callCount += 1
        return response
    }
}
