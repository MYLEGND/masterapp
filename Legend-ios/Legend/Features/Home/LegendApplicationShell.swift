import SwiftUI
import UIKit

private enum LegendAppTab: String, Identifiable {
    case home
    case clients
    case leads
    case discover
    case fyp
    case messages
    case account

    var id: Self { self }

    static func available(for participantType: ParticipantType) -> [Self] {
        // Agents get Discover too. Their scope is the clients they own, resolved on
        // the server; it is not the client community directory.
        participantType == .agent
            ? [.home, .clients, .leads, .discover, .fyp, .messages, .account]
            : [.home, .discover, .fyp, .messages, .account]
    }

    var title: String {
        switch self {
        case .home: return "Home"
        case .clients: return "Clients"
        case .leads: return "Leads"
        case .discover: return "Discover"
        case .fyp: return "For You"
        case .messages: return "Messages"
        case .account: return "Account"
        }
    }

    var symbolName: String {
        switch self {
        case .home: return "house"
        case .clients: return "person.2"
        case .leads: return "person.crop.circle.badge.plus"
        case .discover: return "magnifyingglass"
        case .fyp: return "play.rectangle.on.rectangle"
        case .messages: return "message"
        case .account: return "person"
        }
    }

    var selectedSymbolName: String {
        switch self {
        case .home: return "house.fill"
        case .clients: return "person.2.fill"
        case .leads: return "person.crop.circle.badge.plus"
        case .discover: return "magnifyingglass"
        case .fyp: return "play.rectangle.on.rectangle.fill"
        case .messages: return "message.fill"
        case .account: return "person.fill"
        }
    }
}

struct LegendApplicationShell: View {
    @EnvironmentObject private var scrollChrome: LegendScrollChrome
    @Environment(\.accessibilityReduceMotion) private var reduceMotion

    let currentSession: MobileSession
    @ObservedObject private var coordinator: MobileSessionCoordinator
    @ObservedObject private var bootstrap: LegendApplicationBootstrapCoordinator
    @State private var selectedTab: LegendAppTab = .home
    @ObservedObject private var messages: MessagingStore
    @ObservedObject private var social: MobileSocialStore
    @State private var isMessageThreadActive = false
    @State private var pendingMessageConversationID: UUID?

    init(
        currentSession: MobileSession,
        coordinator: MobileSessionCoordinator,
        bootstrap: LegendApplicationBootstrapCoordinator
    ) {
        self.currentSession = currentSession
        _coordinator = ObservedObject(wrappedValue: coordinator)
        _bootstrap = ObservedObject(wrappedValue: bootstrap)
        _messages = ObservedObject(wrappedValue: bootstrap.stores.messaging)
        _social = ObservedObject(wrappedValue: bootstrap.stores.social)
    }

    var body: some View {
        // The app chrome owns a real layout row rather than overlaying an inset.
        // Primary tabs are therefore constrained to the viewport below the
        // stationary wordmark; neither their initial content nor scroll position
        // can enter the banner's frame.
        VStack(spacing: 0) {
            LegendAppBrandBar(
                showsHomeActions: selectedTab == .home,
                activityCount: homeActivityCount,
                usesDarkSurface: selectedTab == .discover)
                .padding(.bottom, LegendNextSpacing.xs)

            selectedTabContent
                // The shell already reserves the status bar and wordmark row.
                // Do not let nested NavigationStacks reserve the device top
                // safe area again inside this bounded content viewport.
                .ignoresSafeArea(.container, edges: .top)
                .frame(maxWidth: .infinity, maxHeight: .infinity)
                .clipped()
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .top)
        .background {
            (selectedTab == .discover
                ? LegendNextColor.midnight
                : LegendNextColor.canvas)
                .ignoresSafeArea()
        }
        .safeAreaInset(edge: .bottom, spacing: 0) {
            if !isMessageThreadActive {
                LegendNextTabBar(
                    selection: $selectedTab,
                    tabs: LegendAppTab.available(
                        for: currentSession.actor.identity.participantType
                    ),
                    accountAvatar: currentSession.actor.avatar,
                    accountDisplayName: currentSession.actor.displayName,
                    unreadMessageCount: unreadMessageCount,
                    alternateAccountTypes: currentSession.alternateParticipantTypes,
                    switchAccount: { participantType in
                        coordinator.switchToRole(participantType)
                    }
                )
                .opacity(scrollChrome.isBottomNavigationVisible ? 1 : 0)
                .offset(y: scrollChrome.isBottomNavigationVisible ? 0 : 84)
                .frame(height: scrollChrome.isBottomNavigationVisible ? nil : 0)
                .clipped()
                .allowsHitTesting(scrollChrome.isBottomNavigationVisible)
                .accessibilityHidden(!scrollChrome.isBottomNavigationVisible)
            }
        }
        .tint(LegendNextColor.gold)
        .animation(
            reduceMotion ? nil : LegendNextMotion.tab,
            value: scrollChrome.isBottomNavigationVisible)
        .onChange(of: selectedTab) {
            scrollChrome.reset()
        }
    }

    @ViewBuilder
    private var selectedTabContent: some View {
        switch selectedTab {
        case .home:
            NavigationStack {
                LegendHomeView(
                    currentSession: currentSession,
                    store: bootstrap.stores.home,
                    social: social,
                    bootstrap: bootstrap,
                    selectedTab: $selectedTab
                )
            }
            .task {
                bootstrap.stores.home.load()
                social.load()
            }

        case .clients:
            if let agentWorkspace = bootstrap.stores.agentWorkspace {
                NavigationStack {
                    LegendAgentClientsView(
                        store: agentWorkspace,
                        messages: messages,
                        bootstrap: bootstrap,
                        openMessages: {
                            select(.messages)
                        }
                    )
                }
                .task { agentWorkspace.loadClients() }
            } else {
                unavailableTab
            }

        case .leads:
            if let agentWorkspace = bootstrap.stores.agentWorkspace {
                NavigationStack {
                    LegendAgentLeadsView(
                        store: agentWorkspace,
                        bootstrap: bootstrap
                    )
                }
                .task { agentWorkspace.loadLeads() }
            } else {
                unavailableTab
            }

        case .discover:
            NavigationStack {
                LegendDiscoverView(
                    currentSession: currentSession,
                    store: bootstrap.stores.discovery,
                    journeyCircles: bootstrap.stores.journeyCircles,
                    social: bootstrap.stores.social
                )
            }

        case .fyp:
            NavigationStack {
                LegendForYouView(
                    currentIdentity: currentSession.actor.identity,
                    social: social
                )
            }
            .task { social.load() }

        case .messages:
            LegendMessagesTab(
                isThreadActive: $isMessageThreadActive,
                pendingConversationID: $pendingMessageConversationID,
                messages: messages,
                currentSession: currentSession,
                social: social
            )
            .task { messages.load() }

        case .account:
            NavigationStack {
                LegendAccountView(
                    currentSession: currentSession,
                    coordinator: coordinator,
                    account: bootstrap.stores.account,
                    messages: messages,
                    social: social,
                    bootstrap: bootstrap,
                    showMessages: {
                        select(.messages)
                    }
                )
            }
            .task {
                bootstrap.stores.account.load()
                social.loadProfilePosts()
            }
        }
    }

    private var unavailableTab: some View {
        NavigationStack {
            LegendNextEmptyState(
                title: "Page unavailable",
                message: "This page is not available for the current account.",
                systemImage: "exclamationmark.triangle"
            )
            .padding(LegendNextSpacing.sm)
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

    private var homeActivityCount: Int {
        guard case .loaded(let snapshot) = social.state else {
            return 0
        }

        let activitySeenKey =
            "legend.social.activity.seen." +
            currentSession.actor.identity.participantType.rawValue +
            "." +
            currentSession.actor.identity.userID

        guard let seenThroughUTC = UserDefaults.standard.object(
            forKey: activitySeenKey
        ) as? Date else {
            return snapshot.activityCount
        }

        return snapshot.activity.reduce(into: 0) { count, activity in
            if activity.occurredUTC > seenThroughUTC {
                count += 1
            }
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

/// Application-level brand chrome. The wordmark remains stationary on every
/// primary page. Only Home's creation and activity controls follow the shared
/// scroll visibility signal, so they never obscure content while reading.
private struct LegendAppBrandBar: View {
    @EnvironmentObject private var scrollChrome: LegendScrollChrome
    @Environment(\.accessibilityReduceMotion) private var reduceMotion

    let showsHomeActions: Bool
    let activityCount: Int
    let usesDarkSurface: Bool

    var body: some View {
        HStack(spacing: LegendNextSpacing.sm) {
            homeActionButton(
                systemImage: "plus",
                label: "Create a Legend update",
                action: .create)

            Spacer(minLength: LegendNextSpacing.sm)

            Text("LEGEND")
                .font(.system(size: 23, weight: .bold, design: .default))
                .tracking(4.6)
                .foregroundStyle(wordmarkColor)
                .accessibilityAddTraits(.isHeader)

            Spacer(minLength: LegendNextSpacing.sm)

            homeActionButton(
                systemImage: "heart",
                label: activityAccessibilityLabel,
                action: .activity,
                badge: activityCount)
        }
        .padding(.horizontal, LegendNextSpacing.sm)
        .padding(.vertical, LegendNextSpacing.micro)
        .background(surfaceColor)
        .animation(
            reduceMotion ? nil : LegendNextMotion.tab,
            value: scrollChrome.isBottomNavigationVisible)
    }

    @ViewBuilder
    private func homeActionButton(
        systemImage: String,
        label: String,
        action: LegendHomeChromeActionRequest.Kind,
        badge: Int = 0
    ) -> some View {
        Group {
            if showsHomeActions {
                Button {
                    scrollChrome.requestHomeAction(action)
                } label: {
                    ZStack(alignment: .topTrailing) {
                        Image(systemName: systemImage)

                        if badge > 0 {
                            Text("\(min(badge, 99))")
                                .font(.caption2.weight(.bold))
                                .foregroundStyle(.white)
                                .padding(5)
                                .background(LegendNextColor.danger, in: Circle())
                                .offset(x: 4, y: -4)
                        }
                    }
                }
                .buttonStyle(LegendNextIconButtonStyle(tone: .navy))
                .accessibilityLabel(label)
                .opacity(scrollChrome.isBottomNavigationVisible ? 1 : 0)
                .offset(y: scrollChrome.isBottomNavigationVisible ? 0 : -24)
                .allowsHitTesting(scrollChrome.isBottomNavigationVisible)
                .accessibilityHidden(!scrollChrome.isBottomNavigationVisible)
            } else {
                Color.clear
                    .frame(
                        width: LegendNextSize.minimumTapTarget,
                        height: LegendNextSize.minimumTapTarget)
                    .accessibilityHidden(true)
            }
        }
        .frame(
            width: LegendNextSize.minimumTapTarget,
            height: LegendNextSize.minimumTapTarget)
    }

    private var wordmarkColor: Color {
        usesDarkSurface ? .white : LegendNextColor.navy
    }

    private var surfaceColor: Color {
        usesDarkSurface ? LegendNextColor.midnight : LegendNextColor.canvas
    }

    private var activityAccessibilityLabel: String {
        "Open activity, \(activityCount) recent interactions"
    }
}

private struct LegendNextTabBar: View {
    @Environment(\.accessibilityReduceMotion) private var reduceMotion

    @Binding var selection: LegendAppTab
    let tabs: [LegendAppTab]
    let accountAvatar: ProfileAvatar?
    let accountDisplayName: String
    let unreadMessageCount: Int
    let alternateAccountTypes: [ParticipantType]
    let switchAccount: (ParticipantType) -> Void

    @State private var isAccountSwitcherPresented = false
    @State private var suppressNextAccountTap = false

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
        .sheet(isPresented: $isAccountSwitcherPresented) {
            LegendAccountSwitcherSheet(
                accountAvatar: accountAvatar,
                accountDisplayName: accountDisplayName,
                currentAccountType: currentAccountType,
                alternateAccountTypes: alternateAccountTypes,
                switchAccount: { participantType in
                    isAccountSwitcherPresented = false
                    switchAccount(participantType)
                }
            )
        }
    }

    @ViewBuilder
    private func tabButton(
        _ tab: LegendAppTab
    ) -> some View {
        if tab == .account,
           !alternateAccountTypes.isEmpty {
            tabButtonContent(tab)
                .onLongPressGesture(
                    minimumDuration: 0.45,
                    maximumDistance: 24
                ) {
                    suppressNextAccountTap = true
                    UIImpactFeedbackGenerator(style: .medium)
                        .impactOccurred()
                    isAccountSwitcherPresented = true
                }
        } else {
            tabButtonContent(tab)
        }
    }

    private func tabButtonContent(
        _ tab: LegendAppTab
    ) -> some View {
        Button {
            if tab == .account,
               suppressNextAccountTap {
                suppressNextAccountTap = false
                return
            }

            activate(tab)
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
        .accessibilityHint(
            tab == .account && !alternateAccountTypes.isEmpty
                ? "Tap to open your profile. Long press to switch accounts."
                : ""
        )
        .accessibilityAddTraits(
            selection == tab ? .isSelected : []
        )
    }

    private func activate(
        _ tab: LegendAppTab
    ) {
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

    private var currentAccountType: ParticipantType {
        if alternateAccountTypes.contains(.client) {
            return .agent
        }

        return .client
    }

    private var unreadBadgeText: String {
        unreadMessageCount > 99
            ? "99+"
            : "\(unreadMessageCount)"
    }
}

private struct LegendAccountSwitcherSheet: View {
    @Environment(\.dismiss) private var dismiss

    let accountAvatar: ProfileAvatar?
    let accountDisplayName: String
    let currentAccountType: ParticipantType
    let alternateAccountTypes: [ParticipantType]
    let switchAccount: (ParticipantType) -> Void

    var body: some View {
        NavigationStack {
            ZStack {
                LegendNextGradient.hero
                    .ignoresSafeArea()

                LegendScrollView(tracksNavigationChrome: false) {
                    VStack(
                        alignment: .leading,
                        spacing: LegendNextSpacing.sm
                    ) {
                        sheetHeader
                        currentAccountCard
                        availableAccounts
                        securityNotice
                    }
                    .padding(
                        .horizontal,
                        LegendNextSpacing.pageHorizontal
                    )
                    .padding(.top, LegendNextSpacing.xs)
                    .padding(.bottom, LegendNextSpacing.xl)
                }
                .scrollIndicators(.hidden)
            }
            .toolbar(.hidden, for: .navigationBar)
        }
        .tint(LegendNextColor.goldBright)
        .presentationDetents([.height(preferredHeight), .large])
        .presentationDragIndicator(.hidden)
        .presentationCornerRadius(34)
        .legendNextBrandedSheetAppearance()
    }

    private var sheetHeader: some View {
        VStack(
            alignment: .leading,
            spacing: LegendNextSpacing.sm
        ) {
            Capsule()
                .fill(Color.white.opacity(0.34))
                .frame(width: 52, height: 5)
                .frame(maxWidth: .infinity)
                .accessibilityHidden(true)

            HStack(
                alignment: .center,
                spacing: LegendNextSpacing.sm
            ) {
                Image(systemName: "person.2.badge.gearshape.fill")
                    .font(
                        .system(
                            size: 20,
                            weight: .semibold
                        )
                    )
                    .foregroundStyle(LegendNextColor.midnight)
                    .frame(width: 52, height: 52)
                    .background(
                        LegendNextColor.goldBright,
                        in: Circle()
                    )
                    .accessibilityHidden(true)

                VStack(
                    alignment: .leading,
                    spacing: LegendNextSpacing.micro
                ) {
                    Text("LEGEND IDENTITY")
                        .font(LegendNextTypography.eyebrow)
                        .tracking(1)
                        .foregroundStyle(
                            LegendNextColor.goldBright
                        )

                    Text("Switch account")
                        .font(LegendNextTypography.hero)
                        .foregroundStyle(.white)

                    Text(
                        "Move between your authorized Legend experiences without signing out."
                    )
                    .font(LegendNextTypography.supporting)
                    .foregroundStyle(
                        Color.white.opacity(0.68)
                    )
                    .fixedSize(
                        horizontal: false,
                        vertical: true
                    )
                }

                Spacer(minLength: LegendNextSpacing.xs)

                Button {
                    dismiss()
                } label: {
                    Image(systemName: "xmark")
                        .font(
                            .system(
                                size: 15,
                                weight: .bold
                            )
                        )
                        .foregroundStyle(.white)
                        .frame(width: 46, height: 46)
                        .background(
                            Color.white.opacity(0.08),
                            in: Circle()
                        )
                        .overlay {
                            Circle()
                                .strokeBorder(
                                    Color.white.opacity(0.18),
                                    lineWidth: 1
                                )
                        }
                }
                .buttonStyle(.plain)
                .accessibilityLabel("Close account switcher")
            }
        }
        .padding(LegendNextSpacing.sm)
        .background(
            Color.white.opacity(0.055),
            in: RoundedRectangle(
                cornerRadius: LegendNextRadius.prominentCard,
                style: .continuous
            )
        )
        .overlay {
            RoundedRectangle(
                cornerRadius: LegendNextRadius.prominentCard,
                style: .continuous
            )
            .strokeBorder(
                Color.white.opacity(0.14),
                lineWidth: 1
            )
        }
    }

    private var currentAccountCard: some View {
        VStack(
            alignment: .leading,
            spacing: LegendNextSpacing.xs
        ) {
            Text("CURRENT ACCOUNT")
                .font(LegendNextTypography.eyebrow)
                .tracking(0.9)
                .foregroundStyle(
                    LegendNextColor.goldBright
                )

            HStack(
                spacing: LegendNextSpacing.sm
            ) {
                LegendProfileAvatar(
                    avatar: accountAvatar,
                    displayName: accountDisplayName,
                    size: 58
                )
                .overlay {
                    Circle()
                        .strokeBorder(
                            LegendNextColor.goldBright,
                            lineWidth: 2
                        )
                }

                VStack(
                    alignment: .leading,
                    spacing: LegendNextSpacing.micro
                ) {
                    Text(accountDisplayName)
                        .font(LegendNextTypography.section)
                        .foregroundStyle(.white)
                        .lineLimit(1)

                    Label(
                        currentAccountType.accountLabel,
                        systemImage: currentAccountType.accountSystemImage
                    )
                    .font(LegendNextTypography.supporting)
                    .foregroundStyle(
                        Color.white.opacity(0.68)
                    )
                }

                Spacer(minLength: LegendNextSpacing.xs)

                Label(
                    "Active",
                    systemImage: "checkmark.circle.fill"
                )
                .font(LegendNextTypography.caption)
                .foregroundStyle(
                    LegendNextColor.success
                )
                .padding(.horizontal, 12)
                .frame(minHeight: 34)
                .background(
                    LegendNextColor.success.opacity(0.12),
                    in: Capsule()
                )
            }
        }
        .padding(LegendNextSpacing.sm)
        .background(
            Color.white.opacity(0.055),
            in: RoundedRectangle(
                cornerRadius: LegendNextRadius.prominentCard,
                style: .continuous
            )
        )
        .overlay {
            RoundedRectangle(
                cornerRadius: LegendNextRadius.prominentCard,
                style: .continuous
            )
            .strokeBorder(
                LegendNextColor.goldBright.opacity(0.30),
                lineWidth: 1
            )
        }
    }

    private var availableAccounts: some View {
        VStack(
            alignment: .leading,
            spacing: LegendNextSpacing.xs
        ) {
            LegendNextSectionHeader(
                eyebrow: "Authorized access",
                title: "Available accounts",
                detail: "Select the Legend experience you want to enter."
            )
            .foregroundStyle(.white)

            ForEach(
                alternateAccountTypes,
                id: \.self
            ) { participantType in
                accountButton(participantType)
            }
        }
    }

    private func accountButton(
        _ participantType: ParticipantType
    ) -> some View {
        Button {
            UIImpactFeedbackGenerator(style: .medium)
                .impactOccurred()
            switchAccount(participantType)
        } label: {
            HStack(
                spacing: LegendNextSpacing.sm
            ) {
                Image(
                    systemName: participantType.accountSystemImage
                )
                .font(
                    .system(
                        size: 18,
                        weight: .semibold
                    )
                )
                .foregroundStyle(LegendNextColor.midnight)
                .frame(width: 48, height: 48)
                .background(
                    LegendNextColor.goldBright,
                    in: Circle()
                )
                .accessibilityHidden(true)

                VStack(
                    alignment: .leading,
                    spacing: LegendNextSpacing.micro
                ) {
                    Text(
                        "Continue as \(participantType.accountLabel)"
                    )
                    .font(LegendNextTypography.bodyEmphasis)
                    .foregroundStyle(.white)

                    Text(participantType.accountDescription)
                        .font(LegendNextTypography.supporting)
                        .foregroundStyle(
                            Color.white.opacity(0.64)
                        )
                        .fixedSize(
                            horizontal: false,
                            vertical: true
                        )
                }

                Spacer(minLength: LegendNextSpacing.xs)

                Image(systemName: "arrow.right")
                    .font(
                        .system(
                            size: 15,
                            weight: .bold
                        )
                    )
                    .foregroundStyle(
                        LegendNextColor.goldBright
                    )
                    .frame(width: 38, height: 38)
                    .background(
                        LegendNextColor.goldBright.opacity(0.10),
                        in: Circle()
                    )
                    .accessibilityHidden(true)
            }
            .padding(LegendNextSpacing.sm)
            .frame(
                maxWidth: .infinity,
                alignment: .leading
            )
            .background(
                Color.white.opacity(0.055),
                in: RoundedRectangle(
                    cornerRadius: LegendNextRadius.prominentCard,
                    style: .continuous
                )
            )
            .overlay {
                RoundedRectangle(
                    cornerRadius: LegendNextRadius.prominentCard,
                    style: .continuous
                )
                .strokeBorder(
                    Color.white.opacity(0.14),
                    lineWidth: 1
                )
            }
        }
        .buttonStyle(.plain)
        .accessibilityLabel(
            "Switch to \(participantType.accountLabel) account"
        )
    }

    private var securityNotice: some View {
        HStack(
            alignment: .top,
            spacing: LegendNextSpacing.xs
        ) {
            Image(systemName: "lock.shield.fill")
                .font(
                    .system(
                        size: 14,
                        weight: .semibold
                    )
                )
                .foregroundStyle(
                    LegendNextColor.goldBright
                )
                .accessibilityHidden(true)

            Text(
                "Your secure session remains active. Legend validates the selected account before loading its data."
            )
            .font(LegendNextTypography.caption)
            .foregroundStyle(
                Color.white.opacity(0.56)
            )
            .fixedSize(
                horizontal: false,
                vertical: true
            )
        }
        .padding(.horizontal, LegendNextSpacing.xs)
    }

    private var preferredHeight: CGFloat {
        alternateAccountTypes.count > 1
            ? 690
            : 610
    }
}

private extension ParticipantType {
    var accountLabel: String {
        switch self {
        case .agent: "Agent"
        case .client: "Member"
        }
    }

    var accountSystemImage: String {
        switch self {
        case .agent: "briefcase.fill"
        case .client: "person.fill"
        }
    }

    var accountDescription: String {
        switch self {
        case .agent:
            return "Manage clients, leads, operations, and your professional workspace."
        case .client:
            return "Open your personal financial, protection, and community experience."
        }
    }
}

private struct DailyScriptureSheet: View {
    let scripture: MobileDailyScripture

    @Environment(\.dismiss) private var dismiss
    @Environment(\.colorScheme) private var colorScheme

    var body: some View {
        NavigationStack {
            ZStack {
                LegendNextCanvas()

                LegendScrollView(tracksNavigationChrome: false) {
                    VStack(
                        alignment: .leading,
                        spacing: LegendNextSpacing.sm
                    ) {
                        scriptureHero

                        verseCard

                        reflectionFooter
                    }
                    .padding(.horizontal, LegendNextSpacing.sm)
                    .padding(.top, LegendNextSpacing.sm)
                    .padding(.bottom, LegendNextSpacing.xl)
                }
                .scrollIndicators(.hidden)
            }
            .navigationTitle("God Above All")
            .navigationBarTitleDisplayMode(.inline)
            .toolbarBackground(
                LegendNextColor.midnight,
                for: .navigationBar
            )
            .toolbarBackground(
                .visible,
                for: .navigationBar
            )
            .toolbar {
                ToolbarItem(placement: .topBarTrailing) {
                    Button {
                        dismiss()
                    } label: {
                        Text("Done")
                            .font(LegendNextTypography.bodyEmphasis)
                            .foregroundStyle(LegendNextColor.midnight)
                            .padding(.horizontal, 18)
                            .frame(minHeight: 44)
                            .background(
                                LegendNextColor.goldBright,
                                in: Capsule()
                            )
                    }
                    .buttonStyle(.plain)
                    .accessibilityLabel("Close scripture")
                }
            }
        }
        .tint(LegendNextColor.gold)
        .legendNextBrandedSheetAppearance()
        .presentationCornerRadius(32)
    }

    private var scriptureHero: some View {
        LegendNextSurface(
            style: .navy,
            cornerRadius: LegendNextRadius.prominentCard,
            padding: LegendNextSpacing.sm
        ) {
            VStack(
                alignment: .leading,
                spacing: LegendNextSpacing.xs
            ) {
                HStack(
                    alignment: .center,
                    spacing: LegendNextSpacing.xs
                ) {
                    Image(systemName: "book.closed.fill")
                        .font(
                            .system(
                                size: 17,
                                weight: .semibold
                            )
                        )
                        .foregroundStyle(LegendNextColor.midnight)
                        .frame(width: 40, height: 40)
                        .background(
                            LegendNextColor.goldBright,
                            in: Circle()
                        )

                    VStack(
                        alignment: .leading,
                        spacing: LegendNextSpacing.micro
                    ) {
                        Text("LIFE HAC")
                            .font(LegendNextTypography.eyebrow)
                            .tracking(1.1)
                            .foregroundStyle(
                                LegendNextColor.goldBright
                            )

                        Text("GOD ABOVE ALL")
                            .font(LegendNextTypography.supporting)
                            .foregroundStyle(
                                Color.white.opacity(0.70)
                            )
                    }

                    Spacer(minLength: LegendNextSpacing.sm)

                    Image(systemName: "cross.fill")
                        .font(
                            .system(
                                size: 18,
                                weight: .semibold
                            )
                        )
                        .foregroundStyle(
                            LegendNextColor.goldBright.opacity(0.90)
                        )
                }

                LegendNextDivider()
                    .opacity(0.35)

                Text(scripture.reference)
                    .font(LegendNextTypography.hero)
                    .foregroundStyle(.white)
                    .lineLimit(2)
                    .minimumScaleFactor(0.72)

                HStack(
                    spacing: LegendNextSpacing.micro
                ) {
                    Text(scripture.translation.uppercased())
                        .font(LegendNextTypography.eyebrow)
                        .tracking(0.8)
                        .foregroundStyle(
                            LegendNextColor.goldBright
                        )

                    Text("•")

                    Text("DAILY SCRIPTURE")
                        .font(LegendNextTypography.eyebrow)
                        .tracking(0.8)
                }
                .foregroundStyle(Color.white.opacity(0.62))
            }
        }
    }

    private var verseCard: some View {
        LegendNextSurface(
            style: .elevated,
            cornerRadius: LegendNextRadius.prominentCard,
            padding: LegendNextSpacing.sm
        ) {
            VStack(
                alignment: .leading,
                spacing: 0
            ) {
                HStack(
                    alignment: .center,
                    spacing: LegendNextSpacing.xs
                ) {
                    VStack(
                        alignment: .leading,
                        spacing: LegendNextSpacing.micro
                    ) {
                        Text("THE WORD")
                            .font(LegendNextTypography.eyebrow)
                            .tracking(1)
                            .foregroundStyle(LegendNextColor.gold)

                        Text("Read slowly. Reflect deeply.")
                            .font(LegendNextTypography.supporting)
                            .foregroundStyle(
                                LegendNextColor.textSecondary
                            )
                    }

                    Spacer(minLength: LegendNextSpacing.sm)

                    Image(systemName: "sparkles")
                        .font(
                            .system(
                                size: 17,
                                weight: .semibold
                            )
                        )
                        .foregroundStyle(LegendNextColor.gold)
                }
                .padding(.bottom, LegendNextSpacing.sm)

                ForEach(
                    Array(scripture.verses.enumerated()),
                    id: \.offset
                ) { index, verse in
                    if index > 0 {
                        LegendNextDivider()
                            .padding(.vertical, LegendNextSpacing.sm)
                    }

                    verseRow(
                        number: index + 1,
                        text: verse
                    )
                }
            }
        }
    }

    private func verseRow(
        number: Int,
        text: String
    ) -> some View {
        HStack(
            alignment: .top,
            spacing: LegendNextSpacing.xs
        ) {
            Text("\(number)")
                .font(
                    .system(
                        size: 13,
                        weight: .bold,
                        design: .rounded
                    )
                )
                .foregroundStyle(LegendNextColor.midnight)
                .frame(width: 30, height: 30)
                .background(
                    LegendNextColor.goldBright,
                    in: Circle()
                )
                .accessibilityHidden(true)

            Text(text)
                .font(LegendNextTypography.body)
                .foregroundStyle(LegendNextColor.textPrimary)
                .lineSpacing(5)
                .fixedSize(
                    horizontal: false,
                    vertical: true
                )
                .frame(
                    maxWidth: .infinity,
                    alignment: .leading
                )
                .accessibilityLabel("Verse \(number). \(text)")
        }
    }

    private var reflectionFooter: some View {
        HStack(
            alignment: .top,
            spacing: LegendNextSpacing.xs
        ) {
            Image(systemName: "sun.max.fill")
                .font(
                    .system(
                        size: 15,
                        weight: .semibold
                    )
                )
                .foregroundStyle(LegendNextColor.gold)
                .frame(width: 34, height: 34)
                .background(
                    LegendNextColor.gold.opacity(0.10),
                    in: Circle()
                )

            VStack(
                alignment: .leading,
                spacing: LegendNextSpacing.micro
            ) {
                Text("CARRY IT WITH YOU")
                    .font(LegendNextTypography.eyebrow)
                    .tracking(0.8)
                    .foregroundStyle(LegendNextColor.gold)

                Text(
                    "Let today’s scripture shape your decisions, your discipline, and the legacy you are building."
                )
                .font(LegendNextTypography.supporting)
                .foregroundStyle(LegendNextColor.textSecondary)
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
            LegendNextColor.gold.opacity(
                colorScheme == .dark ? 0.12 : 0.07
            ),
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
                LegendNextColor.gold.opacity(0.20),
                lineWidth: 1
            )
        }
    }
}

extension MobileDailyScripture: Identifiable {
    var id: String { date }
}

private struct LegendMessagesTab: View {
    @Binding var isThreadActive: Bool
    @Binding var pendingConversationID: UUID?
    @ObservedObject var messages: MessagingStore
    let currentSession: MobileSession
    @ObservedObject var social: MobileSocialStore
    @State private var navigationPath: [UUID] = []

    var body: some View {
        NavigationStack(
            path: Binding(
                get: {
                    navigationPath
                },
                set: { updatedPath in
                    navigationPath = updatedPath
                    isThreadActive = !updatedPath.isEmpty
                }
            )
        ) {
            MessagingHomeView(
                store: messages,
                openConversation: { conversationID in
                    navigationPath = [conversationID]
                    isThreadActive = true
                })
                .navigationDestination(for: UUID.self) { conversationID in
                    ConversationThreadView(
                        store: messages,
                        conversationID: conversationID,
                        currentIdentity: currentSession.actor.identity,
                        social: social)
                }
        }
        .onChange(of: pendingConversationID) { _, conversationID in
            guard let conversationID else { return }
            navigationPath = [conversationID]
            isThreadActive = true
            pendingConversationID = nil
        }
    }
}

private struct LegendHomeView: View {
    let currentSession: MobileSession

    @Binding private var selectedTab: LegendAppTab
    @ObservedObject private var store: MobileHomeStore
    @ObservedObject private var social: MobileSocialStore
    @ObservedObject private var bootstrap: LegendApplicationBootstrapCoordinator
    @State private var presentedScripture: MobileDailyScripture? = nil

    init(
        currentSession: MobileSession,
        store: MobileHomeStore,
        social: MobileSocialStore,
        bootstrap: LegendApplicationBootstrapCoordinator,
        selectedTab: Binding<LegendAppTab>
    ) {
        self.currentSession = currentSession
        _selectedTab = selectedTab
        _store = ObservedObject(wrappedValue: store)
        _social = ObservedObject(wrappedValue: social)
        _bootstrap = ObservedObject(wrappedValue: bootstrap)
        _presentedScripture = State(initialValue: nil)
    }

    var body: some View {
        Group {
            switch store.state {
            case .idle, .loading:
                LegendScreenSkeleton(accessibilityMessage: "Preparing your Legend home") {
                    LegendHomeSkeleton()
                }

            case .loaded(let home):
                homeContent(home)

            case .unavailable(let failure):
                LegendNextErrorState(
                    title: failure.title,
                    message: failure.message,
                    retryTitle: "Retry",
                    retry: {
                        Task {
                            await bootstrap.refreshHome()
                        }
                    }
                )
                .padding(LegendNextSpacing.sm)
            }
        }
        .legendNextPageBackground()
        .sheet(item: $presentedScripture) { scripture in
            DailyScriptureSheet(scripture: scripture)
        }
    }

    private func homeContent(
        _ home: MobileHomeResponse
    ) -> some View {
        LegendScrollView {
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
                        open(.discover)
                    },
                    refreshSocial: {
                        await bootstrap.refreshSocial()
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
        .refreshable {
            await bootstrap.refreshHome()
        }
    }

    private func homeHero(
        _ home: MobileHomeResponse
    ) -> some View {
        LegendNextSurface(
            style: .navy,
            cornerRadius: LegendNextRadius.prominentCard,
            padding: LegendNextSpacing.intermediate
        ) {
            VStack(alignment: .leading, spacing: LegendNextSpacing.sm) {
                HStack(spacing: LegendNextSpacing.xs) {
                    Capsule()
                        .fill(LegendNextGradient.gold)
                        .frame(width: 22, height: 3)

                    Text("TODAY'S WORD")
                        .font(LegendNextTypography.eyebrow)
                        .tracking(1.05)
                        .foregroundStyle(LegendNextColor.goldBright)

                    Spacer()

                    Image(systemName: "sparkles")
                        .font(.caption.weight(.semibold))
                        .foregroundStyle(.white.opacity(0.66))
                }

                HStack(alignment: .firstTextBaseline, spacing: LegendNextSpacing.xs) {
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

                Button {
                    presentedScripture = home.dailyScripture
                } label: {
                    Text("\(home.dailyScripture.reference) — \(home.dailyScripture.text)")
                        .font(.subheadline)
                        .foregroundStyle(.white.opacity(0.88))
                        .multilineTextAlignment(.leading)
                        .lineLimit(2)
                        .truncationMode(.tail)
                        .frame(maxWidth: .infinity, alignment: .leading)
                }
                .buttonStyle(.plain)
                .accessibilityLabel("Verse of the day, \(home.dailyScripture.reference). Double tap to read the full passage.")
            }
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
                            detail: "Connections",
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
                    if let journey = home.journey {
                        Button {
                            open(.discover)
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
                            open(.discover)
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

    private func journeyCard(
        _ journey: MobileJourneySummary
    ) -> some View {
        Button {
            open(.discover)
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
                        Text("Discover")
                            .font(LegendNextTypography.cardTitle)
                            .foregroundStyle(
                                LegendNextColor.textPrimary
                            )

                        Text(
                            journey.hasProfile
                                ? "\(journey.connectedPeerCount) connections · \(journey.recommendationCount) recommendations"
                                : "Complete your Discover profile to receive relevant member recommendations and connection requests."
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
                        title: "Journey",
                        systemImage: "person.3.fill",
                        tone: .gold
                    ) {
                        open(.discover)
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

        return "Continue building the protection, community, and legacy behind your journey."
    }

    private func hasPriorityContent(
        _ home: MobileHomeResponse
    ) -> Bool {
        return home.messaging.unreadCount > 0
            || !home.actions.isEmpty
            || !home.upcomingAppointments.isEmpty
            || !home.notifications.isEmpty
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
    @ObservedObject private var store: MobileAgentWorkspaceStore
    @ObservedObject private var messages: MessagingStore
    @ObservedObject private var bootstrap: LegendApplicationBootstrapCoordinator
    let openMessages: () -> Void

    init(
        store: MobileAgentWorkspaceStore,
        messages: MessagingStore,
        bootstrap: LegendApplicationBootstrapCoordinator,
        openMessages: @escaping () -> Void
    ) {
        _store = ObservedObject(wrappedValue: store)
        _messages = ObservedObject(wrappedValue: messages)
        _bootstrap = ObservedObject(wrappedValue: bootstrap)
        self.openMessages = openMessages
    }

    var body: some View {
        Group {
            switch store.clientsState {
            case .idle, .loading:
                LegendScreenSkeleton(accessibilityMessage: "Loading your client CRM") {
                    LegendListSkeleton(rows: 8)
                }
            case .loaded(let clients):
                clientContent(clients)
            case .unavailable(let failure):
                LegendNextErrorState(title: failure.title, message: failure.message, retryTitle: "Retry", retry: { Task { await bootstrap.refreshClients() } })
                    .padding(LegendNextSpacing.sm)
            }
        }
        .background(LegendNextCanvas())
        .navigationTitle("Clients")
        .navigationBarTitleDisplayMode(.inline)
        .refreshable { await bootstrap.refreshClients() }
    }

    @ViewBuilder
    private func clientContent(_ clients: [MobileAgentClientSummary]) -> some View {
        if clients.isEmpty {
            LegendNextEmptyState(
                title: "No active clients",
                message: "Your active Client and Business Client CRM records will appear here.",
                systemImage: "person.2")
        } else {
            LegendScrollView {
                LazyVStack(alignment: .leading, spacing: LegendNextSpacing.sm) {
                    LegendNextSectionHeader(
                        eyebrow: "CRM",
                        title: "Active clients",
                        detail: "(clients.count) live records"
                    )

                    ForEach(clients) { client in
                        LegendContactCard(
                            displayName: client.displayName,
                            subtitle: client.email,
                            detail: client.crmStatus,
                            avatar: {
                                LegendProfileAvatar(
                                    avatar: client.avatar,
                                    displayName: client.displayName,
                                    size: 46)
                            },
                            action: {
                                Button {
                                    messages.startConversation(forClientProfileID: client.profileID) { _ in
                                        openMessages()
                                    }
                                } label: {
                                    Label("Message", systemImage: "message.fill")
                                }
                                .buttonStyle(LegendNextButtonStyle(
                                    kind: .primary,
                                    isFullWidth: false,
                                    controlHeight: 30
                                ))
                                .disabled(messages.isStartingConversation)
                            }
                        )
                    }
                }
                .padding(.horizontal, LegendNextSpacing.sm)
                .padding(.vertical, LegendNextSpacing.md)
            }
            .scrollIndicators(.hidden)
        }
    }
}

private struct LegendAgentLeadsView: View {
    @ObservedObject private var store: MobileAgentWorkspaceStore
    @ObservedObject private var bootstrap: LegendApplicationBootstrapCoordinator

    init(
        store: MobileAgentWorkspaceStore,
        bootstrap: LegendApplicationBootstrapCoordinator
    ) {
        _store = ObservedObject(wrappedValue: store)
        _bootstrap = ObservedObject(wrappedValue: bootstrap)
    }

    var body: some View {
        Group {
            switch store.leadsState {
            case .idle, .loading:
                LegendScreenSkeleton(accessibilityMessage: "Loading your lead CRM") {
                    LegendListSkeleton(rows: 8)
                }
            case .loaded(let leads):
                leadContent(leads)
            case .unavailable(let failure):
                LegendNextErrorState(title: failure.title, message: failure.message, retryTitle: "Retry", retry: { Task { await bootstrap.refreshLeads() } })
                    .padding(LegendNextSpacing.sm)
            }
        }
        .background(LegendNextCanvas())
        .navigationTitle("Leads")
        .navigationBarTitleDisplayMode(.inline)
        .refreshable { await bootstrap.refreshLeads() }
    }

    @ViewBuilder
    private func leadContent(_ leads: [MobileAgentLeadSummary]) -> some View {
        if leads.isEmpty {
            LegendNextEmptyState(
                title: "No active leads",
                message: "Your active workstation lead records will appear here.",
                systemImage: "person.crop.circle.badge.plus")
        } else {
            LegendScrollView {
                LazyVStack(alignment: .leading, spacing: LegendNextSpacing.sm) {
                    LegendNextSectionHeader(
                        eyebrow: "Pipeline",
                        title: "Active leads",
                        detail: "(leads.count) current opportunities"
                    )

                    ForEach(leads) { lead in
                        LegendContactCard(
                            displayName: lead.displayName,
                            subtitle: lead.crmStage,
                            detail: "Updated \(lead.updatedUTC.formatted(.dateTime.month(.abbreviated).day().hour().minute()))",
                            avatar: {
                                Image(systemName: "person.crop.circle")
                                    .font(.title2)
                                    .foregroundStyle(LegendNextColor.goldBright)
                                    .frame(width: 46, height: 46)
                                    .background(.white.opacity(0.10), in: Circle())
                            },
                            action: {
                                EmptyView()
                            }
                        )
                    }
                }
                .padding(.horizontal, LegendNextSpacing.sm)
                .padding(.vertical, LegendNextSpacing.md)
            }
            .scrollIndicators(.hidden)
        }
    }
}

/// Shared with the Discover surface, which owns the entry point for a client's
/// own discoverability settings.
struct LegendJourneyProfileEditor: View {
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
            LegendScrollView(tracksNavigationChrome: false) {
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
                            Toggle("Show my profile in Discover", isOn: $isDiscoverable)
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
            .navigationTitle("Discover")
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

struct JourneyMultiSelectSection: View {
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
                        .buttonStyle(LegendNextButtonStyle(
                            kind: selections.contains(option) ? .gold : .secondary,
                            isFullWidth: false,
                            controlHeight: 34
                        ))
                        .accessibilityValue(selections.contains(option) ? "Selected" : "Not selected")
                    }
                }
            }
        }
    }
}

/// A selected, server-authoritative cash-flow view. The home cards pass the
/// exact response payload into this sheet; the native client never recreates
/// a projection or mutates financial state locally.
private enum LegendFinancialOutlookSelection: Identifiable {
    case week(MobileFinancialWeekAtGlanceResponse)
    case month(MobileFinancialMonthAtGlanceResponse)

    var id: String {
        switch self {
        case .week(let week):
            return "week-\(week.weekKey)"
        case .month(let month):
            return "month-\(month.monthKey)"
        }
    }

    var title: String {
        switch self {
        case .week:
            return "Week at a Glance"
        case .month:
            return "Month at a Glance"
        }
    }

    var eyebrow: String {
        switch self {
        case .week:
            return "Synced weekly outlook"
        case .month:
            return "Synced monthly outlook"
        }
    }

    var period: String {
        switch self {
        case .week(let week):
            return "\(MobileFinancialDisplay.date(week.startDate)) – \(MobileFinancialDisplay.date(week.endDate))"
        case .month(let month):
            return MobileFinancialDisplay.month(month.monthKey)
        }
    }

    var pressureStatus: String {
        switch self {
        case .week(let week):
            return week.pressureStatus
        case .month(let month):
            return month.pressureStatus
        }
    }

    var pressureSummary: String? {
        switch self {
        case .week(let week):
            return week.pressureSummary
        case .month(let month):
            return month.pressureSummary
        }
    }
}


private struct LegendFinancialProfilePanel: View {
    @ObservedObject private var store: MobileFinancialStore
    @ObservedObject private var bootstrap: LegendApplicationBootstrapCoordinator
    let openFinancialIntelligence: () -> Void
    @State private var selectedOutlook: LegendFinancialOutlookSelection?

    init(
        store: MobileFinancialStore,
        bootstrap: LegendApplicationBootstrapCoordinator,
        openFinancialIntelligence: @escaping () -> Void
    ) {
        _store = ObservedObject(wrappedValue: store)
        _bootstrap = ObservedObject(wrappedValue: bootstrap)
        self.openFinancialIntelligence = openFinancialIntelligence
    }

    var body: some View {
        Group {
            switch store.state {
            case .idle, .loading:
                LegendHomeSkeleton()
                    .accessibilityLabel("Preparing your financial outlook")

            case .available(let financial):
                financialPanel(financial)

            case .incomplete(let financial, let detail):
                financialPanel(financial, availabilityDetail: detail)

            case .neverSaved(let financial, let detail):
                financialPanel(financial, availabilityDetail: detail)

            case .projectionUnavailable(let financial, let detail):
                financialPanel(financial, availabilityDetail: detail)

            case .authenticationRequired(let failure):
                LegendNextErrorState(
                    title: failure.title,
                    message: failure.message,
                    retryTitle: "Sign in again",
                    retry: {
                        Task {
                            await bootstrap.refreshFinancial()
                        }
                    }
                )
                .padding(LegendNextSpacing.sm)

            case .retryableFailure(let failure):
                LegendNextErrorState(
                    title: failure.title,
                    message: failure.message,
                    retryTitle: "Retry",
                    retry: {
                        Task {
                            await bootstrap.refreshFinancial()
                        }
                    }
                )
                .padding(LegendNextSpacing.sm)
            }
        }
        .legendNextPageBackground()
        .sheet(item: $selectedOutlook) { selection in
            LegendFinancialOutlookSheet(selection: selection)
        }
    }

    private func financialPanel(
        _ financial: MobileFinancialSnapshotResponse,
        availabilityDetail: String? = nil
    ) -> some View {
        LegendScrollView {
            LazyVStack(
                alignment: .leading,
                spacing: LegendNextSpacing.sm
            ) {
                LegendNextHero(
                    eyebrow: "Financial Intelligence",
                    title: "Cash flow at a glance",
                    detail: "Swipe back any time. This view keeps your current week and month within reach."
                ) {
                    Button(action: openFinancialIntelligence) {
                        Label(
                            "Financial Intelligence",
                            systemImage: "chart.line.uptrend.xyaxis"
                        )
                    }
                    .buttonStyle(LegendNextButtonStyle(kind: .gold))
                }

                if let availabilityDetail,
                   !availabilityDetail.isEmpty {
                    LegendNextSurface(style: .elevated) {
                        Label(
                            availabilityDetail,
                            systemImage: "info.circle.fill"
                        )
                        .font(LegendNextTypography.supporting)
                        .foregroundStyle(LegendNextColor.textSecondary)
                    }
                }

                if let operatingSystem = financial.operatingSystem {
                    if let week = operatingSystem.weekAtGlance {
                        outlookSection(
                            eyebrow: "This week",
                            title: "Week at a Glance",
                            detail: "\(MobileFinancialDisplay.date(week.startDate)) – \(MobileFinancialDisplay.date(week.endDate))",
                            status: week.pressureStatus,
                            openingCashCents: week.openingCashCents,
                            incomeCents: week.incomeCents,
                            expenseCents: week.debitExpenseCents + week.creditExpenseCents,
                            endingCashCents: week.endingCashCents,
                            action: {
                                selectedOutlook = .week(week)
                            }
                        )
                    }

                    if let month = operatingSystem.monthAtGlance {
                        outlookSection(
                            eyebrow: "This month",
                            title: "Month at a Glance",
                            detail: MobileFinancialDisplay.month(month.monthKey),
                            status: month.pressureStatus,
                            openingCashCents: month.openingCashCents,
                            incomeCents: month.incomeCents,
                            expenseCents: month.debitExpenseCents + month.creditExpenseCents,
                            endingCashCents: month.endingCashCents,
                            action: {
                                selectedOutlook = .month(month)
                            }
                        )
                    }

                    if operatingSystem.weekAtGlance == nil,
                       operatingSystem.monthAtGlance == nil {
                        unavailableOutlook(
                            operatingSystem.projection.summary
                                ?? "Your saved financial outlook will appear here when it is ready."
                        )
                    }
                } else {
                    unavailableOutlook(
                        "Your saved financial outlook will appear here when it is ready."
                    )
                }
            }
            .padding(.horizontal, LegendNextSpacing.pageHorizontal)
            .padding(.top, LegendNextSpacing.sm)
            .padding(.bottom, LegendNextSpacing.xl)
        }
        .refreshable {
            await bootstrap.refreshFinancial()
        }
    }

    private func outlookSection(
        eyebrow: String,
        title: String,
        detail: String,
        status: String,
        openingCashCents: Int64,
        incomeCents: Int64,
        expenseCents: Int64,
        endingCashCents: Int64,
        action: @escaping () -> Void
    ) -> some View {
        Button(action: action) {
            outlookPreview(
                eyebrow: eyebrow,
                title: title,
                detail: detail,
                status: status,
                openingCashCents: openingCashCents,
                incomeCents: incomeCents,
                expenseCents: expenseCents,
                endingCashCents: endingCashCents
            )
        }
        .buttonStyle(.plain)
        .accessibilityLabel(
            "\(title). \(status). Open the complete synced financial breakdown."
        )
    }

    private func outlookPreview(
        eyebrow: String,
        title: String,
        detail: String,
        status: String,
        openingCashCents: Int64,
        incomeCents: Int64,
        expenseCents: Int64,
        endingCashCents: Int64
    ) -> some View {
        LegendNextSurface(
            style: .navy,
            cornerRadius: LegendNextRadius.prominentCard
        ) {
            VStack(alignment: .leading, spacing: LegendNextSpacing.sm) {
                HStack(alignment: .top, spacing: LegendNextSpacing.xs) {
                    VStack(alignment: .leading, spacing: LegendNextSpacing.micro) {
                        Text(eyebrow.uppercased())
                            .font(LegendNextTypography.eyebrow)
                            .tracking(0.8)
                            .foregroundStyle(LegendNextColor.goldBright)

                        Text(title)
                            .font(LegendNextTypography.section)
                            .foregroundStyle(.white)

                        Text(detail)
                            .font(LegendNextTypography.supporting)
                            .foregroundStyle(Color.white.opacity(0.66))
                    }

                    Spacer(minLength: LegendNextSpacing.sm)

                    LegendNextBadge(
                        status,
                        tone: MobileFinancialAmountSemantic.tone(
                            forStatus: status
                        ),
                        systemImage: "circle.fill"
                    )
                }

                LazyVGrid(
                    columns: [
                        GridItem(.flexible(), spacing: LegendNextSpacing.xs),
                        GridItem(.flexible(), spacing: LegendNextSpacing.xs)
                    ],
                    spacing: LegendNextSpacing.xs
                ) {
                    outlookMetric(
                        title: "Opening cash",
                        value: MobileFinancialDisplay.currency(
                            cents: openingCashCents
                        ),
                        systemImage: "wallet.bifold.fill",
                        tone: MobileFinancialAmountSemantic.tone(
                            forCents: openingCashCents,
                            kind: .openingCash
                        )
                    )
                    outlookMetric(
                        title: "Income",
                        value: MobileFinancialDisplay.currency(
                            cents: incomeCents
                        ),
                        systemImage: "arrow.down.left.circle.fill",
                        tone: MobileFinancialAmountSemantic.tone(
                            forCents: incomeCents,
                            kind: .income
                        )
                    )
                    outlookMetric(
                        title: "Bills",
                        value: MobileFinancialDisplay.currency(
                            cents: expenseCents
                        ),
                        systemImage: "doc.text.fill",
                        tone: MobileFinancialAmountSemantic.tone(
                            forCents: expenseCents,
                            kind: .bills
                        )
                    )
                    outlookMetric(
                        title: "Ending cash",
                        value: MobileFinancialDisplay.currency(
                            cents: endingCashCents
                        ),
                        systemImage: "banknote.fill",
                        tone: MobileFinancialAmountSemantic.tone(
                            forCents: endingCashCents,
                            kind: .endingCash
                        )
                    )
                }

                HStack(spacing: LegendNextSpacing.micro) {
                    Text("Open full breakdown")
                        .font(LegendNextTypography.caption)
                        .foregroundStyle(LegendNextColor.goldBright)

                    Spacer(minLength: 0)

                    Image(systemName: "arrow.up.right.square")
                        .font(.caption.weight(.semibold))
                        .foregroundStyle(LegendNextColor.goldBright)
                        .accessibilityHidden(true)
                }
            }
        }
        .accessibilityElement(children: .contain)
    }

    private func outlookMetric(
        title: String,
        value: String,
        systemImage: String,
        tone: LegendNextTone
    ) -> some View {
        HStack(spacing: LegendNextSpacing.xs) {
            Image(systemName: systemImage)
                .font(.system(size: 13, weight: .semibold))
                .foregroundStyle(tone.color)
                .frame(width: 28, height: 28)
                .background(tone.color.opacity(0.16), in: Circle())
                .accessibilityHidden(true)

            VStack(alignment: .leading, spacing: LegendNextSpacing.micro) {
                Text(title)
                    .font(LegendNextTypography.eyebrow)
                    .foregroundStyle(Color.white.opacity(0.66))
                    .lineLimit(1)

                Text(value)
                    .font(.subheadline.weight(.bold))
                    .foregroundStyle(tone.color)
                    .monospacedDigit()
                    .lineLimit(1)
                    .minimumScaleFactor(0.66)
            }
        }
        .padding(LegendNextSpacing.xs)
        .frame(maxWidth: .infinity, alignment: .leading)
        .background(
            Color.white.opacity(0.055),
            in: RoundedRectangle(
                cornerRadius: LegendNextRadius.control,
                style: .continuous
            )
        )
    }

    private func unavailableOutlook(
        _ detail: String
    ) -> some View {
        LegendNextSurface(style: .elevated) {
            Label(detail, systemImage: "chart.xyaxis.line")
                .font(LegendNextTypography.supporting)
                .foregroundStyle(LegendNextColor.textSecondary)
        }
    }
}

private struct LegendFinancialOutlookSheet: View {
    @Environment(\.dismiss) private var dismiss

    let selection: LegendFinancialOutlookSelection

    var body: some View {
        NavigationStack {
            ZStack {
                LegendNextGradient.financialSheet
                    .ignoresSafeArea()

                LegendScrollView(tracksNavigationChrome: false) {
                    LazyVStack(
                        alignment: .leading,
                        spacing: LegendNextSpacing.sm
                    ) {
                        premiumModalHeader

                        if let pressureSummary = selection.pressureSummary,
                           !pressureSummary.isEmpty {
                            outlookSummary(pressureSummary)
                        }

                        switch selection {
                        case .week(let week):
                            weekBreakdown(week)

                        case .month(let month):
                            monthBreakdown(month)
                        }
                    }
                    .padding(
                        .horizontal,
                        LegendNextSpacing.pageHorizontal
                    )
                    .padding(.top, LegendNextSpacing.xs)
                    .padding(.bottom, LegendNextSpacing.xl)
                }
                .scrollIndicators(.hidden)
            }
            .toolbar(.hidden, for: .navigationBar)
        }
        .tint(LegendNextColor.goldBright)
        .presentationDetents([.large])
        .presentationDragIndicator(.hidden)
        .presentationCornerRadius(34)
        .legendNextBrandedSheetAppearance(
            background: LegendNextGradient.financialSheet)
    }

    private var premiumModalHeader: some View {
        VStack(
            alignment: .leading,
            spacing: LegendNextSpacing.sm
        ) {
            Capsule()
                .fill(Color.white.opacity(0.34))
                .frame(width: 52, height: 5)
                .frame(maxWidth: .infinity)
                .accessibilityHidden(true)

            HStack(
                alignment: .top,
                spacing: LegendNextSpacing.sm
            ) {
                Image(systemName: outlookSystemImage)
                    .font(
                        .system(
                            size: 20,
                            weight: .semibold
                        )
                    )
                    .foregroundStyle(LegendNextColor.midnight)
                    .frame(width: 52, height: 52)
                    .background(
                        LegendNextColor.goldBright,
                        in: Circle()
                    )
                    .overlay {
                        Circle()
                            .strokeBorder(
                                Color.white.opacity(0.24),
                                lineWidth: 1
                            )
                    }
                    .accessibilityHidden(true)

                VStack(
                    alignment: .leading,
                    spacing: LegendNextSpacing.micro
                ) {
                    Text(selection.eyebrow.uppercased())
                        .font(LegendNextTypography.eyebrow)
                        .tracking(1)
                        .foregroundStyle(
                            LegendNextColor.goldBright
                        )

                    Text(selection.title)
                        .font(LegendNextTypography.hero)
                        .foregroundStyle(.white)
                        .fixedSize(
                            horizontal: false,
                            vertical: true
                        )

                    Text(selection.period)
                        .font(LegendNextTypography.body)
                        .foregroundStyle(
                            Color.white.opacity(0.70)
                        )
                }

                Spacer(minLength: LegendNextSpacing.xs)

                Button {
                    dismiss()
                } label: {
                    Image(systemName: "xmark")
                        .font(
                            .system(
                                size: 16,
                                weight: .bold
                            )
                        )
                        .foregroundStyle(.white)
                        .frame(width: 48, height: 48)
                        .background(
                            Color.white.opacity(0.08),
                            in: Circle()
                        )
                        .overlay {
                            Circle()
                                .strokeBorder(
                                    Color.white.opacity(0.20),
                                    lineWidth: 1
                                )
                        }
                        .contentShape(Circle())
                }
                .buttonStyle(.plain)
                .accessibilityLabel("Close \(selection.title)")
            }

            HStack(
                spacing: LegendNextSpacing.xs
            ) {
                LegendNextBadge(
                    selection.pressureStatus,
                    tone: MobileFinancialAmountSemantic.tone(
                        forStatus: selection.pressureStatus
                    ),
                    systemImage: "circle.fill"
                )

                Spacer(minLength: 0)

                Text("SERVER-SYNCED")
                    .font(LegendNextTypography.eyebrow)
                    .tracking(0.8)
                    .foregroundStyle(
                        Color.white.opacity(0.52)
                    )
            }
        }
        .padding(LegendNextSpacing.sm)
        .background(
            LinearGradient(
                colors: [
                    Color.white.opacity(0.075),
                    Color.white.opacity(0.025)
                ],
                startPoint: .topLeading,
                endPoint: .bottomTrailing
            ),
            in: RoundedRectangle(
                cornerRadius: LegendNextRadius.prominentCard,
                style: .continuous
            )
        )
        .overlay {
            RoundedRectangle(
                cornerRadius: LegendNextRadius.prominentCard,
                style: .continuous
            )
            .strokeBorder(
                Color.white.opacity(0.16),
                lineWidth: 1
            )
        }
    }

    private func outlookSummary(
        _ summary: String
    ) -> some View {
        HStack(
            alignment: .top,
            spacing: LegendNextSpacing.xs
        ) {
            Image(systemName: "chart.line.uptrend.xyaxis")
                .font(
                    .system(
                        size: 15,
                        weight: .semibold
                    )
                )
                .foregroundStyle(LegendNextColor.goldBright)
                .frame(width: 38, height: 38)
                .background(
                    LegendNextColor.goldBright.opacity(0.12),
                    in: Circle()
                )
                .accessibilityHidden(true)

            VStack(
                alignment: .leading,
                spacing: LegendNextSpacing.micro
            ) {
                Text("OUTLOOK SUMMARY")
                    .font(LegendNextTypography.eyebrow)
                    .tracking(0.8)
                    .foregroundStyle(
                        LegendNextColor.goldBright
                    )

                Text(summary)
                    .font(LegendNextTypography.supporting)
                    .foregroundStyle(
                        Color.white.opacity(0.72)
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
            .strokeBorder(
                Color.white.opacity(0.12),
                lineWidth: 1
            )
        }
    }

    private var outlookSystemImage: String {
        switch selection {
        case .week:
            return "calendar.badge.clock"

        case .month:
            return "calendar"
        }
    }

    @ViewBuilder
    private func weekBreakdown(
        _ week: MobileFinancialWeekAtGlanceResponse
    ) -> some View {
        outlookBalances(
            openingCashCents: week.openingCashCents,
            incomeCents: week.incomeCents,
            debitExpenseCents: week.debitExpenseCents,
            creditExpenseCents: week.creditExpenseCents,
            endingCashCents: week.endingCashCents
        )

        debtBreakdown(
            openingDebtCents: week.openingDebtCents,
            requiredDebtPaymentCents: week.requiredDebtPaymentCents,
            extraDebtPaymentCents: week.extraDebtPaymentCents,
            endingDebtCents: week.endingDebtCents
        )

        cashFlowEvents(week.events)
    }

    @ViewBuilder
    private func monthBreakdown(
        _ month: MobileFinancialMonthAtGlanceResponse
    ) -> some View {
        outlookBalances(
            openingCashCents: month.openingCashCents,
            incomeCents: month.incomeCents,
            debitExpenseCents: month.debitExpenseCents,
            creditExpenseCents: month.creditExpenseCents,
            endingCashCents: month.endingCashCents
        )

        debtBreakdown(
            openingDebtCents: month.openingDebtCents,
            requiredDebtPaymentCents: month.requiredDebtPaymentCents,
            extraDebtPaymentCents: month.extraDebtPaymentCents,
            endingDebtCents: month.endingDebtCents
        )

        LegendNextSurface(
            style: .navy,
            cornerRadius: LegendNextRadius.prominentCard
        ) {
            outlookMetric(
                title: "Savings contribution",
                cents: month.savingsContributionCents,
                systemImage: "banknote.fill",
                tone: MobileFinancialAmountSemantic.tone(
                    forCents: month.savingsContributionCents,
                    kind: .income
                )
            )
        }

        if let obligation = month.largestObligation {
            LegendNextSurface(
                style: .navy,
                cornerRadius: LegendNextRadius.prominentCard
            ) {
                VStack(alignment: .leading, spacing: LegendNextSpacing.xs) {
                    Text("LARGEST SCHEDULED OBLIGATION")
                        .font(LegendNextTypography.eyebrow)
                        .tracking(0.8)
                        .foregroundStyle(LegendNextColor.goldBright)

                    Text(obligation.title)
                        .font(LegendNextTypography.bodyEmphasis)
                        .foregroundStyle(.white)

                    HStack(alignment: .firstTextBaseline) {
                        Text("\(MobileFinancialDisplay.date(obligation.occursOn)) · \(obligation.kind)")
                            .font(LegendNextTypography.supporting)
                            .foregroundStyle(Color.white.opacity(0.66))

                        Spacer(minLength: LegendNextSpacing.sm)

                        Text(MobileFinancialDisplay.currency(cents: obligation.amountCents))
                            .font(LegendNextTypography.bodyEmphasis)
                            .foregroundStyle(LegendNextColor.danger)
                            .monospacedDigit()
                    }
                }
            }
        }

        monthWeeks(month.weeks)
    }

    private func outlookBalances(
        openingCashCents: Int64,
        incomeCents: Int64,
        debitExpenseCents: Int64,
        creditExpenseCents: Int64,
        endingCashCents: Int64
    ) -> some View {
        LegendNextSurface(
            style: .navy,
            cornerRadius: LegendNextRadius.prominentCard
        ) {
            VStack(alignment: .leading, spacing: LegendNextSpacing.sm) {
                Text("CASH FLOW")
                    .font(LegendNextTypography.eyebrow)
                    .tracking(0.8)
                    .foregroundStyle(LegendNextColor.goldBright)

                LazyVGrid(
                    columns: [
                        GridItem(.flexible(), spacing: LegendNextSpacing.xs),
                        GridItem(.flexible(), spacing: LegendNextSpacing.xs)
                    ],
                    spacing: LegendNextSpacing.xs
                ) {
                    outlookMetric(
                        title: "Opening cash",
                        cents: openingCashCents,
                        systemImage: "wallet.bifold.fill",
                        tone: MobileFinancialAmountSemantic.tone(
                            forCents: openingCashCents,
                            kind: .openingCash
                        )
                    )
                    outlookMetric(
                        title: "Income",
                        cents: incomeCents,
                        systemImage: "arrow.down.left.circle.fill",
                        tone: MobileFinancialAmountSemantic.tone(
                            forCents: incomeCents,
                            kind: .income
                        )
                    )
                    outlookMetric(
                        title: "Debit expenses",
                        cents: debitExpenseCents,
                        systemImage: "doc.text.fill",
                        tone: MobileFinancialAmountSemantic.tone(
                            forCents: debitExpenseCents,
                            kind: .bills
                        )
                    )
                    outlookMetric(
                        title: "Credit expenses",
                        cents: creditExpenseCents,
                        systemImage: "creditcard.fill",
                        tone: MobileFinancialAmountSemantic.tone(
                            forCents: creditExpenseCents,
                            kind: .bills
                        )
                    )
                    outlookMetric(
                        title: "Ending cash",
                        cents: endingCashCents,
                        systemImage: "banknote.fill",
                        tone: MobileFinancialAmountSemantic.tone(
                            forCents: endingCashCents,
                            kind: .endingCash
                        )
                    )
                }
            }
        }
    }

    private func debtBreakdown(
        openingDebtCents: Int64,
        requiredDebtPaymentCents: Int64,
        extraDebtPaymentCents: Int64,
        endingDebtCents: Int64
    ) -> some View {
        LegendNextSurface(
            style: .navy,
            cornerRadius: LegendNextRadius.prominentCard
        ) {
            VStack(alignment: .leading, spacing: LegendNextSpacing.sm) {
                Text("DEBT POSITION")
                    .font(LegendNextTypography.eyebrow)
                    .tracking(0.8)
                    .foregroundStyle(LegendNextColor.goldBright)

                LazyVGrid(
                    columns: [
                        GridItem(.flexible(), spacing: LegendNextSpacing.xs),
                        GridItem(.flexible(), spacing: LegendNextSpacing.xs)
                    ],
                    spacing: LegendNextSpacing.xs
                ) {
                    outlookMetric(
                        title: "Opening debt",
                        cents: openingDebtCents,
                        systemImage: "creditcard.fill",
                        tone: MobileFinancialAmountSemantic.tone(
                            forCents: openingDebtCents,
                            kind: .debt
                        )
                    )
                    outlookMetric(
                        title: "Required payment",
                        cents: requiredDebtPaymentCents,
                        systemImage: "arrow.down.circle.fill",
                        tone: MobileFinancialAmountSemantic.tone(
                            forCents: requiredDebtPaymentCents,
                            kind: .debt
                        )
                    )
                    outlookMetric(
                        title: "Extra payment",
                        cents: extraDebtPaymentCents,
                        systemImage: "arrow.down.circle.fill",
                        tone: MobileFinancialAmountSemantic.tone(
                            forCents: extraDebtPaymentCents,
                            kind: .debt
                        )
                    )
                    outlookMetric(
                        title: "Ending debt",
                        cents: endingDebtCents,
                        systemImage: "creditcard.fill",
                        tone: MobileFinancialAmountSemantic.tone(
                            forCents: endingDebtCents,
                            kind: .endingDebt
                        )
                    )
                }
            }
        }
    }

    private func cashFlowEvents(
        _ events: [MobileFinancialCashFlowEventResponse]
    ) -> some View {
        VStack(alignment: .leading, spacing: LegendNextSpacing.xs) {
            LegendNextSectionHeader(
                eyebrow: "Synced schedule",
                title: "Scheduled activity",
                detail: "Every dated cash-flow event returned by your saved projection."
            )

            if events.isEmpty {
                LegendNextSurface(style: .elevated) {
                    Label(
                        "No dated cash-flow events were returned for this week.",
                        systemImage: "calendar"
                    )
                    .font(LegendNextTypography.supporting)
                    .foregroundStyle(LegendNextColor.textSecondary)
                }
            } else {
                ForEach(events) { event in
                    cashFlowEvent(event)
                }
            }
        }
    }

    private func cashFlowEvent(
        _ event: MobileFinancialCashFlowEventResponse
    ) -> some View {
        let tone = eventTone(event)

        return LegendNextSurface(
            style: .navy,
            cornerRadius: LegendNextRadius.control,
            padding: LegendNextSpacing.sm
        ) {
            HStack(alignment: .top, spacing: LegendNextSpacing.xs) {
                Image(systemName: eventSystemImage(event))
                    .font(.system(size: 14, weight: .semibold))
                    .foregroundStyle(tone.color)
                    .frame(width: 32, height: 32)
                    .background(tone.color.opacity(0.16), in: Circle())
                    .accessibilityHidden(true)

                VStack(alignment: .leading, spacing: LegendNextSpacing.micro) {
                    Text(event.title)
                        .font(LegendNextTypography.bodyEmphasis)
                        .foregroundStyle(.white)
                        .fixedSize(horizontal: false, vertical: true)

                    Text("\(MobileFinancialDisplay.date(event.occursOn)) · \(event.kind) · \(event.status)")
                        .font(LegendNextTypography.caption)
                        .foregroundStyle(Color.white.opacity(0.66))

                    if let sourceToolID = event.sourceToolId,
                       !sourceToolID.isEmpty {
                        Text("Synced from \(sourceToolID)")
                            .font(LegendNextTypography.caption)
                            .foregroundStyle(Color.white.opacity(0.54))
                    }
                }

                Spacer(minLength: LegendNextSpacing.xs)

                Text(MobileFinancialDisplay.currency(cents: event.amountCents))
                    .font(LegendNextTypography.bodyEmphasis)
                    .foregroundStyle(tone.color)
                    .monospacedDigit()
                    .lineLimit(1)
                    .minimumScaleFactor(0.7)
            }
        }
    }

    private func monthWeeks(
        _ weeks: [MobileFinancialWeekSummaryResponse]
    ) -> some View {
        VStack(alignment: .leading, spacing: LegendNextSpacing.xs) {
            LegendNextSectionHeader(
                eyebrow: "Monthly breakdown",
                title: "Week by week",
                detail: "The full weekly cash and debt rollup saved for this month."
            )

            if weeks.isEmpty {
                LegendNextSurface(style: .elevated) {
                    Label(
                        "No weekly rollups were returned for this month.",
                        systemImage: "calendar"
                    )
                    .font(LegendNextTypography.supporting)
                    .foregroundStyle(LegendNextColor.textSecondary)
                }
            } else {
                ForEach(weeks) { week in
                    LegendNextSurface(
                        style: .navy,
                        cornerRadius: LegendNextRadius.control,
                        padding: LegendNextSpacing.sm
                    ) {
                        VStack(alignment: .leading, spacing: LegendNextSpacing.xs) {
                            HStack(alignment: .firstTextBaseline) {
                                Text("\(MobileFinancialDisplay.date(week.startDate)) – \(MobileFinancialDisplay.date(week.endDate))")
                                    .font(LegendNextTypography.bodyEmphasis)
                                    .foregroundStyle(.white)

                                Spacer(minLength: LegendNextSpacing.xs)

                                LegendNextBadge(
                                    week.pressureStatus,
                                    tone: MobileFinancialAmountSemantic.tone(
                                        forStatus: week.pressureStatus
                                    ),
                                    systemImage: "circle.fill"
                                )
                            }

                            LazyVGrid(
                                columns: [
                                    GridItem(.flexible(), spacing: LegendNextSpacing.xs),
                                    GridItem(.flexible(), spacing: LegendNextSpacing.xs)
                                ],
                                spacing: LegendNextSpacing.xs
                            ) {
                                outlookMetric(
                                    title: "Income",
                                    cents: week.incomeCents,
                                    systemImage: "arrow.down.left.circle.fill",
                                    tone: MobileFinancialAmountSemantic.tone(
                                        forCents: week.incomeCents,
                                        kind: .income
                                    )
                                )
                                outlookMetric(
                                    title: "Outflow",
                                    cents: week.outflowCents,
                                    systemImage: "doc.text.fill",
                                    tone: MobileFinancialAmountSemantic.tone(
                                        forCents: week.outflowCents,
                                        kind: .bills
                                    )
                                )
                                outlookMetric(
                                    title: "Ending cash",
                                    cents: week.endingCashCents,
                                    systemImage: "banknote.fill",
                                    tone: MobileFinancialAmountSemantic.tone(
                                        forCents: week.endingCashCents,
                                        kind: .endingCash
                                    )
                                )
                                outlookMetric(
                                    title: "Ending debt",
                                    cents: week.endingDebtCents,
                                    systemImage: "creditcard.fill",
                                    tone: MobileFinancialAmountSemantic.tone(
                                        forCents: week.endingDebtCents,
                                        kind: .endingDebt
                                    )
                                )
                            }
                        }
                    }
                }
            }
        }
    }

    private func outlookMetric(
        title: String,
        cents: Int64,
        systemImage: String,
        tone: LegendNextTone
    ) -> some View {
        HStack(spacing: LegendNextSpacing.xs) {
            Image(systemName: systemImage)
                .font(.system(size: 13, weight: .semibold))
                .foregroundStyle(tone.color)
                .frame(width: 28, height: 28)
                .background(tone.color.opacity(0.16), in: Circle())
                .accessibilityHidden(true)

            VStack(alignment: .leading, spacing: LegendNextSpacing.micro) {
                Text(title)
                    .font(LegendNextTypography.eyebrow)
                    .foregroundStyle(Color.white.opacity(0.66))
                    .lineLimit(1)

                Text(MobileFinancialDisplay.currency(cents: cents))
                    .font(.subheadline.weight(.bold))
                    .foregroundStyle(tone.color)
                    .monospacedDigit()
                    .lineLimit(1)
                    .minimumScaleFactor(0.66)
            }
        }
        .padding(LegendNextSpacing.xs)
        .frame(maxWidth: .infinity, alignment: .leading)
        .background(
            Color.white.opacity(0.055),
            in: RoundedRectangle(
                cornerRadius: LegendNextRadius.control,
                style: .continuous
            )
        )
    }

    private func eventTone(
        _ event: MobileFinancialCashFlowEventResponse
    ) -> LegendNextTone {
        let kind = event.kind.lowercased()
        if kind.contains("income") {
            return MobileFinancialAmountSemantic.tone(
                forCents: event.amountCents,
                kind: .income
            )
        }
        if kind.contains("debt") {
            return MobileFinancialAmountSemantic.tone(
                forCents: event.amountCents,
                kind: .debt
            )
        }
        return MobileFinancialAmountSemantic.tone(
            forCents: event.amountCents,
            kind: .bills
        )
    }

    private func eventSystemImage(
        _ event: MobileFinancialCashFlowEventResponse
    ) -> String {
        let kind = event.kind.lowercased()
        if kind.contains("income") {
            return "arrow.down.left.circle.fill"
        }
        if kind.contains("debt") {
            return "creditcard.fill"
        }
        return "doc.text.fill"
    }
}

private struct LegendFinanceView: View {
    @Environment(\.colorScheme) private var colorScheme

    let currentSession: MobileSession
    @ObservedObject private var store: MobileFinancialStore
    @ObservedObject private var bootstrap: LegendApplicationBootstrapCoordinator
    @State private var detailDestination: MobileFinancialDetailDestination?

    init(
        currentSession: MobileSession,
        store: MobileFinancialStore,
        bootstrap: LegendApplicationBootstrapCoordinator
    ) {
        self.currentSession = currentSession
        _store = ObservedObject(wrappedValue: store)
        _bootstrap = ObservedObject(wrappedValue: bootstrap)
    }

    var body: some View {
        Group {
            switch store.state {
            case .idle, .loading:
                LegendHomeSkeleton()
                    .accessibilityLabel("Loading financial intelligence")

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
                LegendNextErrorState(
                    title: failure.title,
                    message: failure.message,
                    retryTitle: "Sign in again",
                    retry: { Task { await bootstrap.refreshFinancial() } }
                )
                .padding(LegendNextSpacing.sm)

            case .retryableFailure(let failure):
                LegendNextErrorState(
                    title: failure.title,
                    message: failure.message,
                    retryTitle: "Retry",
                    retry: { Task { await bootstrap.refreshFinancial() } }
                )
                .padding(LegendNextSpacing.sm)
            }
        }
        .background(
            LegendNextColor.canvas.ignoresSafeArea()
        )
        .navigationTitle("Financial intelligence")
        .navigationBarTitleDisplayMode(.inline)
    }

    @ViewBuilder
    private func financialContent(
        _ financial: MobileFinancialSnapshotResponse,
        availability: FinancialAvailability? = nil
    ) -> some View {
        LegendScrollView {
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
                    LegendNextEmptyState(
                        title: "Financial snapshot incomplete",
                        message:
                            "Your saved financial health data will appear here after it is completed in your account workspace.",
                        systemImage:
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
            await bootstrap.refreshFinancial()
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
            let preferredOrder = [
                MobileFinancialDetailDestination.financialPosition.rawValue,
                MobileFinancialDetailDestination.dataAttention.rawValue
            ]

            let orderedSections = presentation.prioritySections
                .filter {
                    $0.key != MobileFinancialDetailDestination.currentOutlook.rawValue
                        && $0.key != MobileFinancialDetailDestination.monthlyOutlook.rawValue
                }
                .enumerated()
                .sorted { lhs, rhs in
                    let lhsRank =
                        preferredOrder.firstIndex(of: lhs.element.key)
                        ?? preferredOrder.count + lhs.offset
                    let rhsRank =
                        preferredOrder.firstIndex(of: rhs.element.key)
                        ?? preferredOrder.count + rhs.offset

                    return lhsRank < rhsRank
                }
                .map { $0.element }

            if !orderedSections.isEmpty {
                VStack(
                    alignment: .leading,
                    spacing: LegendNextSpacing.sm
                ) {
                    LegendNextHero(
                        eyebrow: "Prioritized view",
                        title: "Financial intelligence",
                        detail: "Open a card to review the complete synced breakdown."
                    )

                    ForEach(orderedSections) { section in
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
        LegendScrollView {
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
                case .currentOutlook, .monthlyOutlook:
                    operatingSystemUnavailable(
                        summary: "Week and month outlooks are available from the Financial Intelligence panel in your Profile."
                    )

                case .debtObligations:
                    if let month = financial.operatingSystem?.monthAtGlance {
                        if let obligation = month.largestObligation {
                            largestObligation(obligation)
                        } else {
                            operatingSystemUnavailable(
                                summary: "No largest scheduled obligation is available for the current month."
                            )
                        }
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
                        LegendNextEmptyState(
                            title: "Financial snapshot incomplete",
                            message: "Saved balance-sheet details are not available yet.",
                            systemImage: "building.columns"
                        )
                    }

                case .upcomingActivity:
                    if financial.upcomingBills.isEmpty {
                        LegendNextEmptyState(
                            title: "No upcoming activity",
                            message: "No saved recurring financial items are currently scheduled.",
                            systemImage: "calendar"
                        )
                    } else {
                        upcomingBills(financial.upcomingBills)
                    }

                case .protectionDiscussion:
                    if let position = financial.position {
                        financialHealth(position)
                        positionMetrics(position)
                    } else {
                        LegendNextEmptyState(
                            title: "Protection information unavailable",
                            message: "Saved financial health information is not available yet.",
                            systemImage: "shield.lefthalf.filled"
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
            await bootstrap.refreshFinancial()
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
                        "\(MobileFinancialDisplay.date(obligation.occursOn)) · \(obligation.kind)"
                    )
                    .font(
                        LegendNextTypography.supporting
                    )
                    .foregroundStyle(
                        Color.white.opacity(0.64)
                    )
                }

                Spacer(minLength: 8)

                Text(
                    MobileFinancialDisplay.currency(
                        cents: obligation.amountCents
                    )
                )
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

}

private enum LegendProfileContentFilter: String, CaseIterable, Identifiable {
    case posts
    case hacs
    case stories

    var id: Self { self }

    var symbolName: String {
        switch self {
        case .posts:
            return "square.grid.3x3"
        case .hacs:
            return "play.rectangle"
        case .stories:
            return "circle.dashed"
        }
    }

    var accessibilityTitle: String {
        switch self {
        case .posts:
            return "Posts"
        case .hacs:
            return "Hacs"
        case .stories:
            return "Stories"
        }
    }

    var socialContentType: MobileSocialContentType {
        switch self {
        case .posts:
            return .post
        case .hacs:
            return .hac
        case .stories:
            return .story
        }
    }
}

private struct LegendAccountView: View {
    let currentSession: MobileSession

    @ObservedObject private var coordinator: MobileSessionCoordinator
    @ObservedObject private var account: MobileAccountStore
    @ObservedObject private var messages: MessagingStore
    @ObservedObject private var social: MobileSocialStore
    @ObservedObject private var bootstrap: LegendApplicationBootstrapCoordinator
    private let showMessages: () -> Void

    @State private var selectedContent: LegendProfileContentFilter = .posts
    @State private var isEditing = false
    @State private var isShowingSettings = false
    @State private var isPresentingCreatorInsights = false
    @State private var isPresentingFollowRequests = false
    @State private var isPresentingTranslationLanguagePicker = false
    @State private var isPresentingTranslationManagement = false
    @State private var translationLanguageNames: [String: String] = [:]
    @State private var isConfirmingSignOut = false
    @State private var creationRoute: LegendSocialCreationRoute?
    @State private var selectedPost: MobileSocialPost?
    @State private var profilePage = 0
    @State private var isFinancialIntelligencePresented = false

    init(
        currentSession: MobileSession,
        coordinator: MobileSessionCoordinator,
        account: MobileAccountStore,
        messages: MessagingStore,
        social: MobileSocialStore,
        bootstrap: LegendApplicationBootstrapCoordinator,
        showMessages: @escaping () -> Void
    ) {
        self.currentSession = currentSession
        _coordinator = ObservedObject(wrappedValue: coordinator)
        _account = ObservedObject(wrappedValue: account)
        _messages = ObservedObject(wrappedValue: messages)
        _social = ObservedObject(wrappedValue: social)
        _bootstrap = ObservedObject(wrappedValue: bootstrap)
        self.showMessages = showMessages
    }

    var body: some View {
        Group {
            switch account.state {
            case .idle, .loading:
                LegendScreenSkeleton(accessibilityMessage: "Loading your profile") {
                    LegendListSkeleton(rows: 5)
                }

            case .loaded(let profile):
                profileWorkspace(profile)

            case .unavailable(let failure):
                LegendNextErrorState(
                    title: failure.title,
                    message: failure.message,
                    retryTitle: "Retry",
                    retry: { Task { await bootstrap.refreshProfile() } }
                )
                .padding(LegendNextSpacing.sm)
            }
        }
        .background(LegendNextCanvas())
        .toolbar(.hidden, for: .navigationBar)
        .refreshable {
            await bootstrap.refreshProfile()
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
            if case .loaded(let profile) = account.state {
                profileSettingsSheet(profile)
            }
        }
        .sheet(isPresented: $isPresentingTranslationLanguagePicker) {
            if case .loaded(let profile) = account.state {
                LegendTranslationLanguagePicker(profile: profile, store: account, messages: messages)
            }
        }
        .sheet(isPresented: $isPresentingTranslationManagement) {
            LegendTranslationAccessManager(messages: messages)
        }
        .sheet(item: $creationRoute) { _ in
            LegendSocialCreationSheet(
                route: $creationRoute,
                social: social)
        }
        .navigationDestination(
            isPresented: Binding(
                get: { selectedPost != nil },
                set: { if !$0 { selectedPost = nil } }
            )
        ) {
            if let selectedPost {
                LegendPostDetailView(
                    post: selectedPost,
                    currentIdentity: currentSession.actor.identity,
                    social: social,
                    profilePosts: selectedProfileItems)
            }
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

    @ViewBuilder
    private var profileAccountMenu: some View {
        let handle = profileNavigationHandle
        if currentSession.alternateParticipantTypes.isEmpty {
            Text(handle)
                .font(LegendNextTypography.label)
                .tracking(0.4)
                .foregroundStyle(LegendNextColor.textPrimary)
        } else {
            Menu {
                Section {
                    Label(
                        "Current: \(currentSession.actor.identity.participantType.accountLabel)",
                        systemImage: "checkmark.circle.fill"
                    )
                }

                Section("Switch account") {
                    ForEach(currentSession.alternateParticipantTypes, id: \.self) { participantType in
                        Button {
                            coordinator.switchToRole(participantType)
                        } label: {
                            Label(
                                "Continue as \(participantType.accountLabel)",
                                systemImage: participantType.accountSystemImage
                            )
                        }
                    }
                }
            } label: {
                HStack(spacing: 4) {
                    Text(handle)
                    Image(systemName: "chevron.down")
                        .font(.caption.weight(.bold))
                }
                .font(LegendNextTypography.label)
                .tracking(0.4)
                .foregroundStyle(LegendNextColor.textPrimary)
            }
            .accessibilityLabel("\(handle). Switch account")
            .accessibilityHint("Choose another authorized Legend account")
        }
    }

    private var profileNavigationHandle: String {
        guard case .loaded(let profile) = account.state,
              let username = normalized(profile.username) else {
            return currentSession.actor.displayName
        }

        return "@\(username)"
    }

    private func profileContent(
        _ profile: MobileAccountProfile
    ) -> some View {
        LegendScrollView {
            LazyVStack(spacing: LegendNextSpacing.sm) {
                profileIdentityHeader(profile)

                profileActions

                profileContentSelector

                profileGrid()
            }
            .padding(.horizontal, LegendNextSpacing.pageHorizontal)
            .padding(.top, LegendNextSpacing.xs)
            .padding(.bottom, 116)
        }
        .scrollIndicators(.hidden)
    }

    private func profileWorkspace(
        _ profile: MobileAccountProfile
    ) -> some View {
        TabView(selection: $profilePage) {
            profileContent(profile)
                .tag(0)

            LegendFinancialProfilePanel(
                store: bootstrap.stores.financial,
                bootstrap: bootstrap,
                openFinancialIntelligence: {
                    isFinancialIntelligencePresented = true
                }
            )
            .tag(1)
        }
        .tabViewStyle(.page(indexDisplayMode: .never))
        // Profile content is vertically scrollable and contains tappable media.
        // Keep the pager's one horizontal intent explicit so a left swipe always
        // reaches the financial page instead of being interpreted by that content.
        .simultaneousGesture(
            DragGesture(minimumDistance: 20)
                .onEnded { value in
                    let horizontalDistance = abs(value.translation.width)
                    let verticalDistance = abs(value.translation.height)
                    guard horizontalDistance > verticalDistance else { return }

                    let destination = value.translation.width < 0 ? 1 : 0
                    guard destination != profilePage else { return }
                    withAnimation(LegendNextMotion.tab) {
                        profilePage = destination
                    }
                }
        )
        .onChange(of: profilePage) { _, page in
            guard page == 1 else { return }

            Task {
                await bootstrap.loadFinancialIntelligenceIfNeeded()
            }
        }
        .navigationDestination(
            isPresented: $isFinancialIntelligencePresented
        ) {
            LegendFinanceView(
                currentSession: currentSession,
                store: bootstrap.stores.financial,
                bootstrap: bootstrap
            )
        }
    }

    private func profileIdentityHeader(
        _ profile: MobileAccountProfile
    ) -> some View {
        LegendNextSurface(
            style: .elevated,
            cornerRadius: LegendNextRadius.prominentCard,
            padding: LegendNextSpacing.sm
        ) {
            VStack(alignment: .leading, spacing: LegendNextSpacing.xs) {
                HStack(alignment: .center, spacing: LegendNextSpacing.sm) {
                    LegendProfileAvatar(
                        avatar: profile.avatar,
                        displayName: profile.displayName,
                        size: 64
                    )

                    VStack(alignment: .leading, spacing: LegendNextSpacing.micro) {
                        profileAccountMenu

                        LegendVerifiedName(
                            profile.displayName,
                            isVerified: profile.isVerified,
                            font: .title2.weight(.bold)
                        )

                        if let roleLabel = normalized(profile.roleLabel) {
                            LegendNextBadge(roleLabel, tone: .gold, systemImage: "briefcase.fill")
                        }
                    }

                    Spacer(minLength: 0)

                    Button {
                        isShowingSettings = true
                    } label: {
                        Image(systemName: "gearshape.fill")
                    }
                    .buttonStyle(LegendNextIconButtonStyle(tone: .navy, size: 38))
                    .accessibilityLabel("Open profile settings")
                }

                profileDetails(profile)

                HStack(spacing: LegendNextSpacing.tiny) {
                    Button {
                        selectedContent = .hacs
                    } label: {
                        profileMetric(value: hacCount, title: "Hacs")
                    }
                    .buttonStyle(.plain)
                    .accessibilityHint("Show your Hacs")

                    NavigationLink {
                        LegendFollowListView(
                            kind: .follows,
                            currentIdentity: currentSession.actor.identity,
                            social: social)
                    } label: {
                        profileMetric(
                            value: currentProfileMetrics?.followingCount ?? 0,
                            title: "Following"
                        )
                    }
                    .buttonStyle(.plain)
                    .accessibilityHint("Show people you follow")

                    NavigationLink {
                        LegendFollowListView(
                            kind: .followers,
                            currentIdentity: currentSession.actor.identity,
                            social: social)
                    } label: {
                        profileMetric(
                            value: currentProfileMetrics?.followerCount ?? 0,
                            title: "Followers"
                        )
                    }
                    .buttonStyle(.plain)
                    .accessibilityHint("Show people who follow you")
                }
                .frame(maxWidth: .infinity)
                .padding(.top, LegendNextSpacing.micro)
            }
        }
    }

    @ViewBuilder
    private func profileDetails(_ profile: MobileAccountProfile) -> some View {
        VStack(alignment: .leading, spacing: LegendNextSpacing.micro) {
            if let bio = normalized(profile.bio) ?? normalized(profile.shortBio) {
                Text(bio)
                    .font(LegendNextTypography.caption)
                    .foregroundStyle(LegendNextColor.textPrimary)
                    .lineLimit(2)
                    .fixedSize(horizontal: false, vertical: true)
            }

            if let location = normalized(profile.location) {
                Label(location, systemImage: "mappin.and.ellipse")
                    .font(LegendNextTypography.caption)
                    .foregroundStyle(LegendNextColor.textSecondary)
            }

            if let website = normalized(profile.website) {
                LegendProfileContactLink(value: website, kind: .website)
            }

            if profile.isEmailVisible,
               let email = normalized(profile.profileEmail) {
                LegendProfileContactLink(value: email, kind: .email)
            }

            if profile.isPhoneVisible,
               let phone = normalized(profile.phone) {
                LegendProfileContactLink(value: phone, kind: .phone)
            }
        }
        .frame(maxWidth: .infinity, alignment: .leading)
    }

    private var profileActions: some View {
        Button {
            isEditing = true
        } label: {
            Text("Edit profile")
                .frame(maxWidth: .infinity)
        }
        .buttonStyle(LegendNextButtonStyle(
            kind: .secondary,
            controlHeight: 40
        ))
    }

    private var profileContentSelector: some View {
        HStack(spacing: LegendNextSpacing.xs) {
            ForEach(LegendProfileContentFilter.allCases) { filter in
                profileContentButton(filter)
            }
        }
        .padding(LegendNextSpacing.tiny)
        .background(LegendNextColor.brandBlueSurface, in: RoundedRectangle(
            cornerRadius: LegendNextRadius.control,
            style: .continuous
        ))
        .overlay {
            RoundedRectangle(cornerRadius: LegendNextRadius.control, style: .continuous)
                .strokeBorder(LegendNextColor.navy.opacity(0.22), lineWidth: 1)
        }
    }

    private func profileContentButton(
        _ filter: LegendProfileContentFilter
    ) -> some View {
        let isSelected = selectedContent == filter

        return Button {
            withAnimation(.easeInOut(duration: 0.18)) {
                selectedContent = filter
            }
        } label: {
            Label(filter.accessibilityTitle, systemImage: filter.symbolName)
                .font(LegendNextTypography.caption.weight(.bold))
                .foregroundStyle(isSelected ? Color.white : LegendNextColor.navy)
                .frame(maxWidth: .infinity, minHeight: 40)
                .background {
                    RoundedRectangle(
                        cornerRadius: LegendNextRadius.compact,
                        style: .continuous
                    )
                    .fill(isSelected
                        ? AnyShapeStyle(LegendNextGradient.finance)
                        : AnyShapeStyle(Color.clear)
                    )
                }
                .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .accessibilityLabel(filter.accessibilityTitle)
        .accessibilityAddTraits(isSelected ? .isSelected : [])
    }

    @ViewBuilder
    private func profileGrid() -> some View {
        switch social.profileContentState {
        case .idle, .loading:
            LazyVGrid(columns: LegendProfileGridLayout.columns, spacing: LegendNextSpacing.tiny) {
                ForEach(0..<6, id: \.self) { _ in
                    LegendProfileGridCell {
                        Rectangle()
                            .fill(LegendNextColor.brandBlueSurface)
                            .clipShape(RoundedRectangle(
                                cornerRadius: LegendNextRadius.compact,
                                style: .continuous
                            ))
                            .legendNextShimmer()
                    }
                }
            }

        case .unavailable(let failure):
            LegendNextErrorState(
                title: failure.title,
                message: failure.message,
                retryTitle: "Retry",
                retry: { Task { await bootstrap.refreshProfile() } }
            )
            .padding(LegendNextSpacing.sm)

        case .loaded:
            let items = selectedProfileItems

            if items.isEmpty {
                profileEmptyState()
            } else {
                LazyVGrid(columns: LegendProfileGridLayout.columns, spacing: LegendNextSpacing.tiny) {
                    ForEach(items) { post in
                        Button {
                            selectedPost = post
                        } label: {
                            LegendProfileGridCell {
                                LegendProfileGridTile(post: post, social: social)
                            }
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
            .buttonStyle(LegendNextButtonStyle(kind: .primary))
        }
        .frame(maxWidth: .infinity)
        .padding(.vertical, 54)
        .padding(.horizontal, LegendNextSpacing.sm)
    }

    private func profileSettingsSheet(_ profile: MobileAccountProfile) -> some View {
        NavigationStack {
            LegendScrollView(tracksNavigationChrome: false) {
                VStack(alignment: .leading, spacing: LegendNextSpacing.md) {
                    LegendNextSheetHeader(
                        eyebrow: "Member experience",
                        title: "Profile settings",
                        detail: "Personalize the details people see here. These settings are private to the Legend mobile app.",
                        dismiss: { isShowingSettings = false }
                    )

                    LegendProfileSettingsSection(title: "Profile") {
                        VStack(spacing: 0) {
                            Button {
                                isShowingSettings = false
                                isEditing = true
                            } label: {
                                LegendProfileSettingsRow(
                                    title: "Edit profile",
                                    detail: "Name, bio, links, and privacy",
                                    systemImage: "person.crop.circle",
                                    showsChevron: true)
                            }
                            .buttonStyle(.plain)

                            LegendProfileSettingsDivider()

                            Button {
                                isPresentingCreatorInsights = true
                            } label: {
                                LegendProfileSettingsRow(
                                    title: "Creator insights",
                                    detail: "Review reach and engagement",
                                    systemImage: "chart.line.uptrend.xyaxis",
                                    showsChevron: true)
                            }
                            .buttonStyle(.plain)

                            LegendProfileSettingsDivider()

                            Button {
                                Task { await bootstrap.refreshProfile() }
                                isShowingSettings = false
                            } label: {
                                LegendProfileSettingsRow(
                                    title: "Refresh profile",
                                    detail: "Check for the latest account details",
                                    systemImage: "arrow.clockwise",
                                    showsChevron: false)
                            }
                            .buttonStyle(.plain)
                        }
                    }

                    LegendProfileSettingsSection(title: "Privacy") {
                        VStack(spacing: 0) {
                            LegendProfileSettingsRow(
                                title: "Profile email",
                                detail: "Shown only when you enable it",
                                systemImage: "lock.shield",
                                showsChevron: false)

                            LegendProfileSettingsDivider()

                            LegendProfileSettingsToggleRow(
                                title: "Private account",
                                detail: "Only approved followers can view your posts",
                                systemImage: "lock.fill",
                                isOn: privateAccountBinding)

                            if privateAccountBinding.wrappedValue {
                                LegendProfileSettingsDivider()

                                Button {
                                    isPresentingFollowRequests = true
                                } label: {
                                    LegendProfileSettingsRow(
                                        title: "Follow requests",
                                        detail: "Approve or decline people waiting to follow you",
                                        systemImage: "person.badge.clock",
                                        showsChevron: true)
                                }
                                .buttonStyle(.plain)
                            }
                        }
                    }

                    if !profile.isVerified {
                        LegendProfileSettingsSection(title: "Verification") {
                            Button {
                                messages.startVerificationRequest {
                                    isShowingSettings = false
                                    showMessages()
                                }
                            } label: {
                                LegendProfileSettingsRow(
                                    title: messages.isCreatingGroup
                                        ? "Opening verification review…"
                                        : "Request verification",
                                    detail: "Submit your profile to the private Legend review team.",
                                    systemImage: "checkmark.seal",
                                    showsChevron: true)
                            }
                            .buttonStyle(.plain)
                            .disabled(messages.isCreatingGroup)
                        }
                    }

                    LegendProfileSettingsSection(title: "Language translation") {
                        VStack(spacing: 0) {
                            if profile.translationAccess.isGranted {
                                Button {
                                    isPresentingTranslationLanguagePicker = true
                                } label: {
                                    LegendProfileSettingsRow(
                                        title: "Translation language",
                                        detail: legendLanguageName(profile.translationAccess.preferredCommunicationLanguage),
                                        systemImage: "character.bubble",
                                        showsChevron: true)
                                }
                                .buttonStyle(.plain)

                                if profile.translationAccess.canManage {
                                    LegendProfileSettingsDivider()

                                    Button {
                                        isPresentingTranslationManagement = true
                                    } label: {
                                        LegendProfileSettingsRow(
                                            title: "Manage translation access",
                                            detail: "Grant or remove access for any Legend profile.",
                                            systemImage: "person.badge.key",
                                            showsChevron: true)
                                    }
                                    .buttonStyle(.plain)
                                }
                            } else {
                                Button {
                                    messages.startControlledResourceRequest(.languageTranslation) {
                                        isShowingSettings = false
                                        showMessages()
                                    }
                                } label: {
                                    LegendProfileSettingsRow(
                                        title: profile.translationAccess.isPending
                                            ? "Translation access pending"
                                            : "Request translation access",
                                        detail: profile.translationAccess.isPending
                                            ? "Your request is with the private Legend review team."
                                            : "Choose Haitian Creole or another supported language after approval.",
                                        systemImage: "character.bubble",
                                        showsChevron: !profile.translationAccess.isPending)
                                }
                                .buttonStyle(.plain)
                                .disabled(profile.translationAccess.isPending || messages.isCreatingGroup)
                            }
                        }
                    }

                    LegendProfileSettingsSection(title: "Security") {
                        VStack(spacing: 0) {
                            LegendProfileSettingsRow(
                                title: "Secure session",
                                detail: "Protected",
                                systemImage: "lock.shield.fill",
                                showsChevron: false)

                            LegendProfileSettingsDivider()

                            LegendProfileSettingsToggleRow(
                                title: "Face ID",
                                detail: coordinator.isBiometricSignInAvailable
                                    ? "Optional protection for this account on this device"
                                    : "Face ID is not available on this device",
                                systemImage: "faceid",
                                isOn: biometricSignInBinding)
                            .disabled(
                                !coordinator.isBiometricSignInAvailable &&
                                !coordinator.isBiometricSignInEnabled)

                            LegendProfileSettingsDivider()

                            LegendProfileSettingsRow(
                                title: "Security checkpoint",
                                detail: "Sign in again every 90 days",
                                systemImage: "calendar.badge.exclamationmark",
                                showsChevron: false)

                            LegendProfileSettingsDivider()

                            LegendProfileSettingsRow(
                                title: "Token storage",
                                detail: "iOS Keychain",
                                systemImage: "key.fill",
                                showsChevron: false)
                        }
                    }

                    Button {
                        isShowingSettings = false
                        isConfirmingSignOut = true
                    } label: {
                        LegendProfileSettingsRow(
                            title: "Sign out",
                            detail: nil,
                            systemImage: "rectangle.portrait.and.arrow.right",
                            showsChevron: false,
                            isDestructive: true)
                    }
                    .buttonStyle(.plain)
                }
                .padding(.horizontal, LegendNextSpacing.sm)
                .padding(.top, LegendNextSpacing.xs)
                .padding(.bottom, LegendNextSpacing.md)
            }
            .background(LegendNextCanvas())
            .toolbar(.hidden, for: .navigationBar)
        }
        .tint(LegendNextColor.gold)
        .legendNextSheetChrome()
        .sheet(isPresented: $isPresentingCreatorInsights) {
            if case .loaded(let snapshot) = social.state {
                LegendCreatorInsightsSheet(
                    insights: snapshot.creatorInsights,
                    profileMetrics: snapshot.currentProfileMetrics)
            }
        }
        .sheet(isPresented: $isPresentingFollowRequests) {
            LegendFollowRequestsSheet(social: social)
        }
        .task {
            guard profile.translationAccess.isGranted,
                  let languages = await messages.communicationLanguages() else {
                return
            }
            translationLanguageNames = Dictionary(
                uniqueKeysWithValues: languages.map { ($0.code, $0.displayName) })
        }
    }

    private var biometricSignInBinding: Binding<Bool> {
        Binding(
            get: { coordinator.isBiometricSignInEnabled },
            set: { coordinator.setBiometricSignInEnabled($0) })
    }

    private var privateAccountBinding: Binding<Bool> {
        Binding(
            get: {
                guard case .loaded(let profile) = account.state else { return false }
                return profile.isPrivate
            },
            set: { account.setPrivateAccount($0) })
    }

    private var selectedProfileItems: [MobileSocialPost] {
        switch selectedContent {
        case .posts:
            return profilePosts
        case .hacs:
            return profileHacs
        case .stories:
            return profileStories
        }
    }

    private var profilePosts: [MobileSocialPost] {
        profileItems(for: .post)
    }

    private var profileHacs: [MobileSocialPost] {
        profileItems(for: .hac)
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

    private var currentProfileMetrics: MobileSocialProfileMetrics? {
        guard case .loaded(let snapshot) = social.state else {
            return nil
        }

        return snapshot.currentProfileMetrics
    }

    private var hacCount: Int {
        if case .loaded = social.profileContentState {
            return currentProfilePosts.count {
                $0.contentType == MobileSocialContentType.hac.rawValue
            }
        }

        return currentProfileMetrics?.videoCount ?? 0
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

    private func legendLanguageName(_ code: String?) -> String {
        guard let code, !code.isEmpty else { return "Choose a language" }
        return translationLanguageNames[code] ?? (code == "ht" ? "Haitian Creole" : code)
    }
}

private struct LegendTranslationLanguagePicker: View {
    let profile: MobileAccountProfile
    @ObservedObject var store: MobileAccountStore
    @ObservedObject var messages: MessagingStore
    @Environment(\.dismiss) private var dismiss
    @State private var languages: [LegendCommunicationLanguage] = []
    @State private var selectedLanguageCode: String

    init(profile: MobileAccountProfile, store: MobileAccountStore, messages: MessagingStore) {
        self.profile = profile
        _store = ObservedObject(wrappedValue: store)
        _messages = ObservedObject(wrappedValue: messages)
        _selectedLanguageCode = State(initialValue: profile.translationAccess.preferredCommunicationLanguage ?? "")
    }

    var body: some View {
        NavigationStack {
            LegendScrollView(tracksNavigationChrome: false) {
                VStack(alignment: .leading, spacing: LegendNextSpacing.md) {
                    LegendNextSheetHeader(
                        eyebrow: "Language translation",
                        title: "Choose your language",
                        detail: "Messages are translated only for your view. The sender’s original message is always available.",
                        dismiss: { dismiss() })

                    LegendProfileSettingsSection(title: "Preferred language") {
                        VStack(spacing: 0) {
                            if languages.isEmpty {
                                ProgressView("Loading available languages")
                                    .tint(LegendNextColor.goldBright)
                                    .frame(maxWidth: .infinity, minHeight: 64)
                            } else {
                                ForEach(Array(languages.enumerated()), id: \.element.id) { index, language in
                                    Button {
                                        selectedLanguageCode = language.code
                                    } label: {
                                        HStack(spacing: LegendNextSpacing.sm) {
                                            Image(systemName: selectedLanguageCode == language.code
                                                  ? "checkmark.circle.fill"
                                                  : "circle")
                                                .foregroundStyle(selectedLanguageCode == language.code
                                                    ? LegendNextColor.goldBright
                                                    : .white.opacity(0.74))
                                            Text(language.displayName)
                                                .font(.subheadline.weight(.semibold))
                                                .foregroundStyle(.white)
                                            Spacer(minLength: 0)
                                        }
                                        .padding(.vertical, LegendNextSpacing.micro)
                                    }
                                    .buttonStyle(.plain)

                                    if index < languages.count - 1 {
                                        LegendProfileSettingsDivider()
                                    }
                                }
                            }
                        }
                    }

                    Button(store.isSaving ? "Saving language…" : "Save language") {
                        Task {
                            if await store.savePreferredCommunicationLanguage(selectedLanguageCode) {
                                dismiss()
                            }
                        }
                    }
                    .buttonStyle(LegendNextButtonStyle(kind: .primary))
                    .disabled(store.isSaving || selectedLanguageCode.isEmpty || languages.isEmpty)
                }
                .padding(LegendNextSpacing.sm)
                .padding(.bottom, LegendNextSpacing.xl)
            }
            .background(LegendNextCanvas())
            .toolbar(.hidden, for: .navigationBar)
        }
        .tint(LegendNextColor.gold)
        .legendNextSheetChrome(detents: [.large])
        .task {
            guard let serverLanguages = await messages.communicationLanguages() else { return }
            languages = serverLanguages
            if !serverLanguages.contains(where: { $0.code == selectedLanguageCode }) {
                selectedLanguageCode = serverLanguages.first?.code ?? ""
            }
        }
    }
}

private struct LegendTranslationAccessManager: View {
    @ObservedObject var messages: MessagingStore
    @Environment(\.dismiss) private var dismiss
    @State private var recipients: MobileDataLoadState<[MessagingRecipient]> = .idle
    @State private var search = ""
    @State private var isUpdatingRecipientID: LogicalParticipantIdentity?

    var body: some View {
        NavigationStack {
            LegendScrollView(tracksNavigationChrome: false) {
                VStack(alignment: .leading, spacing: LegendNextSpacing.md) {
                    LegendNextSheetHeader(
                        eyebrow: "Founder controls",
                        title: "Translation access",
                        detail: "Grant or remove Language Translation Access for any active Legend profile.",
                        dismiss: { dismiss() })

                    TextField("Search people", text: $search)
                        .textInputAutocapitalization(.words)
                        .autocorrectionDisabled()
                        .padding(.horizontal, LegendNextSpacing.sm)
                        .frame(minHeight: 44)
                        .background(LegendNextColor.brandBlueSurface, in: RoundedRectangle(
                            cornerRadius: LegendNextRadius.control,
                            style: .continuous))
                        .overlay {
                            RoundedRectangle(
                                cornerRadius: LegendNextRadius.control,
                                style: .continuous)
                                .strokeBorder(LegendNextColor.navy.opacity(0.18), lineWidth: 1)
                        }

                    directoryContent
                }
                .padding(LegendNextSpacing.sm)
                .padding(.bottom, LegendNextSpacing.xl)
            }
            .background(LegendNextCanvas())
            .toolbar(.hidden, for: .navigationBar)
        }
        .tint(LegendNextColor.gold)
        .legendNextSheetChrome(detents: [.large])
        .task { await reloadDirectory() }
        .onChange(of: search) { _, _ in
            Task { await reloadDirectory() }
        }
    }

    @ViewBuilder
    private var directoryContent: some View {
        switch recipients {
        case .idle, .loading:
            ProgressView("Loading the Legend directory")
                .frame(maxWidth: .infinity, minHeight: 144)

        case .unavailable(let failure):
            LegendNextErrorState(
                title: failure.title,
                message: failure.message,
                retryTitle: "Retry",
                retry: { Task { await reloadDirectory() } })

        case .loaded(let directory):
            if directory.isEmpty {
                LegendNextEmptyState(
                    title: "No profiles found",
                    message: "Try a name, username, or email address.",
                    systemImage: "person.crop.circle.badge.questionmark")
            } else {
                LazyVStack(spacing: LegendNextSpacing.xs) {
                    ForEach(directory) { recipient in
                        accessRow(recipient)
                    }
                }
            }
        }
    }

    private func accessRow(_ recipient: MessagingRecipient) -> some View {
        let isGranted = recipient.resourceAccessState == "Granted"
        let isUpdating = isUpdatingRecipientID == recipient.identity
        return HStack(spacing: LegendNextSpacing.sm) {
            LegendProfileAvatar(
                avatar: recipient.avatar,
                displayName: recipient.displayName,
                size: 42)

            VStack(alignment: .leading, spacing: 2) {
                HStack(spacing: 4) {
                    Text(recipient.displayName)
                        .font(.subheadline.weight(.bold))
                        .foregroundStyle(.white)
                    if recipient.isVerified == true {
                        Image(systemName: "checkmark.seal.fill")
                            .font(.caption)
                            .foregroundStyle(LegendNextColor.verified)
                    }
                }
                Text(recipient.email ?? recipient.roleLabel ?? recipient.identity.participantType.rawValue)
                    .font(LegendNextTypography.caption)
                    .foregroundStyle(.white.opacity(0.74))
                    .lineLimit(1)
            }

            Spacer(minLength: 0)

            Button(isUpdating ? "Updating…" : (isGranted ? "Remove" : "Grant")) {
                Task { await update(recipient, isGranted: !isGranted) }
            }
            .buttonStyle(LegendNextButtonStyle(
                kind: isGranted ? .secondary : .primary,
                isFullWidth: false,
                controlHeight: 34))
            .disabled(isUpdating)
        }
        .padding(LegendNextSpacing.sm)
        .background(LegendNextGradient.finance, in: RoundedRectangle(
            cornerRadius: LegendNextRadius.compact,
            style: .continuous))
        .overlay {
            RoundedRectangle(cornerRadius: LegendNextRadius.compact, style: .continuous)
                .strokeBorder(LegendNextColor.gold.opacity(0.58), lineWidth: 1)
        }
    }

    private func reloadDirectory() async {
        recipients = .loading
        guard let loaded = await messages.controlledResourceRecipients(
            .languageTranslation,
            search: search.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty ? nil : search) else {
            recipients = .unavailable(UserFacingFailure(
                title: "Access directory unavailable",
                message: "The Legend directory could not be loaded. Try again.",
                correlationID: nil))
            return
        }
        recipients = .loaded(loaded)
    }

    private func update(_ recipient: MessagingRecipient, isGranted: Bool) async {
        isUpdatingRecipientID = recipient.identity
        defer { isUpdatingRecipientID = nil }
        guard await messages.setControlledResourceGrant(
            .languageTranslation,
            recipient: recipient,
            isGranted: isGranted) else {
            return
        }
        await reloadDirectory()
    }
}

/// The shared, server-backed relationship-list presentation for a profile. It
/// intentionally has no page cap: the social service supplies every authorized
/// follow edge, while this shared elevated directory supplies the scrolling.
private struct LegendFollowListView: View {
    let kind: MobileSocialFollowListKind
    let currentIdentity: LogicalParticipantIdentity

    @ObservedObject private var social: MobileSocialStore
    @State private var state: MobileDataLoadState<[MobileSocialFollowListEntry]> = .idle

    init(
        kind: MobileSocialFollowListKind,
        currentIdentity: LogicalParticipantIdentity,
        social: MobileSocialStore
    ) {
        self.kind = kind
        self.currentIdentity = currentIdentity
        _social = ObservedObject(wrappedValue: social)
    }

    var body: some View {
        Group {
            switch state {
            case .idle, .loading:
                ProgressView("Loading \(kind.title.lowercased())")
                    .frame(maxWidth: .infinity, maxHeight: .infinity)

            case .loaded(let entries):
                if entries.isEmpty {
                    ContentUnavailableView(
                        kind.title,
                        systemImage: kind == .follows ? "person.2" : "person.2.fill",
                        description: Text(kind.emptyMessage))
                } else {
                    LegendScrollView(tracksNavigationChrome: false) {
                        LazyVStack(alignment: .leading, spacing: LegendNextSpacing.sm) {
                            LegendNextSectionHeader(
                                eyebrow: "Legend network",
                                title: kind.title,
                                detail: "(entries.count) member\(entries.count == 1 ? "" : "s")"
                            )

                            ForEach(entries) { entry in
                                NavigationLink {
                                    LegendPublicProfileView(
                                        profile: entry.profile,
                                        currentIdentity: currentIdentity,
                                        social: social,
                                        isFollowing: entry.followedByCurrentActor)
                                } label: {
                                    followRow(entry)
                                }
                                .buttonStyle(.plain)
                                .accessibilityHint("Visit \(entry.profile.displayName)'s profile")
                            }
                        }
                        .padding(.horizontal, LegendNextSpacing.sm)
                        .padding(.vertical, LegendNextSpacing.md)
                    }
                    .scrollIndicators(.hidden)
                }

            case .unavailable(let failure):
                LegendNextErrorState(
                    title: failure.title,
                    message: failure.message,
                    retryTitle: "Retry",
                    retry: { Task { await refresh() } })
                .padding(LegendNextSpacing.sm)
            }
        }
        .background(LegendNextCanvas())
        .navigationTitle(kind.title)
        .navigationBarTitleDisplayMode(.inline)
        .onAppear {
            Task { await refresh() }
        }
        .refreshable {
            await refresh()
        }
    }

    private func followRow(
        _ entry: MobileSocialFollowListEntry
    ) -> some View {
        LegendContactCard(
            displayName: entry.profile.displayName,
            subtitle: entry.profile.identity.participantType == .agent
                ? entry.profile.roleLabel
                : entry.profile.username.map { "@\($0)" },
            detail: entry.followedByCurrentActor && kind == .followers
                ? "Following"
                : nil,
            isVerified: entry.profile.isVerified == true,
            avatar: {
                LegendProfileAvatar(
                    avatar: entry.profile.avatar,
                    displayName: entry.profile.displayName,
                    size: 46)
            },
            action: {
                Image(systemName: "chevron.right")
                    .font(.caption.weight(.bold))
                    .foregroundStyle(LegendNextColor.contactAction)
            }
        )
    }

    private func refresh() async {
        state = .loading
        async let profileRefresh = social.refresh()
        let list = await social.followList(kind: kind)
        _ = await profileRefresh
        state = list
    }
}

private struct LegendProfileContactLink: View {
    enum Kind {
        case website
        case email
        case phone

        var systemImage: String {
            switch self {
            case .website: "link"
            case .email: "envelope"
            case .phone: "phone"
            }
        }

        var accessibilityAction: String {
            switch self {
            case .website: "Open website"
            case .email: "Compose email"
            case .phone: "Call phone number"
            }
        }
    }

    let value: String
    let kind: Kind

    var body: some View {
        if let destination {
            Link(destination: destination) {
                label
            }
            .accessibilityHint(kind.accessibilityAction)
        } else {
            label
        }
    }

    private var label: some View {
        Label(value, systemImage: kind.systemImage)
            .font(LegendNextTypography.caption.weight(.semibold))
            .foregroundStyle(kind == .website ? LegendNextColor.gold : LegendNextColor.textSecondary)
            .lineLimit(1)
            .contentShape(Rectangle())
    }

    private var destination: URL? {
        let trimmed = value.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !trimmed.isEmpty else { return nil }

        switch kind {
        case .website:
            let candidate = trimmed.contains("://") ? trimmed : "https://\(trimmed)"
            guard let components = URLComponents(string: candidate),
                  let scheme = components.scheme?.lowercased(),
                  ["http", "https"].contains(scheme),
                  !(components.host?.isEmpty ?? true) else {
                return nil
            }
            return components.url

        case .email:
            guard !trimmed.contains(where: { $0.isWhitespace }) else { return nil }
            var components = URLComponents()
            components.scheme = "mailto"
            components.path = trimmed
            return components.url

        case .phone:
            let phone = trimmed.filter { $0.isNumber || $0 == "+" || $0 == "*" || $0 == "#" }
            guard !phone.isEmpty else { return nil }
            var components = URLComponents()
            components.scheme = "tel"
            components.path = phone
            return components.url
        }
    }
}

/// The single public profile route for Discover, feed posts, Follows, and
/// Followers. Identity, counters, and updates are independently fetched from
/// the social authority; this view never turns directory metadata into a
/// profile mockup.
struct LegendPublicProfileView: View {
    let profile: MobileSocialAuthor
    let currentIdentity: LogicalParticipantIdentity

    @ObservedObject private var social: MobileSocialStore
    @State private var metricsState: MobileDataLoadState<MobileSocialProfileMetrics> = .idle
    @State private var postsState: MobileDataLoadState<[MobileSocialPost]> = .idle
    @State private var isFollowing: Bool
    @State private var isFollowRequestPending: Bool
    @State private var isUpdatingFollow = false
    @State private var isConnectionActive: Bool
    @State private var isRemovingConnection = false
    @State private var verificationReview: VerificationReview?
    @State private var isResolvingVerification = false
    private let journeyConnectionID: UUID?
    private let disconnectConnection: ((UUID) async -> Bool)?
    private let performVerificationResolution: ((VerificationReview, Bool) async -> Bool)?

    init(
        profile: MobileSocialAuthor,
        currentIdentity: LogicalParticipantIdentity,
        social: MobileSocialStore,
        isFollowing: Bool,
        isFollowRequestPending: Bool = false,
        journeyConnectionID: UUID? = nil,
        disconnectConnection: ((UUID) async -> Bool)? = nil,
        verificationReview: VerificationReview? = nil,
        resolveVerification: ((VerificationReview, Bool) async -> Bool)? = nil
    ) {
        self.profile = profile
        self.currentIdentity = currentIdentity
        _social = ObservedObject(wrappedValue: social)
        _isFollowing = State(initialValue: isFollowing)
        _isFollowRequestPending = State(initialValue: isFollowRequestPending)
        _isConnectionActive = State(initialValue: journeyConnectionID != nil)
        _verificationReview = State(initialValue: verificationReview)
        self.journeyConnectionID = journeyConnectionID
        self.disconnectConnection = disconnectConnection
        performVerificationResolution = resolveVerification
    }

    var body: some View {
        LegendScrollView {
            VStack(alignment: .leading, spacing: LegendNextSpacing.lg) {
                profileHero

                if profile.identity != currentIdentity {
                    Button {
                        Task { await toggleFollow() }
                    } label: {
                        Text(followTitle)
                            .frame(maxWidth: .infinity)
                    }
                    .buttonStyle(LegendProfileActionButtonStyle())
                    .disabled(isUpdatingFollow)
                    .accessibilityHint(isFollowing
                        ? "Stop following \(displayedProfile.displayName)"
                        : isFollowRequestPending
                            ? "Cancel your follow request to \(displayedProfile.displayName)"
                            : "Follow \(displayedProfile.displayName)")
                }

                if isConnectionActive,
                   let journeyConnectionID,
                   let disconnectConnection {
                    Button(role: .destructive) {
                        Task {
                            isRemovingConnection = true
                            defer { isRemovingConnection = false }
                            if await disconnectConnection(journeyConnectionID) {
                                isConnectionActive = false
                            }
                        }
                    } label: {
                        if isRemovingConnection {
                            ProgressView()
                                .frame(maxWidth: .infinity)
                        } else {
                            Text("Remove connection")
                                .frame(maxWidth: .infinity)
                        }
                    }
                    .buttonStyle(LegendNextButtonStyle(kind: .destructive, isFullWidth: true))
                    .disabled(isRemovingConnection)
                }

                verificationDecisionSection

                aboutSection
                updatesSection
            }
            .padding(LegendNextSpacing.md)
            .frame(maxWidth: .infinity, alignment: .leading)
        }
        .background(LegendNextCanvas())
        .navigationTitle(displayedProfile.displayName)
        .navigationBarTitleDisplayMode(.inline)
        .onAppear {
            Task { await refresh() }
        }
        .refreshable {
            await refresh()
        }
    }

    @ViewBuilder
    private var verificationDecisionSection: some View {
        if let verificationReview,
           verificationReview.canResolve,
           verificationReview.status == "Pending" {
            LegendNextSurface(style: .navy) {
                VStack(alignment: .leading, spacing: LegendNextSpacing.sm) {
                    Label("\(verificationReview.resourceType.displayName) review", systemImage: "checkmark.seal.fill")
                        .font(.headline.weight(.bold))
                        .foregroundStyle(.white)

                    Text("This member requested \(verificationReview.resourceType.displayName). Your decision is recorded in the private founder review queue.")
                        .font(.footnote)
                        .foregroundStyle(.white.opacity(0.74))
                        .fixedSize(horizontal: false, vertical: true)

                    HStack(spacing: LegendNextSpacing.sm) {
                        Button(role: .destructive) {
                            Task { await resolveVerification(verificationReview, approve: false) }
                        } label: {
                            Text("Decline")
                                .frame(maxWidth: .infinity)
                        }
                        .buttonStyle(LegendNextButtonStyle(kind: .secondary, controlHeight: 40))
                        .disabled(isResolvingVerification)

                        Button {
                            Task { await resolveVerification(verificationReview, approve: true) }
                        } label: {
                            if isResolvingVerification {
                                ProgressView()
                                    .tint(LegendNextColor.midnight)
                                    .frame(maxWidth: .infinity)
                            } else {
                                Text("Approve")
                                    .frame(maxWidth: .infinity)
                            }
                        }
                        .buttonStyle(LegendNextButtonStyle(kind: .primary, controlHeight: 40))
                        .disabled(isResolvingVerification)
                    }
                }
            }
        }
    }

    private func resolveVerification(
        _ review: VerificationReview,
        approve: Bool
    ) async {
        guard let performVerificationResolution else { return }
        isResolvingVerification = true
        defer { isResolvingVerification = false }
        guard await performVerificationResolution(review, approve) else { return }
        verificationReview = nil
        await refresh()
    }

    private var displayedProfile: MobileSocialAuthor {
        if case .loaded(let metrics) = metricsState {
            return metrics.profile
        }
        return profile
    }

    private var followTitle: String {
        if isFollowing { return "Following" }
        if isFollowRequestPending { return "Requested" }
        return displayedProfile.isPrivate == true ? "Request to follow" : "Follow"
    }

    private var identityHeader: some View {
        HStack(alignment: .top, spacing: LegendNextSpacing.md) {
            LegendProfileAvatar(
                avatar: displayedProfile.avatar,
                displayName: displayedProfile.displayName,
                size: 92)

            VStack(alignment: .leading, spacing: 5) {
                LegendVerifiedName(
                    displayedProfile.displayName,
                    isVerified: displayedProfile.isVerified == true,
                    font: .title2.weight(.bold)
                )

                if let roleLabel = normalized(displayedProfile.roleLabel) {
                    Text(roleLabel)
                        .font(.caption2.weight(.bold))
                        .foregroundStyle(LegendNextColor.navy)
                        .padding(.horizontal, 8)
                        .padding(.vertical, 4)
                        .background(LegendNextColor.gold.opacity(0.22), in: Capsule())
                }

                if let username = normalized(displayedProfile.username) {
                    Text("@\(username)")
                        .font(.subheadline.weight(.semibold))
                        .foregroundStyle(LegendNextColor.gold)
                }
            }

            Spacer(minLength: 0)
        }
    }

    private var profileHero: some View {
        LegendNextSurface(
            style: .elevated,
            cornerRadius: LegendNextRadius.prominentCard,
            padding: LegendNextSpacing.intermediate
        ) {
            VStack(alignment: .leading, spacing: LegendNextSpacing.md) {
                identityHeader
                LegendNextDivider()
                metricRow
            }
        }
    }

    private var metricRow: some View {
            HStack(spacing: LegendNextSpacing.md) {
                metric(value: loadedMetrics?.videoCount, title: "Hacs")
                metric(value: loadedMetrics?.followingCount, title: "Following")
                metric(value: loadedMetrics?.followerCount, title: "Followers")
            }
            .frame(maxWidth: .infinity)
    }

    @ViewBuilder
    private var aboutSection: some View {
        let bio = normalized(displayedProfile.bio)
        let location = normalized(displayedProfile.location)
        let website = normalized(displayedProfile.website)
        let publicEmail = normalized(displayedProfile.publicEmail)
        let publicPhone = normalized(displayedProfile.publicPhone)
        if bio != nil || location != nil || website != nil || publicEmail != nil || publicPhone != nil {
            LegendNextSurface(style: .plain) {
                VStack(alignment: .leading, spacing: LegendNextSpacing.xs) {
                    Text("About")
                        .font(LegendNextTypography.section)
                        .foregroundStyle(LegendNextColor.textPrimary)

                    if let bio {
                        Text(bio)
                            .font(LegendNextTypography.body)
                            .foregroundStyle(LegendNextColor.textPrimary)
                            .fixedSize(horizontal: false, vertical: true)
                    }

                    if let location {
                        Label(location, systemImage: "mappin.and.ellipse")
                            .font(LegendNextTypography.supporting)
                            .foregroundStyle(LegendNextColor.textSecondary)
                    }

                    if let website {
                        LegendProfileContactLink(value: website, kind: .website)
                    }

                    // Public contact is deliberately last, and only appears when the
                    // member explicitly enabled it in mobile profile settings.
                    if let publicEmail {
                        LegendProfileContactLink(value: publicEmail, kind: .email)
                    }

                    if let publicPhone {
                        LegendProfileContactLink(value: publicPhone, kind: .phone)
                    }
                }
                .frame(maxWidth: .infinity, alignment: .leading)
            }
        }
    }

    @ViewBuilder
    private var updatesSection: some View {
        VStack(alignment: .leading, spacing: LegendNextSpacing.sm) {
            Text("Updates")
                .font(LegendNextTypography.section)
                .foregroundStyle(LegendNextColor.textPrimary)

            switch postsState {
            case .idle, .loading:
                ProgressView("Loading updates")
                    .frame(maxWidth: .infinity, minHeight: 90)

            case .loaded(let posts):
                if posts.isEmpty {
                    LegendNextEmptyState(
                        title: "No updates yet",
                        message: "This member has not shared a public Legend update.",
                        systemImage: "rectangle.stack")
                } else {
                    ForEach(posts) { post in
                        LegendPublicProfilePost(
                            post: post,
                            currentIdentity: currentIdentity,
                            social: social)
                    }
                }

            case .unavailable(let failure):
                LegendNextInsetSurface {
                    VStack(alignment: .leading, spacing: 4) {
                        Text("Updates unavailable")
                            .font(.subheadline.weight(.semibold))
                            .foregroundStyle(LegendNextColor.textPrimary)
                        Text(failure.message)
                            .font(LegendNextTypography.supporting)
                            .foregroundStyle(LegendNextColor.textSecondary)
                    }
                    .frame(maxWidth: .infinity, alignment: .leading)
                }
            }
        }
    }

    private var loadedMetrics: MobileSocialProfileMetrics? {
        if case .loaded(let metrics) = metricsState {
            return metrics
        }
        return nil
    }

    private func metric(value: Int?, title: String) -> some View {
        VStack(spacing: 2) {
            Text(value?.formatted() ?? "—")
                .font(.headline.weight(.bold))
                .foregroundStyle(LegendNextColor.textPrimary)
                .contentTransition(.numericText())

            Text(title)
                .font(.caption)
                .foregroundStyle(LegendNextColor.textSecondary)
        }
        .frame(minWidth: 64)
    }

    private func normalized(_ value: String?) -> String? {
        guard let normalized = value?
            .trimmingCharacters(in: .whitespacesAndNewlines),
              !normalized.isEmpty else {
            return nil
        }

        return normalized
    }

    private func refresh() async {
        metricsState = .loading
        postsState = .loading
        async let metrics = social.profileMetrics(for: profile)
        async let posts = social.publicProfilePosts(for: profile)
        metricsState = await metrics
        postsState = await posts
    }

    private func toggleFollow() async {
        guard !isUpdatingFollow else { return }
        isUpdatingFollow = true
        defer { isUpdatingFollow = false }

        guard let confirmed = await social.setFollow(
            userID: profile.identity.userID,
            participantType: profile.identity.participantType,
            isFollowing: !isFollowing) else {
            return
        }

        isFollowing = confirmed.isFollowing
        isFollowRequestPending = confirmed.hasPendingRequest
        await refresh()
    }
}

/// Renders the actual post DTO returned by the profile endpoint. It deliberately
/// shares the feed's canonical image and video loaders rather than duplicating
/// remote-media handling for profile pages.
private struct LegendPublicProfilePost: View {
    let post: MobileSocialPost
    let currentIdentity: LogicalParticipantIdentity
    @ObservedObject var social: MobileSocialStore

    var body: some View {
        NavigationLink {
            LegendPostDetailView(
                post: post,
                currentIdentity: currentIdentity,
                social: social)
        } label: {
            LegendNextSurface {
                VStack(alignment: .leading, spacing: LegendNextSpacing.sm) {
                    if let media = post.media.first {
                        if media.isImage {
                            LegendSocialMediaImage(
                                media: media,
                                social: social,
                                contentMode: .fit,
                                placeholderHeight: 220)
                        } else if media.isVideo {
                            LegendSocialMediaVideo(
                                postID: post.id,
                                media: media,
                                music: post.music,
                                social: social)
                        }
                    }

                    if !post.body.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
                        Text(post.body)
                            .font(LegendNextTypography.body)
                            .foregroundStyle(LegendNextColor.textPrimary)
                            .fixedSize(horizontal: false, vertical: true)
                    }

                    HStack(spacing: LegendNextSpacing.sm) {
                        Text(post.displayContentType)
                        Text(post.postedUTC.formatted(date: .abbreviated, time: .omitted))
                        Spacer(minLength: 0)
                        Label(post.reactionCount.formatted(), systemImage: "heart")
                        Label(post.commentCount.formatted(), systemImage: "bubble.right")
                    }
                    .font(LegendNextTypography.caption)
                    .foregroundStyle(LegendNextColor.textSecondary)
                }
            }
        }
        .buttonStyle(.plain)
        .accessibilityLabel("Open \(post.displayContentType) by \(post.author.displayName)")
    }
}

private struct LegendProfileActionButtonStyle: ButtonStyle {
    func makeBody(
        configuration: Configuration
    ) -> some View {
        LegendNextButtonStyle(
            kind: .secondary,
            controlHeight: 40
        )
        .makeBody(configuration: configuration)
    }
}

private enum LegendProfileGridLayout {
    static let columns = [
        GridItem(.flexible(), spacing: LegendNextSpacing.tiny),
        GridItem(.flexible(), spacing: LegendNextSpacing.tiny),
        GridItem(.flexible(), spacing: LegendNextSpacing.tiny)
    ]
    static let tileAspectRatio: CGFloat = 4 / 5
}

/// The profile's loading and loaded states deliberately share this frame so a
/// completed refresh cannot change the grid from its established portrait layout.
private struct LegendProfileGridCell<Content: View>: View {
    private let content: Content

    init(@ViewBuilder content: () -> Content) {
        self.content = content()
    }

    var body: some View {
        content
            .frame(maxWidth: .infinity)
            .aspectRatio(
                LegendProfileGridLayout.tileAspectRatio,
                contentMode: .fit)
    }
}

private struct LegendProfileGridTile: View {
    let post: MobileSocialPost
    @ObservedObject var social: MobileSocialStore

    var body: some View {
        GeometryReader { proxy in
            ZStack {
                if let media = post.media.first(where: \.isImage) {
                    LegendSocialMediaImage(
                        media: media,
                        social: social,
                        contentMode: .fill,
                        placeholderHeight: proxy.size.height,
                        usesRoundedCorners: false
                    )
                    .frame(width: proxy.size.width, height: proxy.size.height)
                    .clipped()
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
                        Image(systemName: post.contentType == MobileSocialContentType.hac.rawValue
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

                if post.contentType == MobileSocialContentType.hac.rawValue {
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
        }
        .clipShape(RoundedRectangle(
            cornerRadius: LegendNextRadius.compact,
            style: .continuous
        ))
        .overlay {
            RoundedRectangle(
                cornerRadius: LegendNextRadius.compact,
                style: .continuous
            )
            .strokeBorder(LegendNextColor.navy.opacity(0.20), lineWidth: 1)
        }
        .shadow(
            color: LegendNextColor.navy.opacity(0.10),
            radius: 5,
            y: 2
        )
        .accessibilityElement(children: .ignore)
        .accessibilityLabel(
            "\(post.displayContentType) by \(post.author.displayName): \(post.body)"
        )
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
    @State private var username: String
    @State private var bio: String
    @State private var website: String
    @State private var location: String
    @State private var profileEmail: String
    @State private var isEmailVisible: Bool
    @State private var isPhoneVisible: Bool

    init(profile: MobileAccountProfile, store: MobileAccountStore) {
        self.profile = profile
        _store = ObservedObject(wrappedValue: store)
        _displayName = State(initialValue: profile.displayName)
        _phone = State(initialValue: profile.phone ?? "")
        _title = State(initialValue: profile.title ?? "")
        _shortBio = State(initialValue: profile.shortBio ?? "")
        _username = State(initialValue: profile.username ?? "")
        _bio = State(initialValue: profile.bio ?? "")
        _website = State(initialValue: profile.website ?? "")
        _location = State(initialValue: profile.location ?? "")
        _profileEmail = State(initialValue: profile.profileEmail ?? "")
        _isEmailVisible = State(initialValue: profile.isEmailVisible)
        _isPhoneVisible = State(initialValue: profile.isPhoneVisible)
    }

    var body: some View {
        NavigationStack {
            LegendScrollView(tracksNavigationChrome: false) {
                VStack(alignment: .leading, spacing: LegendNextSpacing.lg) {
                    LegendNextSheetHeader(
                        eyebrow: "Your identity",
                        title: "Shape your Legend",
                        detail: "These details are specific to the mobile Legend experience. Your secure account email is never shown automatically.",
                        dismiss: { dismiss() }
                    )

                    LegendProfileSettingsSection(title: "Identity") {
                        VStack(spacing: LegendNextSpacing.md) {
                            LegendProfileEditorField(
                                title: "Display name",
                                prompt: "How people know you",
                                text: $displayName,
                                contentType: .name)
                            LegendProfileEditorField(
                                title: "Username",
                                prompt: "your.legend",
                                text: $username,
                                autocapitalization: .never,
                                autocorrectionDisabled: true)

                            if store.isCheckingUsername {
                                Label("Checking username…", systemImage: "clock")
                                    .font(LegendNextTypography.caption)
                                    .foregroundStyle(.white.opacity(0.76))
                            } else if let availability = store.usernameAvailability {
                                Label(
                                    availability.message ?? "Username available",
                                    systemImage: availability.isAvailable
                                        ? "checkmark.circle"
                                        : "exclamationmark.circle")
                                    .font(LegendNextTypography.caption.weight(.semibold))
                                    .foregroundStyle(availability.isAvailable
                                        ? LegendNextColor.goldBright
                                        : .red)
                            }

                            Text(usernameChangeLimitDetail)
                                .font(LegendNextTypography.caption)
                                .foregroundStyle(.white.opacity(0.78))
                                .fixedSize(horizontal: false, vertical: true)
                        }
                    }

                    LegendProfileSettingsSection(title: "About") {
                        VStack(spacing: LegendNextSpacing.md) {
                            LegendProfileEditorField(
                                title: "Bio",
                                prompt: "Tell your story",
                                text: $bio,
                                isMultiline: true)
                            LegendProfileEditorField(
                                title: "Website",
                                prompt: "https://example.com",
                                text: $website,
                                keyboardType: .URL,
                                autocapitalization: .never,
                                autocorrectionDisabled: true)
                            LegendProfileEditorField(
                                title: "Location",
                                prompt: "City, state, or region",
                                text: $location)
                        }
                    }

                    LegendProfileSettingsSection(title: "Contact privacy") {
                        VStack(spacing: LegendNextSpacing.md) {
                            LegendProfileEditorField(
                                title: "Profile email",
                                prompt: "The email you want to share",
                                text: $profileEmail,
                                keyboardType: .emailAddress,
                                contentType: .emailAddress,
                                autocapitalization: .never,
                                autocorrectionDisabled: true)

                            Toggle(isOn: $isEmailVisible) {
                                VStack(alignment: .leading, spacing: 3) {
                                    Text("Show email on profile")
                                        .font(.subheadline.weight(.semibold))
                                        .foregroundStyle(.white)
                                    Text("Only this member-entered address is shown. Your account email remains private.")
                                        .font(LegendNextTypography.caption)
                                        .foregroundStyle(.white.opacity(0.76))
                                }
                            }
                            .tint(LegendNextColor.goldBright)

                            LegendProfileEditorField(
                                title: "Phone",
                                prompt: "Phone number",
                                text: $phone,
                                keyboardType: .phonePad,
                                contentType: .telephoneNumber)

                            Toggle(isOn: $isPhoneVisible) {
                                VStack(alignment: .leading, spacing: 3) {
                                    Text("Show phone on profile")
                                        .font(.subheadline.weight(.semibold))
                                        .foregroundStyle(.white)
                                    Text("Your number stays private unless you turn this on.")
                                        .font(LegendNextTypography.caption)
                                        .foregroundStyle(.white.opacity(0.76))
                                }
                            }
                            .tint(LegendNextColor.goldBright)
                        }
                    }

                    if profile.participantType == .agent {
                        LegendProfileSettingsSection(title: "Account details") {
                            VStack(spacing: LegendNextSpacing.md) {
                                LegendProfileEditorField(
                                    title: "Professional title",
                                    prompt: "Advisor, coach, or specialist",
                                    text: $title,
                                    contentType: .jobTitle)
                                LegendProfileEditorField(
                                    title: "Professional introduction",
                                    prompt: "Optional",
                                    text: $shortBio,
                                    isMultiline: true)
                            }
                        }
                    }

                    Button(store.isSaving ? "Saving changes…" : "Save changes") {
                        saveChanges()
                    }
                    .buttonStyle(LegendNextButtonStyle(kind: .primary))
                    .disabled(
                        store.isSaving
                            || store.isCheckingUsername
                            || store.isUsernameUnavailable
                            || displayName.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty
                    )
                }
                .padding(LegendNextSpacing.sm)
                .padding(.bottom, LegendNextSpacing.xl)
            }
            .background(LegendNextCanvas())
            .toolbar(.hidden, for: .navigationBar)
        }
        .tint(LegendNextColor.gold)
        .legendNextSheetChrome(detents: [.large])
        .onChange(of: username) { _, newUsername in
            store.checkUsernameAvailability(newUsername)
        }
    }

    private func saveChanges() {
        Task {
            let didSave = await store.save(MobileAccountUpdate(
                displayName: displayName,
                phone: phone,
                title: profile.participantType == .agent ? title : nil,
                shortBio: profile.participantType == .agent ? shortBio : nil,
                username: username,
                bio: bio,
                website: website,
                location: location,
                publicEmail: profileEmail,
                isEmailVisible: isEmailVisible,
                isPhoneVisible: isPhoneVisible))
            if didSave {
                dismiss()
            }
        }
    }

    private var usernameChangeLimitDetail: String {
        let remaining = profile.usernameChangesRemaining
        let remainingDetail = remaining == 1
            ? "1 username change remaining this month."
            : "\(remaining) username changes remaining this month."
        return "Your username is searchable across Legend. Your first username is a reservation; after that, you can change it twice per calendar month. \(remainingDetail)"
    }
}

private struct LegendProfileSettingsSection<Content: View>: View {
    let title: String
    @ViewBuilder let content: Content

    init(
        title: String,
        @ViewBuilder content: () -> Content
    ) {
        self.title = title
        self.content = content()
    }

    var body: some View {
        VStack(alignment: .leading, spacing: LegendNextSpacing.micro) {
            Text(title.uppercased())
                .font(LegendNextTypography.eyebrow)
                .foregroundStyle(LegendNextColor.goldBright)

            LegendNextSurface(style: .profileSettings, padding: LegendNextSpacing.xs) {
                content
            }
        }
    }
}

private struct LegendProfileSettingsRow: View {
    let title: String
    let detail: String?
    let systemImage: String
    let showsChevron: Bool
    var isDestructive = false

    var body: some View {
        HStack(spacing: LegendNextSpacing.sm) {
            Image(systemName: systemImage)
                .font(.body.weight(.semibold))
                .foregroundStyle(isDestructive ? LegendNextColor.danger : LegendNextColor.goldBright)
                .frame(width: 26)

            VStack(alignment: .leading, spacing: 2) {
                Text(title)
                    .font(.subheadline.weight(.semibold))
                    .foregroundStyle(isDestructive ? LegendNextColor.danger : .white)

                if let detail {
                    Text(detail)
                        .font(LegendNextTypography.caption)
                        .foregroundStyle(.white.opacity(0.76))
                }
            }

            Spacer(minLength: LegendNextSpacing.sm)

            if showsChevron {
                Image(systemName: "chevron.right")
                    .font(.caption.weight(.bold))
                    .foregroundStyle(.white.opacity(0.82))
            }
        }
        .padding(.vertical, LegendNextSpacing.micro)
        .contentShape(Rectangle())
    }
}

private struct LegendProfileSettingsToggleRow: View {
    let title: String
    let detail: String
    let systemImage: String
    @Binding var isOn: Bool

    var body: some View {
        HStack(spacing: LegendNextSpacing.sm) {
            Image(systemName: systemImage)
                .font(.body.weight(.semibold))
                .foregroundStyle(LegendNextColor.goldBright)
                .frame(width: 26)

            VStack(alignment: .leading, spacing: 2) {
                Text(title)
                    .font(.subheadline.weight(.semibold))
                    .foregroundStyle(.white)
                Text(detail)
                    .font(LegendNextTypography.caption)
                    .foregroundStyle(.white.opacity(0.76))
            }

            Spacer(minLength: LegendNextSpacing.xs)

            Toggle(title, isOn: $isOn)
                .labelsHidden()
                .tint(LegendNextColor.goldBright)
        }
        .padding(.vertical, LegendNextSpacing.micro)
    }
}

private struct LegendProfileSettingsDivider: View {
    var body: some View {
        Rectangle()
            .fill(Color.white.opacity(0.22))
            .frame(height: 1)
            .padding(.leading, 36)
    }
}

private struct LegendProfileEditorField: View {
    let title: String
    let prompt: String
    @Binding var text: String
    var keyboardType: UIKeyboardType = .default
    var contentType: UITextContentType? = nil
    var autocapitalization: TextInputAutocapitalization? = .sentences
    var autocorrectionDisabled = false
    var isMultiline = false

    var body: some View {
        VStack(alignment: .leading, spacing: LegendNextSpacing.tiny) {
            Text(title)
                .font(.caption.weight(.semibold))
                .foregroundStyle(.white.opacity(0.84))

            LegendNextInsetSurface(style: .profileSettings) {
                Group {
                    if isMultiline {
                        TextField(prompt, text: $text, axis: .vertical)
                            .lineLimit(3...6)
                    } else {
                        TextField(prompt, text: $text)
                    }
                }
                .font(.subheadline)
                .foregroundStyle(LegendNextColor.textPrimary)
                .textInputAutocapitalization(autocapitalization)
                .autocorrectionDisabled(autocorrectionDisabled)
                .keyboardType(keyboardType)
                .textContentType(contentType)
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
