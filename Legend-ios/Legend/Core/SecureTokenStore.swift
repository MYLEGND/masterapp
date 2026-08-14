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
        Date().timeIntervalSince(interactiveSignInAt) >= Self.interactiveSignInRetention
    }

    private static var interactiveSignInRetention: TimeInterval {
        TimeInterval(LegendSharedDesign.accountSession.interactiveSignInRetentionDays)
            * 60 * 60 * 24
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

/// A server-confirmed Legend identity with a locally retained credential. The
/// browser/OAuth authority remains unchanged; this is only the secure device
/// index needed to reopen another already-signed-in Legend account.
struct MobileSignedInAccount: Codable, Equatable, Sendable, Identifiable {
    let id: String
    var displayName: String
    var participantType: ParticipantType
    var lastUsedAt: Date

    init(
        id: String,
        displayName: String,
        participantType: ParticipantType,
        lastUsedAt: Date = Date()
    ) {
        self.id = id
        self.displayName = displayName
        self.participantType = participantType
        self.lastUsedAt = lastUsedAt
    }
}

/// The same secure storage authority can retain several independently issued
/// credentials. A selected record is still exposed through SecureTokenStoring
/// so existing API stores never receive a second token path.
protocol MultiAccountSecureTokenStoring: SecureTokenStoring {
    func signedInAccounts() throws -> [MobileSignedInAccount]
    func selectedAccountID() throws -> String?
    func selectAccount(id: String) throws -> OAuthTokenSet?
    func upsert(_ tokens: OAuthTokenSet, for account: MobileSignedInAccount) throws -> MobileSignedInAccount
    func removeAccount(id: String) throws
}

struct KeychainTokenStore: MultiAccountSecureTokenStoring {
    private let service: String
    private let account = "session"

    init(service: String) {
        self.service = service
    }

    func read() throws -> OAuthTokenSet? {
        let catalog = try readCatalog()
        if let selectedID = catalog.selectedAccountID,
           let selected = catalog.accounts.first(where: { $0.account.id == selectedID }) {
            return selected.tokens
        }
        if let first = catalog.accounts.sorted(by: { $0.account.lastUsedAt > $1.account.lastUsedAt }).first {
            return first.tokens
        }
        return catalog.legacyTokens
    }

    func save(_ tokens: OAuthTokenSet) throws {
        var catalog = try readCatalog()
        if let selectedID = catalog.selectedAccountID,
           let index = catalog.accounts.firstIndex(where: { $0.account.id == selectedID }) {
            catalog.accounts[index].tokens = tokens
            catalog.accounts[index].account.lastUsedAt = Date()
        } else {
            // A first interactive login is intentionally provisional until the
            // server confirms the Legend identity that owns it.
            catalog.legacyTokens = tokens
        }
        try writeCatalog(catalog)
    }

    func clear() throws {
        var catalog = try readCatalog()
        guard let selectedID = catalog.selectedAccountID else {
            try deleteKeychainValue()
            return
        }
        catalog.accounts.removeAll { $0.account.id == selectedID }
        catalog.selectedAccountID = catalog.accounts
            .max(by: { $0.account.lastUsedAt < $1.account.lastUsedAt })?
            .account.id
        try writeCatalog(catalog)
    }

    func signedInAccounts() throws -> [MobileSignedInAccount] {
        try readCatalog().accounts
            .filter { !$0.tokens.requiresInteractiveSignIn }
            .map(\.account)
            .sorted { $0.lastUsedAt > $1.lastUsedAt }
    }

    func selectedAccountID() throws -> String? {
        try readCatalog().selectedAccountID
    }

    func selectAccount(id: String) throws -> OAuthTokenSet? {
        var catalog = try readCatalog()
        guard let index = catalog.accounts.firstIndex(where: { $0.account.id == id }) else {
            return nil
        }
        catalog.selectedAccountID = id
        catalog.accounts[index].account.lastUsedAt = Date()
        let tokens = catalog.accounts[index].tokens
        try writeCatalog(catalog)
        return tokens
    }

    func upsert(_ tokens: OAuthTokenSet, for account: MobileSignedInAccount) throws -> MobileSignedInAccount {
        var catalog = try readCatalog()
        let existingCredentialID = catalog.accounts.first(where: {
            Self.sameCredential($0.tokens, tokens)
        })?.account.id
        let normalized = MobileSignedInAccount(
            id: existingCredentialID ?? account.id,
            displayName: account.displayName,
            participantType: account.participantType,
            lastUsedAt: Date())
        if let index = catalog.accounts.firstIndex(where: { $0.account.id == normalized.id }) {
            catalog.accounts[index] = StoredAccount(account: normalized, tokens: tokens)
        } else {
            catalog.accounts.append(StoredAccount(account: normalized, tokens: tokens))
        }
        catalog.selectedAccountID = normalized.id
        catalog.legacyTokens = nil
        try writeCatalog(catalog)
        return normalized
    }

    func removeAccount(id: String) throws {
        var catalog = try readCatalog()
        catalog.accounts.removeAll { $0.account.id == id }
        if catalog.selectedAccountID == id {
            catalog.selectedAccountID = catalog.accounts
                .max(by: { $0.account.lastUsedAt < $1.account.lastUsedAt })?
                .account.id
        }
        try writeCatalog(catalog)
    }

    private func readCatalog() throws -> CredentialCatalog {
        var query = baseQuery
        query[kSecReturnData as String] = true
        query[kSecMatchLimit as String] = kSecMatchLimitOne

        var result: CFTypeRef?
        let status = SecItemCopyMatching(query as CFDictionary, &result)
        if status == errSecItemNotFound { return CredentialCatalog() }
        guard status == errSecSuccess, let data = result as? Data else {
            throw KeychainStoreError.readFailed(status)
        }
        if let catalog = try? JSONDecoder().decode(CredentialCatalog.self, from: data) {
            return catalog
        }
        // One-record installations are migrated only after their identity has
        // been revalidated by the server; no guessed account association.
        return CredentialCatalog(legacyTokens: try JSONDecoder().decode(OAuthTokenSet.self, from: data))
    }

    private func writeCatalog(_ catalog: CredentialCatalog) throws {
        let data = try JSONEncoder().encode(catalog)
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

    private func deleteKeychainValue() throws {
        let status = SecItemDelete(baseQuery as CFDictionary)
        guard status == errSecSuccess || status == errSecItemNotFound else {
            throw KeychainStoreError.deleteFailed(status)
        }
    }

    private struct StoredAccount: Codable, Equatable {
        var account: MobileSignedInAccount
        var tokens: OAuthTokenSet
    }

    private struct CredentialCatalog: Codable, Equatable {
        var selectedAccountID: String?
        var accounts: [StoredAccount]
        var legacyTokens: OAuthTokenSet?

        init(
            selectedAccountID: String? = nil,
            accounts: [StoredAccount] = [],
            legacyTokens: OAuthTokenSet? = nil
        ) {
            self.selectedAccountID = selectedAccountID
            self.accounts = accounts
            self.legacyTokens = legacyTokens
        }
    }

    private static func sameCredential(_ lhs: OAuthTokenSet, _ rhs: OAuthTokenSet) -> Bool {
        LegendSessionCredentialFingerprint.make(from: lhs)
            == LegendSessionCredentialFingerprint.make(from: rhs)
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
