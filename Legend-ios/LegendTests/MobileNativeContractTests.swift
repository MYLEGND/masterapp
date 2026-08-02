import Foundation
import XCTest
@testable import Legend

@MainActor
final class MobileNativeContractTests: XCTestCase {
    func testVerificationBadgeIsLimitedToTheProfileImageIdentitySurface() {
        XCTAssertTrue(
            LegendVerifiedBadgePlacement.alongsideProfileImage
                .displaysBadge(for: true))
        XCTAssertFalse(
            LegendVerifiedBadgePlacement.none.displaysBadge(for: true))
        XCTAssertFalse(
            LegendVerifiedBadgePlacement.alongsideProfileImage
                .displaysBadge(for: false))
    }

    func testSharedScrollChromeHidesAllActionChromeDownwardAndRestoresItUpward() {
        let chrome = LegendScrollChrome()

        chrome.record(verticalDragTranslation: -1.1)
        XCTAssertFalse(chrome.isBottomNavigationVisible)

        chrome.record(verticalDragTranslation: 0.6)
        XCTAssertTrue(chrome.isBottomNavigationVisible)
    }

    func testPKCEUsesAS256ChallengeAndAuthorizationRequestUsesStandardParameters() throws {
        let pkce = try PKCEChallenge.create()
        XCTAssertGreaterThanOrEqual(pkce.verifier.count, 43)
        XCTAssertFalse(pkce.challenge.contains("="))

        let request = OAuthAuthorizationRequest(
            authorizationEndpoint: URL(string: "https://login.example.test/authorize")!,
            clientID: "public-client",
            redirectScheme: "com-mylegnd-legend",
            scope: "openid profile api://legend/mobile_access",
            state: "expected-state",
            pkce: pkce)
        let query = try XCTUnwrap(URLComponents(url: request.url(), resolvingAgainstBaseURL: false)?.queryItems)

        XCTAssertEqual(query.first(where: { $0.name == "code_challenge_method" })?.value, "S256")
        XCTAssertEqual(query.first(where: { $0.name == "state" })?.value, "expected-state")
        XCTAssertNil(query.first(where: { $0.name == "audience" }))
    }

    func testCallbackValidationRequiresExactSchemePathAndState() throws {
        let valid = URL(string: "com-mylegnd-legend://oauth/callback?code=auth-code&state=expected")!
        XCTAssertEqual(
            try OAuthCallbackValidator.authorizationCode(
                from: valid,
                redirectScheme: "com-mylegnd-legend",
                expectedState: "expected"),
            "auth-code")

        XCTAssertThrowsError(try OAuthCallbackValidator.authorizationCode(
            from: URL(string: "com-mylegnd-legend://oauth/callback?code=auth-code&state=wrong")!,
            redirectScheme: "com-mylegnd-legend",
            expectedState: "expected"))
        XCTAssertThrowsError(try OAuthCallbackValidator.authorizationCode(
            from: URL(string: "other-scheme://oauth/callback?code=auth-code&state=expected")!,
            redirectScheme: "com-mylegnd-legend",
            expectedState: "expected"))
    }

    func testSessionAndConversationDTOsDecodeTheServerContract() throws {
        let sessionData = Data("""
        {
          "authenticated": true,
          "actor": {
            "identity": { "userId": "same-oid", "participantType": "Agent" },
            "profileId": "00000000-0000-0000-0000-000000000001",
            "displayName": "Agent One",
            "avatar": { "kind": "inline", "contentType": "image/png", "base64Content": "aW1hZ2U=" }
          },
          "permittedParticipantTypes": ["Agent", "Client"],
          "requiresParticipantSelection": true,
          "capabilities": { "messaging": true },
          "correlationId": "correlation-1"
        }
        """.utf8)
        let session = try JSONDecoder.mobile.decode(MobileBootstrapResponse.self, from: sessionData)
        XCTAssertTrue(session.authenticated)
        XCTAssertEqual(session.actor?.identity.userID, "same-oid")
        XCTAssertEqual(session.permittedParticipantTypes, [.agent, .client])
        XCTAssertTrue(session.requiresParticipantSelection)
        XCTAssertTrue(session.capabilities.messaging)
        XCTAssertEqual(session.actor?.avatar?.imageData, Data("image".utf8))

        let conversationData = Data("""
        {
          "id": "00000000-0000-0000-0000-000000000010",
          "title": "Secure conversation",
          "conversationType": "ClientAgent",
          "participants": [{
            "identity": { "userId": "same-oid", "participantType": "Client" },
            "profileId": "00000000-0000-0000-0000-000000000002",
            "displayName": "Client One",
            "avatar": null
          }],
          "messages": [{
            "id": "00000000-0000-0000-0000-000000000011",
            "conversationId": "00000000-0000-0000-0000-000000000010",
            "sender": {
              "identity": { "userId": "same-oid", "participantType": "Client" },
              "profileId": "00000000-0000-0000-0000-000000000002",
              "displayName": "Client One",
              "avatar": null
            },
            "body": "Hello",
            "sentUtc": "2026-07-25T20:00:00Z",
            "attachments": [],
            "isMine": false
          }],
          "isMuted": false,
          "isClosed": false,
          "canManageMembers": false
        }
        """.utf8)
        let conversation = try JSONDecoder.mobile.decode(ConversationDetail.self, from: conversationData)
        XCTAssertEqual(conversation.participants.first?.identity.participantType, .client)
        XCTAssertEqual(conversation.messages.first?.conversationID, conversation.id)
        XCTAssertFalse(conversation.messages.first?.isMine ?? true)
    }

    func testJSONDecoderAcceptsAspNetCoreFractionalSecondUtcTimestamps() throws {
        let data = Data("""
        {
          "id": "00000000-0000-0000-0000-000000000011",
          "conversationId": "00000000-0000-0000-0000-000000000010",
          "sender": {
            "identity": { "userId": "agent-oid", "participantType": "Agent" },
            "profileId": "00000000-0000-0000-0000-000000000001",
            "displayName": "Agent One",
            "avatar": null
          },
          "body": "Hello",
          "sentUtc": "2026-07-26T11:27:01.1234567Z",
          "attachments": [],
          "isMine": true
        }
        """.utf8)

        let message = try JSONDecoder.mobile.decode(ConversationMessage.self, from: data)

        XCTAssertEqual(message.sentUTC.timeIntervalSince1970, 1_785_065_221.1234567, accuracy: 0.001)
    }

    func testMessageAttachmentDTOUsesTheServerScanState() throws {
        let data = Data("""
        {
          "id": "00000000-0000-0000-0000-000000000011",
          "conversationId": "00000000-0000-0000-0000-000000000010",
          "sender": {
            "identity": { "userId": "agent-oid", "participantType": "Agent" },
            "profileId": "00000000-0000-0000-0000-000000000001",
            "displayName": "Agent One",
            "avatar": null
          },
          "body": "Your plan is attached.",
          "sentUtc": "2026-07-26T11:27:01Z",
          "attachments": [{
            "id": "00000000-0000-0000-0000-000000000012",
            "originalFileName": "plan.pdf",
            "contentType": "application/pdf",
            "sizeBytes": 512,
            "scanStatus": "Pending",
            "createdUtc": "2026-07-26T11:27:01Z",
            "canDownload": false
          }],
          "isMine": true
        }
        """.utf8)

        let message = try JSONDecoder.mobile.decode(ConversationMessage.self, from: data)
        let attachment = try XCTUnwrap(message.attachments.first)

        XCTAssertEqual(attachment.originalFileName, "plan.pdf")
        XCTAssertEqual(attachment.scanStatus, "Pending")
        XCTAssertFalse(attachment.canDownload)
    }

    func testJSONDecoderTreatsAZoneLessAspNetCoreUtcFieldAsUtc() throws {
        let data = Data("""
        {
          "id": "00000000-0000-0000-0000-000000000011",
          "conversationId": "00000000-0000-0000-0000-000000000010",
          "sender": {
            "identity": { "userId": "agent-oid", "participantType": "Agent" },
            "profileId": "00000000-0000-0000-0000-000000000001",
            "displayName": "Agent One",
            "avatar": null
          },
          "body": "Hello",
          "sentUtc": "2026-07-26T11:27:01.1234567",
          "attachments": [],
          "isMine": true
        }
        """.utf8)

        let message = try JSONDecoder.mobile.decode(ConversationMessage.self, from: data)

        XCTAssertEqual(message.sentUTC.timeIntervalSince1970, 1_785_065_221.1234567, accuracy: 0.001)
    }

    func testSendRequestEncodingDoesNotContainSenderOrParticipantIdentity() throws {
        let data = try JSONEncoder.mobile.encode(SendMessageRequest(body: "Secure hello", replyToMessageID: nil))
        let object = try XCTUnwrap(JSONSerialization.jsonObject(with: data) as? [String: Any])

        XCTAssertEqual(object["body"] as? String, "Secure hello")
        XCTAssertEqual(Set(object.keys), Set(["body"]))
    }

    func testAccountAndJourneyContractsKeepProfileFieldsAndSelectionsTyped() throws {
        let accountData = Data("""
        {
          "participantType": "Client",
          "profileId": "00000000-0000-0000-0000-000000000123",
          "displayName": "Client Identity",
          "email": "hello@example.test",
          "phone": "555-0100",
          "title": null,
          "shortBio": null,
          "profileEmail": "hello@example.test",
          "isEmailVisible": true,
          "isPhoneVisible": true,
          "username": "client.legend",
          "bio": "Building a legacy.",
          "website": "https://legend.example.test",
          "location": "Phoenix, Arizona",
          "avatar": { "kind": "inline", "contentType": "image/png", "base64Content": "Y2xpZW50" }
        }
        """.utf8)
        let account = try JSONDecoder.mobile.decode(MobileAccountProfile.self, from: accountData)
        XCTAssertEqual(account.participantType, .client)
        XCTAssertEqual(account.profileEmail, "hello@example.test")
        XCTAssertTrue(account.isEmailVisible)
        XCTAssertTrue(account.isPhoneVisible)
        XCTAssertEqual(account.username, "client.legend")
        XCTAssertEqual(account.avatar?.imageData, Data("client".utf8))

        let accountUpdate = MobileAccountUpdate(
            displayName: "Client Identity",
            phone: "555-0100",
            title: nil,
            shortBio: nil,
            username: "client.legend",
            bio: "Building a legacy.",
            website: "https://legend.example.test",
            location: "Phoenix, Arizona",
            publicEmail: "hello@example.test",
            isEmailVisible: true,
            isPhoneVisible: true)
        let accountUpdateObject = try XCTUnwrap(
            JSONSerialization.jsonObject(with: JSONEncoder.mobile.encode(accountUpdate)) as? [String: Any])
        XCTAssertEqual(accountUpdateObject["publicEmail"] as? String, "hello@example.test")
        XCTAssertEqual(accountUpdateObject["isEmailVisible"] as? Bool, true)
        XCTAssertEqual(accountUpdateObject["isPhoneVisible"] as? Bool, true)

        let input = MobileJourneyProfileInput(
            consentAffirmed: true,
            isOptedIn: true,
            isDiscoverable: true,
            allowSuggestions: true,
            allowConnectionRequests: true,
            introduction: "Building a legacy.",
            lifeStages: ["Business ownership"],
            locations: ["Southwest"],
            goals: ["Growing a business"],
            interests: ["Leadership"],
            circleCodes: ["Entrepreneurs Circle"],
            connectionTypes: ["Business peer"],
            communicationStyles: ["Detailed planning"],
            accountabilityFrequencies: ["Weekly"])
        let encoded = try JSONEncoder.mobile.encode(input)
        let object = try XCTUnwrap(JSONSerialization.jsonObject(with: encoded) as? [String: Any])
        XCTAssertEqual(object["introduction"] as? String, "Building a legacy.")
        XCTAssertEqual(object["goals"] as? [String], ["Growing a business"])
        XCTAssertNil(object["email"])
    }

    func testMobileHTTPClientMapsUnauthorizedAndForbiddenResponses() async throws {
        StubURLProtocol.responseStatus = 401
        let unauthorizedClient = MobileHTTPClient(baseURL: URL(string: "https://api.example.test")!, session: stubSession())
        do {
            let _: MobileBootstrapResponse = try await unauthorizedClient.get("/api/v1/mobile/session", accessToken: "token", response: MobileBootstrapResponse.self)
            XCTFail("Expected an unauthorized error")
        } catch let error as MobileAPIError {
            XCTAssertEqual(error, .apiUnauthorized(code: "mobile_authentication_required", correlationID: "test-correlation"))
        }

        StubURLProtocol.responseStatus = 403
        let forbiddenClient = MobileHTTPClient(baseURL: URL(string: "https://api.example.test")!, session: stubSession())
        do {
            let _: MobileBootstrapResponse = try await forbiddenClient.get("/api/v1/mobile/session", accessToken: "token", response: MobileBootstrapResponse.self)
            XCTFail("Expected a forbidden error")
        } catch let error as MobileAPIError {
            XCTAssertEqual(error, .apiForbidden(code: "mobile_access_forbidden", correlationID: "test-correlation"))
        }
    }

    func testMobileHTTPClientBoundsProtectedMediaDownloads() async throws {
        StubURLProtocol.responseStatus = 200
        StubURLProtocol.lastRequestTimeout = nil

        let client = MobileHTTPClient(
            baseURL: URL(string: "https://api.example.test")!,
            session: stubSession())
        let data = try await client.getData(
            "/api/v1/mobile/social/media/00000000-0000-0000-0000-000000000001",
            accessToken: "token")

        XCTAssertTrue(data.isEmpty)
        XCTAssertEqual(StubURLProtocol.lastRequestTimeout, 20)
    }

    func testSignOutClearsTheKeychainAbstraction() {
        let store = InMemoryTokenStore()
        let coordinator = MobileSessionCoordinator(
            configuration: completeConfiguration(),
            tokenStore: store,
            authorizer: TestAuthorizer(),
            tokenExchanger: TestTokenExchanger())

        coordinator.signOut()

        XCTAssertTrue(store.didClear)
        XCTAssertEqual(coordinator.state, .signedOut)
    }

    func testAuthorizedDualRoleSwitchReusesTheCurrentBearerWithoutSigningOut() async throws {
        let store = InMemoryTokenStore(
            storedTokens: OAuthTokenSet(
                accessToken: "stored-access-token",
                refreshToken: "stored-refresh-token",
                expiresAt: .distantFuture))
        let service = try DualRoleSessionService()
        let coordinator = MobileSessionCoordinator(
            configuration: completeConfiguration(),
            tokenStore: store,
            authorizer: TestAuthorizer(),
            tokenExchanger: TestTokenExchanger(),
            sessionService: service)

        coordinator.restore()
        try await waitForState(
            coordinator,
            matching: { state in
                if case .roleSelection = state { return true }
                return false
            })

        coordinator.selectRole(.agent)
        let agentSession = try await waitForAuthenticatedSession(
            coordinator,
            participantType: .agent)
        XCTAssertEqual(agentSession.alternateParticipantTypes, [.client])

        coordinator.switchToRole(.client)
        let clientSession = try await waitForAuthenticatedSession(
            coordinator,
            participantType: .client)
        XCTAssertEqual(clientSession.alternateParticipantTypes, [.agent])
        XCTAssertFalse(store.didClear)
        let requestedRoles = await service.requestedRoles()
        XCTAssertEqual(requestedRoles, [.agent, .client])
    }

    func testFaceIDRestoreReopensTheLastDualRoleAccountWithoutRoleSelection() async throws {
        let cache = LegendLaunchCache(
            directoryName: "MobileNativeContractTests-\(UUID().uuidString)")
        defer { cache.clear() }

        let cachedActor = try MobileActor(
            identity: LogicalParticipantIdentity(
                userID: "shared-entra-oid",
                participantType: .agent),
            profileID: "00000000-0000-0000-0000-000000000001",
            displayName: "Agent Account",
            avatar: nil)
        cache.writeSession(MobileSessionCacheEntry(
            actor: cachedActor,
            capabilities: ["messaging"],
            permittedParticipantTypes: [.agent, .client],
            cachedUtc: Date(),
            credentialFingerprint: LegendSessionCredentialFingerprint.make(
                from: OAuthTokenSet(
                    accessToken: "stored-access-token",
                    refreshToken: "stored-refresh-token",
                    expiresAt: .distantFuture))))

        let store = InMemoryTokenStore(
            storedTokens: OAuthTokenSet(
                accessToken: "stored-access-token",
                refreshToken: "stored-refresh-token",
                expiresAt: .distantFuture))
        let service = try DualRoleSessionService()
        let coordinator = MobileSessionCoordinator(
            configuration: completeConfiguration(),
            tokenStore: store,
            authorizer: TestAuthorizer(),
            tokenExchanger: TestTokenExchanger(),
            sessionService: service,
            launchCache: cache,
            biometricSecurity: AcceptingBiometricSecurity())

        coordinator.restore()

        let restored = try await waitForAuthenticatedSession(
            coordinator,
            participantType: .agent)
        XCTAssertEqual(restored.actor.displayName, "Agent Account")
        let requestedRoles = await service.requestedRoles()
        XCTAssertEqual(requestedRoles, [.agent])
        XCTAssertFalse(store.didClear)
    }

    func testMessagingStoreTransitionsFromLoadingToLoaded() async {
        let store = MessagingStore(
            api: StubMessagingAPI(),
            accessTokenProvider: { "token" },
            diagnostics: LegendDiagnostics(),
            actorParticipantType: .client)

        store.load()
        try? await Task.sleep(for: .milliseconds(50))

        guard case .loaded(let conversations) = store.state else {
            return XCTFail("Expected a loaded messaging state")
        }
        XCTAssertTrue(conversations.isEmpty)
    }

    func testMessagingStoreShowsOfflineStateForNetworkFailure() async {
        let store = MessagingStore(
            api: OfflineMessagingAPI(),
            accessTokenProvider: { "token" },
            diagnostics: LegendDiagnostics(),
            actorParticipantType: .client)

        store.load()
        let deadline = ContinuousClock.now.advanced(by: .seconds(1))
        while case .loading = store.state,
              ContinuousClock.now < deadline {
            try? await Task.sleep(for: .milliseconds(10))
        }

        guard case .offline = store.state else {
            return XCTFail("Expected an offline messaging state")
        }
    }

    func testMessagingStoreShowsUnauthorizedStateForTheBearerApiContract() async {
        let store = MessagingStore(
            api: UnauthorizedMessagingAPI(),
            accessTokenProvider: { "token" },
            diagnostics: LegendDiagnostics(),
            actorParticipantType: .client)

        store.load()
        try? await Task.sleep(for: .milliseconds(50))

        guard case .unauthorized(let failure) = store.state else {
            return XCTFail("Expected an unauthorized messaging state")
        }
        XCTAssertEqual(failure.message, "Your session has ended. Please sign in again.")
    }

    func testAgentClientMessageActionUsesTheExactAuthorizedClientProfile() async {
        let clientProfileID = UUID(uuidString: "00000000-0000-0000-0000-000000000222")!
        let api = TypedClientRecipientMessagingAPI(clientProfileID: clientProfileID)
        let store = MessagingStore(
            api: api,
            accessTokenProvider: { "token" },
            diagnostics: LegendDiagnostics(),
            actorParticipantType: .agent)
        let started = expectation(description: "Starts the exact client conversation")

        store.startConversation(forClientProfileID: clientProfileID) { _ in
            started.fulfill()
        }

        await fulfillment(of: [started], timeout: 1)
        XCTAssertEqual(api.startedRecipient?.identity.participantType, .client)
        XCTAssertEqual(UUID(uuidString: api.startedRecipient?.profileID ?? ""), clientProfileID)
    }

    func testFinancialStoreUsesDedicatedFinancialProjection() async throws {
        let snapshot = MobileFinancialSnapshotResponse(
            position: MobileFinancialPosition(
                healthScore: 72,
                assetsTotal: 125_000,
                liabilitiesTotal: 45_000,
                netWorth: 80_000,
                annualEarnings: 100_000,
                annualLifestyleRemaining: 55_000,
                annualTaxes: 12_000,
                protectionGapTotal: 0,
                positionStatus: "Stable",
                positionSummary: "Your saved financial position is available.",
                estatePlanningStatus: "In progress",
                estatePlanningRiskLevel: "Moderate",
                updatedUTC: .now
            ),
            intelligence: nil,
            upcomingBills: [],
            operatingSystem: availableOperatingSystem
        )
        let store = MobileFinancialStore(
            api: StubMobileFinancialAPI(snapshot: snapshot),
            accessTokenProvider: { "token" },
            diagnostics: LegendDiagnostics()
        )

        store.load()
        try await Task.sleep(for: .milliseconds(50))

        guard case .available(let loaded) = store.state else {
            return XCTFail("Expected the dedicated financial projection")
        }

        XCTAssertEqual(loaded.position?.netWorth, 80_000)
        XCTAssertEqual(loaded.operatingSystem?.projection.status, "Available")
    }

    func testFinancialStoreDistinguishesNeverSavedExpenseLensState() async throws {
        let neverSaved = MobileFinancialSnapshotResponse(
            position: nil,
            intelligence: nil,
            upcomingBills: [],
            operatingSystem: MobileFinancialOperatingSystemSnapshotResponse(
                projection: MobileFinancialProjectionStatusResponse(
                    status: "Unavailable",
                    reasonCode: "EXPENSE_LENS_STATE_NOT_FOUND",
                    summary: "Save Expense Lens to begin."
                ),
                freshness: MobileFinancialDataFreshnessResponse(
                    financeStateUpdatedUTC: nil,
                    intelligenceEvaluatedUTC: nil,
                    generatedUTC: .now
                ),
                weekAtGlance: nil,
                monthAtGlance: nil,
                tools: []
            )
        )
        let store = MobileFinancialStore(
            api: StubMobileFinancialAPI(snapshot: neverSaved),
            accessTokenProvider: { "token" },
            diagnostics: LegendDiagnostics()
        )

        store.load()
        try await Task.sleep(for: .milliseconds(50))

        guard case .neverSaved(_, let detail) = store.state else {
            return XCTFail("Expected the unsaved Expense Lens state")
        }

        XCTAssertEqual(detail, "Save Expense Lens to begin.")
    }

    private var availableOperatingSystem: MobileFinancialOperatingSystemSnapshotResponse {
        MobileFinancialOperatingSystemSnapshotResponse(
            projection: MobileFinancialProjectionStatusResponse(
                status: "Available",
                reasonCode: nil,
                summary: "Your saved weekly plan is available."
            ),
            freshness: MobileFinancialDataFreshnessResponse(
                financeStateUpdatedUTC: .now,
                intelligenceEvaluatedUTC: nil,
                generatedUTC: .now
            ),
            weekAtGlance: nil,
            monthAtGlance: nil,
            tools: []
        )
    }

    private func completeConfiguration() -> MobileConfiguration {
        MobileConfiguration(
            bundleIdentifier: "com.mylegnd.legend",
            apiBaseURL: URL(string: "https://api.example.test")!,
            authorizationEndpoint: URL(string: "https://identity.example.test/authorize")!,
            tokenEndpoint: URL(string: "https://identity.example.test/token")!,
            clientID: "public-client",
            redirectScheme: "com-mylegnd-legend",
            scope: "openid profile api://legend/mobile_access",
            audience: "api://legend")
    }

    private func waitForState(
        _ coordinator: MobileSessionCoordinator,
        matching predicate: (MobileSessionState) -> Bool
    ) async throws {
        let deadline = ContinuousClock.now.advanced(by: .seconds(1))
        while !predicate(coordinator.state),
              ContinuousClock.now < deadline {
            try await Task.sleep(for: .milliseconds(10))
        }
        XCTAssertTrue(predicate(coordinator.state), "The expected session state was not reached.")
    }

    private func waitForAuthenticatedSession(
        _ coordinator: MobileSessionCoordinator,
        participantType: ParticipantType
    ) async throws -> MobileSession {
        let deadline = ContinuousClock.now.advanced(by: .seconds(1))
        while ContinuousClock.now < deadline {
            if case .authenticated(let session) = coordinator.state,
               session.actor.identity.participantType == participantType {
                return session
            }
            try await Task.sleep(for: .milliseconds(10))
        }

        XCTFail("The expected authenticated role was not reached.")
        throw SessionSwitchTestError.expectedSessionWasNotReached
    }

    private func stubSession() -> URLSession {
        let configuration = URLSessionConfiguration.ephemeral
        configuration.protocolClasses = [StubURLProtocol.self]
        return URLSession(configuration: configuration)
    }
}

private struct StubMobileFinancialAPI: MobileFinancialAPI {
    let snapshot: MobileFinancialSnapshotResponse

    func financial(
        accessToken: String
    ) async throws -> MobileFinancialSnapshotResponse {
        snapshot
    }
}

private final class StubURLProtocol: URLProtocol {
    static var responseStatus = 200
    static var lastRequestTimeout: TimeInterval?

    override class func canInit(with request: URLRequest) -> Bool { true }
    override class func canonicalRequest(for request: URLRequest) -> URLRequest { request }

    override func startLoading() {
        Self.lastRequestTimeout = request.timeoutInterval
        let response = HTTPURLResponse(
            url: request.url!,
            statusCode: Self.responseStatus,
            httpVersion: nil,
            headerFields: ["X-Correlation-ID": "test-correlation"])!
        client?.urlProtocol(self, didReceive: response, cacheStoragePolicy: .notAllowed)
        let body: Data
        switch Self.responseStatus {
        case 401:
            body = Data(#"{"code":"mobile_authentication_required","correlationId":"test-correlation"}"#.utf8)
        case 403:
            body = Data(#"{"code":"mobile_access_forbidden","correlationId":"test-correlation"}"#.utf8)
        default:
            body = Data()
        }
        client?.urlProtocol(self, didLoad: body)
        client?.urlProtocolDidFinishLoading(self)
    }

    override func stopLoading() {}
}

private final class InMemoryTokenStore: SecureTokenStoring, @unchecked Sendable {
    var storedTokens: OAuthTokenSet?
    var didClear = false

    init(storedTokens: OAuthTokenSet? = nil) {
        self.storedTokens = storedTokens
    }

    func read() throws -> OAuthTokenSet? { storedTokens }
    func save(_ tokens: OAuthTokenSet) throws { storedTokens = tokens }
    func clear() throws {
        storedTokens = nil
        didClear = true
    }
}

@MainActor
private final class AcceptingBiometricSecurity: MobileBiometricSessionSecuring {
    var isAvailable: Bool { true }

    func hasPrompted(for identity: LogicalParticipantIdentity) -> Bool { true }
    func markPrompted(for identity: LogicalParticipantIdentity) {}
    func isEnabled(for identity: LogicalParticipantIdentity) -> Bool { true }
    func disable(for identity: LogicalParticipantIdentity) {}
    func enable(for identity: LogicalParticipantIdentity) async -> Bool { true }
    func authenticate() async -> Bool { true }
}

private enum SessionSwitchTestError: Error {
    case expectedSessionWasNotReached
}

private actor DualRoleSessionService: MobileSessionServicing {
    private let permittedParticipantTypes: [ParticipantType] = [.agent, .client]
    private let agent: MobileActor
    private let client: MobileActor
    private var roles: [ParticipantType] = []

    init() throws {
        let agentIdentity = try LogicalParticipantIdentity(
            userID: "shared-entra-oid",
            participantType: .agent)
        let clientIdentity = try LogicalParticipantIdentity(
            userID: "shared-entra-oid",
            participantType: .client)
        agent = try MobileActor(
            identity: agentIdentity,
            profileID: "00000000-0000-0000-0000-000000000001",
            displayName: "Agent Account",
            avatar: nil)
        client = try MobileActor(
            identity: clientIdentity,
            profileID: "00000000-0000-0000-0000-000000000002",
            displayName: "Client Account",
            avatar: nil)
    }

    func bootstrap(accessToken: String) async throws -> MobileBootstrapResponse {
        MobileBootstrapResponse(
            authenticated: true,
            actor: nil,
            permittedParticipantTypes: permittedParticipantTypes,
            requiresParticipantSelection: true,
            capabilities: MobileCapabilities(messaging: true),
            correlationID: "dual-role-bootstrap")
    }

    func selectRole(
        _ participantType: ParticipantType,
        accessToken: String
    ) async throws -> MobileRoleSelectionResponse {
        roles.append(participantType)
        let selectedActor = switch participantType {
        case .agent: agent
        case .client: client
        }
        return MobileRoleSelectionResponse(
            actor: selectedActor,
            permittedParticipantTypes: permittedParticipantTypes,
            correlationID: "dual-role-selection")
    }

    func requestedRoles() -> [ParticipantType] {
        roles
    }
}

private final class TestAuthorizer: OAuthAuthorizing {
    func authorize(_ request: OAuthAuthorizationRequest) async throws -> URL {
        URL(string: "com-mylegnd-legend://oauth/callback?code=test&state=test")!
    }
}

private struct TestTokenExchanger: OAuthTokenExchanging {
    func exchange(code: String, pkceVerifier: String, configuration: MobileConfiguration) async throws -> OAuthTokenSet {
        OAuthTokenSet(accessToken: "token", refreshToken: nil, expiresAt: .distantFuture)
    }

    func refresh(refreshToken: String, configuration: MobileConfiguration) async throws -> OAuthTokenSet {
        OAuthTokenSet(accessToken: "token", refreshToken: refreshToken, expiresAt: .distantFuture)
    }
}

private struct StubMessagingAPI: MessagingAPI {
    func conversations(accessToken: String) async throws -> [ConversationSummary] { [] }
    func recipients(search: String?, scope: MessagingRecipientScope?, accessToken: String) async throws -> [MessagingRecipient] { [] }
    func start(recipient: MessagingRecipient, accessToken: String) async throws -> ConversationDetail { throw MobileMessagingContractError.unavailable }
    func conversation(id: UUID, accessToken: String) async throws -> ConversationDetail { throw MobileMessagingContractError.unavailable }
    func messages(conversationID: UUID, accessToken: String) async throws -> [ConversationMessage] { throw MobileMessagingContractError.unavailable }
    func send(conversationID: UUID, body: String, replyToMessageID: UUID?, accessToken: String) async throws -> ConversationMessage { throw MobileMessagingContractError.unavailable }
    func upload(conversationID: UUID, messageID: UUID, attachment: MessagingAttachmentDraft, accessToken: String) async throws -> MessagingAttachment { throw MobileMessagingContractError.unavailable }
    func markRead(conversationID: UUID, accessToken: String) async throws {}
}

private struct OfflineMessagingAPI: MessagingAPI {
    func conversations(accessToken: String) async throws -> [ConversationSummary] {
        throw MobileAPIError.networkUnavailable
    }

    func recipients(search: String?, scope: MessagingRecipientScope?, accessToken: String) async throws -> [MessagingRecipient] {
        throw MobileAPIError.networkUnavailable
    }

    func start(recipient: MessagingRecipient, accessToken: String) async throws -> ConversationDetail {
        throw MobileAPIError.networkUnavailable
    }

    func conversation(id: UUID, accessToken: String) async throws -> ConversationDetail {
        throw MobileAPIError.networkUnavailable
    }

    func messages(conversationID: UUID, accessToken: String) async throws -> [ConversationMessage] {
        throw MobileAPIError.networkUnavailable
    }

    func send(conversationID: UUID, body: String, replyToMessageID: UUID?, accessToken: String) async throws -> ConversationMessage {
        throw MobileAPIError.networkUnavailable
    }

    func upload(conversationID: UUID, messageID: UUID, attachment: MessagingAttachmentDraft, accessToken: String) async throws -> MessagingAttachment {
        throw MobileAPIError.networkUnavailable
    }

    func markRead(conversationID: UUID, accessToken: String) async throws {
        throw MobileAPIError.networkUnavailable
    }
}

private struct UnauthorizedMessagingAPI: MessagingAPI {
    func conversations(accessToken: String) async throws -> [ConversationSummary] {
        throw MobileAPIError.apiUnauthorized(code: "mobile_authentication_required", correlationID: "test-correlation")
    }

    func recipients(search: String?, scope: MessagingRecipientScope?, accessToken: String) async throws -> [MessagingRecipient] {
        throw MobileAPIError.apiUnauthorized(code: "mobile_authentication_required", correlationID: "test-correlation")
    }

    func start(recipient: MessagingRecipient, accessToken: String) async throws -> ConversationDetail {
        throw MobileAPIError.apiUnauthorized(code: "mobile_authentication_required", correlationID: "test-correlation")
    }

    func conversation(id: UUID, accessToken: String) async throws -> ConversationDetail {
        throw MobileAPIError.apiUnauthorized(code: "mobile_authentication_required", correlationID: "test-correlation")
    }

    func messages(conversationID: UUID, accessToken: String) async throws -> [ConversationMessage] {
        throw MobileAPIError.apiUnauthorized(code: "mobile_authentication_required", correlationID: "test-correlation")
    }

    func send(conversationID: UUID, body: String, replyToMessageID: UUID?, accessToken: String) async throws -> ConversationMessage {
        throw MobileAPIError.apiUnauthorized(code: "mobile_authentication_required", correlationID: "test-correlation")
    }

    func upload(conversationID: UUID, messageID: UUID, attachment: MessagingAttachmentDraft, accessToken: String) async throws -> MessagingAttachment {
        throw MobileAPIError.apiUnauthorized(code: "mobile_authentication_required", correlationID: "test-correlation")
    }

    func markRead(conversationID: UUID, accessToken: String) async throws {
        throw MobileAPIError.apiUnauthorized(code: "mobile_authentication_required", correlationID: "test-correlation")
    }
}

private final class TypedClientRecipientMessagingAPI: MessagingAPI, @unchecked Sendable {
    let clientProfileID: UUID
    private(set) var startedRecipient: MessagingRecipient?

    init(clientProfileID: UUID) {
        self.clientProfileID = clientProfileID
    }

    func conversations(accessToken: String) async throws -> [ConversationSummary] { [] }

    func recipients(search: String?, scope: MessagingRecipientScope?, accessToken: String) async throws -> [MessagingRecipient] {
        [
            MessagingRecipient(
                identity: try LogicalParticipantIdentity(userID: "same-person", participantType: .agent),
                profileID: "00000000-0000-0000-0000-000000000111",
                displayName: "Agent identity",
                email: "agent@example.test",
                roleLabel: nil,
                relationshipLabel: "Company agent",
                existingConversationID: nil,
                avatar: nil),
            MessagingRecipient(
                identity: try LogicalParticipantIdentity(userID: "same-person", participantType: .client),
                profileID: clientProfileID.uuidString,
                displayName: "Client identity",
                email: "client@example.test",
                roleLabel: nil,
                relationshipLabel: "Active client",
                existingConversationID: nil,
                avatar: nil)
        ]
    }

    func start(recipient: MessagingRecipient, accessToken: String) async throws -> ConversationDetail {
        startedRecipient = recipient
        return ConversationDetail(
            id: UUID(),
            conversationType: "ClientAgent",
            title: recipient.displayName,
            participants: [],
            messages: [],
            isMuted: false,
            isClosed: false,
            canManageMembers: false)
    }

    func createGroup(subject: String, recipients: [MessagingRecipient], groupImage: MessagingGroupImageRequest?, accessToken: String) async throws -> ConversationDetail {
        throw MobileMessagingContractError.unavailable
    }

    func startVerificationRequest(accessToken: String) async throws -> VerificationRequestSubmission {
        throw MobileMessagingContractError.unavailable
    }

    func updateGroup(conversationID: UUID, subject: String, groupImage: MessagingGroupImageRequest?, accessToken: String) async throws {
        throw MobileMessagingContractError.unavailable
    }

    func addGroupParticipant(conversationID: UUID, recipient: MessagingRecipient, accessToken: String) async throws {
        throw MobileMessagingContractError.unavailable
    }

    func resolveVerificationRequest(requestID: UUID, approve: Bool, accessToken: String) async throws {
        throw MobileMessagingContractError.unavailable
    }

    func conversation(id: UUID, accessToken: String) async throws -> ConversationDetail { throw MobileMessagingContractError.unavailable }
    func messages(conversationID: UUID, accessToken: String) async throws -> [ConversationMessage] { [] }
    func send(conversationID: UUID, body: String, replyToMessageID: UUID?, accessToken: String) async throws -> ConversationMessage { throw MobileMessagingContractError.unavailable }
    func upload(conversationID: UUID, messageID: UUID, attachment: MessagingAttachmentDraft, accessToken: String) async throws -> MessagingAttachment { throw MobileMessagingContractError.unavailable }
    func markRead(conversationID: UUID, accessToken: String) async throws {}
}
