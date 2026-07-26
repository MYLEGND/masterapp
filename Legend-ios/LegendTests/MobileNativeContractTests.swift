import Foundation
import XCTest
@testable import Legend

@MainActor
final class MobileNativeContractTests: XCTestCase {
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
            "isMine": false
          }],
          "isMuted": false,
          "isClosed": false
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
          "isMine": true
        }
        """.utf8)

        let message = try JSONDecoder.mobile.decode(ConversationMessage.self, from: data)

        XCTAssertEqual(message.sentUTC.timeIntervalSince1970, 1_785_065_221.1234567, accuracy: 0.001)
    }

    func testSendRequestEncodingDoesNotContainSenderOrParticipantIdentity() throws {
        let data = try JSONEncoder.mobile.encode(SendMessageRequest(body: "Secure hello"))
        let object = try XCTUnwrap(JSONSerialization.jsonObject(with: data) as? [String: Any])

        XCTAssertEqual(object["body"] as? String, "Secure hello")
        XCTAssertEqual(Set(object.keys), Set(["body"]))
    }

    func testMobileHTTPClientMapsUnauthorizedAndForbiddenResponses() async throws {
        StubURLProtocol.responseStatus = 401
        let unauthorizedClient = MobileHTTPClient(baseURL: URL(string: "https://api.example.test")!, session: stubSession())
        do {
            let _: MobileBootstrapResponse = try await unauthorizedClient.get("/api/v1/mobile/session", accessToken: "token", response: MobileBootstrapResponse.self)
            XCTFail("Expected an unauthorized error")
        } catch let error as MobileAPIError {
            XCTAssertEqual(error, .unauthorized(correlationID: "test-correlation"))
        }

        StubURLProtocol.responseStatus = 403
        let forbiddenClient = MobileHTTPClient(baseURL: URL(string: "https://api.example.test")!, session: stubSession())
        do {
            let _: MobileBootstrapResponse = try await forbiddenClient.get("/api/v1/mobile/session", accessToken: "token", response: MobileBootstrapResponse.self)
            XCTFail("Expected a forbidden error")
        } catch let error as MobileAPIError {
            XCTAssertEqual(error, .forbidden(correlationID: "test-correlation"))
        }
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

    func testMessagingStoreTransitionsFromLoadingToLoaded() async {
        let store = MessagingStore(
            api: StubMessagingAPI(),
            accessTokenProvider: { "token" },
            diagnostics: LegendDiagnostics())

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
            diagnostics: LegendDiagnostics())

        store.load()
        try? await Task.sleep(for: .milliseconds(50))

        guard case .offline = store.state else {
            return XCTFail("Expected an offline messaging state")
        }
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

    private func stubSession() -> URLSession {
        let configuration = URLSessionConfiguration.ephemeral
        configuration.protocolClasses = [StubURLProtocol.self]
        return URLSession(configuration: configuration)
    }
}

private final class StubURLProtocol: URLProtocol {
    static var responseStatus = 200

    override class func canInit(with request: URLRequest) -> Bool { true }
    override class func canonicalRequest(for request: URLRequest) -> URLRequest { request }

    override func startLoading() {
        let response = HTTPURLResponse(
            url: request.url!,
            statusCode: Self.responseStatus,
            httpVersion: nil,
            headerFields: ["X-Correlation-ID": "test-correlation"])!
        client?.urlProtocol(self, didReceive: response, cacheStoragePolicy: .notAllowed)
        client?.urlProtocol(self, didLoad: Data())
        client?.urlProtocolDidFinishLoading(self)
    }

    override func stopLoading() {}
}

private final class InMemoryTokenStore: SecureTokenStoring, @unchecked Sendable {
    var didClear = false

    func read() throws -> OAuthTokenSet? { nil }
    func save(_ tokens: OAuthTokenSet) throws {}
    func clear() throws { didClear = true }
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
    func conversation(id: UUID, accessToken: String) async throws -> ConversationDetail { throw MobileMessagingContractError.unavailable }
    func messages(conversationID: UUID, accessToken: String) async throws -> [ConversationMessage] { throw MobileMessagingContractError.unavailable }
    func send(conversationID: UUID, body: String, accessToken: String) async throws -> ConversationMessage { throw MobileMessagingContractError.unavailable }
    func markRead(conversationID: UUID, accessToken: String) async throws {}
}

private struct OfflineMessagingAPI: MessagingAPI {
    func conversations(accessToken: String) async throws -> [ConversationSummary] {
        throw MobileAPIError.networkUnavailable
    }

    func conversation(id: UUID, accessToken: String) async throws -> ConversationDetail {
        throw MobileAPIError.networkUnavailable
    }

    func messages(conversationID: UUID, accessToken: String) async throws -> [ConversationMessage] {
        throw MobileAPIError.networkUnavailable
    }

    func send(conversationID: UUID, body: String, accessToken: String) async throws -> ConversationMessage {
        throw MobileAPIError.networkUnavailable
    }

    func markRead(conversationID: UUID, accessToken: String) async throws {
        throw MobileAPIError.networkUnavailable
    }
}
