import Foundation

protocol MobileDiscoveryAPI: Sendable {
    func search(
        query: String?,
        offset: Int,
        pageSize: Int,
        sort: MobileDiscoverySortMode?,
        accessToken: String
    ) async throws -> MobileDiscoveryPage

    func profile(clientProfileID: UUID, accessToken: String) async throws -> MobileDiscoveryProfile
}

struct MobileUnavailableDiscoveryAPI: MobileDiscoveryAPI {
    func search(
        query: String?,
        offset: Int,
        pageSize: Int,
        sort: MobileDiscoverySortMode?,
        accessToken: String
    ) async throws -> MobileDiscoveryPage {
        throw MobileAPIError.unauthorized(correlationID: nil)
    }

    func profile(clientProfileID: UUID, accessToken: String) async throws -> MobileDiscoveryProfile {
        throw MobileAPIError.unauthorized(correlationID: nil)
    }
}

struct URLSessionMobileDiscoveryAPI: MobileDiscoveryAPI {
    let client: MobileHTTPClient
    let participantType: ParticipantType

    private var participantHeader: [String: String] {
        ["X-Legend-Participant-Type": participantType.rawValue]
    }

    func search(
        query: String?,
        offset: Int,
        pageSize: Int,
        sort: MobileDiscoverySortMode?,
        accessToken: String
    ) async throws -> MobileDiscoveryPage {
        var items = [
            URLQueryItem(name: "offset", value: String(offset)),
            URLQueryItem(name: "pageSize", value: String(pageSize))
        ]
        if let query, !query.isEmpty {
            items.append(URLQueryItem(name: "query", value: query))
        }
        if let sort {
            items.append(URLQueryItem(name: "sort", value: sort.rawValue))
        }

        return try await client.get(
            "/api/v1/mobile/discovery/search",
            accessToken: accessToken,
            queryItems: items,
            headers: participantHeader,
            response: MobileDiscoveryPage.self)
    }

    func profile(clientProfileID: UUID, accessToken: String) async throws -> MobileDiscoveryProfile {
        try await client.get(
            "/api/v1/mobile/discovery/profiles/\(clientProfileID.uuidString)",
            accessToken: accessToken,
            headers: participantHeader,
            response: MobileDiscoveryProfile.self)
    }
}

/// Drives the Discover surface: debounced server-side search, infinite scroll, and
/// relationship actions. Results always come from the server; this store never
/// filters or ranks locally, so what the user sees is what the directory holds.
@MainActor
final class MobileDiscoveryStore: ObservableObject {
    @Published var searchText = "" {
        didSet {
            guard searchText != oldValue else { return }
            scheduleSearch()
        }
    }

    @Published private(set) var state: MobileDataLoadState<[MobileDiscoveryResult]> = .idle
    @Published private(set) var isLoadingMore = false
    @Published private(set) var isSearching = false
    @Published private(set) var totalCount = 0
    @Published private(set) var hasMore = false
    @Published private(set) var scope: MobileDiscoveryScope?
    @Published private(set) var sortMode: MobileDiscoverySortMode = .recommended
    @Published private(set) var recommendations: [MobileDiscoveryResult] = []
    @Published private(set) var actionFailure: UserFacingFailure?
    @Published private(set) var pendingRelationshipProfileIDs: Set<UUID> = []

    /// How long to wait after the last keystroke before asking the server.
    private static let searchDebounce = Duration.milliseconds(300)
    private static let pageSize = 20
    private static let recommendationPageSize = 6

    private let api: any MobileDiscoveryAPI
    private let social: MobileSocialStore
    private let journeyCircles: MobileJourneyCirclesStore
    private let accessTokenProvider: () async throws -> String
    private let diagnostics: LegendDiagnostics

    private var searchTask: Task<Void, Never>?
    private var loadMoreTask: Task<Void, Never>?
    /// Guards against a slow earlier response overwriting a newer one.
    private var requestGeneration = 0

    init(
        api: any MobileDiscoveryAPI,
        social: MobileSocialStore,
        journeyCircles: MobileJourneyCirclesStore,
        accessTokenProvider: @escaping () async throws -> String,
        diagnostics: LegendDiagnostics
    ) {
        self.api = api
        self.social = social
        self.journeyCircles = journeyCircles
        self.accessTokenProvider = accessTokenProvider
        self.diagnostics = diagnostics
    }

    var results: [MobileDiscoveryResult] {
        if case .loaded(let results) = state { return results }
        return []
    }

    func load() {
        guard case .idle = state else { return }
        Task { await refresh() }
    }

    func refresh() async {
        await runSearch(resetting: true, sort: preferredSort)
    }

    func loadMoreIfNeeded(currentItem: MobileDiscoveryResult) {
        guard hasMore, !isLoadingMore, loadMoreTask == nil else { return }
        guard results.suffix(5).contains(where: { $0.id == currentItem.id }) else { return }

        loadMoreTask = Task { [weak self] in
            await self?.loadNextPage()
            self?.loadMoreTask = nil
        }
    }

    func dismissActionFailure() {
        actionFailure = nil
    }

    // ------------------------------------------------------------ relationships

    /// Follows or unfollows through the shared social service, then reconciles this
    /// list with the state the server confirmed.
    func toggleFollow(_ result: MobileDiscoveryResult) {
        guard result.relationship.canFollow,
              !pendingRelationshipProfileIDs.contains(result.id) else {
            return
        }

        pendingRelationshipProfileIDs.insert(result.id)
        let wasFollowing = result.relationship.followedByCurrentActor

        Task { [weak self] in
            guard let self else { return }
            defer { self.pendingRelationshipProfileIDs.remove(result.id) }

            let confirmed = await self.social.setFollow(
                userID: result.identity.userID,
                participantType: result.identity.participantType,
                isFollowing: !wasFollowing)

            guard let confirmed else {
                self.actionFailure = self.social.actionFailure
                    ?? UserFacingFailure(
                        title: "Could not update your connection",
                        message: "Please try again.",
                        correlationID: nil)
                return
            }

            self.applyRelationship(
                profileID: result.id,
                transform: {
                    $0.replacing(
                        followedByCurrentActor: confirmed.isFollowing,
                        followRequestPending: confirmed.hasPendingRequest)
                })
        }
    }

    /// Sends a Journey Circles connection request through the existing consented
    /// two-way flow. Discover does not invent a second relationship primitive.
    func requestConnection(_ result: MobileDiscoveryResult) {
        guard result.relationship.canRequestConnection,
              !pendingRelationshipProfileIDs.contains(result.id) else {
            return
        }

        pendingRelationshipProfileIDs.insert(result.id)

        Task { [weak self] in
            guard let self else { return }
            defer { self.pendingRelationshipProfileIDs.remove(result.id) }

            let sent = await self.journeyCircles.requestConnectionConfirmed(to: result.id)
            if sent {
                self.applyRelationship(
                    profileID: result.id,
                    transform: { $0.replacing(connectionStatus: .pending) })
            } else {
                self.actionFailure = self.journeyCircles.actionFailure
                    ?? UserFacingFailure(
                        title: "Could not send the request",
                        message: "Please try again.",
                        correlationID: nil)
            }
        }
    }

    func profile(for clientProfileID: UUID) async -> MobileDiscoveryProfile? {
        do {
            let token = try await accessTokenProvider()
            return try await api.profile(clientProfileID: clientProfileID, accessToken: token)
        } catch {
            actionFailure = failure(for: error, title: "Profile unavailable")
            return nil
        }
    }

    // ------------------------------------------------------------------ private

    private var preferredSort: MobileDiscoverySortMode {
        if !searchText.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
            return .relevance
        }
        return .recommended
    }

    private func scheduleSearch() {
        searchTask?.cancel()
        searchTask = Task { [weak self] in
            guard let self else { return }
            // Debounce: only the last keystroke in a burst reaches the server.
            try? await Task.sleep(for: Self.searchDebounce)
            guard !Task.isCancelled else { return }
            await self.runSearch(resetting: true, sort: self.preferredSort)
        }
    }

    private func runSearch(resetting: Bool, sort: MobileDiscoverySortMode) async {
        requestGeneration += 1
        let generation = requestGeneration

        let trimmed = searchText.trimmingCharacters(in: .whitespacesAndNewlines)
        let query = trimmed.isEmpty ? nil : trimmed

        if resetting, !hasLoadedResults {
            state = .loading
        }
        isSearching = true
        defer { if generation == requestGeneration { isSearching = false } }

        do {
            let token = try await accessTokenProvider()
            let page = try await api.search(
                query: query,
                offset: 0,
                pageSize: query == nil && sort == .recommended
                    ? Self.recommendationPageSize
                    : Self.pageSize,
                sort: sort,
                accessToken: token)

            // A newer keystroke already superseded this request.
            guard generation == requestGeneration else { return }

            // Clients see the concise recommendation set first, followed by the
            // active Legend directory. Both are server-authoritative queries over
            // the same endpoint; no device-side mirror or ranking is introduced.
            if query == nil, sort == .recommended, page.scope == .community {
                let directory = try await api.search(
                    query: nil,
                    offset: 0,
                    pageSize: Self.pageSize,
                    sort: .directory,
                    accessToken: token)
                guard generation == requestGeneration else { return }

                recommendations = Array(
                    page.results
                        .filter { $0.compatibilityScore > 0 }
                        .prefix(Self.recommendationPageSize))
                state = .loaded(directory.results)
                totalCount = directory.totalCount
                hasMore = directory.hasMore
                scope = directory.scope
                sortMode = directory.sortMode
            } else {
                recommendations = []
                state = .loaded(page.results)
                totalCount = page.totalCount
                hasMore = page.hasMore
                scope = page.scope
                sortMode = page.sortMode
            }
        } catch {
            guard generation == requestGeneration else { return }
            let presentation = failure(for: error, title: "Discover unavailable")
            if hasLoadedResults {
                actionFailure = presentation
            } else {
                state = .unavailable(presentation)
            }
        }
    }

    private func loadNextPage() async {
        guard hasMore else { return }
        let generation = requestGeneration
        let current = results
        isLoadingMore = true
        defer { isLoadingMore = false }

        let trimmed = searchText.trimmingCharacters(in: .whitespacesAndNewlines)

        do {
            let token = try await accessTokenProvider()
            let page = try await api.search(
                query: trimmed.isEmpty ? nil : trimmed,
                offset: current.count,
                pageSize: Self.pageSize,
                sort: sortMode,
                accessToken: token)

            // Discard a late page that belongs to a superseded query.
            guard generation == requestGeneration else { return }

            // Guard against duplicates if the directory shifted between pages.
            var seen = Set(current.map(\.id))
            let appended = page.results.filter { seen.insert($0.id).inserted }

            state = .loaded(current + appended)
            totalCount = page.totalCount
            hasMore = page.hasMore
        } catch {
            guard generation == requestGeneration else { return }
            actionFailure = failure(for: error, title: "Could not load more members")
        }
    }

    private var hasLoadedResults: Bool {
        if case .loaded = state { return true }
        return false
    }

    private func applyRelationship(
        profileID: UUID,
        transform: (MobileDiscoveryRelationship) -> MobileDiscoveryRelationship
    ) {
        recommendations = recommendations.map { result in
            result.id == profileID
                ? result.replacing(relationship: transform(result.relationship))
                : result
        }

        guard case .loaded(let current) = state else { return }
        state = .loaded(current.map { result in
            result.id == profileID
                ? result.replacing(relationship: transform(result.relationship))
                : result
        })
    }

    private func failure(for error: Error, title: String) -> UserFacingFailure {
        let apiError = error as? MobileAPIError
        diagnostics.record(
            category: .networking,
            summary: "A Legend discovery request could not be completed.",
            correlationID: apiError?.correlationID)
        return UserFacingFailure(
            title: title,
            message: error.localizedDescription,
            correlationID: apiError?.correlationID)
    }
}
