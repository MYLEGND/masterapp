import Foundation
import Security

struct OAuthTokenSet: Codable, Equatable, Sendable {
    let accessToken: String
    let refreshToken: String?
    let expiresAt: Date
    /// This is intentionally independent from the short-lived access-token expiry.
    /// Refreshing a bearer token must not reset the member's 90-day interactive
    /// sign-in checkpoint.
    let interactiveSignInAt: Date

    init(
        accessToken: String,
        refreshToken: String?,
        expiresAt: Date,
        interactiveSignInAt: Date = Date()
    ) {
        self.accessToken = accessToken
        self.refreshToken = refreshToken
        self.expiresAt = expiresAt
        self.interactiveSignInAt = interactiveSignInAt
    }

    private enum CodingKeys: String, CodingKey {
        case accessToken
        case refreshToken
        case expiresAt
        case interactiveSignInAt
    }

    init(from decoder: Decoder) throws {
        let container = try decoder.container(keyedBy: CodingKeys.self)
        self.init(
            accessToken: try container.decode(String.self, forKey: .accessToken),
            refreshToken: try container.decodeIfPresent(String.self, forKey: .refreshToken),
            expiresAt: try container.decode(Date.self, forKey: .expiresAt),
            // Existing keychain records predate the checkpoint. They are treated as
            // due now instead of inventing a timestamp that weakens the policy.
            interactiveSignInAt: try container.decodeIfPresent(
                Date.self,
                forKey: .interactiveSignInAt) ?? .distantPast)
    }

    var requiresInteractiveSignIn: Bool {
        Date().timeIntervalSince(interactiveSignInAt) >= 60 * 60 * 24 * 90
    }

    func refreshed(
        accessToken: String,
        refreshToken: String?,
        expiresAt: Date
    ) -> OAuthTokenSet {
        OAuthTokenSet(
            accessToken: accessToken,
            refreshToken: refreshToken,
            expiresAt: expiresAt,
            interactiveSignInAt: interactiveSignInAt)
    }
}

protocol SecureTokenStoring: Sendable {
    func read() throws -> OAuthTokenSet?
    func save(_ tokens: OAuthTokenSet) throws
    func clear() throws
}

struct KeychainTokenStore: SecureTokenStoring {
    private let service: String
    private let account = "session"

    init(service: String) {
        self.service = service
    }

    func read() throws -> OAuthTokenSet? {
        var query = baseQuery
        query[kSecReturnData as String] = true
        query[kSecMatchLimit as String] = kSecMatchLimitOne

        var result: CFTypeRef?
        let status = SecItemCopyMatching(query as CFDictionary, &result)
        if status == errSecItemNotFound { return nil }
        guard status == errSecSuccess, let data = result as? Data else {
            throw KeychainStoreError.readFailed(status)
        }
        return try JSONDecoder().decode(OAuthTokenSet.self, from: data)
    }

    func save(_ tokens: OAuthTokenSet) throws {
        let data = try JSONEncoder().encode(tokens)
        var query = baseQuery
        query[kSecValueData as String] = data
        query[kSecAttrAccessible as String] = kSecAttrAccessibleAfterFirstUnlockThisDeviceOnly

        let addStatus = SecItemAdd(query as CFDictionary, nil)
        if addStatus == errSecDuplicateItem {
            let update = [kSecValueData as String: data]
            let updateStatus = SecItemUpdate(baseQuery as CFDictionary, update as CFDictionary)
            guard updateStatus == errSecSuccess else { throw KeychainStoreError.writeFailed(updateStatus) }
            return
        }
        guard addStatus == errSecSuccess else { throw KeychainStoreError.writeFailed(addStatus) }
    }

    func clear() throws {
        let status = SecItemDelete(baseQuery as CFDictionary)
        guard status == errSecSuccess || status == errSecItemNotFound else {
            throw KeychainStoreError.deleteFailed(status)
        }
    }

    private var baseQuery: [String: Any] {
        [
            kSecClass as String: kSecClassGenericPassword,
            kSecAttrService as String: service,
            kSecAttrAccount as String: account
        ]
    }
}

enum KeychainStoreError: LocalizedError {
    case readFailed(OSStatus)
    case writeFailed(OSStatus)
    case deleteFailed(OSStatus)

    var errorDescription: String? {
        "Secure session storage could not be updated."
    }
}
