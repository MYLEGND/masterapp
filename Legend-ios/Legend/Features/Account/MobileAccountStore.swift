import Foundation

struct MobileAccountProfile: Codable, Equatable, Sendable {
    let participantType: ParticipantType
    let profileID: UUID
    let displayName: String
    let email: String?
    let phone: String?
    let title: String?
    let shortBio: String?
    /// A member-entered mobile-profile email. `email` is only populated when
    /// the member has chosen to make this address visible on their profile.
    let profileEmail: String?
    let isEmailVisible: Bool
    let username: String?
    let bio: String?
    let website: String?
    let location: String?
    let isPrivate: Bool
    let avatar: ProfileAvatar?

    private enum CodingKeys: String, CodingKey {
        case participantType, displayName, email, phone, title, shortBio, profileEmail, isEmailVisible, username, bio, website, location, isPrivate, avatar
        case profileID = "profileId"
    }

    init(
        participantType: ParticipantType,
        profileID: UUID,
        displayName: String,
        email: String?,
        phone: String?,
        title: String?,
        shortBio: String?,
        profileEmail: String? = nil,
        isEmailVisible: Bool = false,
        username: String? = nil,
        bio: String? = nil,
        website: String? = nil,
        location: String? = nil,
        isPrivate: Bool = false,
        avatar: ProfileAvatar?
    ) {
        self.participantType = participantType
        self.profileID = profileID
        self.displayName = displayName
        self.email = email
        self.phone = phone
        self.title = title
        self.shortBio = shortBio
        self.profileEmail = profileEmail
        self.isEmailVisible = isEmailVisible
        self.username = username
        self.bio = bio
        self.website = website
        self.location = location
        self.isPrivate = isPrivate
        self.avatar = avatar
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
            shortBio: try container.decodeIfPresent(String.self, forKey: .shortBio),
            profileEmail: try container.decodeIfPresent(String.self, forKey: .profileEmail),
            isEmailVisible: try container.decodeIfPresent(Bool.self, forKey: .isEmailVisible) ?? false,
            username: try container.decodeIfPresent(String.self, forKey: .username),
            bio: try container.decodeIfPresent(String.self, forKey: .bio),
            website: try container.decodeIfPresent(String.self, forKey: .website),
            location: try container.decodeIfPresent(String.self, forKey: .location),
            isPrivate: try container.decodeIfPresent(Bool.self, forKey: .isPrivate) ?? false,
            avatar: try container.decodeIfPresent(ProfileAvatar.self, forKey: .avatar))
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
    let isPrivate: Bool?

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
        isPrivate: Bool? = nil
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
        self.isPrivate = isPrivate
    }
}

struct MobileUsernameAvailability: Codable, Equatable, Sendable {
    let isAvailable: Bool
    let message: String?
}

private struct MobileAccountPrivacyUpdate: Encodable, Sendable {
    let isPrivate: Bool
}

protocol MobileAccountAPI: Sendable {
    func profile(accessToken: String) async throws -> MobileAccountProfile
    func update(_ update: MobileAccountUpdate, accessToken: String) async throws
    func updatePrivacy(isPrivate: Bool, accessToken: String) async throws -> MobileAccountProfile
    func usernameAvailability(username: String, accessToken: String) async throws -> MobileUsernameAvailability
}

extension MobileAccountAPI {
    func updatePrivacy(isPrivate: Bool, accessToken: String) async throws -> MobileAccountProfile {
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

    func updatePrivacy(isPrivate: Bool, accessToken: String) async throws -> MobileAccountProfile {
        throw MobileAPIError.unauthorized(correlationID: nil)
    }

    func usernameAvailability(username: String, accessToken: String) async throws -> MobileUsernameAvailability {
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

    func dismissActionFailure() {
        actionFailure = nil
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
