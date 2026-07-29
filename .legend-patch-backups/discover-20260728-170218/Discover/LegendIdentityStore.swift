import Foundation

@MainActor
final class LegendIdentityStore: ObservableObject {
    @Published private(set) var discoverState:
        MobileDataLoadState<LegendDiscoverSnapshot> = .idle

    @Published private(set) var searchState:
        MobileDataLoadState<[LegendIdentitySearchResult]> = .idle

    @Published private(set) var selectedProfileState:
        MobileDataLoadState<LegendIdentity> = .idle

    @Published private(set) var isRefreshing = false
    @Published private(set) var refreshFailure: UserFacingFailure?

    private let api: any LegendIdentityAPI
    private let accessTokenProvider: () async throws -> String
    private let diagnostics: LegendDiagnostics

    private var discoverTask: Task<MobileStoreLoadResult, Never>?
    private var searchTask: Task<Void, Never>?
    private var profileTask: Task<Void, Never>?

    init(
        api: any LegendIdentityAPI,
        accessTokenProvider: @escaping () async throws -> String,
        diagnostics: LegendDiagnostics
    ) {
        self.api = api
        self.accessTokenProvider = accessTokenProvider
        self.diagnostics = diagnostics
    }

    func loadIfNeeded() async -> MobileStoreLoadResult {
        if case .loaded = discoverState {
            return .loaded
        }

        return await requestDiscover(
            preservingCachedValue: false
        )
    }

    func refresh() async -> MobileStoreLoadResult {
        let hasCachedValue: Bool

        if case .loaded = discoverState {
            hasCachedValue = true
        } else {
            hasCachedValue = false
        }

        return await requestDiscover(
            preservingCachedValue: hasCachedValue
        )
    }

    func search(_ query: String) {
        let normalizedQuery = query.trimmingCharacters(
            in: .whitespacesAndNewlines
        )

        searchTask?.cancel()

        guard !normalizedQuery.isEmpty else {
            searchState = .idle
            searchTask = nil
            return
        }

        searchState = .loading

        searchTask = Task { [weak self] in
            guard let self else {
                return
            }

            do {
                let token = try await accessTokenProvider()

                guard !Task.isCancelled else {
                    return
                }

                let results = try await api.search(
                    query: normalizedQuery,
                    accessToken: token
                )

                guard !Task.isCancelled else {
                    return
                }

                searchState = .loaded(results)
            } catch is CancellationError {
                return
            } catch {
                guard !Task.isCancelled else {
                    return
                }

                searchState = .unavailable(
                    failure(
                        for: error,
                        title: "Search unavailable"
                    )
                )
            }
        }
    }

    func clearSearch() {
        searchTask?.cancel()
        searchTask = nil
        searchState = .idle
    }

    func loadProfile(username: String) {
        let normalizedUsername = username.trimmingCharacters(
            in: .whitespacesAndNewlines
        )

        profileTask?.cancel()

        guard !normalizedUsername.isEmpty else {
            selectedProfileState = .idle
            profileTask = nil
            return
        }

        selectedProfileState = .loading

        profileTask = Task { [weak self] in
            guard let self else {
                return
            }

            do {
                let token = try await accessTokenProvider()

                guard !Task.isCancelled else {
                    return
                }

                let profile = try await api.profile(
                    username: normalizedUsername,
                    accessToken: token
                )

                guard !Task.isCancelled else {
                    return
                }

                selectedProfileState = .loaded(profile)
            } catch is CancellationError {
                return
            } catch {
                guard !Task.isCancelled else {
                    return
                }

                selectedProfileState = .unavailable(
                    failure(
                        for: error,
                        title: "Profile unavailable"
                    )
                )
            }
        }
    }

    func clearSelectedProfile() {
        profileTask?.cancel()
        profileTask = nil
        selectedProfileState = .idle
    }

    private func requestDiscover(
        preservingCachedValue: Bool
    ) async -> MobileStoreLoadResult {
        if let discoverTask {
            return await discoverTask.value
        }

        if preservingCachedValue {
            isRefreshing = true
            refreshFailure = nil
        } else {
            discoverState = .loading
        }

        let task = Task { [weak self] in
            guard let self else {
                return MobileStoreLoadResult.failed(
                    UserFacingFailure(
                        title: "Discover unavailable",
                        message: "The Legend identity store is no longer available.",
                        correlationID: nil
                    )
                )
            }

            return await executeDiscoverRequest(
                preservingCachedValue: preservingCachedValue
            )
        }

        discoverTask = task
        let result = await task.value
        discoverTask = nil

        return result
    }

    private func executeDiscoverRequest(
        preservingCachedValue: Bool
    ) async -> MobileStoreLoadResult {
        defer {
            isRefreshing = false
        }

        do {
            let token = try await accessTokenProvider()
            let snapshot = try await api.discover(
                accessToken: token
            )

            discoverState = .loaded(snapshot)
            refreshFailure = nil

            return .loaded
        } catch {
            let presentation = failure(
                for: error,
                title: "Discover unavailable"
            )

            if preservingCachedValue {
                refreshFailure = presentation
            } else {
                discoverState = .unavailable(presentation)
            }

            return mobileLoadResult(
                for: error,
                failure: presentation
            )
        }
    }

    private func failure(
        for error: Error,
        title: String
    ) -> UserFacingFailure {
        let apiError = error as? MobileAPIError

        diagnostics.record(
            category: .networking,
            summary: "A Legend identity request could not be completed.",
            correlationID: apiError?.correlationID
        )

        return UserFacingFailure(
            title: title,
            message: error.localizedDescription,
            correlationID: apiError?.correlationID
        )
    }
}
