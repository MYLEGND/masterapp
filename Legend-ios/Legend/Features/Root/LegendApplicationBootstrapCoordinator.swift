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
    let notifications: MobileNotificationStore
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
        let messaging = coordinator.makeMessagingStore()
        let notifications = coordinator.makeNotificationStore()
        self.messaging = messaging
        self.notifications = notifications
        messaging.setNotificationBadgeUpdateHandler { [weak notifications] unreadCount, revision in
            notifications?.applyRealtime(unreadCount: unreadCount, revision: revision)
        }
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
        notifications: MobileNotificationStore,
        agentWorkspace: MobileAgentWorkspaceStore?
    ) {
        self.home = home
        self.financial = financial
        self.social = social
        self.journeyCircles = journeyCircles
        self.discovery = discovery
        self.account = account
        self.messaging = messaging
        self.notifications = notifications
        self.agentWorkspace = agentWorkspace
        messaging.setNotificationBadgeUpdateHandler { [weak notifications] unreadCount, revision in
            notifications?.applyRealtime(unreadCount: unreadCount, revision: revision)
        }
    }
}

@MainActor
final class LegendApplicationBootstrapCoordinator: ObservableObject {
    @Published private(set) var state: LegendApplicationBootstrapState = .idle
    @Published private(set) var isRefreshing = false

    let currentSession: MobileSession
    let stores: LegendApplicationStores
    /// One account-scoped activity authority shared by the Home summary and
    /// the Activity modal. It observes the protected stores; it never owns a
    /// second copy of social, messaging, or home data.
    let activity: LegendDailyActivityStore

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
        self.activity = LegendDailyActivityStore(
            identity: currentSession.actor.identity,
            home: stores.home,
            social: stores.social,
            messages: stores.messaging)
        self.authenticationFailureHandler = coordinator.handleAuthenticationFailure
    }

    init(
        currentSession: MobileSession,
        stores: LegendApplicationStores,
        authenticationFailureHandler: @escaping (UserFacingFailure) -> Void
    ) {
        self.currentSession = currentSession
        self.stores = stores
        self.activity = LegendDailyActivityStore(
            identity: currentSession.actor.identity,
            home: stores.home,
            social: stores.social,
            messages: stores.messaging)
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
            async let notifications = stores.notifications.sync()
            async let clients = agentWorkspace.refreshClients()
            async let leads = agentWorkspace.refreshLeads()
            _ = await (
                home,
                social,
                profilePosts,
                account,
                messaging,
                notifications,
                clients,
                leads
            )
        } else {
            async let home = stores.home.refresh()
            async let social = stores.social.refresh()
            async let profilePosts = stores.social.refreshProfilePosts()
            async let account = stores.account.refresh()
            async let messaging = stores.messaging.refresh()
            async let notifications = stores.notifications.sync()
            async let journey = stores.journeyCircles.refresh()
            _ = await (home, social, profilePosts, account, messaging, notifications, journey)
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

    /// Refreshes only the authoritative projections rendered by Profile.
    ///
    /// A page-level retry or pull-to-refresh must not trigger network work for
    /// Home, Messages, Discovery, Journey Circles, and the entire Social feed.
    func refreshProfile() async {
        async let account = stores.account.refresh()
        async let profilePosts = stores.social.refreshProfilePosts()
        _ = await (account, profilePosts)
    }

    /// Revalidates projections that can embed member identity after an actual
    /// profile mutation such as a name/contact/avatar change.
    ///
    /// This intentionally remains broader than `refreshProfile()`: identity
    /// changes can legitimately affect cached author/participant projections.
    /// It is mutation reconciliation, not ordinary page loading.
    func synchronizeProfileIdentity() async {
        async let home = stores.home.refresh()
        async let account = stores.account.refresh()
        async let social = stores.social.refresh()
        async let profilePosts = stores.social.refreshProfilePosts()
        async let messaging = stores.messaging.refresh()
        async let discovery = stores.discovery.refresh()

        if currentSession.actor.identity.participantType == .client {
            async let journeyCircles = stores.journeyCircles.refresh()
            _ = await (
                home,
                account,
                social,
                profilePosts,
                messaging,
                discovery,
                journeyCircles)
        } else {
            _ = await (
                home,
                account,
                social,
                profilePosts,
                messaging,
                discovery)
        }
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
        async let messaging = stores.messaging.refresh()
        async let notifications = stores.notifications.sync()
        _ = await (messaging, notifications)
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

    /// Home startup loads only the projection required to make Home current.
    ///
    /// Social owns its own cached presentation and first-appearance loading, so
    /// the comparatively expensive SocialFeedSnapshot must never sit on Home's
    /// critical startup path. Messages and notifications are warmed without
    /// becoming presentation gates.
    private func loadCriticalFeatures() async -> [(LegendBootstrapFeature, MobileStoreLoadResult)] {
        stores.messaging.load()

        Task { [notifications = stores.notifications] in
            _ = await notifications.sync()
        }

        let home = await stores.home.loadIfNeeded()
        return [(.home, home)]
    }

    private func loadDeferredFeatures() async -> [(LegendBootstrapFeature, MobileStoreLoadResult)] {
        if currentSession.actor.identity.participantType == .agent {
            guard let agentWorkspace = stores.agentWorkspace else { return [] }

            async let social = stores.social.loadIfNeeded()
            async let profilePosts = stores.social.loadProfilePostsIfNeeded()
            async let account = stores.account.loadIfNeeded()
            async let messaging = stores.messaging.loadIfNeeded()
            async let clients = agentWorkspace.loadClientsIfNeeded()
            async let leads = agentWorkspace.loadLeadsIfNeeded()
            let values = await (social, profilePosts, account, messaging, clients, leads)
            return [
                (.social, values.0),
                (.profilePosts, values.1),
                (.account, values.2),
                (.messaging, values.3),
                (.clients, values.4),
                (.leads, values.5)
            ]
        }

        async let social = stores.social.loadIfNeeded()
        async let profilePosts = stores.social.loadProfilePostsIfNeeded()
        async let account = stores.account.loadIfNeeded()
        async let messaging = stores.messaging.loadIfNeeded()
        async let journey = stores.journeyCircles.loadIfNeeded()
        let values = await (social, profilePosts, account, messaging, journey)
        return [
            (.social, values.0),
            (.profilePosts, values.1),
            (.account, values.2),
            (.messaging, values.3),
            (.journeyCircles, values.4)
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
