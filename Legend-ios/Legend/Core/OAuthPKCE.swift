import AuthenticationServices
import CryptoKit
import Foundation
import UIKit

struct PKCEChallenge: Equatable, Sendable {
    let verifier: String
    let challenge: String

    static func create() throws -> PKCEChallenge {
        var bytes = [UInt8](repeating: 0, count: 64)
        let status = bytes.withUnsafeMutableBufferPointer {
            SecRandomCopyBytes(kSecRandomDefault, $0.count, $0.baseAddress!)
        }
        guard status == errSecSuccess else {
            throw OAuthError.randomGenerationFailed
        }

        let verifier = Data(bytes).base64URLEncodedString()
        let digest = SHA256.hash(data: Data(verifier.utf8))
        return PKCEChallenge(verifier: verifier, challenge: Data(digest).base64URLEncodedString())
    }
}

struct OAuthAuthorizationRequest: Sendable {
    let authorizationEndpoint: URL
    let clientID: String
    let redirectScheme: String
    let scope: String
    let state: String
    let pkce: PKCEChallenge

    func url() throws -> URL {
        guard var components = URLComponents(url: authorizationEndpoint, resolvingAgainstBaseURL: false) else {
            throw OAuthError.invalidAuthorizationEndpoint
        }

        var items = components.queryItems ?? []
        items.append(contentsOf: [
            URLQueryItem(name: "response_type", value: "code"),
            URLQueryItem(name: "client_id", value: clientID),
            URLQueryItem(name: "redirect_uri", value: "\(redirectScheme)://oauth/callback"),
            URLQueryItem(name: "scope", value: scope),
            URLQueryItem(name: "state", value: state),

            // Microsoft must explicitly display its account chooser. Without this,
            // an existing browser session may silently lock Legend to the last account.
            URLQueryItem(name: "prompt", value: "select_account"),

            URLQueryItem(name: "code_challenge", value: pkce.challenge),
            URLQueryItem(name: "code_challenge_method", value: "S256")
        ])
        components.queryItems = items

        guard let url = components.url else { throw OAuthError.invalidAuthorizationEndpoint }
        return url
    }
}

@MainActor
protocol OAuthAuthorizing: AnyObject {
    func authorize(_ request: OAuthAuthorizationRequest) async throws -> URL
}

@MainActor
final class SystemBrowserAuthorizer: NSObject, OAuthAuthorizing, ASWebAuthenticationPresentationContextProviding {
    private var session: ASWebAuthenticationSession?

    func authorize(_ request: OAuthAuthorizationRequest) async throws -> URL {
        let url = try request.url()
        return try await withCheckedThrowingContinuation { continuation in
            let session = ASWebAuthenticationSession(url: url, callbackURLScheme: request.redirectScheme) { callbackURL, error in
                self.session = nil
                if let error {
                    continuation.resume(throwing: error)
                } else if let callbackURL {
                    continuation.resume(returning: callbackURL)
                } else {
                    continuation.resume(throwing: OAuthError.missingCallback)
                }
            }
            session.presentationContextProvider = self
            // Do not reuse Microsoft browser cookies from a previous Legend sign-in.
            // This preserves the existing OAuth/PKCE authority while allowing the user
            // to authenticate with a different Microsoft account.
            session.prefersEphemeralWebBrowserSession = true
            self.session = session

            guard session.start() else {
                self.session = nil
                continuation.resume(throwing: OAuthError.sessionStartFailed)
                return
            }
        }
    }

    func presentationAnchor(for session: ASWebAuthenticationSession) -> ASPresentationAnchor {
        UIApplication.shared.connectedScenes
            .compactMap { ($0 as? UIWindowScene)?.keyWindow }
            .first ?? ASPresentationAnchor()
    }
}

enum OAuthError: LocalizedError, Equatable {
    case randomGenerationFailed
    case invalidAuthorizationEndpoint
    case missingCallback
    case sessionStartFailed

    var errorDescription: String? {
        switch self {
        case .randomGenerationFailed:
            return LegendLocalized("A secure sign-in request could not be created.")
        case .invalidAuthorizationEndpoint:
            return LegendLocalized("The mobile authorization endpoint is invalid.")
        case .missingCallback:
            return LegendLocalized("The authorization service did not return a callback.")
        case .sessionStartFailed:
            return LegendLocalized("The system browser could not start sign-in.")
        }
    }
}

private extension Data {
    func base64URLEncodedString() -> String {
        base64EncodedString()
            .replacingOccurrences(of: "+", with: "-")
            .replacingOccurrences(of: "/", with: "_")
            .replacingOccurrences(of: "=", with: "")
    }
}

private extension UIWindowScene {
    var keyWindow: UIWindow? {
        windows.first(where: \.isKeyWindow)
    }
}
