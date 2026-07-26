import Combine
import Foundation

struct MobileSession: Equatable, Sendable {
    let actor: MobileActor
    let capabilities: Set<String>
    let entitlementState: String
}

enum MobileSessionState: Equatable {
    case loading
    case contractUnavailable(MobileConfigurationValidation)
    case signedOut
    case authenticating
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

    init(
        configuration: MobileConfiguration = .fromBundle(),
        tokenStore: (any SecureTokenStoring)? = nil,
        authorizer: (any OAuthAuthorizing)? = nil,
        tokenExchanger: (any OAuthTokenExchanging)? = nil,
        diagnostics: LegendDiagnostics? = nil
    ) {
        self.configuration = configuration
        self.tokenStore = tokenStore ?? KeychainTokenStore(service: configuration.bundleIdentifier ?? "legend.mobile.session")
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
                guard let tokens = try tokenStore.read(), tokens.expiresAt > Date() else {
                    state = .signedOut
                    return
                }
                try await establishSession(using: tokens)
            } catch {
                try? tokenStore.clear()
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
                try await establishSession(using: tokens)
            } catch {
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
        state = configuration.validation.isReady ? .signedOut : .contractUnavailable(configuration.validation)
    }

    func makeMessagingStore() -> MessagingStore {
        guard let apiBaseURL = configuration.apiBaseURL else {
            return MessagingStore(
                api: MobileContractUnavailableMessagingAPI(),
                accessToken: "",
                diagnostics: diagnostics
            )
        }

        do {
            guard let tokens = try tokenStore.read() else {
                return MessagingStore(
                    api: MobileContractUnavailableMessagingAPI(),
                    accessToken: "",
                    diagnostics: diagnostics
                )
            }
            return MessagingStore(
                api: URLSessionMessagingAPI(client: MobileHTTPClient(baseURL: apiBaseURL)),
                accessToken: tokens.accessToken,
                diagnostics: diagnostics
            )
        } catch {
            return MessagingStore(
                api: MobileContractUnavailableMessagingAPI(),
                accessToken: "",
                diagnostics: diagnostics
            )
        }
    }

    private func establishSession(using tokens: OAuthTokenSet) async throws {
        guard let apiBaseURL = configuration.apiBaseURL else { throw MobileAPIError.invalidBaseURL }
        let response = try await MobileSessionAPI(client: MobileHTTPClient(baseURL: apiBaseURL))
            .bootstrap(accessToken: tokens.accessToken)
        state = .authenticated(MobileSession(
            actor: response.actor,
            capabilities: Set(response.capabilities),
            entitlementState: response.entitlement.state
        ))
    }

    private func authorizationRequest(state: String, pkce: PKCEChallenge) throws -> OAuthAuthorizationRequest {
        guard let authorizationEndpoint = configuration.authorizationEndpoint,
              let clientID = configuration.clientID,
              let redirectScheme = configuration.redirectScheme,
              let scope = configuration.scope,
              let audience = configuration.audience else {
            throw MobileAPIError.invalidBaseURL
        }
        return OAuthAuthorizationRequest(
            authorizationEndpoint: authorizationEndpoint,
            clientID: clientID,
            redirectScheme: redirectScheme,
            scope: scope,
            audience: audience,
            state: state,
            pkce: pkce
        )
    }

    private func validate(callbackURL: URL, expectedState: String) throws -> String {
        guard let components = URLComponents(url: callbackURL, resolvingAgainstBaseURL: false),
              let returnedState = components.queryItems?.first(where: { $0.name == "state" })?.value,
              returnedState == expectedState,
              let code = components.queryItems?.first(where: { $0.name == "code" })?.value,
              !code.isEmpty else {
            throw OAuthCallbackError.invalidCallback
        }
        return code
    }
}

private enum OAuthCallbackError: LocalizedError {
    case invalidCallback

    var errorDescription: String? {
        "The sign-in callback could not be verified."
    }
}

protocol OAuthTokenExchanging: Sendable {
    func exchange(code: String, pkceVerifier: String, configuration: MobileConfiguration) async throws -> OAuthTokenSet
}

struct URLSessionOAuthTokenExchanger: OAuthTokenExchanging {
    func exchange(code: String, pkceVerifier: String, configuration: MobileConfiguration) async throws -> OAuthTokenSet {
        guard let tokenEndpoint = configuration.tokenEndpoint,
              let clientID = configuration.clientID,
              let redirectScheme = configuration.redirectScheme else {
            throw MobileAPIError.invalidBaseURL
        }

        var request = URLRequest(url: tokenEndpoint)
        request.httpMethod = "POST"
        request.setValue("application/x-www-form-urlencoded", forHTTPHeaderField: "Content-Type")
        request.setValue("application/json", forHTTPHeaderField: "Accept")
        request.httpBody = FormURLEncoder.encode([
            "grant_type": "authorization_code",
            "code": code,
            "client_id": clientID,
            "redirect_uri": "\(redirectScheme)://oauth/callback",
            "code_verifier": pkceVerifier
        ])

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
        try await client.get("/api/mobile/v1/session", accessToken: accessToken, response: MobileBootstrapResponse.self)
    }
}

private struct MobileBootstrapResponse: Decodable {
    let actor: MobileActor
    let capabilities: [String]
    let entitlement: MobileEntitlement
}

private struct MobileEntitlement: Decodable {
    let state: String
}
