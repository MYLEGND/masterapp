import Foundation

struct MobileTranslationAccess: Codable, Equatable, Sendable {
    let state: String
    let canManage: Bool
    let preferredCommunicationLanguage: String?
    let characterAllowance: Int64
    let isUnlimited: Bool
    let consumedCharacters: Int64
    let reservedCharacters: Int64
    let remainingCharacters: Int64?
    let percentUsed: Double
    let periodStartUtc: Date?
    let periodEndUtc: Date?
    let nextResetUtc: Date?
    let entitlementSource: String
    let isFounderOverride: Bool
    let lastTranslationActivityUtc: Date?

    var isGranted: Bool { state == "Granted" }
    var isPending: Bool { state == "Pending" }

    private enum CodingKeys: String, CodingKey {
        case state
        case canManage
        case preferredCommunicationLanguage
        case characterAllowance, isUnlimited, consumedCharacters, reservedCharacters, remainingCharacters, percentUsed
        case periodStartUtc, periodEndUtc, nextResetUtc
        case entitlementSource, isFounderOverride, lastTranslationActivityUtc
    }

    init(
        state: String = "NotGranted",
        canManage: Bool = false,
        preferredCommunicationLanguage: String? = nil,
        characterAllowance: Int64 = 0,
        isUnlimited: Bool = false,
        consumedCharacters: Int64 = 0,
        reservedCharacters: Int64 = 0,
        remainingCharacters: Int64? = nil,
        percentUsed: Double = 0,
        periodStartUtc: Date? = nil,
        periodEndUtc: Date? = nil,
        nextResetUtc: Date? = nil,
        entitlementSource: String = "DefaultPolicy",
        isFounderOverride: Bool = false,
        lastTranslationActivityUtc: Date? = nil
    ) {
        self.state = state
        self.canManage = canManage
        self.preferredCommunicationLanguage = preferredCommunicationLanguage
        self.characterAllowance = characterAllowance
        self.isUnlimited = isUnlimited
        self.consumedCharacters = consumedCharacters
        self.reservedCharacters = reservedCharacters
        self.remainingCharacters = remainingCharacters
        self.percentUsed = percentUsed
        self.periodStartUtc = periodStartUtc
        self.periodEndUtc = periodEndUtc
        self.nextResetUtc = nextResetUtc
        self.entitlementSource = entitlementSource
        self.isFounderOverride = isFounderOverride
        self.lastTranslationActivityUtc = lastTranslationActivityUtc
    }

    init(from decoder: Decoder) throws {
        let container = try decoder.container(keyedBy: CodingKeys.self)
        self.init(
            state: try container.decodeIfPresent(String.self, forKey: .state) ?? "NotGranted",
            canManage: try container.decodeIfPresent(Bool.self, forKey: .canManage) ?? false,
            preferredCommunicationLanguage: try container.decodeIfPresent(String.self, forKey: .preferredCommunicationLanguage),
            characterAllowance: try container.decodeIfPresent(Int64.self, forKey: .characterAllowance) ?? 0,
            isUnlimited: try container.decodeIfPresent(Bool.self, forKey: .isUnlimited) ?? false,
            consumedCharacters: try container.decodeIfPresent(Int64.self, forKey: .consumedCharacters) ?? 0,
            reservedCharacters: try container.decodeIfPresent(Int64.self, forKey: .reservedCharacters) ?? 0,
            remainingCharacters: try container.decodeIfPresent(Int64.self, forKey: .remainingCharacters),
            percentUsed: try container.decodeIfPresent(Double.self, forKey: .percentUsed) ?? 0,
            periodStartUtc: try container.decodeIfPresent(Date.self, forKey: .periodStartUtc),
            periodEndUtc: try container.decodeIfPresent(Date.self, forKey: .periodEndUtc),
            nextResetUtc: try container.decodeIfPresent(Date.self, forKey: .nextResetUtc),
            entitlementSource: try container.decodeIfPresent(String.self, forKey: .entitlementSource) ?? "DefaultPolicy",
            isFounderOverride: try container.decodeIfPresent(Bool.self, forKey: .isFounderOverride) ?? false,
            lastTranslationActivityUtc: try container.decodeIfPresent(Date.self, forKey: .lastTranslationActivityUtc))
    }
}

struct MobileAccountProfile: Codable, Equatable, Sendable {
    let participantType: ParticipantType
    let profileID: UUID
    let displayName: String
    let email: String?
    let phone: String?
    let title: String?
    let roleLabel: String?
    let shortBio: String?
    /// A member-entered mobile-profile email. `email` is only populated when
    /// the member has chosen to make this address visible on their profile.
    let profileEmail: String?
    let isEmailVisible: Bool
    let isPhoneVisible: Bool
    let username: String?
    let bio: String?
    let website: String?
    let location: String?
    let isPrivate: Bool
    let avatar: ProfileAvatar?
    let isVerified: Bool
    let usernameChangesRemaining: Int
    let translationAccess: MobileTranslationAccess

    private enum CodingKeys: String, CodingKey {
        case participantType, displayName, email, phone, title, roleLabel, shortBio, profileEmail, isEmailVisible, isPhoneVisible, username, bio, website, location, isPrivate, avatar, isVerified, usernameChangesRemaining, translationAccess
        case profileID = "profileId"
    }

    init(
        participantType: ParticipantType,
        profileID: UUID,
        displayName: String,
        email: String?,
        phone: String?,
        title: String?,
        roleLabel: String? = nil,
        shortBio: String?,
        profileEmail: String? = nil,
        isEmailVisible: Bool = false,
        isPhoneVisible: Bool = false,
        username: String? = nil,
        bio: String? = nil,
        website: String? = nil,
        location: String? = nil,
        isPrivate: Bool = false,
        avatar: ProfileAvatar?,
        isVerified: Bool = false,
        usernameChangesRemaining: Int = 2,
        translationAccess: MobileTranslationAccess = .init()
    ) {
        self.participantType = participantType
        self.profileID = profileID
        self.displayName = displayName
        self.email = email
        self.phone = phone
        self.title = title
        self.roleLabel = roleLabel
        self.shortBio = shortBio
        self.profileEmail = profileEmail
        self.isEmailVisible = isEmailVisible
        self.isPhoneVisible = isPhoneVisible
        self.username = username
        self.bio = bio
        self.website = website
        self.location = location
        self.isPrivate = isPrivate
        self.avatar = avatar
        self.isVerified = isVerified
        self.usernameChangesRemaining = usernameChangesRemaining
        self.translationAccess = translationAccess
    }

    init(from decoder: Decoder) throws {
        let container = try decoder.container(keyedBy: CodingKeys.self)
        self.init(
            participantType: try container.decode(ParticipantType.self, forKey: .participantType),
            profileID: try container.decode(UUID.self, forKey: .profileID),
            displayName: try container.decode(String.self, forKey: .displayName),
            email: try container.decodeIfPresent(String.self, forKey: .email),
            phone: try container.decodeIfPresent(String.self, forKey: .phone),
            title: try container.decodeIfPresent(String.self, forKey: .title),
            roleLabel: try container.decodeIfPresent(String.self, forKey: .roleLabel),
            shortBio: try container.decodeIfPresent(String.self, forKey: .shortBio),
            profileEmail: try container.decodeIfPresent(String.self, forKey: .profileEmail),
            isEmailVisible: try container.decodeIfPresent(Bool.self, forKey: .isEmailVisible) ?? false,
            isPhoneVisible: try container.decodeIfPresent(Bool.self, forKey: .isPhoneVisible) ?? false,
            username: try container.decodeIfPresent(String.self, forKey: .username),
            bio: try container.decodeIfPresent(String.self, forKey: .bio),
            website: try container.decodeIfPresent(String.self, forKey: .website),
            location: try container.decodeIfPresent(String.self, forKey: .location),
            isPrivate: try container.decodeIfPresent(Bool.self, forKey: .isPrivate) ?? false,
            avatar: try container.decodeIfPresent(ProfileAvatar.self, forKey: .avatar),
            isVerified: try container.decodeIfPresent(Bool.self, forKey: .isVerified) ?? false,
            usernameChangesRemaining: try container.decodeIfPresent(Int.self, forKey: .usernameChangesRemaining) ?? 2,
            translationAccess: try container.decodeIfPresent(MobileTranslationAccess.self, forKey: .translationAccess) ?? .init())
    }
}

struct MobileAccountUpdate: Encodable, Sendable {
    let displayName: String
    let phone: String?
    let title: String?
    let shortBio: String?
    let username: String?
    let bio: String?
    let website: String?
    let location: String?
    let publicEmail: String?
    let isEmailVisible: Bool
    let isPhoneVisible: Bool
    let isPrivate: Bool?
    let preferredCommunicationLanguage: String?

    init(
        displayName: String,
        phone: String?,
        title: String?,
        shortBio: String?,
        username: String? = nil,
        bio: String? = nil,
        website: String? = nil,
        location: String? = nil,
        publicEmail: String? = nil,
        isEmailVisible: Bool = false,
        isPhoneVisible: Bool = false,
        isPrivate: Bool? = nil,
        preferredCommunicationLanguage: String? = nil
    ) {
        self.displayName = displayName
        self.phone = phone
        self.title = title
        self.shortBio = shortBio
        self.username = username
        self.bio = bio
        self.website = website
        self.location = location
        self.publicEmail = publicEmail
        self.isEmailVisible = isEmailVisible
        self.isPhoneVisible = isPhoneVisible
        self.isPrivate = isPrivate
        self.preferredCommunicationLanguage = preferredCommunicationLanguage
    }
}

struct MobileAccountAvatarUpdate: Encodable, Sendable {
    let base64Content: String
}

struct MobileUsernameAvailability: Codable, Equatable, Sendable {
    let isAvailable: Bool
    let message: String?
}

private struct MobileAccountPrivacyUpdate: Encodable, Sendable {
    let isPrivate: Bool
}

struct MobileAccountLifecycle: Decodable, Equatable, Sendable {
    let state: String
    let allowsFullAccess: Bool
    let canResume: Bool
    let pausedUtc: Date?
    let deletionRequestedUtc: Date?
    let message: String?

    var blocksFullExperience: Bool { !allowsFullAccess }
}

private struct MobileAccountLifecycleConfirmation: Encodable, Sendable {
    let confirmation: String
}

private struct MobileAccountLifecycleResumeRequest: Encodable, Sendable {}

protocol MobileAccountAPI: Sendable {
    func profile(accessToken: String) async throws -> MobileAccountProfile
    func update(_ update: MobileAccountUpdate, accessToken: String) async throws
    func updateAvatar(_ update: MobileAccountAvatarUpdate, accessToken: String) async throws -> MobileAccountProfile
    func updatePrivacy(isPrivate: Bool, accessToken: String) async throws -> MobileAccountProfile
    func usernameAvailability(username: String, accessToken: String) async throws -> MobileUsernameAvailability
    func lifecycle(accessToken: String) async throws -> MobileAccountLifecycle
    func pauseAccount(accessToken: String) async throws -> MobileAccountLifecycle
    func resumeAccount(accessToken: String) async throws -> MobileAccountLifecycle
    func requestAccountDeletion(confirmation: String, accessToken: String) async throws -> MobileAccountLifecycle
}

extension MobileAccountAPI {
    func updatePrivacy(isPrivate: Bool, accessToken: String) async throws -> MobileAccountProfile {
        throw MobileAPIError.unauthorized(correlationID: nil)
    }

    func lifecycle(accessToken: String) async throws -> MobileAccountLifecycle {
        throw MobileAPIError.unauthorized(correlationID: nil)
    }

    func pauseAccount(accessToken: String) async throws -> MobileAccountLifecycle {
        throw MobileAPIError.unauthorized(correlationID: nil)
    }

    func resumeAccount(accessToken: String) async throws -> MobileAccountLifecycle {
        throw MobileAPIError.unauthorized(correlationID: nil)
    }

    func requestAccountDeletion(confirmation: String, accessToken: String) async throws -> MobileAccountLifecycle {
        throw MobileAPIError.unauthorized(correlationID: nil)
    }
}

struct MobileUnavailableAccountAPI: MobileAccountAPI {
    func profile(accessToken: String) async throws -> MobileAccountProfile {
        throw MobileAPIError.unauthorized(correlationID: nil)
    }

    func update(_ update: MobileAccountUpdate, accessToken: String) async throws {
        throw MobileAPIError.unauthorized(correlationID: nil)
    }

    func updateAvatar(_ update: MobileAccountAvatarUpdate, accessToken: String) async throws -> MobileAccountProfile {
        throw MobileAPIError.unauthorized(correlationID: nil)
    }

    func updatePrivacy(isPrivate: Bool, accessToken: String) async throws -> MobileAccountProfile {
        throw MobileAPIError.unauthorized(correlationID: nil)
    }

    func usernameAvailability(username: String, accessToken: String) async throws -> MobileUsernameAvailability {
        throw MobileAPIError.unauthorized(correlationID: nil)
    }

    func lifecycle(accessToken: String) async throws -> MobileAccountLifecycle {
        throw MobileAPIError.unauthorized(correlationID: nil)
    }

    func pauseAccount(accessToken: String) async throws -> MobileAccountLifecycle {
        throw MobileAPIError.unauthorized(correlationID: nil)
    }

    func resumeAccount(accessToken: String) async throws -> MobileAccountLifecycle {
        throw MobileAPIError.unauthorized(correlationID: nil)
    }

    func requestAccountDeletion(confirmation: String, accessToken: String) async throws -> MobileAccountLifecycle {
        throw MobileAPIError.unauthorized(correlationID: nil)
    }
}

struct URLSessionMobileAccountAPI: MobileAccountAPI {
    let client: MobileHTTPClient
    let participantType: ParticipantType

    func profile(accessToken: String) async throws -> MobileAccountProfile {
        try await client.get(
            "/api/v1/mobile/account",
            accessToken: accessToken,
            headers: participantHeader,
            response: MobileAccountProfile.self)
    }

    func update(_ update: MobileAccountUpdate, accessToken: String) async throws {
        try await client.put(
            "/api/v1/mobile/account",
            body: update,
            accessToken: accessToken,
            headers: participantHeader)
    }

    func updateAvatar(_ update: MobileAccountAvatarUpdate, accessToken: String) async throws -> MobileAccountProfile {
        try await client.put(
            "/api/v1/mobile/account/avatar",
            body: update,
            accessToken: accessToken,
            headers: participantHeader,
            response: MobileAccountProfile.self)
    }

    func updatePrivacy(isPrivate: Bool, accessToken: String) async throws -> MobileAccountProfile {
        try await client.put(
            "/api/v1/mobile/account/privacy",
            body: MobileAccountPrivacyUpdate(isPrivate: isPrivate),
            accessToken: accessToken,
            headers: participantHeader,
            response: MobileAccountProfile.self)
    }

    func usernameAvailability(username: String, accessToken: String) async throws -> MobileUsernameAvailability {
        try await client.get(
            "/api/v1/mobile/account/username-availability",
            accessToken: accessToken,
            queryItems: [URLQueryItem(name: "username", value: username)],
            headers: participantHeader,
            response: MobileUsernameAvailability.self)
    }

    func lifecycle(accessToken: String) async throws -> MobileAccountLifecycle {
        try await client.get(
            "/api/v1/mobile/account/lifecycle",
            accessToken: accessToken,
            headers: participantHeader,
            response: MobileAccountLifecycle.self)
    }

    func pauseAccount(accessToken: String) async throws -> MobileAccountLifecycle {
        try await client.post(
            "/api/v1/mobile/account/lifecycle/pause",
            body: MobileAccountLifecycleConfirmation(confirmation: "PAUSE"),
            accessToken: accessToken,
            headers: participantHeader,
            response: MobileAccountLifecycle.self)
    }

    func resumeAccount(accessToken: String) async throws -> MobileAccountLifecycle {
        try await client.post(
            "/api/v1/mobile/account/lifecycle/resume",
            body: MobileAccountLifecycleResumeRequest(),
            accessToken: accessToken,
            headers: participantHeader,
            response: MobileAccountLifecycle.self)
    }

    func requestAccountDeletion(confirmation: String, accessToken: String) async throws -> MobileAccountLifecycle {
        try await client.post(
            "/api/v1/mobile/account/lifecycle/deletion-request",
            body: MobileAccountLifecycleConfirmation(confirmation: confirmation),
            accessToken: accessToken,
            headers: participantHeader,
            response: MobileAccountLifecycle.self)
    }

    private var participantHeader: [String: String] {
        ["X-Legend-Participant-Type": participantType.rawValue]
    }
}

@MainActor
final class MobileAccountStore: ObservableObject {
    @Published private(set) var state: MobileDataLoadState<MobileAccountProfile> = .idle
    @Published private(set) var actionFailure: UserFacingFailure?
    @Published private(set) var isSaving = false
    @Published private(set) var usernameAvailability: MobileUsernameAvailability?
    @Published private(set) var isCheckingUsername = false
    @Published private(set) var isRefreshing = false
    @Published private(set) var refreshFailure: UserFacingFailure?
    @Published private(set) var lifecycle: MobileAccountLifecycle?
    @Published private(set) var isUpdatingLifecycle = false

    private let api: any MobileAccountAPI
    private let accessTokenProvider: () async throws -> String
    private let diagnostics: LegendDiagnostics
    private var loadTask: Task<MobileStoreLoadResult, Never>?
    private var usernameCheckTask: Task<Void, Never>?
    private var usernameCheckGeneration = 0

    init(
        api: any MobileAccountAPI,
        accessTokenProvider: @escaping () async throws -> String,
        diagnostics: LegendDiagnostics
    ) {
        self.api = api
        self.accessTokenProvider = accessTokenProvider
        self.diagnostics = diagnostics
    }

    func load() {
        guard loadTask == nil else { return }
        if !hasCachedValue {
            state = .loading
        }
        Task { _ = await loadIfNeeded() }
    }

    func loadIfNeeded() async -> MobileStoreLoadResult {
        hasCachedValue ? .loaded : await request(preservingCachedValue: false)
    }

    func refresh() async -> MobileStoreLoadResult {
        await request(preservingCachedValue: hasCachedValue)
    }

    @discardableResult
    func save(_ update: MobileAccountUpdate) async -> Bool {
        guard !isSaving else { return false }
        isSaving = true
        actionFailure = nil
        defer { isSaving = false }
        do {
            let accessToken = try await accessTokenProvider()
            try await api.update(update, accessToken: accessToken)
            state = .loaded(try await api.profile(accessToken: accessToken))
            refreshFailure = nil
            return true
        } catch {
            actionFailure = failure(for: error, title: "Account update unavailable")
            return false
        }
    }

    @discardableResult
    func uploadAvatar(_ update: MobileAccountAvatarUpdate) async -> Bool {
        guard !isSaving else { return false }
        isSaving = true
        actionFailure = nil
        defer { isSaving = false }
        do {
            let accessToken = try await accessTokenProvider()
            state = .loaded(try await api.updateAvatar(update, accessToken: accessToken))
            refreshFailure = nil
            return true
        } catch {
            actionFailure = failure(for: error, title: "Profile picture unavailable")
            return false
        }
    }

    @discardableResult
    func savePreferredCommunicationLanguage(_ language: String) async -> Bool {
        guard case .loaded(let profile) = state else { return false }
        return await save(MobileAccountUpdate(
            displayName: profile.displayName,
            phone: profile.phone,
            title: profile.title,
            shortBio: profile.shortBio,
            username: profile.username,
            bio: profile.bio,
            website: profile.website,
            location: profile.location,
            publicEmail: profile.profileEmail,
            isEmailVisible: profile.isEmailVisible,
            isPhoneVisible: profile.isPhoneVisible,
            preferredCommunicationLanguage: language))
    }

    func dismissActionFailure() {
        actionFailure = nil
    }

    func loadLifecycle() async {
        guard !isUpdatingLifecycle else { return }
        do {
            let accessToken = try await accessTokenProvider()
            lifecycle = try await api.lifecycle(accessToken: accessToken)
        } catch {
            // A lifecycle status is a protective gate, not a reason to replace a
            // working profile with an error state. Mutations remain server-authorized.
        }
    }

    @discardableResult
    func pauseAccount() async -> Bool {
        await updateLifecycle(
            title: "Account pause unavailable",
            operation: { api, token in
                try await api.pauseAccount(accessToken: token)
            })
    }

    @discardableResult
    func resumeAccount() async -> Bool {
        await updateLifecycle(
            title: "Account resume unavailable",
            operation: { api, token in
                try await api.resumeAccount(accessToken: token)
            })
    }

    @discardableResult
    func requestAccountDeletion(confirmation: String) async -> Bool {
        await updateLifecycle(
            title: "Account deletion unavailable",
            operation: { api, token in
                try await api.requestAccountDeletion(confirmation: confirmation, accessToken: token)
            })
    }

    func setPrivateAccount(_ isPrivate: Bool) {
        guard !isSaving else { return }
        isSaving = true
        actionFailure = nil
        Task {
            defer { isSaving = false }
            do {
                let accessToken = try await accessTokenProvider()
                state = .loaded(try await api.updatePrivacy(isPrivate: isPrivate, accessToken: accessToken))
                refreshFailure = nil
            } catch {
                actionFailure = failure(for: error, title: "Account privacy unavailable")
            }
        }
    }

    var isUsernameUnavailable: Bool {
        usernameAvailability?.isAvailable == false
    }

    func checkUsernameAvailability(_ username: String) {
        usernameCheckTask?.cancel()
        usernameCheckGeneration += 1
        let generation = usernameCheckGeneration
        let requestedUsername = username.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !requestedUsername.isEmpty else {
            usernameAvailability = nil
            isCheckingUsername = false
            return
        }

        usernameAvailability = nil
        isCheckingUsername = true
        usernameCheckTask = Task { [weak self] in
            try? await Task.sleep(for: .milliseconds(250))
            guard !Task.isCancelled, let self else { return }

            defer {
                if generation == self.usernameCheckGeneration {
                    self.isCheckingUsername = false
                }
            }
            do {
                let token = try await self.accessTokenProvider()
                let availability = try await self.api.usernameAvailability(
                    username: requestedUsername,
                    accessToken: token)
                guard !Task.isCancelled,
                      generation == self.usernameCheckGeneration else {
                    return
                }
                self.usernameAvailability = availability
            } catch {
                // Saving remains protected by the same server validation. A transient
                // availability check should not present a misleading availability state.
            }
        }
    }

    private var hasCachedValue: Bool {
        if case .loaded = state { return true }
        return false
    }

    private func request(preservingCachedValue: Bool) async -> MobileStoreLoadResult {
        if let loadTask {
            return await loadTask.value
        }

        if preservingCachedValue {
            isRefreshing = true
            refreshFailure = nil
        } else {
            state = .loading
        }

        let task = Task { [weak self] in
            guard let self else {
                return MobileStoreLoadResult.failed(UserFacingFailure(
                    title: "Account unavailable",
                    message: "The account store is no longer available.",
                    correlationID: nil))
            }

            return await self.executeLoad(preservingCachedValue: preservingCachedValue)
        }
        loadTask = task
        let result = await task.value
        loadTask = nil
        return result
    }

    private func updateLifecycle(
        title: String,
        operation: @escaping @Sendable (any MobileAccountAPI, String) async throws -> MobileAccountLifecycle
    ) async -> Bool {
        guard !isUpdatingLifecycle else { return false }
        isUpdatingLifecycle = true
        actionFailure = nil
        defer { isUpdatingLifecycle = false }
        do {
            let accessToken = try await accessTokenProvider()
            lifecycle = try await operation(api, accessToken)
            return true
        } catch {
            actionFailure = failure(for: error, title: title)
            return false
        }
    }

    private func executeLoad(preservingCachedValue: Bool) async -> MobileStoreLoadResult {
        defer { isRefreshing = false }
        do {
            let accessToken = try await accessTokenProvider()
            state = .loaded(try await api.profile(accessToken: accessToken))
            refreshFailure = nil
            return .loaded
        } catch {
            let presentation = failure(for: error, title: "Account unavailable")
            if preservingCachedValue {
                refreshFailure = presentation
            } else {
                state = .unavailable(presentation)
            }
            return mobileLoadResult(for: error, failure: presentation)
        }
    }

    private func failure(for error: Error, title: String) -> UserFacingFailure {
        let apiError = error as? MobileAPIError
        diagnostics.record(
            category: .networking,
            summary: "A native account request could not be completed.",
            correlationID: apiError?.correlationID)
        return UserFacingFailure(
            title: title,
            message: error.localizedDescription,
            correlationID: apiError?.correlationID)
    }
}
