import Foundation

enum LegendBootstrapFeature: String, CaseIterable, Hashable {
    case home
    case social
    case profilePosts
    case account
    case messaging
    case journeyCircles
    case financial
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
    let account: MobileAccountStore
    let messaging: MessagingStore
    let agentWorkspace: MobileAgentWorkspaceStore?

    init(
        currentSession: MobileSession,
        coordinator: MobileSessionCoordinator
    ) {
        home = coordinator.makeHomeStore()
        financial = coordinator.makeFinancialStore()
        social = coordinator.makeSocialStore()
        journeyCircles = coordinator.makeJourneyCirclesStore()
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
        account: MobileAccountStore,
        messaging: MessagingStore,
        agentWorkspace: MobileAgentWorkspaceStore?
    ) {
        self.home = home
        self.financial = financial
        self.social = social
        self.journeyCircles = journeyCircles
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

        state = .loading
        let task = Task { [weak self] in
            guard let self else { return }
            await self.loadRequiredStartupData()
        }
        bootstrapTask = task
        await task.value
        bootstrapTask = nil
    }

    func retryBootstrap() async {
        state = .idle
        await bootstrapIfNeeded()
    }

    func refreshApplication() async {
        guard !isRefreshing else { return }
        isRefreshing = true
        defer { isRefreshing = false }

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
            async let financial = stores.financial.refresh()
            _ = await (
                home,
                social,
                profilePosts,
                account,
                messaging,
                clients,
                leads,
                financial
            )
        } else {
            async let home = stores.home.refresh()
            async let social = stores.social.refresh()
            async let profilePosts = stores.social.refreshProfilePosts()
            async let account = stores.account.refresh()
            async let messaging = stores.messaging.refresh()
            async let journey = stores.journeyCircles.refresh()
            async let financial = stores.financial.refresh()
            _ = await (home, social, profilePosts, account, messaging, journey, financial)
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

    private func loadRequiredStartupData() async {
        let results: [(LegendBootstrapFeature, MobileStoreLoadResult)]

        if currentSession.actor.identity.participantType == .agent {
            guard let agentWorkspace = stores.agentWorkspace else {
                state = .failed(agentWorkspaceUnavailableFailure)
                return
            }

            async let home = stores.home.loadIfNeeded()
            async let social = stores.social.loadIfNeeded()
            async let profilePosts = stores.social.loadProfilePostsIfNeeded()
            async let account = stores.account.loadIfNeeded()
            async let messaging = stores.messaging.loadIfNeeded()
            async let clients = agentWorkspace.loadClientsIfNeeded()
            async let leads = agentWorkspace.loadLeadsIfNeeded()
            async let financial = stores.financial.loadIfNeeded()
            let values = await (
                home,
                social,
                profilePosts,
                account,
                messaging,
                clients,
                leads,
                financial
            )
            results = [
                (.home, values.0),
                (.social, values.1),
                (.profilePosts, values.2),
                (.account, values.3),
                (.messaging, values.4),
                (.clients, values.5),
                (.leads, values.6),
                (.financial, values.7)
            ]
        } else {
            async let home = stores.home.loadIfNeeded()
            async let social = stores.social.loadIfNeeded()
            async let profilePosts = stores.social.loadProfilePostsIfNeeded()
            async let account = stores.account.loadIfNeeded()
            async let messaging = stores.messaging.loadIfNeeded()
            async let journey = stores.journeyCircles.loadIfNeeded()
            async let financial = stores.financial.loadIfNeeded()
            let values = await (home, social, profilePosts, account, messaging, journey, financial)
            results = [
                (.home, values.0),
                (.social, values.1),
                (.profilePosts, values.2),
                (.account, values.3),
                (.messaging, values.4),
                (.journeyCircles, values.5),
                (.financial, values.6)
            ]
        }

        if let authenticationFailure = results.compactMap(authenticationFailure(from:)).first {
            state = .failed(authenticationFailure)
            authenticationFailureHandler(authenticationFailure)
            return
        }

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
