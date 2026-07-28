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
    @StateObject private var social: MobileSocialStore

    init(
        currentSession: MobileSession,
        coordinator: MobileSessionCoordinator
    ) {
        self.currentSession = currentSession
        _coordinator = ObservedObject(wrappedValue: coordinator)
        _messages = StateObject(
            wrappedValue: coordinator.makeMessagingStore()
        )
        _social = StateObject(
            wrappedValue: coordinator.makeSocialStore()
        )
    }

    var body: some View {
        TabView(selection: $selectedTab) {
            NavigationStack {
                LegendHomeView(
                    currentSession: currentSession,
                    coordinator: coordinator,
                    social: social,
                    selectedTab: $selectedTab
                )
            }
            .tag(LegendAppTab.home)

            if currentSession.actor.identity.participantType == .agent {
                NavigationStack {
                    LegendAgentClientsView(
                        coordinator: coordinator,
                        messages: messages,
                        openMessages: {
                            select(.messages)
                        }
                    )
                }
                .tag(LegendAppTab.clients)

                NavigationStack {
                    LegendAgentLeadsView(
                        coordinator: coordinator
                    )
                }
                .tag(LegendAppTab.leads)
            } else {
                NavigationStack {
                    LegendCirclesView(
                        currentSession: currentSession,
                        coordinator: coordinator
                    )
                }
                .tag(LegendAppTab.circles)

                NavigationStack {
                    LegendFinanceView(
                        currentSession: currentSession,
                        coordinator: coordinator
                    )
                }
                .tag(LegendAppTab.finance)
            }

            LegendMessagesTab(messages: messages)
                .tag(LegendAppTab.messages)

            NavigationStack {
                LegendAccountView(
                    currentSession: currentSession,
                    coordinator: coordinator,
                    social: social
                )
            }
            .tag(LegendAppTab.account)
        }
        .toolbar(.hidden, for: .tabBar)
        .legendNextPageBackground()
        .safeAreaInset(edge: .bottom, spacing: 0) {
            LegendNextTabBar(
                selection: $selectedTab,
                tabs: LegendAppTab.available(
                    for: currentSession.actor.identity.participantType
                ),
                accountAvatar: currentSession.actor.avatar,
                accountDisplayName: currentSession.actor.displayName,
                unreadMessageCount: unreadMessageCount
            )
        }
        .tint(LegendNextColor.gold)
        .task {
            if case .idle = messages.state {
                messages.load()
            }
        }
    }

    private var unreadMessageCount: Int {
        guard case .loaded(let conversations) = messages.state else {
            return 0
        }

        return conversations.reduce(0) {
            $0 + max(0, $1.unreadCount)
        }
    }

    private func select(_ tab: LegendAppTab) {
        guard selectedTab != tab else {
            return
        }

        withAnimation(LegendNextMotion.tab) {
            selectedTab = tab
        }
    }
}

private struct LegendNextTabBar: View {
    @Environment(\.colorScheme) private var colorScheme
    @Environment(\.accessibilityReduceMotion) private var reduceMotion

    @Binding var selection: LegendAppTab
    let tabs: [LegendAppTab]
    let accountAvatar: ProfileAvatar?
    let accountDisplayName: String
    let unreadMessageCount: Int

    var body: some View {
        HStack(spacing: 0) {
            ForEach(tabs) { tab in
                tabButton(tab)
            }
        }
        .padding(.horizontal, 10)
        .padding(.vertical, 3)
        .background {
            Capsule()
                .fill(LegendNextGradient.hero)
        }
        .padding(.horizontal, LegendNextSpacing.sm)
        .padding(.bottom, 4)
    }

    private func tabButton(
        _ tab: LegendAppTab
    ) -> some View {
        Button {
            guard selection != tab else {
                return
            }

            UISelectionFeedbackGenerator()
                .selectionChanged()

            if reduceMotion {
                selection = tab
            } else {
                withAnimation(LegendNextMotion.tab) {
                    selection = tab
                }
            }
        } label: {
            tabIcon(tab)
                .foregroundStyle(
                    selection == tab
                        ? LegendNextColor.goldBright
                        : Color.white.opacity(0.88)
                )
                .frame(
                    maxWidth: .infinity,
                    minHeight: 44
                )
                .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .accessibilityLabel(tab.title)
        .accessibilityValue(
            selection == tab ? "Selected" : ""
        )
        .accessibilityAddTraits(
            selection == tab ? .isSelected : []
        )
    }

    @ViewBuilder
    private func tabIcon(
        _ tab: LegendAppTab
    ) -> some View {
        if tab == .account {
            LegendProfileAvatar(
                avatar: accountAvatar,
                displayName: accountDisplayName,
                size: 23
            )
            .overlay {
                Circle()
                    .strokeBorder(
                        selection == tab
                            ? LegendNextColor.goldBright
                            : Color.white.opacity(0.38),
                        lineWidth: selection == tab ? 2 : 1
                    )
            }
        } else {
            ZStack(alignment: .topTrailing) {
                Image(
                    systemName: selection == tab
                        ? tab.selectedSymbolName
                        : tab.symbolName
                )
                .font(
                    .system(
                        size: 19,
                        weight: .medium
                    )
                )
                .symbolRenderingMode(.monochrome)
                .frame(width: 25, height: 25)

                if tab == .messages,
                   unreadMessageCount > 0 {
                    Text(unreadBadgeText)
                        .font(
                            .system(
                                size: 9,
                                weight: .bold,
                                design: .rounded
                            )
                        )
                        .foregroundStyle(.white)
                        .frame(
                            minWidth: 16,
                            minHeight: 16
                        )
                        .padding(
                            .horizontal,
                            unreadMessageCount > 9 ? 2 : 0
                        )
                        .background(
                            LegendNextColor.danger,
                            in: Capsule()
                        )
                        .overlay {
                            Capsule()
                                .strokeBorder(
                                    Color.white.opacity(0.80),
                                    lineWidth: 1
                                )
                        }
                        .offset(x: 9, y: -7)
                        .accessibilityHidden(true)
                }
            }
        }
    }

    private var unreadBadgeText: String {
        unreadMessageCount > 99
            ? "99+"
            : "\(unreadMessageCount)"
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
    @ObservedObject private var social: MobileSocialStore

    init(
        currentSession: MobileSession,
        coordinator: MobileSessionCoordinator,
        social: MobileSocialStore,
        selectedTab: Binding<LegendAppTab>
    ) {
        self.currentSession = currentSession
        _selectedTab = selectedTab
        _store = StateObject(
            wrappedValue: coordinator.makeHomeStore()
        )
        _social = ObservedObject(wrappedValue: social)
    }

    var body: some View {
        Group {
            switch store.state {
            case .idle, .loading:
                LegendNextLoadingState(
                    "Preparing your Legend home",
                    detail: "Bringing your priorities, progress, and community together."
                )

            case .loaded(let home):
                homeContent(home)

            case .unavailable(let failure):
                LegendNextErrorState(
                    title: failure.title,
                    message: failure.message,
                    retryTitle: "Retry",
                    retry: store.load
                )
                .padding(LegendNextSpacing.sm)
            }
        }
        .legendNextPageBackground()
        .task {
            if case .idle = store.state {
                store.load()
            }
        }
    }

    private func homeContent(
        _ home: MobileHomeResponse
    ) -> some View {
        ScrollView {
            LazyVStack(
                alignment: .leading,
                spacing: LegendNextSpacing.xs
            ) {
                LegendSocialHomeSection(
                    session: currentSession,
                    home: home,
                    social: social,
                    openMessages: {
                        open(.messages)
                    },
                    openCircles: {
                        open(.circles)
                    }
                ) {
                    homeHero(home)

                    if hasPriorityContent(home) {
                        prioritySection(home)
                    }
                }
            }
            .padding(.horizontal, LegendNextSpacing.sm)
            .padding(.top, LegendNextSpacing.sm)
            .padding(.bottom, LegendNextSpacing.xl)
        }
        .navigationBarHidden(true)
        .task {
            if case .idle = social.state {
                social.load()
            }
        }
        .refreshable {
            store.load()
            social.load()
        }
    }

    private func homeHero(
        _ _: MobileHomeResponse
    ) -> some View {
        LegendNextSurface(
            style: .navy,
            cornerRadius: LegendNextRadius.prominentCard,
            padding: LegendNextSpacing.sm
        ) {
            HStack(
                alignment: .firstTextBaseline,
                spacing: LegendNextSpacing.xs
            ) {
                Text(greetingEyebrow.capitalized)
                    .font(LegendNextTypography.title)
                    .foregroundStyle(LegendNextColor.goldBright)
                    .lineLimit(1)

                Text(firstName)
                    .font(LegendNextTypography.title)
                    .foregroundStyle(.white)
                    .lineLimit(1)

                Spacer(minLength: 0)
            }
            .minimumScaleFactor(0.78)
            .allowsTightening(true)
            .accessibilityElement(children: .combine)
            .accessibilityLabel(
                "\(greetingEyebrow.capitalized) \(firstName)"
            )
        }
    }

    private func missionDashboard(
        _ home: MobileHomeResponse
    ) -> some View {
        VStack(
            alignment: .leading,
            spacing: LegendNextSpacing.xs
        ) {
            LegendNextSectionHeader(
                eyebrow: "Live overview",
                title: "Mission dashboard",
                detail: "The signals that matter most right now."
            )

            LazyVGrid(
                columns: [
                    GridItem(
                        .flexible(),
                        spacing: LegendNextSpacing.xs
                    ),
                    GridItem(
                        .flexible(),
                        spacing: LegendNextSpacing.xs
                    )
                ],
                spacing: LegendNextSpacing.xs
            ) {
                Button {
                    open(.messages)
                } label: {
                    LegendNextMetricTile(
                        title: "Messages",
                        value: "\(home.messaging.unreadCount)",
                        detail: "Unread conversations",
                        systemImage: "message.fill",
                        tone: home.messaging.unreadCount > 0
                            ? .information
                            : .neutral
                    )
                }
                .buttonStyle(.plain)

                if home.identity.participantType == .agent {
                    Button {
                        open(.clients)
                    } label: {
                        LegendNextMetricTile(
                            title: "Clients",
                            value: "\(home.activeClientCount)",
                            detail: "Active relationships",
                            systemImage: "person.2.fill",
                            tone: .navy
                        )
                    }
                    .buttonStyle(.plain)

                    LegendNextMetricTile(
                        title: "Open actions",
                        value: "\(home.actions.count)",
                        detail: "Items requiring attention",
                        systemImage: "checklist",
                        tone: home.actions.isEmpty
                            ? .success
                            : .warning
                    )

                    LegendNextMetricTile(
                        title: "Appointments",
                        value: "\(home.upcomingAppointments.count)",
                        detail: "Upcoming meetings",
                        systemImage: "calendar",
                        tone: .gold
                    )
                } else {
                    if let position = home.financial?.position {
                        Button {
                            open(.finance)
                        } label: {
                            LegendNextMetricTile(
                                title: "Health score",
                                value: "\(position.healthScore)",
                                detail: position.positionStatus,
                                systemImage: "heart.text.square.fill",
                                tone: healthTone(position.healthScore)
                            )
                        }
                        .buttonStyle(.plain)
                    } else {
                        Button {
                            open(.finance)
                        } label: {
                            LegendNextMetricTile(
                                title: "Financial health",
                                value: "—",
                                detail: "Complete your snapshot",
                                systemImage: "chart.line.uptrend.xyaxis",
                                tone: .neutral
                            )
                        }
                        .buttonStyle(.plain)
                    }

                    if let journey = home.journey {
                        Button {
                            open(.circles)
                        } label: {
                            LegendNextMetricTile(
                                title: "Connections",
                                value: "\(journey.connectedPeerCount)",
                                detail: "\(journey.recommendationCount) recommendations",
                                systemImage: "person.3.fill",
                                tone: .information
                            )
                        }
                        .buttonStyle(.plain)
                    } else {
                        Button {
                            open(.circles)
                        } label: {
                            LegendNextMetricTile(
                                title: "Journey",
                                value: "—",
                                detail: "Build your circle",
                                systemImage: "person.3.fill",
                                tone: .neutral
                            )
                        }
                        .buttonStyle(.plain)
                    }

                    if let subscription = home.subscription {
                        LegendNextMetricTile(
                            title: "Membership",
                            value: subscription.status,
                            detail: subscription.paymentStanding,
                            systemImage: "crown.fill",
                            tone: membershipTone(subscription)
                        )
                    } else {
                        LegendNextMetricTile(
                            title: "Appointments",
                            value: "\(home.upcomingAppointments.count)",
                            detail: "Upcoming meetings",
                            systemImage: "calendar",
                            tone: .gold
                        )
                    }
                }
            }
        }
    }

    @ViewBuilder
    private func prioritySection(
        _ home: MobileHomeResponse
    ) -> some View {
        VStack(
            alignment: .leading,
            spacing: LegendNextSpacing.xs
        ) {
            LegendNextSectionHeader(
                eyebrow: "Focus",
                title: "Priority today",
                detail: "Your highest-value next moves."
            )

            LegendNextSurface(
                style: .elevated,
                cornerRadius: LegendNextRadius.prominentCard
            ) {
                VStack(
                    alignment: .leading,
                    spacing: LegendNextSpacing.xs
                ) {
                    if home.messaging.unreadCount > 0 {
                        priorityRow(
                            title: "Respond to messages",
                            detail: "\(home.messaging.unreadCount) unread conversation\(home.messaging.unreadCount == 1 ? "" : "s")",
                            systemImage: "message.fill",
                            tone: .information
                        ) {
                            open(.messages)
                        }
                    }

                    if home.identity.participantType == .client {
                        let move = financialMove(for: home.financial)
                        priorityRow(
                            title: move.title,
                            detail: move.detail,
                            systemImage: move.systemImage,
                            tone: move.tone
                        ) {
                            open(.finance)
                        }
                    }

                    if let action = home.actions.first {
                        priorityRow(
                            title: action.title,
                            detail: actionDueDetail(action),
                            systemImage: "checkmark.circle.fill",
                            tone: priorityTone(action.priority)
                        )
                    }

                    if let appointment = home.upcomingAppointments.first {
                        priorityRow(
                            title: "Upcoming appointment",
                            detail: appointment.startUTC.formatted(
                                .dateTime
                                    .month(.abbreviated)
                                    .day()
                                    .hour()
                                    .minute()
                            ),
                            systemImage: "calendar.badge.clock",
                            tone: .gold
                        )
                    }

                    if let notification = home.notifications.first {
                        priorityRow(
                            title: notification.subject,
                            detail: notification.occurredUTC.formatted(
                                .dateTime
                                    .month(.abbreviated)
                                    .day()
                                    .hour()
                                    .minute()
                            ),
                            systemImage: "bell.badge.fill",
                            tone: .warning
                        )
                    }
                }
            }
        }
    }

    private func priorityRow(
        title: String,
        detail: String,
        systemImage: String,
        tone: LegendNextTone,
        action: (() -> Void)? = nil
    ) -> some View {
        Group {
            if let action {
                Button(action: action) {
                    priorityRowContent(
                        title: title,
                        detail: detail,
                        systemImage: systemImage,
                        tone: tone,
                        showsChevron: true
                    )
                }
                .buttonStyle(.plain)
            } else {
                priorityRowContent(
                    title: title,
                    detail: detail,
                    systemImage: systemImage,
                    tone: tone,
                    showsChevron: false
                )
            }
        }
    }

    private func priorityRowContent(
        title: String,
        detail: String,
        systemImage: String,
        tone: LegendNextTone,
        showsChevron: Bool
    ) -> some View {
        HStack(
            alignment: .center,
            spacing: LegendNextSpacing.xs
        ) {
            Image(systemName: systemImage)
                .font(
                    .system(
                        size: 16,
                        weight: .semibold
                    )
                )
                .foregroundStyle(toneColor(tone))
                .frame(width: 40, height: 40)
                .background(
                    toneColor(tone).opacity(0.10),
                    in: Circle()
                )

            VStack(
                alignment: .leading,
                spacing: LegendNextSpacing.micro
            ) {
                Text(title)
                    .font(LegendNextTypography.bodyEmphasis)
                    .foregroundStyle(LegendNextColor.textPrimary)
                    .lineLimit(2)

                Text(detail)
                    .font(LegendNextTypography.supporting)
                    .foregroundStyle(LegendNextColor.textSecondary)
                    .lineLimit(2)
            }

            Spacer(minLength: LegendNextSpacing.sm)

            if showsChevron {
                Image(systemName: "chevron.right")
                    .font(
                        .system(
                            size: 13,
                            weight: .semibold
                        )
                    )
                    .foregroundStyle(LegendNextColor.textSecondary)
            }
        }
        .contentShape(Rectangle())
    }

    private func subscriptionCard(
        _ subscription: MobileSubscriptionSummary,
        entitlement: MobileEntitlementSummary?
    ) -> some View {
        LegendNextSurface(
            style: .gold,
            cornerRadius: LegendNextRadius.prominentCard,
            padding: LegendNextSpacing.sm
        ) {
            VStack(
                alignment: .leading,
                spacing: LegendNextSpacing.xs
            ) {
                HStack(
                    alignment: .top,
                    spacing: LegendNextSpacing.xs
                ) {
                    VStack(
                        alignment: .leading,
                        spacing: LegendNextSpacing.micro
                    ) {
                        Text("MEMBERSHIP")
                            .font(LegendNextTypography.eyebrow)
                            .tracking(0.9)
                            .foregroundStyle(
                                LegendNextColor.midnight.opacity(0.68)
                            )

                        Text(subscription.status)
                            .font(LegendNextTypography.section)
                            .foregroundStyle(LegendNextColor.midnight)
                    }

                    Spacer(minLength: LegendNextSpacing.sm)

                    Image(systemName: "crown.fill")
                        .font(
                            .system(
                                size: 20,
                                weight: .semibold
                            )
                        )
                        .foregroundStyle(LegendNextColor.midnight)
                        .frame(width: 44, height: 44)
                        .background(
                            Color.white.opacity(0.28),
                            in: Circle()
                        )
                }

                HStack(
                    alignment: .top,
                    spacing: LegendNextSpacing.xs
                ) {
                    membershipValue(
                        title: "Monthly",
                        value: subscription.monthlyAmount.formatted(
                            .currency(code: subscription.currency)
                        ),
                        detail: subscription.paymentStanding
                    )

                    if let nextBilling = subscription.nextBillingDateUTC {
                        membershipValue(
                            title: subscription.cancelAtPeriodEnd
                                ? "Access through"
                                : "Next billing",
                            value: nextBilling.formatted(
                                .dateTime
                                    .month(.abbreviated)
                                    .day()
                            ),
                            detail: subscription.cancelAtPeriodEnd
                                ? "Ends after this period"
                                : "Scheduled"
                        )
                    }
                }

                if let entitlement {
                    Text(entitlement.summary)
                        .font(LegendNextTypography.supporting)
                        .foregroundStyle(
                            LegendNextColor.midnight.opacity(0.74)
                        )
                        .fixedSize(
                            horizontal: false,
                            vertical: true
                        )
                }
            }
        }
    }

    private func membershipValue(
        title: String,
        value: String,
        detail: String?
    ) -> some View {
        VStack(
            alignment: .leading,
            spacing: LegendNextSpacing.micro
        ) {
            Text(title.uppercased())
                .font(LegendNextTypography.eyebrow)
                .tracking(0.6)
                .foregroundStyle(
                    LegendNextColor.midnight.opacity(0.60)
                )

            Text(value)
                .font(LegendNextTypography.metric)
                .foregroundStyle(LegendNextColor.midnight)
                .lineLimit(1)
                .minimumScaleFactor(0.68)

            if let detail, !detail.isEmpty {
                Text(detail)
                    .font(LegendNextTypography.caption)
                    .foregroundStyle(
                        LegendNextColor.midnight.opacity(0.68)
                    )
                    .lineLimit(2)
            }
        }
        .frame(
            maxWidth: .infinity,
            alignment: .leading
        )
    }

    private func financialCard(
        _ financial: MobileFinancialSnapshotResponse
    ) -> some View {
        VStack(
            alignment: .leading,
            spacing: LegendNextSpacing.xs
        ) {
            LegendNextSectionHeader(
                eyebrow: "Intelligence",
                title: "Financial position",
                detail: financial.intelligence?.status
            )

            LegendNextSurface(
                style: .navy,
                cornerRadius: LegendNextRadius.prominentCard,
                padding: LegendNextSpacing.sm
            ) {
                VStack(
                    alignment: .leading,
                    spacing: LegendNextSpacing.xs
                ) {
                    if let position = financial.position {
                        HStack(
                            alignment: .top,
                            spacing: LegendNextSpacing.xs
                        ) {
                            VStack(
                                alignment: .leading,
                                spacing: LegendNextSpacing.micro
                            ) {
                                Text("HEALTH SCORE")
                                    .font(LegendNextTypography.eyebrow)
                                    .tracking(0.8)
                                    .foregroundStyle(
                                        LegendNextColor.goldBright
                                    )

                                Text("\(position.healthScore)")
                                    .font(LegendNextTypography.hero)
                                    .foregroundStyle(.white)
                                    .monospacedDigit()
                            }

                            Spacer(minLength: LegendNextSpacing.sm)

                            LegendNextBadge(
                                position.positionStatus,
                                tone: healthTone(position.healthScore),
                                systemImage: "heart.fill"
                            )
                        }

                        Text(position.positionSummary)
                            .font(LegendNextTypography.body)
                            .foregroundStyle(.white.opacity(0.76))
                            .fixedSize(
                                horizontal: false,
                                vertical: true
                            )

                        HStack(
                            alignment: .top,
                            spacing: LegendNextSpacing.xs
                        ) {
                            financialValue(
                                title: "Net worth",
                                value: position.netWorth.formatted(
                                    .currency(code: "USD")
                                )
                            )

                            financialValue(
                                title: "Annual cash",
                                value: position.annualLifestyleRemaining.formatted(
                                    .currency(code: "USD")
                                )
                            )
                        }
                    } else {
                        VStack(
                            alignment: .leading,
                            spacing: LegendNextSpacing.xs
                        ) {
                            Image(systemName: "chart.line.uptrend.xyaxis")
                                .font(
                                    .system(
                                        size: 24,
                                        weight: .semibold
                                    )
                                )
                                .foregroundStyle(
                                    LegendNextColor.goldBright
                                )

                            Text("No saved financial snapshot")
                                .font(LegendNextTypography.section)
                                .foregroundStyle(.white)

                            Text(
                                "Complete your financial health profile to unlock your position, trends, and next opportunities."
                            )
                            .font(LegendNextTypography.body)
                            .foregroundStyle(.white.opacity(0.74))
                            .fixedSize(
                                horizontal: false,
                                vertical: true
                            )
                        }
                    }
                }
            }

            if let intelligence = financial.intelligence,
               !intelligence.findings.isEmpty {
                LegendNextSurface(style: .elevated) {
                    VStack(
                        alignment: .leading,
                        spacing: LegendNextSpacing.xs
                    ) {
                        ForEach(
                            Array(intelligence.findings.prefix(3).enumerated()),
                            id: \.element.id
                        ) { index, finding in
                            if index > 0 {
                                LegendNextDivider()
                            }

                            HStack(
                                alignment: .top,
                                spacing: LegendNextSpacing.xs
                            ) {
                                Image(systemName: "sparkles")
                                    .font(
                                        .system(
                                            size: 14,
                                            weight: .semibold
                                        )
                                    )
                                    .foregroundStyle(
                                        LegendNextColor.gold
                                    )
                                    .frame(width: 34, height: 34)
                                    .background(
                                        LegendNextColor.gold.opacity(0.10),
                                        in: Circle()
                                    )

                                VStack(
                                    alignment: .leading,
                                    spacing: LegendNextSpacing.micro
                                ) {
                                    Text(finding.title)
                                        .font(
                                            LegendNextTypography.bodyEmphasis
                                        )
                                        .foregroundStyle(
                                            LegendNextColor.textPrimary
                                        )

                                    Text(finding.explanation)
                                        .font(
                                            LegendNextTypography.supporting
                                        )
                                        .foregroundStyle(
                                            LegendNextColor.textSecondary
                                        )
                                        .lineLimit(3)
                                }
                            }
                        }
                    }
                }
            }

            Button {
                open(.finance)
            } label: {
                Label(
                    "Open financial intelligence",
                    systemImage: "chart.line.uptrend.xyaxis"
                )
            }
            .buttonStyle(
                LegendNextButtonStyle(kind: .secondary)
            )
        }
    }

    private func financialValue(
        title: String,
        value: String
    ) -> some View {
        VStack(
            alignment: .leading,
            spacing: LegendNextSpacing.micro
        ) {
            Text(title.uppercased())
                .font(LegendNextTypography.eyebrow)
                .tracking(0.55)
                .foregroundStyle(.white.opacity(0.54))

            Text(value)
                .font(LegendNextTypography.cardTitle)
                .foregroundStyle(.white)
                .lineLimit(1)
                .minimumScaleFactor(0.62)
                .monospacedDigit()
        }
        .frame(
            maxWidth: .infinity,
            alignment: .leading
        )
    }

    private func journeyCard(
        _ journey: MobileJourneySummary
    ) -> some View {
        Button {
            open(.circles)
        } label: {
            LegendNextSurface(
                style: .elevated,
                cornerRadius: LegendNextRadius.prominentCard
            ) {
                HStack(
                    alignment: .center,
                    spacing: LegendNextSpacing.xs
                ) {
                    Image(systemName: "person.3.fill")
                        .font(
                            .system(
                                size: 20,
                                weight: .semibold
                            )
                        )
                        .foregroundStyle(LegendNextColor.gold)
                        .frame(width: 50, height: 50)
                        .background(
                            LegendNextColor.gold.opacity(0.11),
                            in: Circle()
                        )

                    VStack(
                        alignment: .leading,
                        spacing: LegendNextSpacing.micro
                    ) {
                        Text("Journey Circles")
                            .font(LegendNextTypography.cardTitle)
                            .foregroundStyle(
                                LegendNextColor.textPrimary
                            )

                        Text(
                            journey.hasProfile
                                ? "\(journey.connectedPeerCount) connections · \(journey.recommendationCount) recommendations"
                                : "Set up your profile to receive authorized connections and recommendations."
                        )
                        .font(LegendNextTypography.supporting)
                        .foregroundStyle(
                            LegendNextColor.textSecondary
                        )
                        .fixedSize(
                            horizontal: false,
                            vertical: true
                        )
                    }

                    Spacer(minLength: LegendNextSpacing.sm)

                    Image(systemName: "chevron.right")
                        .font(
                            .system(
                                size: 14,
                                weight: .semibold
                            )
                        )
                        .foregroundStyle(
                            LegendNextColor.textSecondary
                        )
                }
            }
        }
        .buttonStyle(.plain)
        .accessibilityLabel("Open Journey Circles")
    }

    private func appointmentsCard(
        _ appointments: [MobileUpcomingAppointment]
    ) -> some View {
        VStack(
            alignment: .leading,
            spacing: LegendNextSpacing.xs
        ) {
            LegendNextSectionHeader(
                eyebrow: "Calendar",
                title: "Upcoming appointments",
                detail: "Your next scheduled conversations."
            )

            LegendNextSurface(style: .elevated) {
                VStack(
                    alignment: .leading,
                    spacing: LegendNextSpacing.xs
                ) {
                    ForEach(
                        Array(appointments.prefix(4).enumerated()),
                        id: \.element.id
                    ) { index, appointment in
                        if index > 0 {
                            LegendNextDivider()
                        }

                        HStack(
                            alignment: .center,
                            spacing: LegendNextSpacing.xs
                        ) {
                            VStack(
                                alignment: .center,
                                spacing: 1
                            ) {
                                Text(
                                    appointment.startUTC.formatted(
                                        .dateTime.month(.abbreviated)
                                    )
                                    .uppercased()
                                )
                                .font(LegendNextTypography.eyebrow)
                                .foregroundStyle(
                                    LegendNextColor.gold
                                )

                                Text(
                                    appointment.startUTC.formatted(
                                        .dateTime.day()
                                    )
                                )
                                .font(LegendNextTypography.cardTitle)
                                .foregroundStyle(
                                    LegendNextColor.textPrimary
                                )
                            }
                            .frame(width: 48, height: 48)
                            .background(
                                LegendNextColor.gold.opacity(0.09),
                                in: RoundedRectangle(
                                    cornerRadius: LegendNextRadius.control,
                                    style: .continuous
                                )
                            )

                            VStack(
                                alignment: .leading,
                                spacing: LegendNextSpacing.micro
                            ) {
                                Text(
                                    appointment.startUTC.formatted(
                                        .dateTime
                                            .hour()
                                            .minute()
                                    )
                                )
                                .font(
                                    LegendNextTypography.bodyEmphasis
                                )
                                .foregroundStyle(
                                    LegendNextColor.textPrimary
                                )

                                Text("Scheduled appointment")
                                    .font(
                                        LegendNextTypography.supporting
                                    )
                                    .foregroundStyle(
                                        LegendNextColor.textSecondary
                                    )
                            }

                            Spacer(minLength: LegendNextSpacing.sm)

                            LegendNextBadge(
                                appointment.status,
                                tone: appointmentTone(
                                    appointment.status
                                )
                            )
                        }
                    }
                }
            }
        }
    }

    private func actionsCard(
        _ actions: [MobileActionItem]
    ) -> some View {
        VStack(
            alignment: .leading,
            spacing: LegendNextSpacing.xs
        ) {
            LegendNextSectionHeader(
                eyebrow: "Execution",
                title: "Open actions",
                detail: "Keep momentum on the work in front of you."
            )

            LegendNextSurface(style: .elevated) {
                VStack(
                    alignment: .leading,
                    spacing: LegendNextSpacing.xs
                ) {
                    ForEach(
                        Array(actions.prefix(5).enumerated()),
                        id: \.element.id
                    ) { index, action in
                        if index > 0 {
                            LegendNextDivider()
                        }

                        HStack(
                            alignment: .top,
                            spacing: LegendNextSpacing.xs
                        ) {
                            Image(systemName: "checkmark.circle")
                                .font(
                                    .system(
                                        size: 17,
                                        weight: .semibold
                                    )
                                )
                                .foregroundStyle(
                                    toneColor(
                                        priorityTone(action.priority)
                                    )
                                )
                                .frame(width: 38, height: 38)
                                .background(
                                    toneColor(
                                        priorityTone(action.priority)
                                    )
                                    .opacity(0.10),
                                    in: Circle()
                                )

                            VStack(
                                alignment: .leading,
                                spacing: LegendNextSpacing.micro
                            ) {
                                Text(action.title)
                                    .font(
                                        LegendNextTypography.bodyEmphasis
                                    )
                                    .foregroundStyle(
                                        LegendNextColor.textPrimary
                                    )
                                    .lineLimit(2)

                                if let dueDate = action.dueDateUTC {
                                    Text(
                                        dueDate.formatted(
                                            .dateTime
                                                .month(.abbreviated)
                                                .day()
                                                .hour()
                                                .minute()
                                        )
                                    )
                                    .font(
                                        LegendNextTypography.supporting
                                    )
                                    .foregroundStyle(
                                        LegendNextColor.textSecondary
                                    )
                                }
                            }

                            Spacer(minLength: LegendNextSpacing.sm)

                            LegendNextBadge(
                                action.priority,
                                tone: priorityTone(action.priority)
                            )
                        }
                    }
                }
            }
        }
    }

    private func notificationsCard(
        _ notifications: [MobileBillingNotification]
    ) -> some View {
        VStack(
            alignment: .leading,
            spacing: LegendNextSpacing.xs
        ) {
            LegendNextSectionHeader(
                eyebrow: "Membership",
                title: "Notices",
                detail: "Recent billing and access updates."
            )

            LegendNextSurface(style: .elevated) {
                VStack(
                    alignment: .leading,
                    spacing: LegendNextSpacing.xs
                ) {
                    ForEach(
                        Array(notifications.prefix(4).enumerated()),
                        id: \.element.id
                    ) { index, notification in
                        if index > 0 {
                            LegendNextDivider()
                        }

                        HStack(
                            alignment: .top,
                            spacing: LegendNextSpacing.xs
                        ) {
                            Image(systemName: "bell.fill")
                                .font(
                                    .system(
                                        size: 15,
                                        weight: .semibold
                                    )
                                )
                                .foregroundStyle(
                                    LegendNextColor.warning
                                )
                                .frame(width: 38, height: 38)
                                .background(
                                    LegendNextColor.warning.opacity(0.10),
                                    in: Circle()
                                )

                            VStack(
                                alignment: .leading,
                                spacing: LegendNextSpacing.micro
                            ) {
                                Text(notification.subject)
                                    .font(
                                        LegendNextTypography.bodyEmphasis
                                    )
                                    .foregroundStyle(
                                        LegendNextColor.textPrimary
                                    )
                                    .lineLimit(2)

                                Text(
                                    notification.occurredUTC.formatted(
                                        .dateTime
                                            .month(.abbreviated)
                                            .day()
                                            .hour()
                                            .minute()
                                    )
                                )
                                .font(
                                    LegendNextTypography.supporting
                                )
                                .foregroundStyle(
                                    LegendNextColor.textSecondary
                                )
                            }
                        }
                    }
                }
            }
        }
    }

    private func quickActions(
        _ home: MobileHomeResponse
    ) -> some View {
        VStack(
            alignment: .leading,
            spacing: LegendNextSpacing.xs
        ) {
            LegendNextSectionHeader(
                eyebrow: "Navigate",
                title: "Quick actions",
                detail: "Move directly into the work that matters."
            )

            LazyVGrid(
                columns: [
                    GridItem(
                        .flexible(),
                        spacing: LegendNextSpacing.xs
                    ),
                    GridItem(
                        .flexible(),
                        spacing: LegendNextSpacing.xs
                    )
                ],
                spacing: LegendNextSpacing.xs
            ) {
                quickActionButton(
                    title: "Messages",
                    systemImage: "message.fill",
                    tone: .information
                ) {
                    open(.messages)
                }

                if home.identity.participantType == .agent {
                    quickActionButton(
                        title: "Clients",
                        systemImage: "person.2.fill",
                        tone: .navy
                    ) {
                        open(.clients)
                    }

                    quickActionButton(
                        title: "Leads",
                        systemImage: "person.crop.circle.badge.plus",
                        tone: .gold
                    ) {
                        open(.leads)
                    }
                } else {
                    quickActionButton(
                        title: "Finance",
                        systemImage: "chart.line.uptrend.xyaxis",
                        tone: .success
                    ) {
                        open(.finance)
                    }

                    quickActionButton(
                        title: "Journey",
                        systemImage: "person.3.fill",
                        tone: .gold
                    ) {
                        open(.circles)
                    }
                }

                quickActionButton(
                    title: "Account",
                    systemImage: "person.crop.circle.fill",
                    tone: .neutral
                ) {
                    open(.account)
                }
            }
        }
    }

    private func quickActionButton(
        title: String,
        systemImage: String,
        tone: LegendNextTone,
        action: @escaping () -> Void
    ) -> some View {
        Button(action: action) {
            HStack(
                spacing: LegendNextSpacing.xs
            ) {
                Image(systemName: systemImage)
                    .font(
                        .system(
                            size: 16,
                            weight: .semibold
                        )
                    )
                    .foregroundStyle(toneColor(tone))
                    .frame(width: 36, height: 36)
                    .background(
                        toneColor(tone).opacity(0.10),
                        in: Circle()
                    )

                Text(title)
                    .font(LegendNextTypography.bodyEmphasis)
                    .foregroundStyle(
                        LegendNextColor.textPrimary
                    )
                    .lineLimit(1)

                Spacer(minLength: 0)
            }
            .padding(LegendNextSpacing.sm)
            .frame(
                maxWidth: .infinity,
                minHeight: LegendNextSize.prominentControlHeight,
                alignment: .leading
            )
            .background(
                LegendNextColor.surfaceElevated,
                in: RoundedRectangle(
                    cornerRadius: LegendNextRadius.control,
                    style: .continuous
                )
            )
            .overlay {
                RoundedRectangle(
                    cornerRadius: LegendNextRadius.control,
                    style: .continuous
                )
                .strokeBorder(
                    LegendNextColor.separator.opacity(0.34),
                    lineWidth: 1
                )
            }
        }
        .buttonStyle(.plain)
    }

    private var greetingEyebrow: String {
        let hour = Calendar.current.component(
            .hour,
            from: Date()
        )

        switch hour {
        case 5..<12:
            return "Good morning"
        case 12..<17:
            return "Good afternoon"
        case 17..<22:
            return "Good evening"
        default:
            return "Welcome back"
        }
    }

    private var firstName: String {
        let trimmed = currentSession.actor.displayName
            .trimmingCharacters(in: .whitespacesAndNewlines)

        guard let first = trimmed
            .split(separator: " ")
            .first else {
            return "Legend"
        }

        return String(first)
    }

    private func heroDetail(
        for home: MobileHomeResponse
    ) -> String {
        if home.identity.participantType == .agent {
            return "Lead with clarity, serve with precision, and keep every relationship moving forward."
        }

        return "Continue building the financial position, protection, and community behind your legacy."
    }

    private func hasPriorityContent(
        _ home: MobileHomeResponse
    ) -> Bool {
        if home.identity.participantType == .client {
            return true
        }

        return home.messaging.unreadCount > 0
            || !home.actions.isEmpty
            || !home.upcomingAppointments.isEmpty
            || !home.notifications.isEmpty
    }

    private struct FinancialMove {
        let title: String
        let detail: String
        let systemImage: String
        let tone: LegendNextTone
    }

    private func financialMove(
        for financial: MobileFinancialSnapshotResponse?
    ) -> FinancialMove {
        guard let financial else {
            return FinancialMove(
                title: "Open Financial Intelligence",
                detail: "Your financial snapshot is not available in this home projection.",
                systemImage: "chart.line.uptrend.xyaxis",
                tone: .information
            )
        }

        if financial.position == nil,
           financial.operatingSystem?.projection.reasonCode == "EXPENSE_LENS_STATE_NOT_FOUND" {
            return FinancialMove(
                title: "Save your financial picture",
                detail: "Complete and save your Financial Health Snapshot to begin tracking your position.",
                systemImage: "square.and.pencil",
                tone: .information
            )
        }

        if let projection = financial.operatingSystem?.projection,
           projection.status.caseInsensitiveCompare("Available") != .orderedSame {
            return FinancialMove(
                title: "Review Expense Lens",
                detail: projection.summary ?? "Your saved weekly cash-flow projection needs attention.",
                systemImage: "calendar.badge.exclamationmark",
                tone: .warning
            )
        }

        if let position = financial.position {
            return FinancialMove(
                title: "Review your financial position",
                detail: position.positionSummary,
                systemImage: "heart.text.square.fill",
                tone: healthTone(position.healthScore)
            )
        }

        return FinancialMove(
            title: "Finish your financial setup",
            detail: "Add your financial picture to make your weekly plan available.",
            systemImage: "chart.line.uptrend.xyaxis",
            tone: .information
        )
    }

    private func actionDueDetail(
        _ action: MobileActionItem
    ) -> String {
        if let dueDate = action.dueDateUTC {
            return "Due \(dueDate.formatted(.dateTime.month(.abbreviated).day().hour().minute()))"
        }

        return "\(action.priority) priority"
    }

    private func membershipTone(
        _ subscription: MobileSubscriptionSummary
    ) -> LegendNextTone {
        let combined = "\(subscription.status) \(subscription.paymentStanding)"
            .lowercased()

        if combined.contains("active")
            || combined.contains("current")
            || combined.contains("paid") {
            return .success
        }

        if combined.contains("past")
            || combined.contains("failed")
            || combined.contains("overdue")
            || combined.contains("cancel") {
            return .danger
        }

        if combined.contains("pending")
            || combined.contains("grace")
            || combined.contains("due") {
            return .warning
        }

        return .gold
    }

    private func healthTone(
        _ score: Int
    ) -> LegendNextTone {
        switch score {
        case 80...:
            return .success
        case 60..<80:
            return .information
        case 40..<60:
            return .warning
        default:
            return .danger
        }
    }

    private func priorityTone(
        _ priority: String
    ) -> LegendNextTone {
        let normalized = priority.lowercased()

        if normalized.contains("critical")
            || normalized.contains("urgent")
            || normalized.contains("high") {
            return .danger
        }

        if normalized.contains("medium")
            || normalized.contains("normal") {
            return .warning
        }

        if normalized.contains("low") {
            return .information
        }

        return .neutral
    }

    private func appointmentTone(
        _ status: String
    ) -> LegendNextTone {
        let normalized = status.lowercased()

        if normalized.contains("confirm")
            || normalized.contains("complete") {
            return .success
        }

        if normalized.contains("cancel")
            || normalized.contains("decline") {
            return .danger
        }

        if normalized.contains("pending")
            || normalized.contains("tentative") {
            return .warning
        }

        return .information
    }

    private func toneColor(
        _ tone: LegendNextTone
    ) -> Color {
        switch tone {
        case .neutral:
            return LegendNextColor.textSecondary
        case .navy:
            return LegendNextColor.royal
        case .gold:
            return LegendNextColor.gold
        case .information:
            return LegendNextColor.information
        case .success:
            return LegendNextColor.success
        case .warning:
            return LegendNextColor.warning
        case .danger:
            return LegendNextColor.danger
        }
    }

    private func open(
        _ tab: LegendAppTab
    ) {
        guard selectedTab != tab else {
            return
        }

        UISelectionFeedbackGenerator()
            .selectionChanged()

        withAnimation(LegendNextMotion.tab) {
            selectedTab = tab
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
                    .padding(LegendNextSpacing.sm)
            }
        }
        .background(LegendNextColor.canvas.ignoresSafeArea())
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
                VStack(alignment: .leading, spacing: LegendNextSpacing.xs) {
                    HStack(spacing: LegendNextSpacing.xs) {
                        LegendProfileAvatar(avatar: client.avatar, displayName: client.displayName, size: 42)
                        VStack(alignment: .leading, spacing: LegendNextSpacing.micro) {
                            Text(client.displayName).font(.subheadline.weight(.semibold)).lineLimit(1)
                            Text(client.email).font(LegendNextTypography.supporting).foregroundStyle(LegendNextColor.textSecondary).lineLimit(1)
                        }
                        Spacer(minLength: LegendNextSpacing.sm)
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
                .padding(.vertical, LegendNextSpacing.micro)
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
                    .padding(LegendNextSpacing.sm)
            }
        }
        .background(LegendNextColor.canvas.ignoresSafeArea())
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
                HStack(spacing: LegendNextSpacing.xs) {
                    Image(systemName: "person.crop.circle")
                        .font(.title2)
                        .foregroundStyle(LegendNextColor.gold)
                    VStack(alignment: .leading, spacing: LegendNextSpacing.micro) {
                        Text(lead.displayName).font(.subheadline.weight(.semibold)).lineLimit(1)
                        Text("Updated \(lead.updatedUTC, format: .dateTime.month(.abbreviated).day().hour().minute())")
                            .font(LegendNextTypography.supporting)
                            .foregroundStyle(LegendNextColor.textSecondary)
                    }
                    Spacer(minLength: LegendNextSpacing.sm)
                    LegendBadge(title: lead.crmStage, tone: .neutral)
                }
                .padding(.vertical, LegendNextSpacing.micro)
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
                        .padding(LegendNextSpacing.sm)
                }
            }
        }
        .background(LegendNextColor.canvas.ignoresSafeArea())
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
            LazyVStack(alignment: .leading, spacing: LegendNextSpacing.xs) {
                LegendHero(
                    eyebrow: "COMMUNITY",
                    title: "Journey Circles",
                    detail: dashboard.profile == nil
                        ? "Complete your approved selections in the client portal to participate."
                        : "",
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
                    VStack(alignment: .leading, spacing: LegendNextSpacing.xs) {
                        LegendNextSectionHeader(title: "Recommendations")
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
            .padding(.horizontal, LegendNextSpacing.sm)
            .padding(.vertical, LegendNextSpacing.sm)
        }
    }

    private func profileCard(_ profile: MobileJourneyProfile) -> some View {
        LegendNextSurface(style: .navy) {
            HStack(alignment: .top, spacing: LegendNextSpacing.xs) {
                LegendProfileAvatar(avatar: profile.avatar, displayName: profile.displayName, size: 52)
                VStack(alignment: .leading, spacing: LegendNextSpacing.micro) {
                    Text(profile.displayName).font(LegendNextTypography.section).foregroundStyle(.white)
                    if let introduction = profile.introduction, !introduction.isEmpty {
                        Text(introduction).font(LegendNextTypography.supporting).foregroundStyle(.white.opacity(0.78)).lineLimit(3)
                    }
                    if !profile.goals.isEmpty {
                        Text(profile.goals.joined(separator: " · ")).font(.caption).foregroundStyle(LegendNextColor.gold).lineLimit(2)
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
        VStack(alignment: .leading, spacing: LegendNextSpacing.xs) {
            LegendNextSectionHeader(title: title)
            ForEach(connections) { connection in
                LegendNextSurface {
                    HStack(spacing: LegendNextSpacing.xs) {
                        LegendProfileAvatar(avatar: connection.profile.avatar, displayName: connection.profile.displayName, size: 42)
                        VStack(alignment: .leading, spacing: LegendNextSpacing.micro) {
                            Text(connection.profile.displayName).font(.subheadline.weight(.semibold))
                            Text(connection.status).font(LegendNextTypography.supporting).foregroundStyle(LegendNextColor.textSecondary)
                        }
                        Spacer()
                        if kind == .request {
                            HStack(spacing: LegendNextSpacing.xs) {
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
        LegendNextSurface {
            HStack(alignment: .top, spacing: LegendNextSpacing.xs) {
                LegendProfileAvatar(avatar: recommendation.profile.avatar, displayName: recommendation.profile.displayName, size: 44)
                VStack(alignment: .leading, spacing: LegendNextSpacing.micro) {
                    Text(recommendation.profile.displayName).font(.subheadline.weight(.semibold))
                    Text(recommendation.explanation).font(LegendNextTypography.supporting).foregroundStyle(LegendNextColor.textSecondary).fixedSize(horizontal: false, vertical: true)
                }
                Spacer(minLength: LegendNextSpacing.xs)
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
                LazyVStack(alignment: .leading, spacing: LegendNextSpacing.xs) {
                    LegendNextSurface(style: .navy) {
                        VStack(alignment: .leading, spacing: LegendNextSpacing.xs) {
                            Text("YOUR SELECTIONS").font(.caption.weight(.bold)).foregroundStyle(LegendNextColor.gold)
                            Text("Journey Circles profile").font(LegendNextTypography.section).foregroundStyle(.white)
                            Text("Choose the connections and recommendations you want to receive.")
                                .font(LegendNextTypography.supporting)
                                .foregroundStyle(.white.opacity(0.78))
                        }
                    }

                    LegendNextSurface {
                        VStack(alignment: .leading, spacing: LegendNextSpacing.xs) {
                            LegendNextSectionHeader(title: "Participation")
                            Toggle("Confirm community participation", isOn: $consentAffirmed)
                            Toggle("Join Journey Circles", isOn: $isOptedIn)
                            Toggle("Allow recommendations", isOn: $allowSuggestions)
                            Toggle("Allow connection requests", isOn: $allowConnectionRequests)
                            Toggle("Appear to matching members", isOn: $isDiscoverable)
                        }
                        .tint(LegendNextColor.gold)
                    }

                    LegendNextSurface {
                        VStack(alignment: .leading, spacing: LegendNextSpacing.xs) {
                            LegendNextSectionHeader(title: "Introduction")
                            TextField("Share what you are building", text: $introduction, axis: .vertical)
                                .lineLimit(3...6)
                                .textInputAutocapitalization(.sentences)
                                .padding(LegendNextSpacing.sm)
                                .background(LegendNextColor.surfaceInset, in: RoundedRectangle(cornerRadius: LegendNextRadius.control, style: .continuous))
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
                .padding(.horizontal, LegendNextSpacing.sm)
                .padding(.vertical, LegendNextSpacing.sm)
            }
            .background(LegendNextColor.canvas.ignoresSafeArea())
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
        LegendNextSurface {
            VStack(alignment: .leading, spacing: LegendNextSpacing.xs) {
                LegendNextSectionHeader(title: title, detail: selections.isEmpty ? "Select all that apply" : "\(selections.count) selected")
                LazyVGrid(columns: [GridItem(.adaptive(minimum: 118), spacing: LegendNextSpacing.xs)], alignment: .leading, spacing: LegendNextSpacing.xs) {
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
    @Environment(\.colorScheme) private var colorScheme

    let currentSession: MobileSession
    @StateObject private var store: MobileFinancialStore
    @State private var detailDestination: MobileFinancialDetailDestination?

    init(
        currentSession: MobileSession,
        coordinator: MobileSessionCoordinator
    ) {
        self.currentSession = currentSession
        _store = StateObject(
            wrappedValue: coordinator.makeFinancialStore()
        )
    }

    var body: some View {
        Group {
            if currentSession.actor.identity.participantType != .client {
                LegendEmptyState(
                    title: "Financial intelligence",
                    message:
                        "Client financial intelligence is available from an authorized client mobile identity.",
                    symbolName: "chart.line.uptrend.xyaxis"
                )
            } else {
                switch store.state {
                case .idle, .loading:
                    LegendLoadingView(
                        "Loading financial intelligence…"
                    )

                case .available(let financial):
                    financialContent(financial)

                case .incomplete(let financial, let detail):
                    financialContent(
                        financial,
                        availability: .incomplete(detail)
                    )

                case .neverSaved(let financial, let detail):
                    financialContent(
                        financial,
                        availability: .neverSaved(detail)
                    )

                case .projectionUnavailable(let financial, let detail):
                    financialContent(
                        financial,
                        availability: .projectionUnavailable(detail)
                    )

                case .authenticationRequired(let failure):
                    LegendErrorCard(
                        title: failure.title,
                        message: failure.message,
                        retryTitle: "Sign in again",
                        retry: store.load
                    )
                    .padding(LegendNextSpacing.sm)

                case .retryableFailure(let failure):
                    LegendErrorCard(
                        title: failure.title,
                        message: failure.message,
                        retryTitle: "Retry",
                        retry: store.load
                    )
                    .padding(LegendNextSpacing.sm)
                }
            }
        }
        .background(
            LegendNextColor.canvas.ignoresSafeArea()
        )
        .navigationTitle("Financial intelligence")
        .navigationBarTitleDisplayMode(.inline)
        .task {
            if case .idle = store.state {
                store.load()
            }
        }
    }

    @ViewBuilder
    private func financialContent(
        _ financial: MobileFinancialSnapshotResponse,
        availability: FinancialAvailability? = nil
    ) -> some View {
        ScrollView {
            LazyVStack(
                alignment: .leading,
                spacing: LegendNextSpacing.xs
            ) {
                if let availability {
                    availabilityNotice(availability)
                }

                if let position = financial.position {
                    financialHealth(position)
                    positionMetrics(position)
                } else {
                    LegendEmptyState(
                        title: "Financial snapshot incomplete",
                        message:
                            "Your saved financial health data will appear here after it is completed in the client portal.",
                        symbolName:
                            "chart.line.uptrend.xyaxis"
                    )
                }

                lastUpdated(financial)

                financialDashboard(financial)
            }
            .padding(
                .horizontal,
                LegendNextSpacing.pageHorizontal
            )
            .padding(
                .top,
                LegendNextSpacing.sm
            )
            .padding(
                .bottom,
                LegendNextSpacing.xl
            )
        }
        .background(
            LegendNextGradient.pageWash(
                for: colorScheme
            )
            .ignoresSafeArea()
        )
        .refreshable {
            store.load()
        }
        .navigationDestination(item: $detailDestination) { destination in
            financialDetail(destination, financial: financial)
        }
    }

    @ViewBuilder
    private func financialDashboard(
        _ financial: MobileFinancialSnapshotResponse
    ) -> some View {
        if let presentation = financial.presentation,
           !presentation.prioritySections.isEmpty {
            LegendNextSurface(
                style: .navy,
                cornerRadius: LegendNextRadius.prominentCard,
                padding: LegendNextSpacing.sm
            ) {
                VStack(
                    alignment: .leading,
                    spacing: LegendNextSpacing.sm
                ) {
                    Text("PRIORITIZED VIEW")
                        .font(LegendNextTypography.eyebrow)
                        .tracking(0.7)
                        .foregroundStyle(LegendNextColor.goldBright)

                    Text("Financial Intelligence")
                        .font(LegendNextTypography.title)
                        .foregroundStyle(.white)

                    Text("Open a section to review the saved details.")
                        .font(LegendNextTypography.supporting)
                        .foregroundStyle(Color.white.opacity(0.74))
                        .lineLimit(1)
                        .minimumScaleFactor(0.78)
                        .padding(.horizontal, LegendNextSpacing.sm)
                        .padding(.vertical, LegendNextSpacing.xs)
                        .background(
                            Color.white.opacity(0.08),
                            in: Capsule()
                        )
                        .overlay {
                            Capsule()
                                .stroke(
                                    Color.white.opacity(0.10),
                                    lineWidth: 1
                                )
                        }

                    ForEach(presentation.prioritySections) { section in
                        if let destination = MobileFinancialDetailDestination(
                            rawValue: section.key
                        ) {
                            financialDashboardCard(
                                section,
                                destination: destination
                            )
                        }
                    }
                }
            }
        } else {
            operatingSystemUnavailable(
                summary:
                    "A prioritized financial view is not available from the mobile service yet."
            )
        }
    }

    private func financialDashboardCard(
        _ section: MobileFinancialPrioritySectionResponse,
        destination: MobileFinancialDetailDestination
    ) -> some View {
        Button {
            detailDestination = destination
        } label: {
            LegendNextSurface(
                style: .navy,
                cornerRadius: LegendNextRadius.prominentCard,
                padding: LegendNextSpacing.sm
            ) {
                HStack(
                    alignment: .center,
                    spacing: LegendNextSpacing.xs
                ) {
                    Image(systemName: section.systemImage)
                        .font(.system(size: 16, weight: .semibold))
                        .foregroundStyle(
                            financialSummaryTone(section.primaryMetric).color
                        )
                        .frame(width: 36, height: 36)
                        .background(
                            financialSummaryTone(section.primaryMetric).color
                                .opacity(0.16),
                            in: Circle()
                        )
                        .accessibilityHidden(true)

                    VStack(
                        alignment: .leading,
                        spacing: LegendNextSpacing.micro
                    ) {
                        HStack(
                            alignment: .firstTextBaseline,
                            spacing: LegendNextSpacing.xs
                        ) {
                            Text(section.eyebrow.uppercased())
                                .font(LegendNextTypography.eyebrow)
                                .tracking(0.7)
                                .foregroundStyle(
                                    LegendNextColor.goldBright
                                )
                                .lineLimit(1)

                            Spacer(minLength: LegendNextSpacing.micro)

                            LegendNextBadge(
                                section.status,
                                tone:
                                    financialSummaryTone(
                                        section.primaryMetric
                                    ),
                                systemImage: "circle.fill"
                            )
                        }

                        Text(section.title)
                            .font(LegendNextTypography.bodyEmphasis)
                            .foregroundStyle(.white)
                            .lineLimit(1)
                            .minimumScaleFactor(0.78)

                        HStack(
                            alignment: .firstTextBaseline,
                            spacing: LegendNextSpacing.sm
                        ) {
                            summaryMetric(section.primaryMetric)

                            if let secondary = section.secondaryMetric {
                                summaryMetric(secondary)
                            }

                            Spacer(minLength: 0)

                            Image(systemName: "chevron.right")
                                .font(.caption.weight(.bold))
                                .foregroundStyle(
                                    Color.white.opacity(0.62)
                                )
                                .accessibilityHidden(true)
                        }
                    }
                }
            }
        }
        .buttonStyle(.plain)
        .accessibilityLabel(
            "\(section.title). \(section.status). \(section.reason). Open \(destination.title)."
        )
    }


    private func financialSummaryTone(
        _ metric: MobileFinancialSummaryMetricResponse
    ) -> LegendNextTone {
        let normalizedLabel = metric.label
            .trimmingCharacters(
                in: .whitespacesAndNewlines
            )
            .lowercased()

        let kind: MobileFinancialAmountKind?

        if normalizedLabel.contains("opening cash") {
            kind = .openingCash
        } else if normalizedLabel.contains("ending cash") {
            kind = .endingCash
        } else if normalizedLabel.contains("ending debt") {
            kind = .endingDebt
        } else if normalizedLabel.contains("net worth") {
            kind = .netWorth
        } else if normalizedLabel.contains("liabilit") {
            kind = .liabilities
        } else if normalizedLabel.contains("asset") {
            kind = .assets
        } else if normalizedLabel.contains("income") ||
                    normalizedLabel.contains("cash inflow") {
            kind = .income
        } else if normalizedLabel.contains("bill") ||
                    normalizedLabel.contains("expense") ||
                    normalizedLabel.contains("spending") ||
                    normalizedLabel.contains("outflow") {
            kind = .bills
        } else if normalizedLabel.contains("debt") ||
                    normalizedLabel.contains("loan") {
            kind = .debt
        } else if normalizedLabel.contains("payoff") {
            kind = .payoffProgress
        } else {
            kind = nil
        }

        guard let kind,
              let amount = financialDecimal(
                  from: metric.displayValue
              ) else {
            return metric.semantic.tone
        }

        return MobileFinancialAmountSemantic.tone(
            for: amount,
            kind: kind
        )
    }

    private func financialDecimal(
        from displayValue: String
    ) -> Decimal? {
        let trimmed = displayValue
            .trimmingCharacters(
                in: .whitespacesAndNewlines
            )

        guard !trimmed.isEmpty else {
            return nil
        }

        let isParenthesized =
            trimmed.hasPrefix("(") &&
            trimmed.hasSuffix(")")

        let allowed = CharacterSet(
            charactersIn: "0123456789.-"
        )

        let normalized = trimmed
            .unicodeScalars
            .filter { allowed.contains($0) }
            .map(String.init)
            .joined()

        guard !normalized.isEmpty,
              normalized != "-",
              normalized != ".",
              normalized != "-.",
              var amount = Decimal(
                  string: normalized,
                  locale: Locale(identifier: "en_US_POSIX")
              ) else {
            return nil
        }

        if isParenthesized && amount > 0 {
            amount *= -1
        }

        return amount
    }

    private func summaryMetric(
        _ metric: MobileFinancialSummaryMetricResponse
    ) -> some View {
        VStack(
            alignment: .leading,
            spacing: 1
        ) {
            Text(metric.label.uppercased())
                .font(LegendNextTypography.eyebrow)
                .foregroundStyle(Color.white.opacity(0.62))
                .lineLimit(1)

            Text(metric.displayValue)
                .font(LegendNextTypography.bodyEmphasis)
                .foregroundStyle(financialSummaryTone(metric).color)
                .monospacedDigit()
                .lineLimit(1)
                .minimumScaleFactor(0.68)
        }
        .accessibilityElement(children: .combine)
    }

    @ViewBuilder
    private func financialDetail(
        _ destination: MobileFinancialDetailDestination,
        financial: MobileFinancialSnapshotResponse
    ) -> some View {
        ScrollView {
            LazyVStack(
                alignment: .leading,
                spacing: LegendNextSpacing.xs
            ) {
                if let section = financial.presentation?.prioritySections.first(
                    where: { $0.key == destination.rawValue }
                ) {
                    financialIntelligenceStatusBanner(
                        title: section.status,
                        detail: "\(section.reason) \(section.discussionPrompt)",
                        tone: financialSummaryTone(
                            section.primaryMetric
                        ),
                        systemImage: section.systemImage
                    )
                }

                switch destination {
                case .currentOutlook:
                    if let week = financial.operatingSystem?.weekAtGlance {
                        weekAtGlance(week)
                    } else {
                        operatingSystemUnavailable(
                            summary: financial.operatingSystem?.projection.summary
                                ?? "The current week projection is not available."
                        )
                    }

                case .monthlyOutlook:
                    if let month = financial.operatingSystem?.monthAtGlance {
                        monthAtGlance(month)
                    } else {
                        operatingSystemUnavailable(
                            summary: financial.operatingSystem?.projection.summary
                                ?? "The current month projection is not available."
                        )
                    }

                case .debtObligations:
                    if let month = financial.operatingSystem?.monthAtGlance {
                        if let obligation = month.largestObligation {
                            largestObligation(obligation)
                        }
                        monthAtGlance(month)
                    } else {
                        operatingSystemUnavailable(
                            summary: financial.operatingSystem?.projection.summary
                                ?? "Scheduled obligations are not available."
                        )
                    }

                case .financialPosition:
                    if let position = financial.position {
                        financialHealth(position)
                        positionMetrics(position)
                    } else {
                        LegendEmptyState(
                            title: "Financial snapshot incomplete",
                            message: "Saved balance-sheet details are not available yet.",
                            symbolName: "building.columns"
                        )
                    }

                case .upcomingActivity:
                    if financial.upcomingBills.isEmpty {
                        LegendEmptyState(
                            title: "No upcoming activity",
                            message: "No saved recurring financial items are currently scheduled.",
                            symbolName: "calendar"
                        )
                    } else {
                        upcomingBills(financial.upcomingBills)
                    }

                case .protectionDiscussion:
                    if let position = financial.position {
                        financialHealth(position)
                        positionMetrics(position)
                    } else {
                        LegendEmptyState(
                            title: "Protection information unavailable",
                            message: "Saved financial health information is not available yet.",
                            symbolName: "shield.lefthalf.filled"
                        )
                    }

                case .dataAttention:
                    operatingSystemUnavailable(
                        summary: financial.operatingSystem?.projection.summary
                            ?? "The current Expense Lens projection is not available."
                    )
                }
            }
            .padding(.horizontal, LegendNextSpacing.pageHorizontal)
            .padding(.top, LegendNextSpacing.sm)
            .padding(.bottom, LegendNextSpacing.xl)
        }
        .background(
            LegendNextGradient.pageWash(for: colorScheme)
                .ignoresSafeArea()
        )
        .navigationTitle(destination.title)
        .navigationBarTitleDisplayMode(.inline)
        .refreshable {
            store.load()
        }
    }

    private enum FinancialAvailability {
        case incomplete(String)
        case neverSaved(String)
        case projectionUnavailable(String)

        var title: String {
            switch self {
            case .incomplete:
                return "Financial setup incomplete"
            case .neverSaved:
                return "Financial picture not saved"
            case .projectionUnavailable:
                return "Expense Lens needs attention"
            }
        }

        var systemImage: String {
            switch self {
            case .incomplete:
                return "chart.line.uptrend.xyaxis"
            case .neverSaved:
                return "square.and.pencil"
            case .projectionUnavailable:
                return "exclamationmark.triangle"
            }
        }

        var tone: LegendNextTone {
            switch self {
            case .incomplete, .neverSaved:
                return .information
            case .projectionUnavailable:
                return .warning
            }
        }

        var color: Color { tone.color }

        var detail: String {
            switch self {
            case .incomplete(let detail),
                    .neverSaved(let detail),
                    .projectionUnavailable(let detail):
                return detail
            }
        }
    }

    private func availabilityNotice(
        _ availability: FinancialAvailability
    ) -> some View {
        LegendNextSurface(style: .elevated) {
            HStack(
                alignment: .top,
                spacing: LegendNextSpacing.sm
            ) {
                Image(systemName: availability.systemImage)
                    .font(.title3.weight(.semibold))
                    .foregroundStyle(availability.color)
                    .frame(width: 38, height: 38)
                    .background(
                        availability.color.opacity(0.12),
                        in: Circle()
                    )

                VStack(
                    alignment: .leading,
                    spacing: LegendNextSpacing.micro
                ) {
                    Text(availability.title)
                        .font(LegendNextTypography.bodyEmphasis)
                        .foregroundStyle(LegendNextColor.textPrimary)

                    Text(availability.detail)
                        .font(LegendNextTypography.supporting)
                        .foregroundStyle(LegendNextColor.textSecondary)
                        .fixedSize(horizontal: false, vertical: true)
                }
            }
        }
    }

    @ViewBuilder
    private func lastUpdated(
        _ financial: MobileFinancialSnapshotResponse
    ) -> some View {
        LegendNextSurface(
            style: .navy,
            cornerRadius: LegendNextRadius.control,
            padding: LegendNextSpacing.xs
        ) {
                    if let updatedUTC = financial.operatingSystem?.freshness.financeStateUpdatedUTC
                        ?? financial.position?.updatedUTC
                        ?? financial.intelligence?.lastEvaluatedUTC {
                        HStack(spacing: LegendNextSpacing.micro) {
                            Image(systemName: "clock")
                                .accessibilityHidden(true)

                            Text("Last updated")
                            Text(
                                updatedUTC,
                                format: .dateTime
                                    .month(.abbreviated)
                                    .day()
                                    .hour()
                                    .minute()
                            )
                        }
                        .font(LegendNextTypography.supporting)
                        .foregroundStyle(Color.white.opacity(0.72))
                        .frame(maxWidth: .infinity, alignment: .leading)
                    }

        }
    }



    private func financialHealth(
        _ position: MobileFinancialPosition
    ) -> some View {
        LegendNextSurface(
            style: .navy,
            cornerRadius: LegendNextRadius.prominentCard,
            padding: LegendNextSpacing.md
        ) {
            VStack(
                alignment: .leading,
                spacing: LegendNextSpacing.sm
            ) {
                Text("FINANCIAL HEALTH")
                    .font(LegendNextTypography.eyebrow)
                    .tracking(0.7)
                    .foregroundStyle(LegendNextColor.goldBright)

                Text(
                    "This score reflects your saved balance-sheet and cash-flow information."
                )
                .font(LegendNextTypography.supporting)
                .foregroundStyle(Color.white.opacity(0.72))
                .fixedSize(horizontal: false, vertical: true)

                HStack(
                    alignment: .firstTextBaseline,
                    spacing: LegendNextSpacing.sm
                ) {
                    Text("\(position.healthScore)")
                        .font(LegendNextTypography.display)
                        .foregroundStyle(.white)
                        .monospacedDigit()

                    Text("OF 100")
                        .font(LegendNextTypography.eyebrow)
                        .foregroundStyle(Color.white.opacity(0.62))

                    Spacer(minLength: LegendNextSpacing.sm)

                    LegendNextBadge(
                        position.positionStatus,
                        tone: MobileFinancialAmountSemantic.tone(
                            forStatus: position.positionStatus
                        ),
                        systemImage: "circle.fill"
                    )
                }

                LegendNextBadge(
                    "Net Worth \(compactCurrency(position.netWorth))",
                    tone: MobileFinancialAmountSemantic.tone(
                        for: position.netWorth,
                        kind: .netWorth
                    ),
                    systemImage:
                        position.netWorth >= 0
                            ? "arrow.up.right"
                            : "arrow.down.right"
                )
            }
        }
    }



    private func positionMetrics(
        _ position: MobileFinancialPosition
    ) -> some View {
        LegendNextSurface(
            style: .navy,
            cornerRadius: LegendNextRadius.prominentCard,
            padding: LegendNextSpacing.sm
        ) {
            VStack(
                alignment: .leading,
                spacing: LegendNextSpacing.sm
            ) {
                Text("FINANCIAL POSITION")
                    .font(LegendNextTypography.eyebrow)
                    .tracking(0.7)
                    .foregroundStyle(LegendNextColor.goldBright)

                Text("Balance Sheet")
                    .font(LegendNextTypography.title)
                    .foregroundStyle(.white)

                HStack(
                    alignment: .top,
                    spacing: LegendNextSpacing.xs
                ) {
                    positionMetric(
                        title: "Assets",
                        value: position.assetsTotal.formatted(
                            .currency(code: "USD")
                        ),
                        systemImage: "building.columns.fill",
                        tone: MobileFinancialAmountSemantic.tone(
                            for: position.assetsTotal,
                            kind: .assets
                        )
                    )

                    positionMetric(
                        title: "Liabilities",
                        value: position.liabilitiesTotal.formatted(
                            .currency(code: "USD")
                        ),
                        systemImage: "creditcard.fill",
                        tone: MobileFinancialAmountSemantic.tone(
                            for: position.liabilitiesTotal,
                            kind: .liabilities
                        )
                    )

                    positionMetric(
                        title: "Net Worth",
                        value: position.netWorth.formatted(
                            .currency(code: "USD")
                        ),
                        systemImage:
                            position.netWorth >= 0
                                ? "arrow.up.right"
                                : "arrow.down.right",
                        tone: MobileFinancialAmountSemantic.tone(
                            for: position.netWorth,
                            kind: .netWorth
                        )
                    )
                }
            }
        }
    }

    private func positionMetric(
        title: String,
        value: String,
        systemImage: String,
        tone: LegendNextTone
    ) -> some View {
        VStack(
            alignment: .leading,
            spacing: LegendNextSpacing.xs
        ) {
            HStack(spacing: LegendNextSpacing.micro) {
                Image(systemName: systemImage)
                    .font(.system(size: 12, weight: .semibold))
                    .foregroundStyle(tone.color)
                    .frame(width: 26, height: 26)
                    .background(
                        tone.color.opacity(0.16),
                        in: Circle()
                    )
                    .accessibilityHidden(true)

                Text(title.uppercased())
                    .font(LegendNextTypography.eyebrow)
                    .foregroundStyle(Color.white.opacity(0.68))
                    .lineLimit(1)
                    .minimumScaleFactor(0.62)
            }

            Text(value)
                .font(.system(size: 18, weight: .bold, design: .rounded))
                .foregroundStyle(tone.color)
                .monospacedDigit()
                .lineLimit(1)
                .minimumScaleFactor(0.42)
                .allowsTightening(true)
                .frame(maxWidth: .infinity, alignment: .leading)
        }
        .padding(LegendNextSpacing.xs)
        .frame(maxWidth: .infinity, minHeight: 92, alignment: .topLeading)
        .background(
            Color.white.opacity(0.055),
            in: RoundedRectangle(
                cornerRadius: LegendNextRadius.control,
                style: .continuous
            )
        )
        .overlay {
            RoundedRectangle(
                cornerRadius: LegendNextRadius.control,
                style: .continuous
            )
            .stroke(Color.white.opacity(0.10), lineWidth: 1)
        }
        .accessibilityElement(children: .combine)
    }


    private func weekAtGlance(
        _ week: MobileFinancialWeekAtGlanceResponse
    ) -> some View {
        financialIntelligenceSurface {
            VStack(
                alignment: .leading,
                spacing: LegendNextSpacing.sm
            ) {
                sectionHeading(
                    eyebrow: "CURRENT OUTLOOK",
                    title: "Week at a Glance",
                    detail:
                        "\(displayDate(week.startDate)) – \(displayDate(week.endDate))",
                    status: week.pressureStatus
                )

                LazyVGrid(
                    columns: metricColumns,
                    spacing: LegendNextSpacing.xs
                ) {
                    financialMetric(
                        title: "Opening cash",
                        value: money(week.openingCashCents),
                        symbol: "wallet.bifold.fill",
                        tone:
                            MobileFinancialAmountSemantic.tone(
                                forCents:
                                    week.openingCashCents,
                                kind: .openingCash
                            )
                    )

                    financialMetric(
                        title: "Income",
                        value: money(week.incomeCents),
                        symbol:
                            "arrow.down.left.circle.fill",
                        tone:
                            MobileFinancialAmountSemantic.tone(
                                forCents: week.incomeCents,
                                kind: .income
                            )
                    )

                    financialMetric(
                        title: "Bills",
                        value: money(
                            week.debitExpenseCents
                            + week.creditExpenseCents
                        ),
                        symbol: "doc.text.fill",
                        tone:
                            MobileFinancialAmountSemantic.tone(
                                forCents:
                                    week.debitExpenseCents
                                    + week.creditExpenseCents,
                                kind: .bills
                            )
                    )

                    financialMetric(
                        title: "Debt",
                        value: money(
                            week.requiredDebtPaymentCents
                            + week.extraDebtPaymentCents
                        ),
                        symbol: "creditcard.fill",
                        tone:
                            MobileFinancialAmountSemantic.tone(
                                forCents:
                                    week.requiredDebtPaymentCents
                                    + week.extraDebtPaymentCents,
                                kind: .debt
                            )
                    )

                    financialMetric(
                        title: "Ending cash",
                        value: money(week.endingCashCents),
                        symbol: "banknote.fill",
                        tone:
                            MobileFinancialAmountSemantic.tone(
                                forCents:
                                    week.endingCashCents,
                                kind: .endingCash
                            )
                    )

                    financialMetric(
                        title: "Ending debt",
                        value: money(week.endingDebtCents),
                        symbol:
                            "chart.line.downtrend.xyaxis",
                        tone:
                            MobileFinancialAmountSemantic.tone(
                                forCents:
                                    week.endingDebtCents,
                                kind: .endingDebt
                            )
                    )
                }

                if !week.events.isEmpty {
                    financialIntelligenceDivider

                    VStack(
                        alignment: .leading,
                        spacing: LegendNextSpacing.xs
                    ) {
                        Text("THIS WEEK'S ACTIVITY")
                            .font(
                                LegendNextTypography.eyebrow
                            )
                            .tracking(0.8)
                            .foregroundStyle(
                                LegendNextColor.goldBright
                            )

                        ForEach(week.events) { event in
                            HStack(
                                alignment: .firstTextBaseline,
                                spacing: LegendNextSpacing.xs
                            ) {
                                VStack(
                                    alignment: .leading,
                                    spacing:
                                        LegendNextSpacing.micro
                                ) {
                                    Text(event.title)
                                        .font(
                                            .subheadline.weight(
                                                .semibold
                                            )
                                        )
                                        .foregroundStyle(.white)

                                    Text(
                                        "\(displayDate(event.occursOn)) · \(event.kind)"
                                    )
                                    .font(
                                        LegendNextTypography
                                            .supporting
                                    )
                                    .foregroundStyle(
                                        Color.white.opacity(0.62)
                                    )
                                }

                                Spacer(minLength: 8)

                                Text(money(event.amountCents))
                                    .font(
                                        .subheadline.weight(.bold)
                                    )
                                    .foregroundStyle(
                                        MobileFinancialAmountSemantic
                                            .tone(
                                                forEventKind:
                                                    event.kind,
                                                amountCents:
                                                    event.amountCents
                                            )
                                            .color
                                    )
                                    .monospacedDigit()
                            }
                            .padding(
                                .vertical,
                                LegendNextSpacing.micro
                            )
                        }
                    }
                }
            }
        }
    }


    private func monthAtGlance(
        _ month: MobileFinancialMonthAtGlanceResponse
    ) -> some View {
        financialIntelligenceSurface {
            VStack(
                alignment: .leading,
                spacing: LegendNextSpacing.sm
            ) {
                sectionHeading(
                    eyebrow: "FORWARD VIEW",
                    title: "Month at a Glance",
                    detail: monthLabel(month.monthKey),
                    status: month.pressureStatus
                )

                LazyVGrid(
                    columns: metricColumns,
                    spacing: LegendNextSpacing.xs
                ) {
                    financialMetric(
                        title: "Opening cash",
                        value:
                            money(month.openingCashCents),
                        symbol: "wallet.bifold.fill",
                        tone:
                            MobileFinancialAmountSemantic.tone(
                                forCents:
                                    month.openingCashCents,
                                kind: .openingCash
                            )
                    )

                    financialMetric(
                        title: "Income",
                        value: money(month.incomeCents),
                        symbol:
                            "arrow.down.left.circle.fill",
                        tone:
                            MobileFinancialAmountSemantic.tone(
                                forCents: month.incomeCents,
                                kind: .income
                            )
                    )

                    financialMetric(
                        title: "Bills",
                        value: money(
                            month.debitExpenseCents
                            + month.creditExpenseCents
                        ),
                        symbol: "doc.text.fill",
                        tone:
                            MobileFinancialAmountSemantic.tone(
                                forCents:
                                    month.debitExpenseCents
                                    + month.creditExpenseCents,
                                kind: .bills
                            )
                    )

                    financialMetric(
                        title: "Debt",
                        value: money(
                            month.requiredDebtPaymentCents
                            + month.extraDebtPaymentCents
                        ),
                        symbol: "creditcard.fill",
                        tone:
                            MobileFinancialAmountSemantic.tone(
                                forCents:
                                    month.requiredDebtPaymentCents
                                    + month.extraDebtPaymentCents,
                                kind: .debt
                            )
                    )

                    financialMetric(
                        title: "Ending cash",
                        value: money(month.endingCashCents),
                        symbol: "banknote.fill",
                        tone:
                            MobileFinancialAmountSemantic.tone(
                                forCents:
                                    month.endingCashCents,
                                kind: .endingCash
                            )
                    )

                    financialMetric(
                        title: "Ending debt",
                        value: money(month.endingDebtCents),
                        symbol:
                            "chart.line.downtrend.xyaxis",
                        tone:
                            MobileFinancialAmountSemantic.tone(
                                forCents:
                                    month.endingDebtCents,
                                kind: .endingDebt
                            )
                    )
                }

                if month.savingsContributionCents != 0 {
                    HStack(
                        spacing: LegendNextSpacing.xs
                    ) {
                        Image(systemName: "banknote.fill")
                            .foregroundStyle(
                                LegendNextColor.goldBright
                            )

                        Text("Savings contribution")
                            .font(
                                .subheadline.weight(
                                    .semibold
                                )
                            )
                            .foregroundStyle(.white)

                        Spacer()

                        Text(
                            money(
                                month
                                    .savingsContributionCents
                            )
                        )
                        .font(
                            .subheadline.weight(.bold)
                        )
                        .foregroundStyle(
                            LegendNextColor.goldBright
                        )
                        .monospacedDigit()
                    }
                    .padding(LegendNextSpacing.sm)
                    .background(
                        Color.white.opacity(0.055),
                        in: RoundedRectangle(
                            cornerRadius:
                                LegendNextRadius.control,
                            style: .continuous
                        )
                    )
                }

                if !month.weeks.isEmpty {
                    financialIntelligenceDivider

                    VStack(
                        alignment: .leading,
                        spacing: LegendNextSpacing.xs
                    ) {
                        Text("MONTHLY ROADMAP")
                            .font(
                                LegendNextTypography.eyebrow
                            )
                            .tracking(0.8)
                            .foregroundStyle(
                                LegendNextColor.goldBright
                            )

                        ForEach(month.weeks) { week in
                            HStack(
                                spacing: LegendNextSpacing.xs
                            ) {
                                VStack(
                                    alignment: .leading,
                                    spacing:
                                        LegendNextSpacing.micro
                                ) {
                                    Text(
                                        "\(displayDate(week.startDate)) – \(displayDate(week.endDate))"
                                    )
                                    .font(
                                        .subheadline.weight(
                                            .semibold
                                        )
                                    )
                                    .foregroundStyle(.white)

                                    Text(
                                        "\(week.pressureStatus) · Outflow \(money(week.outflowCents))"
                                    )
                                    .font(
                                        LegendNextTypography
                                            .supporting
                                    )
                                    .foregroundStyle(
                                        Color.white.opacity(0.62)
                                    )
                                }

                                Spacer(minLength: 8)

                                VStack(
                                    alignment: .trailing,
                                    spacing:
                                        LegendNextSpacing.micro
                                ) {
                                    Text(
                                        money(
                                            week.endingCashCents
                                        )
                                    )
                                    .font(
                                        .subheadline.weight(.bold)
                                    )
                                    .foregroundStyle(
                                        MobileFinancialAmountSemantic
                                            .tone(
                                                forCents:
                                                    week.endingCashCents,
                                                kind: .endingCash
                                            )
                                            .color
                                    )
                                    .monospacedDigit()

                                    Text("ending cash")
                                        .font(.caption2)
                                        .foregroundStyle(
                                            Color.white.opacity(0.52)
                                        )
                                }
                            }
                            .padding(
                                .vertical,
                                LegendNextSpacing.xs
                            )
                        }
                    }
                }
            }
        }
    }


    private func largestObligation(
        _ obligation:
            MobileFinancialLargestObligationResponse
    ) -> some View {
        financialIntelligenceSurface {
            HStack(
                alignment: .center,
                spacing: LegendNextSpacing.sm
            ) {
                Image(
                    systemName:
                        "calendar.badge.exclamationmark"
                )
                .font(.title2.weight(.semibold))
                .foregroundStyle(
                    LegendNextColor.goldBright
                )
                .frame(width: 42, height: 42)
                .background(
                    LegendNextColor.gold.opacity(0.15),
                    in: Circle()
                )

                VStack(
                    alignment: .leading,
                    spacing: LegendNextSpacing.micro
                ) {
                    Text("LARGEST OBLIGATION")
                        .font(
                            LegendNextTypography.eyebrow
                        )
                        .tracking(0.8)
                        .foregroundStyle(
                            LegendNextColor.goldBright
                        )

                    Text(obligation.title)
                        .font(
                            LegendNextTypography
                                .bodyEmphasis
                        )
                        .foregroundStyle(.white)

                    Text(
                        "\(displayDate(obligation.occursOn)) · \(obligation.kind)"
                    )
                    .font(
                        LegendNextTypography.supporting
                    )
                    .foregroundStyle(
                        Color.white.opacity(0.64)
                    )
                }

                Spacer(minLength: 8)

                Text(money(obligation.amountCents))
                    .font(.title3.weight(.bold))
                    .foregroundStyle(
                        MobileFinancialAmountSemantic.tone(
                            forCents:
                                obligation.amountCents,
                            kind: .bills
                        ).color
                    )
                    .monospacedDigit()
                    .lineLimit(1)
                    .minimumScaleFactor(0.55)
            }
        }
    }



    private func operatingSystemUnavailable(
        summary: String
    ) -> some View {
        financialIntelligenceSurface {
            HStack(
                alignment: .top,
                spacing: LegendNextSpacing.sm
            ) {
                Image(
                    systemName: "chart.xyaxis.line"
                )
                .font(LegendNextTypography.section)
                .foregroundStyle(
                    LegendNextColor.goldBright
                )
                .frame(width: 42, height: 42)
                .background(
                    LegendNextColor.gold.opacity(0.15),
                    in: Circle()
                )

                VStack(
                    alignment: .leading,
                    spacing: LegendNextSpacing.xs
                ) {
                    Text("CASH-FLOW INTELLIGENCE")
                        .font(
                            LegendNextTypography.eyebrow
                        )
                        .tracking(0.8)
                        .foregroundStyle(
                            LegendNextColor.goldBright
                        )

                    Text("Projection not ready")
                        .font(
                            LegendNextTypography.title
                        )
                        .foregroundStyle(.white)

                    Text(summary)
                        .font(
                            LegendNextTypography.supporting
                        )
                        .foregroundStyle(
                            Color.white.opacity(0.66)
                        )
                        .fixedSize(
                            horizontal: false,
                            vertical: true
                        )
                }
            }
        }
    }

    private func upcomingBills(
        _ bills: [MobileUpcomingBill]
    ) -> some View {
        VStack(
            alignment: .leading,
            spacing: LegendNextSpacing.xs
        ) {
            financeSectionLabel(
                eyebrow: "UPCOMING",
                title: "Recurring Bills",
                detail:
                    "Your next scheduled obligations, ordered for quick awareness."
            )

            LegendNextSurface(
                cornerRadius: LegendNextRadius.prominentCard,
                padding: 0
            ) {
                VStack(spacing: 0) {
                    ForEach(
                        Array(bills.enumerated()),
                        id: \.element.id
                    ) { index, bill in
                        HStack(
                            spacing: LegendNextSpacing.xs
                        ) {
                            Image(
                                systemName:
                                    "calendar.badge.clock"
                            )
                            .font(.subheadline.weight(.semibold))
                            .foregroundStyle(
                                LegendNextColor.gold
                            )
                            .frame(width: 38, height: 38)
                            .background(
                                LegendNextColor.gold.opacity(
                                    0.10
                                ),
                                in: RoundedRectangle(
                                    cornerRadius:
                                        LegendNextRadius.compact,
                                    style: .continuous
                                )
                            )

                            VStack(
                                alignment: .leading,
                                spacing: LegendNextSpacing.micro
                            ) {
                                Text(bill.displayName)
                                    .font(
                                        .subheadline.weight(
                                            .semibold
                                        )
                                    )
                                    .foregroundStyle(
                                        LegendNextColor.textPrimary
                                    )

                                Text(
                                    bill.nextExpectedDateUTC,
                                    format:
                                        .dateTime
                                        .month(.abbreviated)
                                        .day()
                                )
                                .font(
                                    LegendNextTypography.supporting
                                )
                                .foregroundStyle(
                                    LegendNextColor.textSecondary
                                )
                            }

                            Spacer(minLength: 8)

                            Text(
                                bill.amount.formatted(
                                    .currency(code: "USD")
                                )
                            )
                            .font(.subheadline.weight(.bold))
                            .foregroundStyle(
                                MobileFinancialAmountSemantic.tone(
                                    for: bill.amount,
                                    kind: .bills
                                ).color
                            )
                        }
                        .padding(
                            .horizontal,
                            LegendNextSpacing.sm
                        )
                        .padding(
                            .vertical,
                            LegendNextSpacing.sm
                        )

                        if index < bills.count - 1 {
                            LegendNextDivider()
                                .padding(
                                    .leading,
                                    38 + LegendNextSpacing.sm * 2
                                )
                        }
                    }
                }
            }
        }
    }



    private func financialIntelligenceSurface<
        Content: View
    >(
        @ViewBuilder content: () -> Content
    ) -> some View {
        LegendNextSurface(
            style: .navy,
            cornerRadius: LegendNextRadius.prominentCard,
            padding: LegendNextSpacing.sm
        ) {
            content()
        }
    }

    private var financialIntelligenceDivider: some View {
        Rectangle()
            .fill(Color.white.opacity(0.10))
            .frame(height: 1)
    }

    private func financialIntelligenceStatusBanner(
        title: String,
        detail: String,
        tone: LegendNextTone,
        systemImage: String
    ) -> some View {
        HStack(
            alignment: .top,
            spacing: LegendNextSpacing.sm
        ) {
            Image(systemName: systemImage)
                .font(
                    .system(
                        size: 18,
                        weight: .semibold
                    )
                )
                .foregroundStyle(tone.color)
                .frame(width: 42, height: 42)
                .background(
                    tone.color.opacity(0.16),
                    in: Circle()
                )
                .accessibilityHidden(true)

            VStack(
                alignment: .leading,
                spacing: LegendNextSpacing.micro
            ) {
                Text(title)
                    .font(
                        LegendNextTypography.bodyEmphasis
                    )
                    .foregroundStyle(.white)

                Text(detail)
                    .font(
                        LegendNextTypography.supporting
                    )
                    .foregroundStyle(
                        Color.white.opacity(0.68)
                    )
                    .fixedSize(
                        horizontal: false,
                        vertical: true
                    )
            }
        }
        .padding(LegendNextSpacing.sm)
        .frame(
            maxWidth: .infinity,
            alignment: .leading
        )
        .background(
            LegendNextColor.navy,
            in: RoundedRectangle(
                cornerRadius:
                    LegendNextRadius.prominentCard,
                style: .continuous
            )
        )
        .overlay {
            RoundedRectangle(
                cornerRadius:
                    LegendNextRadius.prominentCard,
                style: .continuous
            )
            .stroke(
                tone.color.opacity(0.28),
                lineWidth: 1
            )
        }
    }


    private func sectionHeading(
        eyebrow: String,
        title: String,
        detail: String,
        status: String
    ) -> some View {
        VStack(
            alignment: .leading,
            spacing: LegendNextSpacing.xs
        ) {
            HStack(
                alignment: .center,
                spacing: LegendNextSpacing.xs
            ) {
                Text(eyebrow.uppercased())
                    .font(LegendNextTypography.eyebrow)
                    .tracking(0.8)
                    .foregroundStyle(
                        LegendNextColor.goldBright
                    )

                Spacer(minLength: 8)

                LegendNextBadge(
                    status,
                    tone:
                        MobileFinancialAmountSemantic
                            .tone(forStatus: status)
                )
            }

            Text(title)
                .font(LegendNextTypography.title)
                .foregroundStyle(.white)

            if !detail.isEmpty {
                Text(detail)
                    .font(
                        LegendNextTypography.supporting
                    )
                    .foregroundStyle(
                        Color.white.opacity(0.65)
                    )
            }
        }
    }



    private func financeSectionLabel(
        eyebrow: String,
        title: String,
        detail: String
    ) -> some View {
        VStack(
            alignment: .leading,
            spacing: LegendNextSpacing.xs
        ) {
            Text(eyebrow.uppercased())
                .font(LegendNextTypography.eyebrow)
                .tracking(0.8)
                .foregroundStyle(
                    LegendNextColor.goldBright
                )

            Text(title)
                .font(LegendNextTypography.title)
                .foregroundStyle(.white)

            if !detail.isEmpty {
                Text(detail)
                    .font(
                        LegendNextTypography.supporting
                    )
                    .foregroundStyle(
                        Color.white.opacity(0.65)
                    )
                    .fixedSize(
                        horizontal: false,
                        vertical: true
                    )
            }
        }
    }



    private func financialMetric(
        title: String,
        value: String,
        symbol: String = "circle.fill",
        tone: LegendNextTone = .neutral
    ) -> some View {
        VStack(
            alignment: .leading,
            spacing: LegendNextSpacing.sm
        ) {
            HStack(
                spacing: LegendNextSpacing.xs
            ) {
                Image(systemName: symbol)
                    .font(
                        .system(
                            size: 13,
                            weight: .semibold
                        )
                    )
                    .foregroundStyle(tone.color)
                    .frame(width: 30, height: 30)
                    .background(
                        tone.color.opacity(0.17),
                        in: Circle()
                    )
                    .accessibilityHidden(true)

                Text(title.uppercased())
                    .font(LegendNextTypography.eyebrow)
                    .foregroundStyle(
                        Color.white.opacity(0.66)
                    )
                    .lineLimit(1)
                    .minimumScaleFactor(0.55)
            }

            Text(value)
                .font(
                    .system(
                        size: 22,
                        weight: .bold,
                        design: .rounded
                    )
                )
                .foregroundStyle(tone.color)
                .monospacedDigit()
                .lineLimit(1)
                .minimumScaleFactor(0.38)
                .allowsTightening(true)
        }
        .padding(LegendNextSpacing.sm)
        .frame(
            maxWidth: .infinity,
            minHeight: 108,
            alignment: .topLeading
        )
        .background(
            Color.white.opacity(0.055),
            in: RoundedRectangle(
                cornerRadius:
                    LegendNextRadius.control,
                style: .continuous
            )
        )
        .overlay {
            RoundedRectangle(
                cornerRadius:
                    LegendNextRadius.control,
                style: .continuous
            )
            .stroke(
                tone.color.opacity(0.20),
                lineWidth: 1
            )
        }
        .accessibilityElement(
            children: .combine
        )
    }
    private func compactCurrency(
        _ value: Decimal
    ) -> String {
        let number = NSDecimalNumber(
            decimal: value
        ).doubleValue

        let magnitude = abs(number)
        let scaledValue: Double
        let suffix: String

        switch magnitude {
        case 1_000_000_000...:
            scaledValue = number / 1_000_000_000
            suffix = "B"

        case 1_000_000...:
            scaledValue = number / 1_000_000
            suffix = "M"

        case 1_000...:
            scaledValue = number / 1_000
            suffix = "K"

        default:
            scaledValue = number
            suffix = ""
        }

        let formatter = NumberFormatter()
        formatter.numberStyle = .currency
        formatter.currencyCode = "USD"
        formatter.locale = Locale(identifier: "en_US")
        formatter.minimumFractionDigits = 0
        formatter.maximumFractionDigits =
            abs(scaledValue) >= 100
                ? 0
                : abs(scaledValue) >= 10
                    ? 1
                    : 2

        let formattedValue =
            formatter.string(
                from: NSNumber(value: scaledValue)
            )
            ?? "$0"

        return formattedValue + suffix
    }

    private var metricColumns: [GridItem] {
        [
            GridItem(
                .flexible(),
                spacing: LegendNextSpacing.xs
            ),
            GridItem(
                .flexible(),
                spacing: LegendNextSpacing.xs
            )
        ]
    }

    private func money(_ cents: Int64) -> String {
        (Decimal(cents) / Decimal(100))
            .formatted(.currency(code: "USD"))
    }

    private func displayDate(_ value: String) -> String {
        let parts = value.split(separator: "-")

        guard parts.count == 3,
              let month = Int(parts[1]),
              let day = Int(parts[2]) else {
            return value
        }

        let symbols = Calendar.current.shortMonthSymbols

        guard month >= 1, month <= symbols.count else {
            return value
        }

        return "\(symbols[month - 1]) \(day)"
    }

    private func monthLabel(_ value: String) -> String {
        let parts = value.split(separator: "-")

        guard parts.count >= 2,
              let year = Int(parts[0]),
              let month = Int(parts[1]) else {
            return value
        }

        let symbols = Calendar.current.monthSymbols

        guard month >= 1, month <= symbols.count else {
            return value
        }

        return "\(symbols[month - 1]) \(year)"
    }
}

private enum LegendProfileContentFilter: String, CaseIterable, Identifiable {
    case posts
    case reels
    case stories

    var id: Self { self }

    var symbolName: String {
        switch self {
        case .posts:
            return "square.grid.3x3"
        case .reels:
            return "play.rectangle"
        case .stories:
            return "circle.dashed"
        }
    }

    var accessibilityTitle: String {
        switch self {
        case .posts:
            return "Moves"
        case .reels:
            return "Milestones"
        case .stories:
            return "Journey"
        }
    }

    var socialContentType: MobileSocialContentType {
        switch self {
        case .posts:
            return .post
        case .reels:
            return .reel
        case .stories:
            return .story
        }
    }
}

private struct LegendAccountView: View {
    let currentSession: MobileSession

    @ObservedObject private var coordinator: MobileSessionCoordinator
    @StateObject private var account: MobileAccountStore
    @ObservedObject private var social: MobileSocialStore

    @State private var selectedContent: LegendProfileContentFilter = .posts
    @State private var isEditing = false
    @State private var isShowingSettings = false
    @State private var isConfirmingSignOut = false
    @State private var creationRoute: LegendSocialCreationRoute?
    @State private var selectedPost: MobileSocialPost?

    private let profileColumns = [
        GridItem(.flexible(), spacing: 2),
        GridItem(.flexible(), spacing: 2),
        GridItem(.flexible(), spacing: 2)
    ]

    init(
        currentSession: MobileSession,
        coordinator: MobileSessionCoordinator,
        social: MobileSocialStore
    ) {
        self.currentSession = currentSession
        _coordinator = ObservedObject(wrappedValue: coordinator)
        _account = StateObject(
            wrappedValue: coordinator.makeAccountStore()
        )
        _social = ObservedObject(wrappedValue: social)
    }

    var body: some View {
        Group {
            switch account.state {
            case .idle, .loading:
                LegendLoadingView("Loading your profile…")

            case .loaded(let profile):
                profileContent(profile)

            case .unavailable(let failure):
                LegendErrorCard(
                    title: failure.title,
                    message: failure.message,
                    retryTitle: "Retry",
                    retry: account.load
                )
                .padding(LegendNextSpacing.sm)
            }
        }
        .background(LegendNextColor.canvas.ignoresSafeArea())
        .navigationBarTitleDisplayMode(.inline)
        .toolbar {
            ToolbarItem(placement: .principal) {
                Text("Profile")
                    .font(.headline.weight(.bold))
                    .foregroundStyle(LegendNextColor.textPrimary)
            }

            ToolbarItem(placement: .topBarTrailing) {
                Button {
                    isShowingSettings = true
                } label: {
                    Image(systemName: "line.3.horizontal")
                        .font(.body.weight(.semibold))
                        .foregroundStyle(LegendNextColor.textPrimary)
                        .frame(width: 36, height: 36)
                        .contentShape(Rectangle())
                }
                .accessibilityLabel("Open profile settings")
            }
        }
        .task {
            if case .idle = account.state {
                account.load()
            }

            if case .idle = social.state {
                social.load()
            }

            if case .idle = social.profileContentState {
                social.loadProfilePosts()
            }
        }
        .refreshable {
            account.load()
            social.load()
            social.loadProfilePosts()
        }
        .sheet(isPresented: $isEditing) {
            if case .loaded(let profile) = account.state {
                LegendAccountEditor(
                    profile: profile,
                    store: account
                )
            }
        }
        .sheet(isPresented: $isShowingSettings) {
            profileSettingsSheet
        }
        .sheet(item: $creationRoute) { _ in
            LegendSocialCreationSheet(
                route: $creationRoute,
                social: social)
        }
        .sheet(item: $selectedPost) { post in
            LegendProfilePostDetail(
                post: post,
                social: social)
        }
        .confirmationDialog(
            "Sign out of Legend?",
            isPresented: $isConfirmingSignOut,
            titleVisibility: .visible
        ) {
            Button("Sign out", role: .destructive) {
                coordinator.signOut()
            }

            Button("Cancel", role: .cancel) {}
        } message: {
            Text("You will need to securely sign in again to access your account.")
        }
        .alert(
            account.actionFailure?.title ?? "Account update unavailable",
            isPresented: Binding(
                get: { account.actionFailure != nil },
                set: {
                    if !$0 {
                        account.dismissActionFailure()
                    }
                }
            ),
            actions: {
                Button("OK", role: .cancel) {
                    account.dismissActionFailure()
                }
            },
            message: {
                Text(
                    account.actionFailure?.message
                    ?? "The account update could not be completed."
                )
            }
        )
        .alert(
            social.actionFailure?.title ?? "Move unavailable",
            isPresented: Binding(
                get: { social.actionFailure != nil },
                set: {
                    if !$0 {
                        social.dismissActionFailure()
                    }
                }
            ),
            actions: {
                Button("OK", role: .cancel) {
                    social.dismissActionFailure()
                }
            },
            message: {
                Text(
                    social.actionFailure?.message
                    ?? "The move could not be shared."
                )
            }
        )
    }

    private func profileContent(
        _ profile: MobileAccountProfile
    ) -> some View {
        ScrollView {
            LazyVStack(spacing: 0) {
                profileIdentityHeader(profile)
                    .padding(.horizontal, LegendNextSpacing.sm)
                    .padding(.top, LegendNextSpacing.sm)

                profileBiography(profile)
                    .padding(.horizontal, LegendNextSpacing.sm)
                    .padding(.top, LegendNextSpacing.sm)

                profileActions
                    .padding(.horizontal, LegendNextSpacing.sm)
                    .padding(.top, LegendNextSpacing.sm)

                profileContentSelector
                    .padding(.top, LegendNextSpacing.sm)

                profileGrid()
            }
            .padding(.bottom, 116)
        }
        .scrollIndicators(.hidden)
    }

    private func profileIdentityHeader(
        _ profile: MobileAccountProfile
    ) -> some View {
        HStack(alignment: .center, spacing: LegendNextSpacing.xs) {
            LegendProfileAvatar(
                avatar: profile.avatar,
                displayName: profile.displayName,
                size: 92
            )
            .overlay(alignment: .bottomTrailing) {
                Button {
                    isEditing = true
                } label: {
                    Image(systemName: "plus")
                        .font(.caption.weight(.black))
                        .foregroundStyle(LegendNextColor.navy)
                        .frame(width: 26, height: 26)
                        .background(LegendNextColor.gold, in: Circle())
                        .overlay {
                            Circle()
                                .stroke(
                                    LegendNextColor.canvas,
                                    lineWidth: 3
                                )
                        }
                }
                .buttonStyle(.plain)
                .accessibilityLabel("Edit profile")
            }

            HStack(spacing: LegendNextSpacing.xs) {
                profileMetric(
                    value: profileContentCount(for: .posts),
                    title: "Moves"
                )

                profileMetric(
                    value: profileContentCount(for: .reels),
                    title: "Milestones"
                )

                profileMetric(
                    value: profileContentCount(for: .stories),
                    title: "Journey"
                )
            }
            .frame(maxWidth: .infinity)
        }
    }

    private func profileBiography(
        _ profile: MobileAccountProfile
    ) -> some View {
        VStack(alignment: .leading, spacing: 4) {
            HStack(spacing: LegendNextSpacing.xs) {
                Text(profile.displayName)
                    .font(.subheadline.weight(.bold))
                    .foregroundStyle(LegendNextColor.textPrimary)

                Text(profile.participantType.rawValue)
                    .font(.caption2.weight(.bold))
                    .foregroundStyle(LegendNextColor.navy)
                    .padding(.horizontal, 8)
                    .padding(.vertical, 4)
                    .background(
                        LegendNextColor.gold.opacity(0.22),
                        in: Capsule()
                    )
            }

            if let title = normalized(profile.title),
               profile.participantType == .agent {
                Text(title)
                    .font(LegendNextTypography.supporting)
                    .foregroundStyle(LegendNextColor.textSecondary)
            }

            if let shortBio = normalized(profile.shortBio) {
                Text(shortBio)
                    .font(.subheadline)
                    .foregroundStyle(LegendNextColor.textPrimary)
                    .fixedSize(horizontal: false, vertical: true)
            }

            if let email = normalized(profile.email) {
                Text(email)
                    .font(.subheadline.weight(.semibold))
                    .foregroundStyle(LegendNextColor.navy)
                    .textSelection(.enabled)
            }
        }
        .frame(maxWidth: .infinity, alignment: .leading)
    }

    private var profileActions: some View {
        HStack(spacing: LegendNextSpacing.xs) {
            Button {
                isEditing = true
            } label: {
                Text("Edit profile")
                    .frame(maxWidth: .infinity)
            }
            .buttonStyle(LegendProfileActionButtonStyle())

            Button {
                isShowingSettings = true
            } label: {
                Text("Profile settings")
                    .frame(maxWidth: .infinity)
            }
            .buttonStyle(LegendProfileActionButtonStyle())
        }
    }

    private var profileContentSelector: some View {
        HStack(spacing: 0) {
            ForEach(LegendProfileContentFilter.allCases) { filter in
                Button {
                    withAnimation(.easeInOut(duration: 0.18)) {
                        selectedContent = filter
                    }
                } label: {
                    VStack(spacing: 10) {
                        Image(systemName: filter.symbolName)
                            .font(.body.weight(.semibold))
                            .foregroundStyle(
                                selectedContent == filter
                                ? LegendNextColor.textPrimary
                                : LegendNextColor.textSecondary
                            )

                        Rectangle()
                            .fill(
                                selectedContent == filter
                                ? LegendNextColor.navy
                                : Color.clear
                            )
                            .frame(height: 1.5)
                    }
                    .frame(maxWidth: .infinity)
                    .contentShape(Rectangle())
                }
                .buttonStyle(.plain)
                .accessibilityLabel(filter.accessibilityTitle)
                .accessibilityAddTraits(
                    selectedContent == filter
                    ? .isSelected
                    : []
                )
            }
        }
        .overlay(alignment: .bottom) {
            Rectangle()
                .fill(LegendNextColor.separator)
                .frame(height: 0.5)
        }
    }

    @ViewBuilder
    private func profileGrid() -> some View {
        switch social.profileContentState {
        case .idle, .loading:
            LazyVGrid(
                columns: profileColumns,
                spacing: 2
            ) {
                ForEach(0..<9, id: \.self) { _ in
                    Rectangle()
                        .fill(LegendNextColor.surfaceElevated)
                        .aspectRatio(1, contentMode: .fit)
                        .legendNextShimmer()
                }
            }

        case .unavailable(let failure):
            LegendErrorCard(
                title: failure.title,
                message: failure.message,
                retryTitle: "Retry",
                retry: social.loadProfilePosts
            )
            .padding(LegendNextSpacing.sm)

        case .loaded:
            let items = selectedProfileItems

            if items.isEmpty {
                profileEmptyState()
            } else {
                LazyVGrid(
                    columns: profileColumns,
                    spacing: 2
                ) {
                    ForEach(items) { post in
                        Button {
                            selectedPost = post
                        } label: {
                            LegendProfileGridTile(
                                post: post,
                                social: social)
                        }
                        .buttonStyle(.plain)
                        .accessibilityHint("Open post options")
                    }
                }
            }
        }
    }

    private func profileEmptyState() -> some View {
        VStack(spacing: LegendNextSpacing.xs) {
            Image(systemName: selectedContent.symbolName)
                .font(.system(size: 26, weight: .light))
                .foregroundStyle(LegendNextColor.navy)
                .frame(width: 72, height: 72)
                .overlay {
                    Circle()
                        .stroke(
                            LegendNextColor.navy,
                            lineWidth: 1.5
                        )
                }

            Text(emptyTitle)
                .font(.title3.weight(.bold))
                .foregroundStyle(LegendNextColor.textPrimary)

            Text(emptyMessage)
                .font(LegendNextTypography.supporting)
                .foregroundStyle(LegendNextColor.textSecondary)
                .multilineTextAlignment(.center)
                .frame(maxWidth: 290)

            Button("Make Your First Move.") {
                creationRoute = .menu
            }
            .buttonStyle(LegendButtonStyle(kind: .primary))
        }
        .frame(maxWidth: .infinity)
        .padding(.vertical, 54)
        .padding(.horizontal, LegendNextSpacing.sm)
    }

    private var profileSettingsSheet: some View {
        NavigationStack {
            List {
                Section {
                    Button {
                        isShowingSettings = false
                        isEditing = true
                    } label: {
                        Label(
                            "Edit profile",
                            systemImage: "person.crop.circle"
                        )
                    }

                    Button {
                        account.load()
                        social.load()
                        social.loadProfilePosts()
                        isShowingSettings = false
                    } label: {
                        Label(
                            "Refresh profile",
                            systemImage: "arrow.clockwise"
                        )
                    }
                } header: {
                    Text("Profile")
                }

                Section {
                    HStack {
                        Label(
                            "Secure session",
                            systemImage: "lock.shield"
                        )

                        Spacer()

                        Text("Protected")
                            .foregroundStyle(
                                LegendNextColor.textSecondary
                            )
                    }

                    HStack {
                        Label(
                            "Token storage",
                            systemImage: "key"
                        )

                        Spacer()

                        Text("iOS Keychain")
                            .foregroundStyle(
                                LegendNextColor.textSecondary
                            )
                    }
                } header: {
                    Text("Security")
                }

                Section {
                    Button(role: .destructive) {
                        isShowingSettings = false
                        isConfirmingSignOut = true
                    } label: {
                        Label(
                            "Sign out",
                            systemImage: "rectangle.portrait.and.arrow.right"
                        )
                    }
                }
            }
            .navigationTitle("Profile settings")
            .navigationBarTitleDisplayMode(.inline)
            .toolbar {
                ToolbarItem(placement: .confirmationAction) {
                    Button("Done") {
                        isShowingSettings = false
                    }
                }
            }
        }
        .presentationDetents([.medium, .large])
        .presentationDragIndicator(.visible)
    }

    private var selectedProfileItems: [MobileSocialPost] {
        switch selectedContent {
        case .posts:
            return profilePosts
        case .reels:
            return profileReels
        case .stories:
            return profileStories
        }
    }

    private var profilePosts: [MobileSocialPost] {
        profileItems(for: .post)
    }

    private var profileReels: [MobileSocialPost] {
        profileItems(for: .reel)
    }

    private var profileStories: [MobileSocialPost] {
        profileItems(for: .story)
    }

    private func profileItems(
        for type: MobileSocialContentType
    ) -> [MobileSocialPost] {
        currentProfilePosts.filter {
            $0.contentType == type.rawValue
        }
    }

    private var currentProfilePosts: [MobileSocialPost] {
        guard case .loaded(let posts) = social.profileContentState else {
            return []
        }

        return posts
    }

    private func profileContentCount(
        for filter: LegendProfileContentFilter
    ) -> Int {
        guard case .loaded(let snapshot) = social.state else {
            return profileItems(for: filter.socialContentType).count
        }

        switch filter {
        case .posts:
            return snapshot.currentProfileMetrics.postCount
        case .reels:
            return snapshot.currentProfileMetrics.videoCount
        case .stories:
            return snapshot.currentProfileMetrics.storyCount
        }
    }

    private func profileMetric(
        value: Int,
        title: String
    ) -> some View {
        VStack(spacing: 2) {
            Text(value.formatted())
                .font(.headline.weight(.bold))
                .foregroundStyle(LegendNextColor.textPrimary)
                .contentTransition(.numericText())

            Text(title)
                .font(.caption)
                .foregroundStyle(LegendNextColor.textPrimary)
        }
        .frame(minWidth: 54)
    }

    private var emptyTitle: String {
        "Your Legacy starts here"
    }

    private var emptyMessage: String {
        "Every Move you share becomes part of your journey."
    }

    private func normalized(
        _ value: String?
    ) -> String? {
        guard let normalized = value?
            .trimmingCharacters(in: .whitespacesAndNewlines),
              !normalized.isEmpty else {
            return nil
        }

        return normalized
    }
}

private struct LegendProfileActionButtonStyle: ButtonStyle {
    func makeBody(
        configuration: Configuration
    ) -> some View {
        configuration.label
            .font(.subheadline.weight(.semibold))
            .foregroundStyle(LegendNextColor.textPrimary)
            .padding(.horizontal, LegendNextSpacing.sm)
            .frame(height: 36)
            .background(
                LegendNextColor.surfaceElevated.opacity(
                    configuration.isPressed ? 0.65 : 1
                ),
                in: RoundedRectangle(
                    cornerRadius: 8,
                    style: .continuous
                )
            )
            .overlay {
                RoundedRectangle(
                    cornerRadius: 8,
                    style: .continuous
                )
                .stroke(
                    LegendNextColor.separator,
                    lineWidth: 0.75
                )
            }
    }
}

private struct LegendProfileGridTile: View {
    let post: MobileSocialPost
    @ObservedObject var social: MobileSocialStore

    var body: some View {
        ZStack {
            if let media = post.media.first(where: \.isImage) {
                LegendSocialMediaImage(
                    media: media,
                    social: social,
                    contentMode: .fill,
                    placeholderHeight: nil)
            } else {
                LinearGradient(
                    colors: [
                        LegendNextColor.navy,
                        LegendNextColor.navy.opacity(0.78)
                    ],
                    startPoint: .topLeading,
                    endPoint: .bottomTrailing
                )

                VStack(spacing: LegendNextSpacing.xs) {
                    Image(systemName: post.contentType == MobileSocialContentType.reel.rawValue
                          ? "play.rectangle.fill"
                          : "quote.bubble.fill")
                        .font(.title3.weight(.semibold))
                        .foregroundStyle(LegendNextColor.gold)

                    Text(post.body)
                        .font(.caption.weight(.semibold))
                        .foregroundStyle(.white)
                        .multilineTextAlignment(.center)
                        .lineLimit(5)
                        .padding(.horizontal, 10)
                }
                .padding(10)
            }

            if post.contentType == MobileSocialContentType.reel.rawValue {
                Image(systemName: "play.rectangle.fill")
                    .font(.body.weight(.semibold))
                    .foregroundStyle(.white)
                    .padding(8)
                    .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .topTrailing)
            }

            if post.contentType == MobileSocialContentType.story.rawValue {
                Image(systemName: "circle.dashed")
                    .font(.body.weight(.semibold))
                    .foregroundStyle(LegendNextColor.gold)
                    .padding(8)
                    .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .topTrailing)
            }
        }
        .aspectRatio(1, contentMode: .fit)
        .clipped()
        .accessibilityElement(children: .ignore)
        .accessibilityLabel(
            "\(post.contentType) by \(post.author.displayName): \(post.body)"
        )
    }
}

private struct LegendProfilePostDetail: View {
    let post: MobileSocialPost
    @ObservedObject var social: MobileSocialStore

    @Environment(\.dismiss) private var dismiss
    @State private var isEditing = false
    @State private var isConfirmingDeletion = false

    var body: some View {
        NavigationStack {
            ScrollView {
                VStack(alignment: .leading, spacing: LegendNextSpacing.md) {
                    postMedia

                    VStack(alignment: .leading, spacing: LegendNextSpacing.xs) {
                        Text(post.body)
                            .font(LegendNextTypography.body)
                            .foregroundStyle(LegendNextColor.textPrimary)
                            .fixedSize(horizontal: false, vertical: true)

                        Text(post.postedUTC, format: .dateTime.month(.abbreviated).day().year().hour().minute())
                            .font(.caption)
                            .foregroundStyle(LegendNextColor.textSecondary)

                        HStack(spacing: LegendNextSpacing.sm) {
                            Label(post.reactionCount.formatted(), systemImage: "heart")
                            Label(post.commentCount.formatted(), systemImage: "bubble.right")
                            Label(post.metrics.viewCount.formatted(), systemImage: "eye")
                        }
                        .font(.caption.weight(.semibold))
                        .foregroundStyle(LegendNextColor.textSecondary)
                    }
                    .padding(.horizontal, LegendNextSpacing.sm)
                }
                .padding(.vertical, LegendNextSpacing.sm)
            }
            .background(LegendNextColor.canvas)
            .navigationTitle("Your move")
            .navigationBarTitleDisplayMode(.inline)
            .toolbar {
                ToolbarItem(placement: .topBarTrailing) {
                    Menu {
                        Button {
                            isEditing = true
                        } label: {
                            Label("Edit caption", systemImage: "pencil")
                        }

                        Button(role: .destructive) {
                            isConfirmingDeletion = true
                        } label: {
                            Label("Delete post", systemImage: "trash")
                        }
                    } label: {
                        Image(systemName: "ellipsis")
                            .font(.body.weight(.bold))
                    }
                    .accessibilityLabel("Post options")
                }

                ToolbarItem(placement: .cancellationAction) {
                    Button("Done") {
                        dismiss()
                    }
                }
            }
        }
        .sheet(isPresented: $isEditing) {
            LegendProfilePostEditor(
                post: post,
                social: social,
                onSaved: {
                    dismiss()
                }
            )
        }
        .confirmationDialog(
            "Delete this post?",
            isPresented: $isConfirmingDeletion,
            titleVisibility: .visible
        ) {
            Button("Delete post", role: .destructive) {
                Task {
                    if await social.deletePost(postID: post.id) {
                        dismiss()
                    }
                }
            }

            Button("Cancel", role: .cancel) {}
        } message: {
            Text("This removes the post from your authorized Legend network.")
        }
    }

    @ViewBuilder
    private var postMedia: some View {
        if let image = post.media.first(where: \.isImage) {
            LegendSocialMediaImage(
                media: image,
                social: social,
                contentMode: .fit,
                placeholderHeight: 260)
                .padding(.horizontal, LegendNextSpacing.sm)
        } else if let video = post.media.first(where: \.isVideo) {
            LegendSocialMediaVideo(
                postID: post.id,
                media: video,
                music: post.music,
                social: social)
                .padding(.horizontal, LegendNextSpacing.sm)
        } else {
            LegendNextSurface {
                Image(systemName: "quote.bubble.fill")
                    .font(.system(size: 30, weight: .semibold))
                    .foregroundStyle(LegendNextColor.gold)
                    .frame(maxWidth: .infinity)
                    .padding(.vertical, LegendNextSpacing.md)
            }
            .padding(.horizontal, LegendNextSpacing.sm)
        }
    }
}

private struct LegendProfilePostEditor: View {
    let post: MobileSocialPost
    @ObservedObject var social: MobileSocialStore
    let onSaved: () -> Void

    @Environment(\.dismiss) private var dismiss
    @State private var caption: String
    @State private var isSaving = false

    init(
        post: MobileSocialPost,
        social: MobileSocialStore,
        onSaved: @escaping () -> Void) {
        self.post = post
        _social = ObservedObject(wrappedValue: social)
        self.onSaved = onSaved
        _caption = State(initialValue: post.body)
    }

    var body: some View {
        NavigationStack {
            VStack(alignment: .leading, spacing: LegendNextSpacing.sm) {
                TextEditor(text: $caption)
                    .font(LegendNextTypography.body)
                    .padding(LegendNextSpacing.sm)
                    .frame(minHeight: 180)
                    .background(
                        LegendNextColor.surfaceInset,
                        in: RoundedRectangle(
                            cornerRadius: LegendNextRadius.control,
                            style: .continuous))
                    .accessibilityLabel("Post caption")

                if post.media.isEmpty {
                    Text("A text post needs a caption.")
                        .font(.caption)
                        .foregroundStyle(LegendNextColor.textSecondary)
                }

                Spacer()
            }
            .padding(LegendNextSpacing.sm)
            .background(LegendNextColor.canvas)
            .navigationTitle("Edit post")
            .navigationBarTitleDisplayMode(.inline)
            .toolbar {
                ToolbarItem(placement: .cancellationAction) {
                    Button("Cancel") {
                        dismiss()
                    }
                }

                ToolbarItem(placement: .confirmationAction) {
                    Button("Save") {
                        isSaving = true
                        Task {
                            if await social.updatePost(postID: post.id, body: caption) {
                                dismiss()
                                onSaved()
                            }
                            isSaving = false
                        }
                    }
                    .disabled(
                        isSaving ||
                        (post.media.isEmpty && caption.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty))
                }
            }
        }
        .presentationDetents([.medium, .large])
        .presentationDragIndicator(.visible)
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
                        .foregroundStyle(LegendNextColor.textSecondary)
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
                Text(initials).font(.caption.weight(.bold)).foregroundStyle(.white).background(LegendNextColor.navy)
            }
        }
        .frame(width: size, height: size)
        .clipShape(Circle())
        .overlay { Circle().stroke(LegendNextColor.gold.opacity(0.7), lineWidth: 1) }
        .accessibilityLabel("Profile image for \(displayName)")
    }

    private var initials: String {
        let value = displayName.split(separator: " ").prefix(2).compactMap(\.first).map(String.init).joined().uppercased()
        return value.isEmpty ? "L" : value
    }
}
