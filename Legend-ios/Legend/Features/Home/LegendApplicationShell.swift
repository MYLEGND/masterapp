import SwiftUI
import UIKit

private enum LegendAppTab: String, Identifiable {
    case home
    case clients
    case leads
    case circles
    case finance
    case messages
    case account

    var id: Self { self }

    static func available(for participantType: ParticipantType) -> [Self] {
        participantType == .agent
            ? [.home, .clients, .leads, .messages, .account]
            : [.home, .circles, .finance, .messages, .account]
    }

    var title: String {
        switch self {
        case .home: return "Home"
        case .clients: return "Clients"
        case .leads: return "Leads"
        case .circles: return "Circles"
        case .finance: return "Finance"
        case .messages: return "Messages"
        case .account: return "Account"
        }
    }

    var symbolName: String {
        switch self {
        case .home: return "house"
        case .clients: return "person.2"
        case .leads: return "person.crop.circle.badge.plus"
        case .circles: return "person.3"
        case .finance: return "chart.line.uptrend.xyaxis"
        case .messages: return "message"
        case .account: return "person"
        }
    }

    var selectedSymbolName: String {
        switch self {
        case .home: return "house.fill"
        case .clients: return "person.2.fill"
        case .leads: return "person.crop.circle.badge.plus"
        case .circles: return "person.3.fill"
        case .finance: return "chart.line.uptrend.xyaxis"
        case .messages: return "message.fill"
        case .account: return "person.fill"
        }
    }
}

struct LegendApplicationShell: View {
    let currentSession: MobileSession
    @ObservedObject private var coordinator: MobileSessionCoordinator
    @State private var selectedTab: LegendAppTab = .home
    @StateObject private var messages: MessagingStore

    init(currentSession: MobileSession, coordinator: MobileSessionCoordinator) {
        self.currentSession = currentSession
        _coordinator = ObservedObject(wrappedValue: coordinator)
        _messages = StateObject(wrappedValue: coordinator.makeMessagingStore())
    }

    var body: some View {
        TabView(selection: $selectedTab) {
            NavigationStack {
                LegendHomeView(
                    currentSession: currentSession,
                    coordinator: coordinator,
                    selectedTab: $selectedTab)
            }
            .tag(LegendAppTab.home)

            if currentSession.actor.identity.participantType == .agent {
                NavigationStack {
                    LegendAgentClientsView(
                        coordinator: coordinator,
                        messages: messages,
                        openMessages: { selectedTab = .messages })
                }
                .tag(LegendAppTab.clients)

                NavigationStack {
                    LegendAgentLeadsView(coordinator: coordinator)
                }
                .tag(LegendAppTab.leads)
            } else {
                NavigationStack {
                    LegendCirclesView(
                        currentSession: currentSession,
                        coordinator: coordinator)
                }
                .tag(LegendAppTab.circles)

                NavigationStack {
                    LegendFinanceView(
                        currentSession: currentSession,
                        coordinator: coordinator)
                }
                .tag(LegendAppTab.finance)
            }

            LegendMessagesTab(messages: messages)
                .tag(LegendAppTab.messages)

            NavigationStack {
                LegendAccountView(
                    currentSession: currentSession,
                    coordinator: coordinator)
            }
            .tag(LegendAppTab.account)
        }
        .toolbar(.hidden, for: .tabBar)
        .background(LegendPalette.canvas.ignoresSafeArea())
        .safeAreaInset(edge: .bottom, spacing: 0) {
            LegendTabBar(
                selection: $selectedTab,
                tabs: LegendAppTab.available(for: currentSession.actor.identity.participantType),
                accountAvatar: currentSession.actor.avatar,
                accountDisplayName: currentSession.actor.displayName,
                unreadMessageCount: unreadMessageCount)
        }
        .tint(LegendPalette.gold)
        .task {
            if case .idle = messages.state {
                messages.load()
            }
        }
    }

    private var unreadMessageCount: Int {
        guard case .loaded(let conversations) = messages.state else { return 0 }
        return conversations.reduce(0) { $0 + max(0, $1.unreadCount) }
    }
}

private struct LegendTabBar: View {
    @Binding var selection: LegendAppTab
    let tabs: [LegendAppTab]
    let accountAvatar: ProfileAvatar?
    let accountDisplayName: String
    let unreadMessageCount: Int

    var body: some View {
        HStack(alignment: .bottom, spacing: 0) {
            ForEach(tabs) { tab in
                Button {
                    guard selection != tab else { return }
                    UISelectionFeedbackGenerator().selectionChanged()
                    withAnimation(.easeOut(duration: 0.18)) { selection = tab }
                } label: {
                    VStack(spacing: LegendSpacing.xxs) {
                        if tab == .account {
                            LegendProfileAvatar(
                                avatar: accountAvatar,
                                displayName: accountDisplayName,
                                size: 24)
                        } else {
                            ZStack(alignment: .topTrailing) {
                                Image(systemName: selection == tab ? tab.selectedSymbolName : tab.symbolName)
                                    .font(.system(size: 19, weight: .semibold))
                                if tab == .messages, unreadMessageCount > 0 {
                                    Text("\(min(unreadMessageCount, 99))")
                                        .font(.system(size: 9, weight: .bold, design: .rounded))
                                        .foregroundStyle(.white)
                                        .frame(minWidth: 16, minHeight: 16)
                                        .background(LegendPalette.critical, in: Circle())
                                        .offset(x: 8, y: -7)
                                        .accessibilityLabel("\(unreadMessageCount) unread messages")
                                }
                            }
                        }
                        Text(tab.title)
                            .font(.caption2.weight(.semibold))
                            .lineLimit(1)
                    }
                    .foregroundStyle(selection == tab ? LegendPalette.primaryNavy : LegendPalette.secondaryLabel)
                    .frame(maxWidth: .infinity, minHeight: 48)
                    .contentShape(Rectangle())
                }
                .buttonStyle(.plain)
                .accessibilityLabel(tab.title)
                .accessibilityAddTraits(selection == tab ? .isSelected : [])
            }
        }
        .padding(.horizontal, LegendSpacing.xs)
        .padding(.vertical, LegendSpacing.xs)
        .background(.ultraThinMaterial)
        .overlay(alignment: .top) {
            Rectangle().fill(LegendPalette.separator.opacity(0.45)).frame(height: 0.5)
        }
    }
}

private struct LegendMessagesTab: View {
    @ObservedObject var messages: MessagingStore
    @State private var navigationPath: [UUID] = []

    var body: some View {
        NavigationStack(path: $navigationPath) {
            MessagingHomeView(
                store: messages,
                openConversation: { conversationID in
                    navigationPath = [conversationID]
                })
                .navigationDestination(for: UUID.self) { conversationID in
                    ConversationThreadView(store: messages, conversationID: conversationID)
                }
        }
    }
}

private struct LegendHomeView: View {
    let currentSession: MobileSession
    @Binding var selectedTab: LegendAppTab
    @StateObject private var store: MobileHomeStore
    @StateObject private var social: MobileSocialStore

    init(
        currentSession: MobileSession,
        coordinator: MobileSessionCoordinator,
        selectedTab: Binding<LegendAppTab>
    ) {
        self.currentSession = currentSession
        _selectedTab = selectedTab
        _store = StateObject(wrappedValue: coordinator.makeHomeStore())
        _social = StateObject(wrappedValue: coordinator.makeSocialStore())
    }

    var body: some View {
        Group {
            switch store.state {
            case .idle, .loading:
                LegendLoadingView("Loading your Legend home…")
            case .loaded(let home):
                homeContent(home)
            case .unavailable(let failure):
                LegendErrorCard(title: failure.title, message: failure.message, retryTitle: "Retry", retry: store.load)
                    .padding(LegendSpacing.md)
            }
        }
        .background(LegendPalette.canvas.ignoresSafeArea())
        .task { if case .idle = store.state { store.load() } }
    }

    private func homeContent(_ home: MobileHomeResponse) -> some View {
        ScrollView {
            LazyVStack(alignment: .leading, spacing: LegendSpacing.md) {
                LegendSocialHomeSection(
                    session: currentSession,
                    home: home,
                    social: social,
                    openMessages: { selectedTab = .messages },
                    openCircles: { selectedTab = .circles })

                metrics(home)

                if let subscription = home.subscription {
                    subscriptionCard(subscription, entitlement: home.entitlement)
                }

                if let financial = home.financial {
                    financialCard(financial)
                }

                if let journey = home.journey {
                    journeyCard(journey)
                }

                if !home.upcomingAppointments.isEmpty {
                    appointmentsCard(home.upcomingAppointments)
                }

                if !home.actions.isEmpty {
                    actionsCard(home.actions)
                }

                if !home.notifications.isEmpty {
                    notificationsCard(home.notifications)
                }
            }
            .padding(.horizontal, LegendSpacing.md)
            .padding(.vertical, LegendSpacing.sm)
        }
        .navigationBarHidden(true)
        .task { if case .idle = social.state { social.load() } }
    }

    @ViewBuilder
    private func metrics(_ home: MobileHomeResponse) -> some View {
        HStack(spacing: LegendSpacing.sm) {
            LegendMetric(title: "Messages", value: "\(home.messaging.unreadCount)", detail: "Unread")
            if home.identity.participantType == .agent {
                LegendMetric(title: "Clients", value: "\(home.activeClientCount)", detail: "Linked")
            } else if let position = home.financial?.position {
                LegendMetric(title: "Health", value: "\(position.healthScore)", detail: position.positionStatus)
            } else if let journey = home.journey {
                LegendMetric(title: "Connections", value: "\(journey.connectedPeerCount)", detail: "Journey Circles")
            }
            if home.identity.participantType == .agent {
                LegendMetric(title: "Actions", value: "\(home.actions.count)", detail: "Open")
            } else if let subscription = home.subscription {
                LegendMetric(title: "Membership", value: subscription.status, detail: subscription.paymentStanding)
            } else {
                LegendMetric(title: "Appointments", value: "\(home.upcomingAppointments.count)", detail: "Upcoming")
            }
        }
        .padding(LegendSpacing.md)
        .background(LegendPalette.elevatedSurface, in: RoundedRectangle(cornerRadius: LegendRadius.card, style: .continuous))
    }

    private func subscriptionCard(_ subscription: MobileSubscriptionSummary, entitlement: MobileEntitlementSummary?) -> some View {
        LegendCard {
            VStack(alignment: .leading, spacing: LegendSpacing.sm) {
                LegendSectionHeader("Membership", detail: subscription.status)
                HStack {
                    LegendMetric(title: "Monthly", value: subscription.monthlyAmount.formatted(.currency(code: subscription.currency)), detail: subscription.paymentStanding)
                    if let nextBilling = subscription.nextBillingDateUTC {
                        LegendMetric(title: "Next billing", value: nextBilling.formatted(.dateTime.month(.abbreviated).day()), detail: subscription.cancelAtPeriodEnd ? "Ends after this period" : nil)
                    }
                }
                if let entitlement {
                    Text(entitlement.summary)
                        .font(LegendTypography.metadata)
                        .foregroundStyle(LegendPalette.secondaryLabel)
                        .fixedSize(horizontal: false, vertical: true)
                }
            }
        }
    }

    private func financialCard(_ financial: MobileFinancialSnapshotResponse) -> some View {
        LegendCard {
            VStack(alignment: .leading, spacing: LegendSpacing.sm) {
                LegendSectionHeader("Financial intelligence", detail: financial.intelligence?.status)
                if let position = financial.position {
                    HStack {
                        LegendMetric(title: "Net worth", value: position.netWorth.formatted(.currency(code: "USD")), detail: position.positionStatus)
                        LegendMetric(title: "Annual cash", value: position.annualLifestyleRemaining.formatted(.currency(code: "USD")), detail: "Lifestyle remaining")
                    }
                    Text(position.positionSummary)
                        .font(LegendTypography.metadata)
                        .foregroundStyle(LegendPalette.secondaryLabel)
                        .fixedSize(horizontal: false, vertical: true)
                } else {
                    Text("No saved financial health snapshot is available yet.")
                        .font(LegendTypography.metadata)
                        .foregroundStyle(LegendPalette.secondaryLabel)
                }
                if let intelligence = financial.intelligence, !intelligence.findings.isEmpty {
                    Divider()
                    ForEach(intelligence.findings.prefix(3)) { finding in
                        VStack(alignment: .leading, spacing: LegendSpacing.xxs) {
                            Text(finding.title).font(.subheadline.weight(.semibold))
                            Text(finding.explanation).font(LegendTypography.metadata).foregroundStyle(LegendPalette.secondaryLabel).lineLimit(2)
                        }
                    }
                }
            }
        }
    }

    private func journeyCard(_ journey: MobileJourneySummary) -> some View {
        Button { selectedTab = .circles } label: {
            LegendCard {
                HStack(alignment: .top, spacing: LegendSpacing.md) {
                    Image(systemName: "person.3.fill")
                        .font(.title3.weight(.semibold))
                        .foregroundStyle(LegendPalette.gold)
                        .frame(width: 42, height: 42)
                        .background(LegendPalette.gold.opacity(0.12), in: Circle())
                    VStack(alignment: .leading, spacing: LegendSpacing.xxs) {
                        Text("Journey Circles").font(LegendTypography.section).foregroundStyle(LegendPalette.label)
                        Text(journey.hasProfile
                             ? "\(journey.connectedPeerCount) connections · \(journey.recommendationCount) recommendations"
                             : "Set up your Journey Circles profile to see authorized recommendations.")
                            .font(LegendTypography.metadata)
                            .foregroundStyle(LegendPalette.secondaryLabel)
                            .fixedSize(horizontal: false, vertical: true)
                    }
                    Spacer(minLength: 0)
                    Image(systemName: "chevron.right").foregroundStyle(LegendPalette.secondaryLabel)
                }
            }
        }
        .buttonStyle(.plain)
        .accessibilityLabel("Open Journey Circles")
    }

    private func appointmentsCard(_ appointments: [MobileUpcomingAppointment]) -> some View {
        LegendCard {
            VStack(alignment: .leading, spacing: LegendSpacing.sm) {
                LegendSectionHeader("Upcoming appointments")
                ForEach(appointments.prefix(4)) { appointment in
                    HStack {
                        Text(appointment.startUTC, format: .dateTime.month(.abbreviated).day().hour().minute())
                            .font(.subheadline.weight(.semibold))
                        Spacer()
                        LegendBadge(title: appointment.status, tone: .neutral)
                    }
                }
            }
        }
    }

    private func actionsCard(_ actions: [MobileActionItem]) -> some View {
        LegendCard {
            VStack(alignment: .leading, spacing: LegendSpacing.sm) {
                LegendSectionHeader("Open actions")
                ForEach(actions.prefix(5)) { action in
                    HStack(alignment: .firstTextBaseline) {
                        VStack(alignment: .leading, spacing: LegendSpacing.xxs) {
                            Text(action.title).font(.subheadline.weight(.semibold)).lineLimit(1)
                            if let dueDate = action.dueDateUTC {
                                Text(dueDate, format: .dateTime.month(.abbreviated).day().hour().minute())
                                    .font(LegendTypography.metadata).foregroundStyle(LegendPalette.secondaryLabel)
                            }
                        }
                        Spacer()
                        LegendBadge(title: action.priority, tone: .neutral)
                    }
                }
            }
        }
    }

    private func notificationsCard(_ notifications: [MobileBillingNotification]) -> some View {
        LegendCard {
            VStack(alignment: .leading, spacing: LegendSpacing.sm) {
                LegendSectionHeader("Membership notices")
                ForEach(notifications.prefix(4)) { notification in
                    VStack(alignment: .leading, spacing: LegendSpacing.xxs) {
                        Text(notification.subject).font(.subheadline.weight(.semibold)).lineLimit(1)
                        Text(notification.occurredUTC, format: .dateTime.month(.abbreviated).day().hour().minute())
                            .font(LegendTypography.metadata).foregroundStyle(LegendPalette.secondaryLabel)
                    }
                }
            }
        }
    }
}

private struct LegendAgentClientsView: View {
    @StateObject private var store: MobileAgentWorkspaceStore
    @ObservedObject private var messages: MessagingStore
    let openMessages: () -> Void

    init(
        coordinator: MobileSessionCoordinator,
        messages: MessagingStore,
        openMessages: @escaping () -> Void
    ) {
        _store = StateObject(wrappedValue: coordinator.makeAgentWorkspaceStore())
        _messages = ObservedObject(wrappedValue: messages)
        self.openMessages = openMessages
    }

    var body: some View {
        Group {
            switch store.clientsState {
            case .idle, .loading:
                LegendLoadingView("Loading your client CRM…")
            case .loaded(let clients):
                clientContent(clients)
            case .unavailable(let failure):
                LegendErrorCard(title: failure.title, message: failure.message, retryTitle: "Retry", retry: store.loadClients)
                    .padding(LegendSpacing.md)
            }
        }
        .background(LegendPalette.canvas.ignoresSafeArea())
        .navigationTitle("Clients")
        .navigationBarTitleDisplayMode(.inline)
        .task { if case .idle = store.clientsState { store.loadClients() } }
    }

    @ViewBuilder
    private func clientContent(_ clients: [MobileAgentClientSummary]) -> some View {
        if clients.isEmpty {
            LegendEmptyState(
                title: "No active clients",
                message: "Your active Client and Business Client CRM records will appear here.",
                symbolName: "person.2")
        } else {
            List(clients) { client in
                VStack(alignment: .leading, spacing: LegendSpacing.sm) {
                    HStack(spacing: LegendSpacing.sm) {
                        LegendProfileAvatar(avatar: client.avatar, displayName: client.displayName, size: 42)
                        VStack(alignment: .leading, spacing: LegendSpacing.xxs) {
                            Text(client.displayName).font(.subheadline.weight(.semibold)).lineLimit(1)
                            Text(client.email).font(LegendTypography.metadata).foregroundStyle(LegendPalette.secondaryLabel).lineLimit(1)
                        }
                        Spacer(minLength: LegendSpacing.sm)
                        LegendBadge(title: client.crmStatus, tone: .success)
                    }
                    Button {
                        messages.startConversation(forClientProfileID: client.profileID) { _ in
                            openMessages()
                        }
                    } label: {
                        Label("Message", systemImage: "message.fill")
                    }
                    .buttonStyle(LegendInlineButtonStyle(kind: .primary))
                    .disabled(messages.isStartingConversation)
                }
                .padding(.vertical, LegendSpacing.xxs)
            }
            .listStyle(.plain)
        }
    }
}

private struct LegendAgentLeadsView: View {
    @StateObject private var store: MobileAgentWorkspaceStore

    init(coordinator: MobileSessionCoordinator) {
        _store = StateObject(wrappedValue: coordinator.makeAgentWorkspaceStore())
    }

    var body: some View {
        Group {
            switch store.leadsState {
            case .idle, .loading:
                LegendLoadingView("Loading your lead CRM…")
            case .loaded(let leads):
                leadContent(leads)
            case .unavailable(let failure):
                LegendErrorCard(title: failure.title, message: failure.message, retryTitle: "Retry", retry: store.loadLeads)
                    .padding(LegendSpacing.md)
            }
        }
        .background(LegendPalette.canvas.ignoresSafeArea())
        .navigationTitle("Leads")
        .navigationBarTitleDisplayMode(.inline)
        .task { if case .idle = store.leadsState { store.loadLeads() } }
    }

    @ViewBuilder
    private func leadContent(_ leads: [MobileAgentLeadSummary]) -> some View {
        if leads.isEmpty {
            LegendEmptyState(
                title: "No active leads",
                message: "Your active workstation lead records will appear here.",
                symbolName: "person.crop.circle.badge.plus")
        } else {
            List(leads) { lead in
                HStack(spacing: LegendSpacing.sm) {
                    Image(systemName: "person.crop.circle")
                        .font(.title2)
                        .foregroundStyle(LegendPalette.gold)
                    VStack(alignment: .leading, spacing: LegendSpacing.xxs) {
                        Text(lead.displayName).font(.subheadline.weight(.semibold)).lineLimit(1)
                        Text("Updated \(lead.updatedUTC, format: .dateTime.month(.abbreviated).day().hour().minute())")
                            .font(LegendTypography.metadata)
                            .foregroundStyle(LegendPalette.secondaryLabel)
                    }
                    Spacer(minLength: LegendSpacing.sm)
                    LegendBadge(title: lead.crmStage, tone: .neutral)
                }
                .padding(.vertical, LegendSpacing.xxs)
            }
            .listStyle(.plain)
        }
    }
}

private struct LegendCirclesView: View {
    let currentSession: MobileSession
    @StateObject private var store: MobileJourneyCirclesStore
    @State private var isEditingProfile = false

    init(currentSession: MobileSession, coordinator: MobileSessionCoordinator) {
        self.currentSession = currentSession
        _store = StateObject(wrappedValue: coordinator.makeJourneyCirclesStore())
    }

    var body: some View {
        Group {
            if currentSession.actor.identity.participantType != .client {
                LegendEmptyState(
                    title: "Journey Circles",
                    message: "Journey Circles is available from an authorized client mobile identity.",
                    symbolName: "person.3")
            } else {
                switch store.state {
                case .idle, .loading:
                    LegendLoadingView("Loading Journey Circles…")
                case .loaded(let dashboard):
                    dashboardContent(dashboard)
                case .unavailable(let failure):
                    LegendErrorCard(title: failure.title, message: failure.message, retryTitle: "Retry", retry: store.load)
                        .padding(LegendSpacing.md)
                }
            }
        }
        .background(LegendPalette.canvas.ignoresSafeArea())
        .navigationTitle("Journey Circles")
        .navigationBarTitleDisplayMode(.inline)
        .toolbar {
            if case .loaded(let dashboard) = store.state {
                ToolbarItem(placement: .topBarTrailing) {
                    Button {
                        isEditingProfile = true
                    } label: {
                        Label("Manage Journey Circles profile", systemImage: "slider.horizontal.3")
                    }
                    .accessibilityLabel(
                        dashboard.profile == nil
                            ? "Set up Journey Circles profile"
                            : "Manage Journey Circles profile"
                    )
                }
            }
        }
        .task { if case .idle = store.state { store.load() } }
        .sheet(isPresented: $isEditingProfile) {
            if case .loaded(let dashboard) = store.state {
                LegendJourneyProfileEditor(dashboard: dashboard, store: store)
            }
        }
        .alert(
            store.actionFailure?.title ?? "Journey Circles unavailable",
            isPresented: Binding(
                get: { store.actionFailure != nil },
                set: { if !$0 { store.dismissActionFailure() } }),
            actions: { Button("OK", role: .cancel) { store.dismissActionFailure() } },
            message: { Text(store.actionFailure?.message ?? "The request could not be completed.") })
    }

    private func dashboardContent(_ dashboard: MobileJourneyDashboardResponse) -> some View {
        ScrollView {
            LazyVStack(alignment: .leading, spacing: LegendSpacing.md) {
                LegendHero(
                    eyebrow: "COMMUNITY",
                    title: "Journey Circles",
                    detail: dashboard.profile == nil
                        ? "Complete your approved selections in the client portal to participate."
                        : "Your connections and recommendations are based on your saved preferences.",
                    symbolName: "person.3.fill")

                if let profile = dashboard.profile {
                    profileCard(profile)
                }

                if !dashboard.requests.isEmpty {
                    connectionSection("Requests", connections: dashboard.requests, kind: .request)
                }

                if !dashboard.connections.isEmpty {
                    connectionSection("Your connections", connections: dashboard.connections, kind: .connection)
                }

                if !dashboard.recommendations.isEmpty {
                    VStack(alignment: .leading, spacing: LegendSpacing.sm) {
                        LegendSectionHeader("Recommendations")
                        ForEach(dashboard.recommendations) { recommendation in
                            recommendationCard(recommendation)
                        }
                    }
                }

                if dashboard.profile != nil && dashboard.recommendations.isEmpty && dashboard.connections.isEmpty && dashboard.requests.isEmpty {
                    LegendEmptyState(
                        title: "No recommendations yet",
                        message: "New authorized connections will appear here when they match your saved preferences.",
                        symbolName: "person.3")
                }
            }
            .padding(.horizontal, LegendSpacing.md)
            .padding(.vertical, LegendSpacing.sm)
        }
    }

    private func profileCard(_ profile: MobileJourneyProfile) -> some View {
        LegendCard(style: .navy) {
            HStack(alignment: .top, spacing: LegendSpacing.md) {
                LegendProfileAvatar(avatar: profile.avatar, displayName: profile.displayName, size: 52)
                VStack(alignment: .leading, spacing: LegendSpacing.xxs) {
                    Text(profile.displayName).font(LegendTypography.section).foregroundStyle(.white)
                    if let introduction = profile.introduction, !introduction.isEmpty {
                        Text(introduction).font(LegendTypography.metadata).foregroundStyle(.white.opacity(0.78)).lineLimit(3)
                    }
                    if !profile.goals.isEmpty {
                        Text(profile.goals.joined(separator: " · ")).font(.caption).foregroundStyle(LegendPalette.gold).lineLimit(2)
                    }
                }
            }
        }
    }

    private func connectionSection(
        _ title: String,
        connections: [MobileJourneyConnection],
        kind: JourneyConnectionSectionKind
    ) -> some View {
        VStack(alignment: .leading, spacing: LegendSpacing.sm) {
            LegendSectionHeader(title)
            ForEach(connections) { connection in
                LegendCard {
                    HStack(spacing: LegendSpacing.sm) {
                        LegendProfileAvatar(avatar: connection.profile.avatar, displayName: connection.profile.displayName, size: 42)
                        VStack(alignment: .leading, spacing: LegendSpacing.xxs) {
                            Text(connection.profile.displayName).font(.subheadline.weight(.semibold))
                            Text(connection.status).font(LegendTypography.metadata).foregroundStyle(LegendPalette.secondaryLabel)
                        }
                        Spacer()
                        if kind == .request {
                            HStack(spacing: LegendSpacing.xs) {
                                Button("Decline") { store.respondToConnection(id: connection.id, accept: false) }
                                    .buttonStyle(LegendInlineButtonStyle(kind: .secondary))
                                Button("Accept") { store.respondToConnection(id: connection.id, accept: true) }
                                    .buttonStyle(LegendInlineButtonStyle(kind: .primary))
                            }
                        } else {
                            Button("Disconnect") { store.disconnect(id: connection.id) }
                                .buttonStyle(LegendInlineButtonStyle(kind: .destructive))
                        }
                    }
                }
            }
        }
    }

    private func recommendationCard(_ recommendation: MobileJourneyRecommendation) -> some View {
        LegendCard {
            HStack(alignment: .top, spacing: LegendSpacing.sm) {
                LegendProfileAvatar(avatar: recommendation.profile.avatar, displayName: recommendation.profile.displayName, size: 44)
                VStack(alignment: .leading, spacing: LegendSpacing.xxs) {
                    Text(recommendation.profile.displayName).font(.subheadline.weight(.semibold))
                    Text(recommendation.explanation).font(LegendTypography.metadata).foregroundStyle(LegendPalette.secondaryLabel).fixedSize(horizontal: false, vertical: true)
                }
                Spacer(minLength: LegendSpacing.xs)
                Button("Connect") { store.requestConnection(to: recommendation.profile.id) }
                    .buttonStyle(LegendInlineButtonStyle(kind: .primary))
            }
        }
    }

    private enum JourneyConnectionSectionKind {
        case request
        case connection
    }
}

private struct LegendJourneyProfileEditor: View {
    let dashboard: MobileJourneyDashboardResponse
    @ObservedObject var store: MobileJourneyCirclesStore
    @Environment(\.dismiss) private var dismiss
    @State private var consentAffirmed: Bool
    @State private var isOptedIn: Bool
    @State private var isDiscoverable: Bool
    @State private var allowSuggestions: Bool
    @State private var allowConnectionRequests: Bool
    @State private var introduction: String
    @State private var lifeStages: Set<String>
    @State private var locations: Set<String>
    @State private var goals: Set<String>
    @State private var interests: Set<String>
    @State private var circleCodes: Set<String>
    @State private var connectionTypes: Set<String>
    @State private var communicationStyles: Set<String>
    @State private var accountabilityFrequencies: Set<String>

    init(dashboard: MobileJourneyDashboardResponse, store: MobileJourneyCirclesStore) {
        self.dashboard = dashboard
        _store = ObservedObject(wrappedValue: store)
        let preferences = dashboard.preferences
        let profile = dashboard.profile
        _consentAffirmed = State(initialValue: preferences?.consentAffirmed ?? false)
        _isOptedIn = State(initialValue: preferences?.isOptedIn ?? false)
        _isDiscoverable = State(initialValue: preferences?.isDiscoverable ?? true)
        _allowSuggestions = State(initialValue: preferences?.allowSuggestions ?? true)
        _allowConnectionRequests = State(initialValue: preferences?.allowConnectionRequests ?? true)
        _introduction = State(initialValue: profile?.introduction ?? "")
        _lifeStages = State(initialValue: Set(profile?.lifeStages ?? []))
        _locations = State(initialValue: Set(profile?.locations ?? []))
        _goals = State(initialValue: Set(profile?.goals ?? []))
        _interests = State(initialValue: Set(profile?.interests ?? []))
        _circleCodes = State(initialValue: Set(profile?.circleCodes ?? []))
        _connectionTypes = State(initialValue: Set(profile?.connectionTypes ?? []))
        _communicationStyles = State(initialValue: Set(profile?.communicationStyles ?? []))
        _accountabilityFrequencies = State(initialValue: Set(profile?.accountabilityFrequencies ?? []))
    }

    var body: some View {
        NavigationStack {
            ScrollView {
                LazyVStack(alignment: .leading, spacing: LegendSpacing.md) {
                    LegendCard(style: .navy) {
                        VStack(alignment: .leading, spacing: LegendSpacing.xs) {
                            Text("YOUR SELECTIONS").font(.caption.weight(.bold)).foregroundStyle(LegendPalette.gold)
                            Text("Journey Circles profile").font(LegendTypography.section).foregroundStyle(.white)
                            Text("Choose the connections and recommendations you want to receive.")
                                .font(LegendTypography.metadata)
                                .foregroundStyle(.white.opacity(0.78))
                        }
                    }

                    LegendCard {
                        VStack(alignment: .leading, spacing: LegendSpacing.sm) {
                            LegendSectionHeader("Participation")
                            Toggle("Confirm community participation", isOn: $consentAffirmed)
                            Toggle("Join Journey Circles", isOn: $isOptedIn)
                            Toggle("Allow recommendations", isOn: $allowSuggestions)
                            Toggle("Allow connection requests", isOn: $allowConnectionRequests)
                            Toggle("Appear to matching members", isOn: $isDiscoverable)
                        }
                        .tint(LegendPalette.gold)
                    }

                    LegendCard {
                        VStack(alignment: .leading, spacing: LegendSpacing.xs) {
                            LegendSectionHeader("Introduction")
                            TextField("Share what you are building", text: $introduction, axis: .vertical)
                                .lineLimit(3...6)
                                .textInputAutocapitalization(.sentences)
                                .padding(LegendSpacing.sm)
                                .background(LegendPalette.insetSurface, in: RoundedRectangle(cornerRadius: LegendRadius.control, style: .continuous))
                        }
                    }

                    JourneyMultiSelectSection(title: "Goals", options: dashboard.taxonomy.goals, selections: $goals)
                    JourneyMultiSelectSection(title: "Circles", options: dashboard.taxonomy.circles, selections: $circleCodes)
                    JourneyMultiSelectSection(title: "Life stage", options: dashboard.taxonomy.lifeStages, selections: $lifeStages)
                    JourneyMultiSelectSection(title: "Location", options: dashboard.taxonomy.locations, selections: $locations)
                    JourneyMultiSelectSection(title: "Interests", options: dashboard.taxonomy.interests, selections: $interests)
                    JourneyMultiSelectSection(title: "Connection types", options: dashboard.taxonomy.connectionTypes, selections: $connectionTypes)
                    JourneyMultiSelectSection(title: "Communication style", options: dashboard.taxonomy.communicationStyles, selections: $communicationStyles)
                    JourneyMultiSelectSection(title: "Accountability", options: dashboard.taxonomy.accountabilityFrequencies, selections: $accountabilityFrequencies)
                }
                .padding(.horizontal, LegendSpacing.md)
                .padding(.vertical, LegendSpacing.sm)
            }
            .background(LegendPalette.canvas.ignoresSafeArea())
            .navigationTitle("Journey Circles")
            .navigationBarTitleDisplayMode(.inline)
            .toolbar {
                ToolbarItem(placement: .cancellationAction) {
                    Button("Close") { dismiss() }
                }
                ToolbarItem(placement: .confirmationAction) {
                    Button(store.isPerformingAction ? "Saving…" : "Save") {
                        store.saveProfile(MobileJourneyProfileInput(
                            consentAffirmed: consentAffirmed,
                            isOptedIn: isOptedIn,
                            isDiscoverable: isDiscoverable,
                            allowSuggestions: allowSuggestions,
                            allowConnectionRequests: allowConnectionRequests,
                            introduction: introduction.trimmingCharacters(in: .whitespacesAndNewlines),
                            lifeStages: lifeStages.sorted(),
                            locations: locations.sorted(),
                            goals: goals.sorted(),
                            interests: interests.sorted(),
                            circleCodes: circleCodes.sorted(),
                            connectionTypes: connectionTypes.sorted(),
                            communicationStyles: communicationStyles.sorted(),
                            accountabilityFrequencies: accountabilityFrequencies.sorted()))
                    }
                    .disabled(store.isPerformingAction || !consentAffirmed)
                }
            }
        }
    }
}

private struct JourneyMultiSelectSection: View {
    let title: String
    let options: [String]
    @Binding var selections: Set<String>

    var body: some View {
        LegendCard {
            VStack(alignment: .leading, spacing: LegendSpacing.sm) {
                LegendSectionHeader(title, detail: selections.isEmpty ? "Select all that apply" : "\(selections.count) selected")
                LazyVGrid(columns: [GridItem(.adaptive(minimum: 118), spacing: LegendSpacing.xs)], alignment: .leading, spacing: LegendSpacing.xs) {
                    ForEach(options, id: \.self) { option in
                        Button {
                            if selections.contains(option) {
                                selections.remove(option)
                            } else {
                                selections.insert(option)
                            }
                        } label: {
                            Label(option, systemImage: selections.contains(option) ? "checkmark.circle.fill" : "circle")
                        }
                        .buttonStyle(LegendInlineButtonStyle(kind: selections.contains(option) ? .gold : .secondary))
                        .accessibilityValue(selections.contains(option) ? "Selected" : "Not selected")
                    }
                }
            }
        }
    }
}

private struct LegendFinanceView: View {
    let currentSession: MobileSession
    @StateObject private var store: MobileHomeStore

    init(currentSession: MobileSession, coordinator: MobileSessionCoordinator) {
        self.currentSession = currentSession
        _store = StateObject(wrappedValue: coordinator.makeHomeStore())
    }

    var body: some View {
        Group {
            if currentSession.actor.identity.participantType != .client {
                LegendEmptyState(
                    title: "Financial intelligence",
                    message: "Client financial intelligence is available from an authorized client mobile identity.",
                    symbolName: "chart.line.uptrend.xyaxis")
            } else {
                switch store.state {
                case .idle, .loading:
                    LegendLoadingView("Loading financial intelligence…")
                case .loaded(let home):
                    financialContent(home.financial)
                case .unavailable(let failure):
                    LegendErrorCard(title: failure.title, message: failure.message, retryTitle: "Retry", retry: store.load)
                        .padding(LegendSpacing.md)
                }
            }
        }
        .background(LegendPalette.canvas.ignoresSafeArea())
        .navigationTitle("Financial intelligence")
        .navigationBarTitleDisplayMode(.inline)
        .task { if case .idle = store.state { store.load() } }
    }

    @ViewBuilder
    private func financialContent(_ financial: MobileFinancialSnapshotResponse?) -> some View {
        if let financial {
            ScrollView {
                LazyVStack(alignment: .leading, spacing: LegendSpacing.md) {
                    if let position = financial.position {
                        LegendCard(style: .navy) {
                            VStack(alignment: .leading, spacing: LegendSpacing.sm) {
                                Text("FINANCIAL HEALTH").font(.caption.weight(.bold)).foregroundStyle(LegendPalette.gold)
                                Text("\(position.healthScore) health score").font(LegendTypography.hero).foregroundStyle(.white)
                                Text(position.positionSummary).font(LegendTypography.body).foregroundStyle(.white.opacity(0.78)).fixedSize(horizontal: false, vertical: true)
                            }
                        }
                        HStack(spacing: LegendSpacing.sm) {
                            LegendMetric(title: "Assets", value: position.assetsTotal.formatted(.currency(code: "USD")))
                            LegendMetric(title: "Liabilities", value: position.liabilitiesTotal.formatted(.currency(code: "USD")))
                            LegendMetric(title: "Net worth", value: position.netWorth.formatted(.currency(code: "USD")))
                        }
                        .padding(LegendSpacing.md)
                        .background(LegendPalette.elevatedSurface, in: RoundedRectangle(cornerRadius: LegendRadius.card, style: .continuous))
                    } else {
                        LegendEmptyState(
                            title: "No financial snapshot",
                            message: "Your saved financial health data will appear here after it is completed in the client portal.",
                            symbolName: "chart.line.uptrend.xyaxis")
                    }

                    if !financial.upcomingBills.isEmpty {
                        LegendCard {
                            VStack(alignment: .leading, spacing: LegendSpacing.sm) {
                                LegendSectionHeader("Upcoming recurring bills")
                                ForEach(financial.upcomingBills) { bill in
                                    HStack {
                                        VStack(alignment: .leading, spacing: LegendSpacing.xxs) {
                                            Text(bill.displayName).font(.subheadline.weight(.semibold))
                                            Text(bill.nextExpectedDateUTC, format: .dateTime.month(.abbreviated).day())
                                                .font(LegendTypography.metadata).foregroundStyle(LegendPalette.secondaryLabel)
                                        }
                                        Spacer()
                                        Text(bill.amount.formatted(.currency(code: "USD"))).font(.subheadline.weight(.semibold))
                                    }
                                }
                            }
                        }
                    }
                }
                .padding(.horizontal, LegendSpacing.md)
                .padding(.vertical, LegendSpacing.sm)
            }
        } else {
            LegendEmptyState(
                title: "Financial intelligence unavailable",
                message: "No client financial data is available for this mobile identity.",
                symbolName: "lock.chart")
        }
    }
}

private struct LegendAccountView: View {
    let currentSession: MobileSession
    @ObservedObject private var coordinator: MobileSessionCoordinator
    @StateObject private var account: MobileAccountStore
    @State private var isEditing = false

    init(currentSession: MobileSession, coordinator: MobileSessionCoordinator) {
        self.currentSession = currentSession
        _coordinator = ObservedObject(wrappedValue: coordinator)
        _account = StateObject(wrappedValue: coordinator.makeAccountStore())
    }

    var body: some View {
        Group {
            switch account.state {
            case .idle, .loading:
                LegendLoadingView("Loading your account…")
            case .loaded(let profile):
                accountContent(profile)
            case .unavailable(let failure):
                LegendErrorCard(title: failure.title, message: failure.message, retryTitle: "Retry", retry: account.load)
                    .padding(LegendSpacing.md)
            }
        }
        .background(LegendPalette.canvas.ignoresSafeArea())
        .navigationTitle("Account")
        .navigationBarTitleDisplayMode(.inline)
        .task { if case .idle = account.state { account.load() } }
        .sheet(isPresented: $isEditing) {
            if case .loaded(let profile) = account.state {
                LegendAccountEditor(profile: profile, store: account)
            }
        }
        .alert(
            account.actionFailure?.title ?? "Account update unavailable",
            isPresented: Binding(
                get: { account.actionFailure != nil },
                set: { if !$0 { account.dismissActionFailure() } }),
            actions: { Button("OK", role: .cancel) { account.dismissActionFailure() } },
            message: { Text(account.actionFailure?.message ?? "The account update could not be completed.") })
    }

    private func accountContent(_ profile: MobileAccountProfile) -> some View {
        ScrollView {
            VStack(alignment: .leading, spacing: LegendSpacing.lg) {
                LegendCard(style: .navy) {
                    HStack(spacing: LegendSpacing.md) {
                        LegendProfileAvatar(avatar: profile.avatar, displayName: profile.displayName, size: 64)
                        VStack(alignment: .leading, spacing: LegendSpacing.xxs) {
                            Text(profile.displayName).font(LegendTypography.hero).foregroundStyle(.white).lineLimit(2)
                            Text(profile.participantType.rawValue).font(.subheadline.weight(.semibold)).foregroundStyle(LegendPalette.gold)
                            Text("Secure mobile session").font(LegendTypography.metadata).foregroundStyle(.white.opacity(0.78))
                        }
                    }
                }

                LegendCard {
                    VStack(alignment: .leading, spacing: LegendSpacing.sm) {
                        LegendSectionHeader("Profile")
                        accountDetail(title: "Email", value: profile.email ?? "Not available")
                        accountDetail(title: "Phone", value: profile.phone ?? "Not set")
                        if profile.participantType == .agent {
                            accountDetail(title: "Title", value: profile.title ?? "Not set")
                            if let shortBio = profile.shortBio, !shortBio.isEmpty {
                                accountDetail(title: "Introduction", value: shortBio)
                            }
                        }
                    }
                }

                Button("Edit account") { isEditing = true }
                    .buttonStyle(LegendButtonStyle(kind: .primary))

                LegendCard {
                    VStack(alignment: .leading, spacing: LegendSpacing.sm) {
                        LegendSectionHeader("Mobile security")
                        Text("This app uses the configured secure sign-in provider and stores session tokens only in the iOS Keychain.")
                            .font(LegendTypography.body)
                            .foregroundStyle(LegendPalette.secondaryLabel)
                            .fixedSize(horizontal: false, vertical: true)
                    }
                }

                Button("Sign out") { coordinator.signOut() }
                    .buttonStyle(LegendButtonStyle(kind: .destructive))
            }
            .padding(.horizontal, LegendSpacing.md)
            .padding(.vertical, LegendSpacing.sm)
        }
    }

    private func accountDetail(title: String, value: String) -> some View {
        VStack(alignment: .leading, spacing: LegendSpacing.xxs) {
            Text(title.uppercased()).font(.caption.weight(.bold)).foregroundStyle(LegendPalette.secondaryLabel)
            Text(value).font(LegendTypography.body).foregroundStyle(LegendPalette.label).fixedSize(horizontal: false, vertical: true)
        }
    }
}

private struct LegendAccountEditor: View {
    let profile: MobileAccountProfile
    @ObservedObject var store: MobileAccountStore
    @Environment(\.dismiss) private var dismiss
    @State private var displayName: String
    @State private var phone: String
    @State private var title: String
    @State private var shortBio: String

    init(profile: MobileAccountProfile, store: MobileAccountStore) {
        self.profile = profile
        _store = ObservedObject(wrappedValue: store)
        _displayName = State(initialValue: profile.displayName)
        _phone = State(initialValue: profile.phone ?? "")
        _title = State(initialValue: profile.title ?? "")
        _shortBio = State(initialValue: profile.shortBio ?? "")
    }

    var body: some View {
        NavigationStack {
            Form {
                Section("Your profile") {
                    TextField("Name", text: $displayName)
                        .textContentType(.name)
                    TextField("Phone", text: $phone)
                        .textContentType(.telephoneNumber)
                        .keyboardType(.phonePad)
                    if profile.participantType == .agent {
                        TextField("Title", text: $title)
                            .textContentType(.jobTitle)
                        TextField("Introduction", text: $shortBio, axis: .vertical)
                            .lineLimit(3...6)
                    }
                }

                Section {
                    Text(profile.email ?? "Not available")
                        .foregroundStyle(LegendPalette.secondaryLabel)
                } header: {
                    Text("Directory email")
                } footer: {
                    Text("Email remains managed by the secure directory.")
                }
            }
            .navigationTitle("Edit account")
            .navigationBarTitleDisplayMode(.inline)
            .toolbar {
                ToolbarItem(placement: .cancellationAction) {
                    Button("Cancel") { dismiss() }
                }
                ToolbarItem(placement: .confirmationAction) {
                    Button(store.isSaving ? "Saving…" : "Save") {
                        store.save(MobileAccountUpdate(
                            displayName: displayName,
                            phone: phone,
                            title: profile.participantType == .agent ? title : nil,
                            shortBio: profile.participantType == .agent ? shortBio : nil))
                        dismiss()
                    }
                    .disabled(store.isSaving || displayName.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty)
                }
            }
        }
    }
}

struct LegendProfileAvatar: View {
    let avatar: ProfileAvatar?
    let displayName: String
    let size: CGFloat

    var body: some View {
        Group {
            if let data = avatar?.imageData, let image = UIImage(data: data) {
                Image(uiImage: image).resizable().scaledToFill()
            } else {
                Text(initials).font(.caption.weight(.bold)).foregroundStyle(.white).background(LegendPalette.primaryNavy)
            }
        }
        .frame(width: size, height: size)
        .clipShape(Circle())
        .overlay { Circle().stroke(LegendPalette.gold.opacity(0.7), lineWidth: 1) }
        .accessibilityLabel("Profile image for \(displayName)")
    }

    private var initials: String {
        let value = displayName.split(separator: " ").prefix(2).compactMap(\.first).map(String.init).joined().uppercased()
        return value.isEmpty ? "L" : value
    }
}
