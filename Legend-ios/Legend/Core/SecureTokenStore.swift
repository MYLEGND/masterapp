import Foundation
import Security

struct OAuthTokenSet: Codable, Equatable, Sendable {
    let accessToken: String
    let refreshToken: String?
    let expiresAt: Date
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
