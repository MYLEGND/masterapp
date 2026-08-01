import Foundation

enum LegendBootstrapFeature: String, CaseIterable, Hashable {
    case home
    case social
    case profilePosts
    case account
    case messaging
    case journeyCircles
    case clients
    case leads
}

enum LegendApplicationBootstrapState: Equatable {
    case idle
    case loading
    case ready
    case partiallyReady([LegendBootstrapFeature: UserFacingFailure])
    case failed(UserFacingFailure)
}

@MainActor
final class LegendApplicationStores {
    let home: MobileHomeStore
    let financial: MobileFinancialStore
    let social: MobileSocialStore
    let journeyCircles: MobileJourneyCirclesStore
    let discovery: MobileDiscoveryStore
    let account: MobileAccountStore
    let messaging: MessagingStore
    let agentWorkspace: MobileAgentWorkspaceStore?

    init(
        currentSession: MobileSession,
        coordinator: MobileSessionCoordinator
    ) {
        home = coordinator.makeHomeStore()
        financial = coordinator.makeFinancialStore()
        let social = coordinator.makeSocialStore()
        let journeyCircles = coordinator.makeJourneyCirclesStore()
        self.social = social
        self.journeyCircles = journeyCircles
        discovery = coordinator.makeDiscoveryStore()
        account = coordinator.makeAccountStore()
        messaging = coordinator.makeMessagingStore()
        agentWorkspace = currentSession.actor.identity.participantType == .agent
            ? coordinator.makeAgentWorkspaceStore()
            : nil
    }

    init(
        home: MobileHomeStore,
        financial: MobileFinancialStore,
        social: MobileSocialStore,
        journeyCircles: MobileJourneyCirclesStore,
        discovery: MobileDiscoveryStore,
        account: MobileAccountStore,
        messaging: MessagingStore,
        agentWorkspace: MobileAgentWorkspaceStore?
    ) {
        self.home = home
        self.financial = financial
        self.social = social
        self.journeyCircles = journeyCircles
        self.discovery = discovery
        self.account = account
        self.messaging = messaging
        self.agentWorkspace = agentWorkspace
    }
}

@MainActor
final class LegendApplicationBootstrapCoordinator: ObservableObject {
    @Published private(set) var state: LegendApplicationBootstrapState = .idle
    @Published private(set) var isRefreshing = false

    let currentSession: MobileSession
    let stores: LegendApplicationStores

    private let authenticationFailureHandler: (UserFacingFailure) -> Void
    private var bootstrapTask: Task<Void, Never>?

    init(
        currentSession: MobileSession,
        coordinator: MobileSessionCoordinator
    ) {
        self.currentSession = currentSession
        self.stores = LegendApplicationStores(
            currentSession: currentSession,
            coordinator: coordinator)
        self.authenticationFailureHandler = coordinator.handleAuthenticationFailure
    }

    init(
        currentSession: MobileSession,
        stores: LegendApplicationStores,
        authenticationFailureHandler: @escaping (UserFacingFailure) -> Void
    ) {
        self.currentSession = currentSession
        self.stores = stores
        self.authenticationFailureHandler = authenticationFailureHandler
    }

    deinit {
        bootstrapTask?.cancel()
    }

    /// Opens the application shell.
    ///
    /// Startup performs no network work before returning. The only gate is the
    /// synchronous agent-workspace check, which decides whether this identity can
    /// have a shell at all. Everything else streams in afterwards through
    /// `loadStartupDataProgressively`, so the Home shell is interactive as soon as
    /// authentication succeeds rather than after seven or eight round trips.
    func bootstrapIfNeeded() async {
        switch state {
        case .ready, .partiallyReady:
            return
        case .loading:
            if let bootstrapTask {
                await bootstrapTask.value
            }
            return
        case .idle, .failed:
            break
        }

        if currentSession.actor.identity.participantType == .agent,
           stores.agentWorkspace == nil {
            state = .failed(agentWorkspaceUnavailableFailure)
            return
        }

        state = .ready

        let task = Task { [weak self] in
            guard let self else { return }
            await self.loadStartupDataProgressively()
        }
        bootstrapTask = task
    }

    /// Awaits the progressive startup passes. The shell does not need this; callers
    /// that must observe the settled result (such as tests and refresh-all) do.
    func awaitStartupCompletion() async {
        await bootstrapTask?.value
        bootstrapTask = nil
    }

    func retryBootstrap() async {
        bootstrapTask?.cancel()
        bootstrapTask = nil
        state = .idle
        await bootstrapIfNeeded()
    }

    func refreshApplication() async {
        guard !isRefreshing else { return }
        isRefreshing = true
        defer { isRefreshing = false }

        // A pull-to-refresh during startup would otherwise race the background pass.
        await awaitStartupCompletion()

        if currentSession.actor.identity.participantType == .agent {
            guard let agentWorkspace = stores.agentWorkspace else {
                state = .failed(agentWorkspaceUnavailableFailure)
                return
            }

            async let home = stores.home.refresh()
            async let social = stores.social.refresh()
            async let profilePosts = stores.social.refreshProfilePosts()
            async let account = stores.account.refresh()
            async let messaging = stores.messaging.refresh()
            async let clients = agentWorkspace.refreshClients()
            async let leads = agentWorkspace.refreshLeads()
            _ = await (
                home,
                social,
                profilePosts,
                account,
                messaging,
                clients,
                leads
            )
        } else {
            async let home = stores.home.refresh()
            async let social = stores.social.refresh()
            async let profilePosts = stores.social.refreshProfilePosts()
            async let account = stores.account.refresh()
            async let messaging = stores.messaging.refresh()
            async let journey = stores.journeyCircles.refresh()
            _ = await (home, social, profilePosts, account, messaging, journey)
        }
    }

    func refreshHome() async {
        async let home = stores.home.refresh()
        async let social = stores.social.refresh()
        _ = await (home, social)
    }

    func refreshSocial() async {
        _ = await stores.social.refresh()
    }

    func refreshProfile() async {
        async let account = stores.account.refresh()
        async let social = stores.social.refresh()
        async let profilePosts = stores.social.refreshProfilePosts()
        _ = await (account, social, profilePosts)
    }

    func refreshFinancial() async {
        _ = await stores.financial.refresh()
    }

    func loadFinancialIntelligenceIfNeeded() async {
        _ = await stores.financial.loadIfNeeded()
    }

    func refreshJourneyCircles() async {
        _ = await stores.journeyCircles.refresh()
    }

    func refreshMessaging() async {
        _ = await stores.messaging.refresh()
    }

    func refreshClients() async {
        guard let agentWorkspace = stores.agentWorkspace else {
            state = .failed(agentWorkspaceUnavailableFailure)
            return
        }
        _ = await agentWorkspace.refreshClients()
    }

    func refreshLeads() async {
        guard let agentWorkspace = stores.agentWorkspace else {
            state = .failed(agentWorkspaceUnavailableFailure)
            return
        }
        _ = await agentWorkspace.refreshLeads()
    }

    /// Two passes. The first loads only what the Home shell actually renders, so those
    /// requests are never queued behind data for tabs the user has not opened. The
    /// second fills in the remaining tabs in the background; each of those tabs also
    /// loads itself on first appearance, so nothing waits on this pass to be usable.
    private func loadStartupDataProgressively() async {
        let critical = await loadCriticalFeatures()
        if await escalateAuthenticationFailure(in: critical) { return }

        guard !Task.isCancelled else { return }

        let deferred = await loadDeferredFeatures()
        if await escalateAuthenticationFailure(in: deferred) { return }

        guard !Task.isCancelled else { return }

        applyRecoverableFailures(in: critical + deferred)
    }

    /// Home renders only its own projection and social feed. Financial Intelligence
    /// stays dormant until the user opens the hidden Profile drawer.
    private func loadCriticalFeatures() async -> [(LegendBootstrapFeature, MobileStoreLoadResult)] {
        async let home = stores.home.loadIfNeeded()
        async let social = stores.social.loadIfNeeded()
        let values = await (home, social)
        return [(.home, values.0), (.social, values.1)]
    }

    private func loadDeferredFeatures() async -> [(LegendBootstrapFeature, MobileStoreLoadResult)] {
        if currentSession.actor.identity.participantType == .agent {
            guard let agentWorkspace = stores.agentWorkspace else { return [] }

            async let profilePosts = stores.social.loadProfilePostsIfNeeded()
            async let account = stores.account.loadIfNeeded()
            async let messaging = stores.messaging.loadIfNeeded()
            async let clients = agentWorkspace.loadClientsIfNeeded()
            async let leads = agentWorkspace.loadLeadsIfNeeded()
            let values = await (profilePosts, account, messaging, clients, leads)
            return [
                (.profilePosts, values.0),
                (.account, values.1),
                (.messaging, values.2),
                (.clients, values.3),
                (.leads, values.4)
            ]
        }

        async let profilePosts = stores.social.loadProfilePostsIfNeeded()
        async let account = stores.account.loadIfNeeded()
        async let messaging = stores.messaging.loadIfNeeded()
        async let journey = stores.journeyCircles.loadIfNeeded()
        let values = await (profilePosts, account, messaging, journey)
        return [
            (.profilePosts, values.0),
            (.account, values.1),
            (.messaging, values.2),
            (.journeyCircles, values.3)
        ]
    }

    /// An expired session still has to return control to the session authority, even
    /// though the shell is already on screen.
    private func escalateAuthenticationFailure(
        in results: [(LegendBootstrapFeature, MobileStoreLoadResult)]
    ) async -> Bool {
        guard let failure = results.compactMap(authenticationFailure(from:)).first else {
            return false
        }

        state = .failed(failure)
        authenticationFailureHandler(failure)
        return true
    }

    private func applyRecoverableFailures(
        in results: [(LegendBootstrapFeature, MobileStoreLoadResult)]
    ) {
        let failures = Dictionary(
            uniqueKeysWithValues: results.compactMap(recoverableFailure(from:)))

        if failures.isEmpty {
            state = .ready
        } else if failures.count == results.count,
                  let firstFailure = failures.values.first {
            state = .failed(firstFailure)
        } else {
            state = .partiallyReady(failures)
        }
    }

    private func authenticationFailure(
        from result: (LegendBootstrapFeature, MobileStoreLoadResult)
    ) -> UserFacingFailure? {
        guard case .authenticationFailure(let failure) = result.1 else { return nil }
        return failure
    }

    private func recoverableFailure(
        from result: (LegendBootstrapFeature, MobileStoreLoadResult)
    ) -> (LegendBootstrapFeature, UserFacingFailure)? {
        guard case .failed(let failure) = result.1 else { return nil }
        return (result.0, failure)
    }

    private var agentWorkspaceUnavailableFailure: UserFacingFailure {
        UserFacingFailure(
            title: "Agent workspace unavailable",
            message: "The required client and lead workspace could not be initialized for this agent session.",
            correlationID: nil)
    }
}
