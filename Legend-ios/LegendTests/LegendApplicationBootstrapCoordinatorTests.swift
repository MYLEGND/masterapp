import Foundation
import XCTest
@testable import Legend

@MainActor
final class LegendApplicationBootstrapCoordinatorTests: XCTestCase {
    func testClientBootstrapLoadsOnlyClientCoreData() async throws {
        let fixture = try BootstrapFixture()
        let services = BootstrapServices(fixture: fixture)
        let coordinator = makeCoordinator(
            participantType: .client,
            services: services,
            includesAgentWorkspace: true)

        await coordinator.bootstrapIfNeeded()
        await coordinator.awaitStartupCompletion()

        XCTAssertEqual(coordinator.state, .ready)
        let homeCalls = await services.home.calls()
        let socialCalls = await services.social.calls()
        let accountCalls = await services.account.calls()
        let messagingCalls = await services.messaging.calls()
        let journeyCalls = await services.journey.calls()
        let financialCalls = await services.financial.calls()
        let workspaceCalls = await services.workspace.calls()
        XCTAssertEqual(homeCalls, 1)
        XCTAssertEqual(socialCalls.feed, 1)
        XCTAssertEqual(socialCalls.profilePosts, 1)
        XCTAssertEqual(accountCalls, 1)
        XCTAssertEqual(messagingCalls, 1)
        XCTAssertEqual(journeyCalls, 1)
        XCTAssertEqual(financialCalls, 1)
        XCTAssertEqual(workspaceCalls.clients, 0)
        XCTAssertEqual(workspaceCalls.leads, 0)
    }

    /// The shell must be interactive as soon as authentication succeeds. Startup does
    /// no awaiting of network work, so `bootstrapIfNeeded` returns `.ready` before any
    /// feature request has completed.
    func testShellIsReadyBeforeAnyStartupRequestCompletes() async throws {
        let fixture = try BootstrapFixture()
        let services = BootstrapServices(fixture: fixture)
        // Every startup request hangs long enough that a blocking bootstrap could not
        // possibly have returned.
        await services.home.setResponseDelay(.milliseconds(400))
        let coordinator = makeCoordinator(
            participantType: .client,
            services: services,
            includesAgentWorkspace: false)

        await coordinator.bootstrapIfNeeded()

        XCTAssertEqual(coordinator.state, .ready, "The shell must open without waiting on startup data")
        let homeCalls = await services.home.calls()
        XCTAssertEqual(homeCalls, 0, "Startup must not block on a completed home request")

        await coordinator.awaitStartupCompletion()
        let settledHomeCalls = await services.home.calls()
        XCTAssertEqual(settledHomeCalls, 1, "Home still loads, just progressively")
    }

    /// Data for tabs the user has not opened must not be requested on the critical
    /// path ahead of what Home renders.
    func testDeferredTabDataIsNotRequestedBeforeHomeData() async throws {
        let fixture = try BootstrapFixture()
        let services = BootstrapServices(fixture: fixture)
        await services.home.setResponseDelay(.milliseconds(250))
        let coordinator = makeCoordinator(
            participantType: .client,
            services: services,
            includesAgentWorkspace: false)

        await coordinator.bootstrapIfNeeded()
        try await Task.sleep(for: .milliseconds(80))

        // Home is in flight; messaging and Journey Circles belong to the second pass.
        let messagingCalls = await services.messaging.calls()
        let journeyCalls = await services.journey.calls()
        XCTAssertEqual(messagingCalls, 0)
        XCTAssertEqual(journeyCalls, 0)

        await coordinator.awaitStartupCompletion()
        let settledMessaging = await services.messaging.calls()
        XCTAssertEqual(settledMessaging, 1)
    }

    func testAgentBootstrapLoadsAgentCoreDataAndFinancialProjection() async throws {
        let fixture = try BootstrapFixture()
        let services = BootstrapServices(fixture: fixture)
        let coordinator = makeCoordinator(
            participantType: .agent,
            services: services,
            includesAgentWorkspace: true)

        await coordinator.bootstrapIfNeeded()
        await coordinator.awaitStartupCompletion()

        XCTAssertEqual(coordinator.state, .ready)
        let homeCalls = await services.home.calls()
        let socialCalls = await services.social.calls()
        let accountCalls = await services.account.calls()
        let messagingCalls = await services.messaging.calls()
        let workspaceCalls = await services.workspace.calls()
        let journeyCalls = await services.journey.calls()
        let financialCalls = await services.financial.calls()
        XCTAssertEqual(homeCalls, 1)
        XCTAssertEqual(socialCalls.feed, 1)
        XCTAssertEqual(socialCalls.profilePosts, 1)
        XCTAssertEqual(accountCalls, 1)
        XCTAssertEqual(messagingCalls, 1)
        XCTAssertEqual(workspaceCalls.clients, 1)
        XCTAssertEqual(workspaceCalls.leads, 1)
        XCTAssertEqual(journeyCalls, 0)
        XCTAssertEqual(financialCalls, 1)
    }

    func testAgentBootstrapFailsClearlyWithoutItsRequiredWorkspace() async throws {
        let fixture = try BootstrapFixture()
        let services = BootstrapServices(fixture: fixture)
        let coordinator = makeCoordinator(
            participantType: .agent,
            services: services,
            includesAgentWorkspace: false)

        await coordinator.bootstrapIfNeeded()
        await coordinator.awaitStartupCompletion()

        guard case .failed(let failure) = coordinator.state else {
            return XCTFail("Expected agent bootstrap to require its workspace")
        }
        XCTAssertEqual(failure.title, "Agent workspace unavailable")
        let homeCalls = await services.home.calls()
        XCTAssertEqual(homeCalls, 0)
    }

    func testBootstrapCoalescesConcurrentRequests() async throws {
        let fixture = try BootstrapFixture()
        let services = BootstrapServices(fixture: fixture)
        let coordinator = makeCoordinator(
            participantType: .client,
            services: services,
            includesAgentWorkspace: false)

        async let first: Void = coordinator.bootstrapIfNeeded()
        async let second: Void = coordinator.bootstrapIfNeeded()
        _ = await (first, second)
        await coordinator.awaitStartupCompletion()

        XCTAssertEqual(coordinator.state, .ready)
        let homeCalls = await services.home.calls()
        let socialCalls = await services.social.calls()
        let accountCalls = await services.account.calls()
        let messagingCalls = await services.messaging.calls()
        let journeyCalls = await services.journey.calls()
        let financialCalls = await services.financial.calls()
        XCTAssertEqual(homeCalls, 1)
        XCTAssertEqual(socialCalls.feed, 1)
        XCTAssertEqual(socialCalls.profilePosts, 1)
        XCTAssertEqual(accountCalls, 1)
        XCTAssertEqual(messagingCalls, 1)
        XCTAssertEqual(journeyCalls, 1)
        XCTAssertEqual(financialCalls, 1)
    }

    func testRecoverableFeatureFailureOpensAPartiallyReadyShell() async throws {
        let fixture = try BootstrapFixture()
        let services = BootstrapServices(fixture: fixture)
        await services.financial.setNetworkFailure(true)
        let coordinator = makeCoordinator(
            participantType: .client,
            services: services,
            includesAgentWorkspace: false)

        await coordinator.bootstrapIfNeeded()
        await coordinator.awaitStartupCompletion()

        guard case .partiallyReady(let failures) = coordinator.state else {
            return XCTFail("Expected a partially ready application shell")
        }
        XCTAssertEqual(Set(failures.keys), [.financial])
    }

    func testAuthenticationFailureReturnsControlToSessionAuthority() async throws {
        let fixture = try BootstrapFixture()
        let services = BootstrapServices(fixture: fixture)
        await services.home.setAuthenticationFailure(true)
        var handledFailure: UserFacingFailure?
        let coordinator = makeCoordinator(
            participantType: .client,
            services: services,
            includesAgentWorkspace: false,
            authenticationFailureHandler: { handledFailure = $0 })

        await coordinator.bootstrapIfNeeded()
        await coordinator.awaitStartupCompletion()

        guard case .failed(let failure) = coordinator.state else {
            return XCTFail("Expected authentication to leave application bootstrap")
        }
        XCTAssertEqual(failure.title, "Home unavailable")
        XCTAssertEqual(handledFailure, failure)
    }

    func testRefreshFailurePreservesPreviouslyLoadedHomeData() async throws {
        let fixture = try BootstrapFixture()
        let home = HomeBootstrapAPI(response: fixture.home)
        let store = MobileHomeStore(
            api: home,
            accessTokenProvider: { "token" },
            diagnostics: LegendDiagnostics())

        let initialResult = await store.loadIfNeeded()
        XCTAssertEqual(initialResult, .loaded)
        guard case .loaded(let cachedHome) = store.state else {
            return XCTFail("Expected the home fixture to be cached")
        }

        await home.setNetworkFailure(true)
        let result = await store.refresh()

        guard case .failed = result else {
            return XCTFail("Expected refresh failure to remain visible")
        }
        XCTAssertEqual(store.state, .loaded(cachedHome))
        XCTAssertEqual(store.refreshFailure?.title, "Home unavailable")
        XCTAssertFalse(store.isRefreshing)
    }

    func testInitialFailureWithoutCachedDataPublishesAnUnavailableState() async throws {
        let fixture = try BootstrapFixture()
        let home = HomeBootstrapAPI(response: fixture.home)
        let store = MobileHomeStore(
            api: home,
            accessTokenProvider: { "token" },
            diagnostics: LegendDiagnostics())
        await home.setNetworkFailure(true)

        let result = await store.loadIfNeeded()

        guard case .failed(let failure) = result else {
            return XCTFail("Expected an initial network failure")
        }
        guard case .unavailable(let stateFailure) = store.state else {
            return XCTFail("Expected an unavailable initial state")
        }
        XCTAssertEqual(stateFailure, failure)
    }

    func testRetryBootstrapRecoversOnlyTheFailedFeature() async throws {
        let fixture = try BootstrapFixture()
        let services = BootstrapServices(fixture: fixture)
        await services.financial.setNetworkFailure(true)
        let coordinator = makeCoordinator(
            participantType: .client,
            services: services,
            includesAgentWorkspace: false)

        await coordinator.bootstrapIfNeeded()
        await coordinator.awaitStartupCompletion()
        await services.financial.setNetworkFailure(false)
        await coordinator.retryBootstrap()
        await coordinator.awaitStartupCompletion()

        XCTAssertEqual(coordinator.state, .ready)
        let homeCalls = await services.home.calls()
        let financialCalls = await services.financial.calls()
        XCTAssertEqual(homeCalls, 1)
        XCTAssertEqual(financialCalls, 2)
    }

    func testProfileRefreshCoordinatesAccountFeedAndProfilePostsOnce() async throws {
        let fixture = try BootstrapFixture()
        let services = BootstrapServices(fixture: fixture)
        let coordinator = makeCoordinator(
            participantType: .client,
            services: services,
            includesAgentWorkspace: false)

        await coordinator.refreshProfile()

        let accountCalls = await services.account.calls()
        let socialCalls = await services.social.calls()
        XCTAssertEqual(accountCalls, 1)
        XCTAssertEqual(socialCalls.feed, 1)
        XCTAssertEqual(socialCalls.profilePosts, 1)
    }

    func testAgentRefreshApplicationIncludesFinancialProjection() async throws {
        let fixture = try BootstrapFixture()
        let services = BootstrapServices(fixture: fixture)
        let coordinator = makeCoordinator(
            participantType: .agent,
            services: services,
            includesAgentWorkspace: true)

        await coordinator.refreshApplication()

        let workspaceCalls = await services.workspace.calls()
        let journeyCalls = await services.journey.calls()
        let financialCalls = await services.financial.calls()
        XCTAssertEqual(workspaceCalls.clients, 1)
        XCTAssertEqual(workspaceCalls.leads, 1)
        XCTAssertEqual(journeyCalls, 0)
        XCTAssertEqual(financialCalls, 1)
    }

    func testRefreshApplicationCoalescesConcurrentRequests() async throws {
        let fixture = try BootstrapFixture()
        let services = BootstrapServices(fixture: fixture)
        let coordinator = makeCoordinator(
            participantType: .client,
            services: services,
            includesAgentWorkspace: false)

        async let first: Void = coordinator.refreshApplication()
        async let second: Void = coordinator.refreshApplication()
        _ = await (first, second)

        let homeCalls = await services.home.calls()
        let socialCalls = await services.social.calls()
        let accountCalls = await services.account.calls()
        let messagingCalls = await services.messaging.calls()
        let journeyCalls = await services.journey.calls()
        let financialCalls = await services.financial.calls()
        XCTAssertEqual(homeCalls, 1)
        XCTAssertEqual(socialCalls.feed, 1)
        XCTAssertEqual(socialCalls.profilePosts, 1)
        XCTAssertEqual(accountCalls, 1)
        XCTAssertEqual(messagingCalls, 1)
        XCTAssertEqual(journeyCalls, 1)
        XCTAssertEqual(financialCalls, 1)
    }

    private func makeCoordinator(
        participantType: ParticipantType,
        services: BootstrapServices,
        includesAgentWorkspace: Bool,
        authenticationFailureHandler: @escaping (UserFacingFailure) -> Void = { _ in }
    ) -> LegendApplicationBootstrapCoordinator {
        let tokenProvider: () async throws -> String = { "token" }
        let diagnostics = LegendDiagnostics()
        // Discover composes these two, so they are built once and shared.
        let socialStore = MobileSocialStore(
            api: services.social,
            accessTokenProvider: tokenProvider,
            diagnostics: diagnostics)
        let journeyStore = MobileJourneyCirclesStore(
            api: services.journey,
            accessTokenProvider: tokenProvider,
            diagnostics: diagnostics)
        let stores = LegendApplicationStores(
            home: MobileHomeStore(
                api: services.home,
                accessTokenProvider: tokenProvider,
                diagnostics: diagnostics),
            financial: MobileFinancialStore(
                api: services.financial,
                accessTokenProvider: tokenProvider,
                diagnostics: diagnostics),
            social: socialStore,
            journeyCircles: journeyStore,
            discovery: MobileDiscoveryStore(
                api: MobileUnavailableDiscoveryAPI(),
                social: socialStore,
                journeyCircles: journeyStore,
                accessTokenProvider: tokenProvider,
                diagnostics: diagnostics),
            account: MobileAccountStore(
                api: services.account,
                accessTokenProvider: tokenProvider,
                diagnostics: diagnostics),
            messaging: MessagingStore(
                api: services.messaging,
                accessTokenProvider: tokenProvider,
                diagnostics: diagnostics,
                actorParticipantType: participantType),
            agentWorkspace: includesAgentWorkspace
                ? MobileAgentWorkspaceStore(
                    api: services.workspace,
                    accessTokenProvider: tokenProvider,
                    diagnostics: diagnostics)
                : nil)

        return LegendApplicationBootstrapCoordinator(
            currentSession: makeSession(participantType: participantType),
            stores: stores,
            authenticationFailureHandler: authenticationFailureHandler)
    }

    private func makeSession(participantType: ParticipantType) -> MobileSession {
        let identity = try! LogicalParticipantIdentity(
            userID: "bootstrap-user",
            participantType: participantType)
        let actor = try! MobileActor(
            identity: identity,
            profileID: UUID().uuidString,
            displayName: "Bootstrap User",
            avatar: nil)
        return MobileSession(actor: actor, capabilities: ["messaging"])
    }
}

private struct BootstrapFixture {
    let home: MobileHomeResponse
    let financial: MobileFinancialSnapshotResponse
    let social: MobileSocialSnapshot
    let account: MobileAccountProfile
    let journey: MobileJourneyDashboardResponse

    init() throws {
        let identity = try LogicalParticipantIdentity(
            userID: "bootstrap-user",
            participantType: .client)
        let profileID = UUID()
        let author = MobileSocialAuthor(
            identity: identity,
            profileID: profileID.uuidString,
            displayName: "Bootstrap User",
            avatar: nil)
        home = MobileHomeResponse(
            identity: MobileHomeIdentity(
                userID: identity.userID,
                participantType: .client,
                profileID: profileID,
                displayName: "Bootstrap User"),
            messaging: MobileMessagingSummary(unreadCount: 0, conversationCount: 0),
            subscription: nil,
            entitlement: nil,
            journey: nil,
            financial: nil,
            upcomingAppointments: [],
            actions: [],
            notifications: [],
            dailyScripture: MobileDailyScripture(
                date: "2026-07-29",
                reference: "Psalm 1:1",
                translation: "KJV",
                verses: ["Psalm 1:1"],
                text: "Blessed is the man that walketh not in the counsel of the ungodly."),
            activeClientCount: 0)
        financial = MobileFinancialSnapshotResponse(
            position: nil,
            intelligence: nil,
            upcomingBills: [],
            operatingSystem: MobileFinancialOperatingSystemSnapshotResponse(
                projection: MobileFinancialProjectionStatusResponse(
                    status: "Unavailable",
                    reasonCode: "EXPENSE_LENS_STATE_NOT_FOUND",
                    summary: "Save Expense Lens to begin."),
                freshness: MobileFinancialDataFreshnessResponse(
                    financeStateUpdatedUTC: nil,
                    intelligenceEvaluatedUTC: nil,
                    generatedUTC: .now),
                weekAtGlance: nil,
                monthAtGlance: nil,
                tools: []))
        social = MobileSocialSnapshot(
            stories: [],
            posts: [],
            activity: [],
            activityCount: 0,
            currentProfileMetrics: MobileSocialProfileMetrics(
                profile: author,
                postCount: 0,
                videoCount: 0,
                storyCount: 0,
                followerCount: 0,
                followingCount: 0,
                totalReactionCount: 0,
                totalContentViewCount: 0,
                totalReachCount: 0,
                privateProfileVisitCount: nil),
            creatorInsights: MobileSocialCreatorInsights(
                generatedUTC: .now,
                totalViews: 0,
                totalReach: 0,
                followerCount: 0,
                followingCount: 0,
                followersGained: 0,
                profileVisits: 0,
                totalReactions: 0,
                totalComments: 0,
                totalReplies: 0,
                totalShares: 0,
                totalReposts: 0,
                totalSaves: 0,
                engagementRatePercentage: 0,
                topPosts: [],
                topVideos: [],
                topStories: []))
        account = MobileAccountProfile(
            participantType: .client,
            profileID: profileID,
            displayName: "Bootstrap User",
            email: "bootstrap@example.test",
            phone: nil,
            title: nil,
            shortBio: nil,
            avatar: nil)
        journey = MobileJourneyDashboardResponse(
            profile: nil,
            preferences: nil,
            recommendations: [],
            connections: [],
            requests: [],
            taxonomy: MobileJourneyTaxonomy(
                goals: [],
                circles: [],
                lifeStages: [],
                locations: [],
                interests: [],
                connectionTypes: [],
                communicationStyles: [],
                accountabilityFrequencies: []))
    }
}

private final class BootstrapServices {
    let home: HomeBootstrapAPI
    let financial: FinancialBootstrapAPI
    let social: SocialBootstrapAPI
    let journey: JourneyBootstrapAPI
    let account: AccountBootstrapAPI
    let messaging = MessagingBootstrapAPI()
    let workspace = AgentWorkspaceBootstrapAPI()

    init(fixture: BootstrapFixture) {
        home = HomeBootstrapAPI(response: fixture.home)
        financial = FinancialBootstrapAPI(response: fixture.financial)
        social = SocialBootstrapAPI(snapshot: fixture.social)
        journey = JourneyBootstrapAPI(dashboard: fixture.journey)
        account = AccountBootstrapAPI(profile: fixture.account)
    }
}

private actor HomeBootstrapAPI: MobileHomeAPI {
    let response: MobileHomeResponse
    /// Counts *completed* calls, so a test can distinguish "in flight" from "done".
    var callCount = 0
    var failWithNetworkError = false
    var failWithAuthenticationError = false
    private var responseDelay: Duration = .zero

    init(response: MobileHomeResponse) {
        self.response = response
    }

    func setResponseDelay(_ delay: Duration) {
        responseDelay = delay
    }

    func home(accessToken: String) async throws -> MobileHomeResponse {
        if responseDelay > .zero {
            try? await Task.sleep(for: responseDelay)
        }
        callCount += 1
        if failWithAuthenticationError {
            throw MobileAPIError.apiUnauthorized(
                code: "mobile_authentication_required",
                correlationID: "bootstrap-auth")
        }
        if failWithNetworkError {
            throw MobileAPIError.networkUnavailable
        }
        return response
    }

    func calls() -> Int { callCount }
    func setNetworkFailure(_ enabled: Bool) { failWithNetworkError = enabled }
    func setAuthenticationFailure(_ enabled: Bool) { failWithAuthenticationError = enabled }
}

private actor FinancialBootstrapAPI: MobileFinancialAPI {
    let response: MobileFinancialSnapshotResponse
    var callCount = 0
    var failWithNetworkError = false

    init(response: MobileFinancialSnapshotResponse) {
        self.response = response
    }

    func financial(accessToken: String) async throws -> MobileFinancialSnapshotResponse {
        callCount += 1
        if failWithNetworkError {
            throw MobileAPIError.networkUnavailable
        }
        return response
    }

    func calls() -> Int { callCount }
    func setNetworkFailure(_ enabled: Bool) { failWithNetworkError = enabled }
}

private actor JourneyBootstrapAPI: MobileJourneyCirclesAPI {
    let dashboardResponse: MobileJourneyDashboardResponse
    var dashboardCallCount = 0

    init(dashboard: MobileJourneyDashboardResponse) {
        dashboardResponse = dashboard
    }

    func dashboard(accessToken: String) async throws -> MobileJourneyDashboardResponse {
        dashboardCallCount += 1
        return dashboardResponse
    }

    func calls() -> Int { dashboardCallCount }

    func saveProfile(_ profile: MobileJourneyProfileInput, accessToken: String) async throws {}
    func requestConnection(_ request: MobileJourneyConnectionRequestBody, accessToken: String) async throws {}
    func respondToConnection(id: UUID, accept: Bool, accessToken: String) async throws {}
}

private actor AccountBootstrapAPI: MobileAccountAPI {
    let profileResponse: MobileAccountProfile
    var callCount = 0

    init(profile: MobileAccountProfile) {
        profileResponse = profile
    }

    func profile(accessToken: String) async throws -> MobileAccountProfile {
        callCount += 1
        return profileResponse
    }

    func calls() -> Int { callCount }

    func update(_ update: MobileAccountUpdate, accessToken: String) async throws {}

    func usernameAvailability(username: String, accessToken: String) async throws -> MobileUsernameAvailability {
        MobileUsernameAvailability(isAvailable: true, message: nil)
    }
}

private actor AgentWorkspaceBootstrapAPI: MobileAgentWorkspaceAPI {
    var clientCallCount = 0
    var leadCallCount = 0

    func clients(accessToken: String) async throws -> [MobileAgentClientSummary] {
        clientCallCount += 1
        return []
    }

    func leads(accessToken: String) async throws -> [MobileAgentLeadSummary] {
        leadCallCount += 1
        return []
    }

    func calls() -> (clients: Int, leads: Int) {
        (clientCallCount, leadCallCount)
    }
}

private actor MessagingBootstrapAPI: MessagingAPI {
    var conversationCallCount = 0

    func conversations(accessToken: String) async throws -> [ConversationSummary] {
        conversationCallCount += 1
        return []
    }

    func calls() -> Int { conversationCallCount }

    func recipients(search: String?, scope: MessagingRecipientScope?, accessToken: String) async throws -> [MessagingRecipient] { [] }
    func start(recipient: MessagingRecipient, accessToken: String) async throws -> ConversationDetail { fatalError("Not used by bootstrap") }
    func conversation(id: UUID, accessToken: String) async throws -> ConversationDetail { fatalError("Not used by bootstrap") }
    func messages(conversationID: UUID, accessToken: String) async throws -> [ConversationMessage] { [] }
    func send(conversationID: UUID, body: String, replyToMessageID: UUID?, accessToken: String) async throws -> ConversationMessage { fatalError("Not used by bootstrap") }
    func upload(conversationID: UUID, messageID: UUID, attachment: MessagingAttachmentDraft, accessToken: String) async throws -> MessagingAttachment { fatalError("Not used by bootstrap") }
    func markRead(conversationID: UUID, accessToken: String) async throws {}
}

private actor SocialBootstrapAPI: MobileSocialAPI {
    let snapshot: MobileSocialSnapshot
    var feedCallCount = 0
    var profilePostsCallCount = 0

    init(snapshot: MobileSocialSnapshot) {
        self.snapshot = snapshot
    }

    func feed(accessToken: String) async throws -> MobileSocialSnapshot {
        feedCallCount += 1
        return snapshot
    }

    func currentProfilePosts(accessToken: String) async throws -> [MobileSocialPost] {
        profilePostsCallCount += 1
        return []
    }

    func calls() -> (feed: Int, profilePosts: Int) {
        (feedCallCount, profilePostsCallCount)
    }

    func createPost(_ request: MobileCreateSocialPost, accessToken: String) async throws -> MobileSocialPost { fatalError("Not used by bootstrap") }
    func createMediaPost(type: MobileSocialContentType, body: String, files: [MultipartFormFile], accessibilityText: String?, music: MobileSocialMusicSelection?, audience: MobileSocialAudience, location: String?, commentsEnabled: Bool, accessToken: String) async throws -> MobileSocialPost { fatalError("Not used by bootstrap") }
    func updatePost(postID: UUID, request: MobileUpdateSocialPost, accessToken: String) async throws -> MobileSocialPost { fatalError("Not used by bootstrap") }
    func deletePost(postID: UUID, accessToken: String) async throws {}
    func mediaData(assetID: UUID, accessToken: String) async throws -> Data { Data() }
    func toggleReaction(postID: UUID, accessToken: String) async throws -> MobileSocialPost { fatalError("Not used by bootstrap") }
    func addComment(postID: UUID, request: MobileCreateSocialComment, accessToken: String) async throws -> MobileSocialComment { fatalError("Not used by bootstrap") }
    func toggleFollow(_ request: MobileToggleSocialFollow, accessToken: String) async throws -> MobileSocialFollowResult { fatalError("Not used by bootstrap") }
    func toggleSave(postID: UUID, accessToken: String) async throws -> MobileSocialShareState { fatalError("Not used by bootstrap") }
    func toggleRepost(postID: UUID, accessToken: String) async throws -> MobileSocialShareState { fatalError("Not used by bootstrap") }
    func recordShare(postID: UUID, accessToken: String) async throws -> MobileSocialShareState { fatalError("Not used by bootstrap") }
    func recordView(postID: UUID, request: MobileRecordSocialView, accessToken: String) async throws -> MobileSocialPostMetrics { fatalError("Not used by bootstrap") }
    func postInsights(postID: UUID, accessToken: String) async throws -> MobileSocialPostInsight { fatalError("Not used by bootstrap") }
    func searchMusic(query: String, accessToken: String) async throws -> [MobileSocialMusicTrack] { [] }
}
