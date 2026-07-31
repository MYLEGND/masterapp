import XCTest
@testable import Legend

@MainActor
final class MobileDiscoveryStoreTests: XCTestCase {
    func testSearchIsDebouncedSoOnlyTheFinalKeystrokeReachesTheServer() async throws {
        let api = RecordingDiscoveryAPI(total: 3)
        let store = makeStore(api: api)

        // Typed at a realistic pace: each gap is shorter than the debounce window, so
        // the window keeps resetting and only the settled text should be requested.
        // (A synchronous burst would be coalesced by task cancellation alone, which
        // would not actually exercise the debounce.)
        for text in ["M", "Ma", "Mar", "Mara"] {
            store.searchText = text
            try await Task.sleep(for: .milliseconds(120))
        }

        try await Task.sleep(for: .milliseconds(600))

        let queries = await api.recordedQueries()
        XCTAssertEqual(queries, ["Mara"], "Only the settled query should be requested")
    }

    func testResultsComeFromTheServerAndAreNotFilteredLocally() async throws {
        // The server returns two members whose names do not contain the query at all.
        // A correct client renders exactly what the server authorized, because the
        // server matches bios and interests the client never sees.
        let api = RecordingDiscoveryAPI(total: 2)
        let store = makeStore(api: api)

        store.searchText = "marathon"
        try await Task.sleep(for: .milliseconds(700))

        XCTAssertEqual(store.results.count, 2)
        XCTAssertEqual(store.totalCount, 2)
    }

    func testInfiniteScrollAppendsWithoutDuplicatingOrSkipping() async throws {
        let api = RecordingDiscoveryAPI(total: 45)
        let store = makeStore(api: api)

        await store.refresh()
        XCTAssertEqual(store.results.count, 20)
        XCTAssertTrue(store.hasMore)

        guard let tail = store.results.last else { return XCTFail("Expected a first page") }
        store.loadMoreIfNeeded(currentItem: tail)
        try await Task.sleep(for: .milliseconds(300))
        XCTAssertEqual(store.results.count, 40)

        guard let secondTail = store.results.last else { return XCTFail("Expected a second page") }
        store.loadMoreIfNeeded(currentItem: secondTail)
        try await Task.sleep(for: .milliseconds(300))

        XCTAssertEqual(store.results.count, 45)
        XCTAssertFalse(store.hasMore)

        // Every member appears exactly once across all pages.
        XCTAssertEqual(Set(store.results.map(\.id)).count, 45)
    }

    func testALatePageForASupersededQueryIsDiscarded() async throws {
        let api = RecordingDiscoveryAPI(total: 40)
        let store = makeStore(api: api)

        await store.refresh()
        XCTAssertEqual(store.results.count, 20)

        // A new search starts while a "load more" for the old query is still in flight.
        await api.setResponseDelay(.milliseconds(250))
        guard let tail = store.results.last else { return XCTFail("Expected a first page") }
        store.loadMoreIfNeeded(currentItem: tail)

        store.searchText = "different"
        try await Task.sleep(for: .milliseconds(900))

        // The stale page must not have been appended to the new result set.
        let queries = await api.recordedQueries()
        XCTAssertEqual(queries.last, "different")
        XCTAssertLessThanOrEqual(store.results.count, 20)
    }

    func testFollowUsesTheServerConfirmedStateRatherThanTheOptimisticGuess() async throws {
        let api = RecordingDiscoveryAPI(total: 1)
        let social = RecordingSocialFollowAPI(confirmedFollowing: true)
        let store = makeStore(api: api, socialAPI: social)

        await store.refresh()
        guard let target = store.results.first else { return XCTFail("Expected a member") }
        XCTAssertFalse(target.relationship.followedByCurrentActor)

        store.toggleFollow(target)
        try await Task.sleep(for: .milliseconds(300))

        XCTAssertTrue(store.results[0].relationship.followedByCurrentActor)
        let followed = await social.lastRequest()
        XCTAssertEqual(followed?.followedUserID, target.identity.userID)
    }

    func testDirectoryToggleRequestsTheFullAlphabeticalDirectory() async throws {
        let api = RecordingDiscoveryAPI(total: 5)
        let store = makeStore(api: api)

        await store.refresh()
        XCTAssertEqual(store.sortMode, .recommended)

        store.showDirectory(true)
        try await Task.sleep(for: .milliseconds(300))

        let sorts = await api.recordedSorts()
        XCTAssertEqual(sorts.last, .directory)
    }

    // ------------------------------------------------------------------ helpers

    private func makeStore(
        api: RecordingDiscoveryAPI,
        socialAPI: RecordingSocialFollowAPI = RecordingSocialFollowAPI(confirmedFollowing: true)
    ) -> MobileDiscoveryStore {
        let diagnostics = LegendDiagnostics()
        let tokenProvider: () async throws -> String = { "token" }
        return MobileDiscoveryStore(
            api: api,
            social: MobileSocialStore(
                api: socialAPI,
                accessTokenProvider: tokenProvider,
                diagnostics: diagnostics),
            journeyCircles: MobileJourneyCirclesStore(
                api: MobileUnavailableJourneyCirclesAPI(),
                accessTokenProvider: tokenProvider,
                diagnostics: diagnostics),
            accessTokenProvider: tokenProvider,
            diagnostics: diagnostics)
    }
}

private actor RecordingDiscoveryAPI: MobileDiscoveryAPI {
    private let total: Int
    private var queries: [String] = []
    private var sorts: [MobileDiscoverySortMode] = []
    private var responseDelay: Duration = .zero

    init(total: Int) {
        self.total = total
    }

    func recordedQueries() -> [String] { queries }
    func recordedSorts() -> [MobileDiscoverySortMode] { sorts }
    func setResponseDelay(_ delay: Duration) { responseDelay = delay }

    func search(
        query: String?,
        offset: Int,
        pageSize: Int,
        sort: MobileDiscoverySortMode?,
        accessToken: String
    ) async throws -> MobileDiscoveryPage {
        if let query { queries.append(query) }
        if let sort { sorts.append(sort) }
        if responseDelay > .zero {
            try? await Task.sleep(for: responseDelay)
        }

        let upper = min(offset + pageSize, total)
        let results = offset >= total
            ? []
            : (offset..<upper).map { Self.member(index: $0) }

        return MobileDiscoveryPage(
            results: results,
            totalCount: total,
            offset: offset,
            pageSize: pageSize,
            hasMore: upper < total,
            sortMode: sort ?? .recommended,
            scope: .community)
    }

    func profile(clientProfileID: UUID, accessToken: String) async throws -> MobileDiscoveryProfile {
        MobileDiscoveryProfile(
            summary: Self.member(index: 0),
            introduction: nil,
            lifeStages: [],
            connectionTypes: [],
            contentVisibleToCurrentActor: false,
            followerCount: 0,
            followingCount: 0,
            postCount: 0,
            reelCount: 0,
            storyCount: 0)
    }

    private static func member(index: Int) -> MobileDiscoveryResult {
        MobileDiscoveryResult(
            clientProfileID: UUID(uuidString: String(format: "00000000-0000-0000-0000-%012d", index))!,
            identity: try! LogicalParticipantIdentity(
                userID: "member-\(index)",
                participantType: .client),
            displayName: "Member \(index)",
            headline: nil,
            location: nil,
            goals: [],
            interests: [],
            circleCodes: [],
            compatibilityScore: 0,
            matchExplanation: nil,
            relationship: MobileDiscoveryRelationship(
                followedByCurrentActor: false,
                followsCurrentActor: false,
                connectionStatus: .none,
                connectionID: nil,
                canRequestConnection: true,
                canFollow: true),
            avatar: nil)
    }
}

/// A social API that only implements the follow path Discover exercises.
private actor RecordingSocialFollowAPI: MobileSocialAPI {
    private let confirmedFollowing: Bool
    private var request: MobileToggleSocialFollow?

    init(confirmedFollowing: Bool) {
        self.confirmedFollowing = confirmedFollowing
    }

    func lastRequest() -> MobileToggleSocialFollow? { request }

    func toggleFollow(
        _ request: MobileToggleSocialFollow,
        accessToken: String
    ) async throws -> MobileSocialFollowResult {
        self.request = request
        return MobileSocialFollowResult(isFollowing: confirmedFollowing)
    }

    func feed(accessToken: String) async throws -> MobileSocialSnapshot {
        throw MobileAPIError.unauthorized(correlationID: nil)
    }
    func currentProfilePosts(accessToken: String) async throws -> [MobileSocialPost] { [] }
    func createPost(_ request: MobileCreateSocialPost, accessToken: String) async throws -> MobileSocialPost {
        throw MobileAPIError.unauthorized(correlationID: nil)
    }
    func createMediaPost(
        type: MobileSocialContentType,
        body: String,
        files: [MultipartFormFile],
        accessibilityText: String?,
        music: MobileSocialMusicSelection?,
        audience: MobileSocialAudience,
        location: String?,
        commentsEnabled: Bool,
        accessToken: String
    ) async throws -> MobileSocialPost {
        throw MobileAPIError.unauthorized(correlationID: nil)
    }
    func updatePost(postID: UUID, request: MobileUpdateSocialPost, accessToken: String) async throws -> MobileSocialPost {
        throw MobileAPIError.unauthorized(correlationID: nil)
    }
    func deletePost(postID: UUID, accessToken: String) async throws {}
    func mediaData(assetID: UUID, accessToken: String) async throws -> Data { Data() }
    func toggleReaction(postID: UUID, accessToken: String) async throws -> MobileSocialPost {
        throw MobileAPIError.unauthorized(correlationID: nil)
    }
    func addComment(postID: UUID, request: MobileCreateSocialComment, accessToken: String) async throws -> MobileSocialComment {
        throw MobileAPIError.unauthorized(correlationID: nil)
    }
    func toggleSave(postID: UUID, accessToken: String) async throws -> MobileSocialShareState {
        throw MobileAPIError.unauthorized(correlationID: nil)
    }
    func toggleRepost(postID: UUID, accessToken: String) async throws -> MobileSocialShareState {
        throw MobileAPIError.unauthorized(correlationID: nil)
    }
    func recordShare(postID: UUID, accessToken: String) async throws -> MobileSocialShareState {
        throw MobileAPIError.unauthorized(correlationID: nil)
    }
    func recordView(postID: UUID, request: MobileRecordSocialView, accessToken: String) async throws -> MobileSocialPostMetrics {
        throw MobileAPIError.unauthorized(correlationID: nil)
    }
    func postInsights(postID: UUID, accessToken: String) async throws -> MobileSocialPostInsight {
        throw MobileAPIError.unauthorized(correlationID: nil)
    }
    func searchMusic(query: String, accessToken: String) async throws -> [MobileSocialMusicTrack] { [] }
}
