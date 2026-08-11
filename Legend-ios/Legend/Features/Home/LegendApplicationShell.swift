import SwiftUI
import UIKit
import PhotosUI

private func legendMobileAvatarUpdate(from imageData: Data) -> MobileAccountAvatarUpdate? {
    guard let image = UIImage(data: imageData) else { return nil }

    let maximumSide: CGFloat = 512
    let scale = min(1, maximumSide / max(image.size.width, image.size.height))
    let targetSize = CGSize(
        width: max(1, image.size.width * scale),
        height: max(1, image.size.height * scale))
    let normalizedImage = UIGraphicsImageRenderer(size: targetSize).image { _ in
        image.draw(in: CGRect(origin: .zero, size: targetSize))
    }

    for quality in [CGFloat(0.74), 0.60, 0.48, 0.36] {
        guard let content = normalizedImage.jpegData(compressionQuality: quality) else {
            continue
        }
        if content.count <= 384 * 1_024 {
            return MobileAccountAvatarUpdate(base64Content: content.base64EncodedString())
        }
    }
    return nil
}

private enum LegendAppTab: String, Identifiable {
    case home
    case clients
    case discover
    case fyp
    case messages
    case account

    var id: Self { self }

    static func available(for participantType: ParticipantType) -> [Self] {
        // Agents get Discover too. Their scope is the clients they own, resolved on
        // the server; it is not the client community directory.
        participantType == .agent
            ? [.home, .clients, .discover, .fyp, .messages, .account]
            : [.home, .discover, .fyp, .messages, .account]
    }

    var title: String {
        switch self {
        case .home: return LegendSharedDesign.copy("tab.home")
        case .clients: return LegendSharedDesign.copy("tab.clients")
        case .discover: return LegendSharedDesign.copy("tab.discover")
        case .fyp: return LegendSharedDesign.copy("tab.forYou")
        case .messages: return LegendSharedDesign.copy("tab.messages")
        case .account: return LegendSharedDesign.copy("tab.account")
        }
    }

    var symbolName: String {
        switch self {
        case .home: return "house"
        case .clients: return "person.2"
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
        case .discover: return "magnifyingglass"
        case .fyp: return "play.rectangle.on.rectangle.fill"
        case .messages: return "message.fill"
        case .account: return "person.fill"
        }
    }
}

/// Routes attached to the stable account NavigationStack. Profile and finance
/// data refresh independently, so a refresh must never dismiss navigation a
/// member explicitly initiated.
private enum LegendAccountNavigationRoute: Hashable {
    case financialIntelligence
}

struct LegendApplicationShell: View {
    @EnvironmentObject private var scrollChrome: LegendScrollChrome
    @Environment(\.accessibilityReduceMotion) private var reduceMotion

    let currentSession: MobileSession
    let onSignOut: () -> Void
    @ObservedObject private var coordinator: MobileSessionCoordinator
    @ObservedObject private var bootstrap: LegendApplicationBootstrapCoordinator
    @ObservedObject private var account: MobileAccountStore
    @State private var selectedTab: LegendAppTab = .home
    @ObservedObject private var messages: MessagingStore
    @ObservedObject private var social: MobileSocialStore
    @ObservedObject private var activity: LegendDailyActivityStore
    @State private var isMessageThreadActive = false
    @State private var pendingMessageConversationID: UUID?
    @State private var accountNavigationPath: [LegendAccountNavigationRoute] = []

    init(
        currentSession: MobileSession,
        coordinator: MobileSessionCoordinator,
        bootstrap: LegendApplicationBootstrapCoordinator,
        onSignOut: @escaping () -> Void
    ) {
        self.currentSession = currentSession
        self.onSignOut = onSignOut
        _coordinator = ObservedObject(wrappedValue: coordinator)
        _bootstrap = ObservedObject(wrappedValue: bootstrap)
        _account = ObservedObject(wrappedValue: bootstrap.stores.account)
        _messages = ObservedObject(wrappedValue: bootstrap.stores.messaging)
        _social = ObservedObject(wrappedValue: bootstrap.stores.social)
        _activity = ObservedObject(wrappedValue: bootstrap.activity)
    }

    var body: some View {
        Group {
            if let lifecycle = account.lifecycle, lifecycle.blocksFullExperience {
                LegendAccountLifecycleLockedView(
                    lifecycle: lifecycle,
                    account: account,
                    coordinator: coordinator,
                    onSignOut: onSignOut)
            } else {
                fullExperience
            }
        }
        // Global sharing consumes the SAME account-scoped MessagingStore that
        // owns the Messages tab. This environment value is a reference only:
        // it creates no second store, recipient directory, inbox, or send path.
        .environment(\.legendMessagingStore, messages)
        .task {
            await account.loadLifecycle()
        }
    }

    private var fullExperience: some View {
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
                    accountAvatar: activeAccountAvatar,
                    accountDisplayName: activeAccountDisplayName,
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
            if selectedTab != .account {
                accountNavigationPath.removeAll()
            }
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
                    messages: messages,
                    activity: activity,
                    bootstrap: bootstrap,
                    selectedTab: $selectedTab,
                    pendingMessageConversationID: $pendingMessageConversationID
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
            NavigationStack(path: $accountNavigationPath) {
                LegendAccountView(
                    currentSession: currentSession,
                    coordinator: coordinator,
                    account: bootstrap.stores.account,
                    messages: messages,
                    social: social,
                    bootstrap: bootstrap,
                    openFinancialIntelligence: openFinancialIntelligence,
                    onSignOut: onSignOut
                )
                .navigationDestination(for: LegendAccountNavigationRoute.self) { route in
                    switch route {
                    case .financialIntelligence:
                        LegendFinanceView(
                            currentSession: currentSession,
                            store: bootstrap.stores.financial,
                            bootstrap: bootstrap
                        )
                    }
                }
            }
            .task {
                bootstrap.stores.account.load()
                social.loadProfilePosts()
            }
        }
    }

    private func openFinancialIntelligence() {
        guard !accountNavigationPath.contains(.financialIntelligence) else {
            return
        }
        accountNavigationPath.append(.financialIntelligence)
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
        activity.unreadBadgeCount
    }

    private var activeAccountAvatar: ProfileAvatar? {
        guard case .loaded(let profile) = account.state else {
            return currentSession.actor.avatar
        }
        return profile.avatar
    }

    private var activeAccountDisplayName: String {
        guard case .loaded(let profile) = account.state else {
            return currentSession.actor.displayName
        }
        return profile.displayName
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

/// A paused or closing account has no route to regular app content. The
/// lifecycle response is authoritative; this view offers only the permitted
/// recovery action and sign out.
private struct LegendAccountLifecycleLockedView: View {
    let lifecycle: MobileAccountLifecycle
    @ObservedObject var account: MobileAccountStore
    @ObservedObject var coordinator: MobileSessionCoordinator
    let onSignOut: () -> Void

    var body: some View {
        VStack(spacing: LegendNextSpacing.md) {
            Spacer(minLength: LegendNextSpacing.xl)

            Image(systemName: lifecycle.canResume ? "pause.circle.fill" : "lock.fill")
                .font(.system(size: 44, weight: .semibold))
                .foregroundStyle(LegendNextColor.goldBright)

            Text(lifecycle.canResume ? "Your account is paused" : "Account deletion is in progress")
                .font(LegendNextTypography.title)
                .foregroundStyle(LegendNextColor.textPrimary)
                .multilineTextAlignment(.center)

            Text(lifecycle.message ?? lifecycleDetail)
                .font(.body)
                .foregroundStyle(LegendNextColor.textSecondary)
                .multilineTextAlignment(.center)
                .fixedSize(horizontal: false, vertical: true)
                .frame(maxWidth: 360)

            if lifecycle.canResume {
                Button(account.isUpdatingLifecycle ? "Resuming account…" : "Resume account") {
                    Task { _ = await account.resumeAccount() }
                }
                .buttonStyle(LegendNextButtonStyle(kind: .primary))
                .disabled(account.isUpdatingLifecycle)
                .padding(.top, LegendNextSpacing.xs)
            }

            if let failure = account.actionFailure {
                Text(failure.message)
                    .font(.footnote)
                    .foregroundStyle(LegendNextColor.textSecondary)
                    .multilineTextAlignment(.center)
            }

            Button("Sign out") {
                onSignOut()
            }
            .buttonStyle(LegendNextButtonStyle(kind: .secondary))

            Spacer()
        }
        .padding(LegendNextSpacing.md)
        .frame(maxWidth: .infinity, maxHeight: .infinity)
        .background(LegendNextCanvas())
        .accessibilityElement(children: .contain)
    }

    private var lifecycleDetail: String {
        lifecycle.canResume
            ? "Legend access is paused. Resume your account here when you are ready to return."
            : "Legend access is disabled while the required account and data-handling process is completed."
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

            Text("LEGEND®")
                .font(LegendNextTypography.wordmark)
                .tracking(LegendSharedDesign.tracking("wordmark"))
                .foregroundStyle(wordmarkColor)
                .accessibilityAddTraits(.isHeader)

            Spacer(minLength: LegendNextSpacing.sm)

            homeActionButton(
                systemImage: "heart",
                label: notificationAccessibilityLabel,
                action: .notifications,
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

    private var notificationAccessibilityLabel: String {
        "Open notifications, \(activityCount) recent interactions"
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
                    Text("LEGEND® IDENTITY")
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

struct DailyScriptureSheet: View {
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
                        Image(systemName: "xmark")
                            .font(.system(size: 14, weight: .bold))
                            .foregroundStyle(.white)
                            .frame(width: 38, height: 38)
                            .background(LegendNextGradient.finance, in: Circle())
                            .overlay {
                                Circle().strokeBorder(
                                    Color.white.opacity(0.16),
                                    lineWidth: 1)
                            }
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

                        Text("Untill all have heard.")
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

                LegendDailyScripturePassageView(scripture: scripture)
            }
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
                    // Begin the single-flight detail request before SwiftUI
                    // schedules the navigation destination. This removes a
                    // frame-cycle of avoidable latency on every thread open.
                    messages.openConversation(conversationID)
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
        // Messages owns its own branded header. Keep the navigation chrome
        // suppressed at the tab root so account/profile toolbar items (such as
        // the settings gear) cannot leak into the inbox landing screen.
        .toolbar(.hidden, for: .navigationBar)
        .onChange(of: pendingConversationID) { _, conversationID in
            guard let conversationID else { return }
            messages.openConversation(conversationID)
            navigationPath = [conversationID]
            isThreadActive = true
            pendingConversationID = nil
        }
    }
}

private struct LegendHomeView: View {
    let currentSession: MobileSession

    @Binding private var selectedTab: LegendAppTab
    @Binding private var pendingMessageConversationID: UUID?
    @EnvironmentObject private var scrollChrome: LegendScrollChrome
    @ObservedObject private var store: MobileHomeStore
    @ObservedObject private var social: MobileSocialStore
    @ObservedObject private var messages: MessagingStore
    @ObservedObject private var activity: LegendDailyActivityStore
    @ObservedObject private var bootstrap: LegendApplicationBootstrapCoordinator
    @State private var presentedScripture: MobileDailyScripture? = nil

    init(
        currentSession: MobileSession,
        store: MobileHomeStore,
        social: MobileSocialStore,
        messages: MessagingStore,
        activity: LegendDailyActivityStore,
        bootstrap: LegendApplicationBootstrapCoordinator,
        selectedTab: Binding<LegendAppTab>,
        pendingMessageConversationID: Binding<UUID?>
    ) {
        self.currentSession = currentSession
        _selectedTab = selectedTab
        _pendingMessageConversationID = pendingMessageConversationID
        _store = ObservedObject(wrappedValue: store)
        _social = ObservedObject(wrappedValue: social)
        _messages = ObservedObject(wrappedValue: messages)
        _activity = ObservedObject(wrappedValue: activity)
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
            VStack(
                alignment: .leading,
                spacing: LegendNextSpacing.xs
            ) {
                LegendSocialHomeSection(
                    session: currentSession,
                    social: social,
                    messaging: messages,
                    activity: activity,
                    openJoinedGroup: { conversationID in
                        pendingMessageConversationID = conversationID
                        open(.messages)
                    },
                    refreshSocial: {
                        await bootstrap.refreshSocial()
                    }
                ) {
                    homeHero(home)
                    LegendTodayActivitySummaryPill(activity: activity) {
                        scrollChrome.requestHomeAction(.todayActivity)
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
            padding: LegendNextSpacing.sm
        ) {
            VStack(alignment: .leading, spacing: LegendNextSpacing.sm) {
                HStack(
                    alignment: .firstTextBaseline,
                    spacing: LegendNextSpacing.xs
                ) {
                    Text("\(greetingEyebrow.capitalized),")
                        .font(LegendNextTypography.supporting.weight(.semibold))
                        .foregroundStyle(LegendNextColor.goldBright)
                        .lineLimit(1)

                    Text(firstName)
                        .font(.title3.weight(.bold))
                        .foregroundStyle(.white)
                        .lineLimit(1)

                    Spacer(minLength: 0)

                    Image(systemName: "sparkles")
                        .font(.system(size: 14, weight: .semibold))
                        .foregroundStyle(LegendNextColor.goldBright)
                        .shadow(
                            color: LegendNextColor.gold.opacity(0.50),
                            radius: 6
                        )
                }
                .minimumScaleFactor(0.76)
                .allowsTightening(true)

                Button {
                    presentedScripture = home.dailyScripture
                } label: {
                    VStack(alignment: .leading, spacing: LegendNextSpacing.micro) {
                        HStack(spacing: LegendNextSpacing.xs) {
                            Text("DAILY SCRIPTURE")
                                .font(LegendNextTypography.eyebrow)
                                .tracking(0.9)
                                .foregroundStyle(LegendNextColor.goldBright)

                            Spacer(minLength: 0)

                            Image(systemName: "arrow.up.right")
                                .font(.caption.weight(.bold))
                                .foregroundStyle(.white.opacity(0.72))
                        }

                        Text(home.dailyScripture.reference)
                            .font(.subheadline.weight(.semibold))
                            .foregroundStyle(.white)

                        Text(home.dailyScripture.text)
                            .font(LegendNextTypography.caption)
                            .foregroundStyle(.white.opacity(0.78))
                            .multilineTextAlignment(.leading)
                            .lineLimit(2)
                            .truncationMode(.tail)
                    }
                    .frame(maxWidth: .infinity, alignment: .leading)
                }
                .buttonStyle(.plain)
                .accessibilityLabel("Verse of the day, \(home.dailyScripture.reference). Double tap to read the full passage.")
            }
        }
        .overlay {
            RoundedRectangle(
                cornerRadius: LegendNextRadius.prominentCard,
                style: .continuous
            )
            .stroke(
                LinearGradient(
                    colors: [
                        LegendNextColor.goldBright.opacity(0.62),
                        LegendNextColor.gold.opacity(0.18),
                        Color.white.opacity(0.06)
                    ],
                    startPoint: .topLeading,
                    endPoint: .bottomTrailing
                ),
                lineWidth: 1
            )
            .allowsHitTesting(false)
        }
        .shadow(
            color: LegendNextColor.midnight.opacity(0.22),
            radius: 11,
            x: 0,
            y: 6
        )
        .shadow(
            color: LegendNextColor.gold.opacity(0.09),
            radius: 14,
            x: 0,
            y: 2
        )
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
            return "Lead with purpose, discipline, clarity, and confidence while helping others grow intentionally."
        }

        return "Build discipline, confidence, clarity, and purpose through Christ-centered growth and intentional living."
    }

    private func actionDueDetail(
        _ action: MobileActionItem
    ) -> String {
        if let dueDate = action.dueDateUTC {
            return "Due \(dueDate.formatted(.dateTime.month(.abbreviated).day().hour().minute()))"
        }

        return "\(action.priority) priority"
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
    @State private var isLeadsPresented = false
    @State private var isClientCreationPresented = false

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
        .sheet(isPresented: $isLeadsPresented) {
            NavigationStack {
                LegendAgentLeadsView(
                    store: store,
                    bootstrap: bootstrap
                )
            }
            .tint(LegendNextColor.gold)
            .task { store.loadLeads() }
        }
        .fullScreenCover(isPresented: $isClientCreationPresented, onDismiss: {
            Task { await bootstrap.refreshClients() }
        }) {
            LegendAgentClientCreationPortalView(store: store, onClose: {
                isClientCreationPresented = false
            })
        }
    }

    @ViewBuilder
    private func clientContent(_ clients: [MobileAgentClientSummary]) -> some View {
        LegendScrollView {
            LazyVStack(alignment: .leading, spacing: LegendNextSpacing.sm) {
                LegendNextSectionHeader(
                    eyebrow: "CRM",
                    title: "Active clients",
                    detail: "\(clients.count) live records"
                )

                agentCRMCommands

                if clients.isEmpty {
                    LegendNextEmptyState(
                        title: "No active clients",
                        message: "Your active Client and Business Client CRM records will appear here.",
                        systemImage: "person.2")
                } else {
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
            }
        }
        .padding(.horizontal, LegendNextSpacing.sm)
        .padding(.vertical, LegendNextSpacing.md)
        .scrollIndicators(.hidden)
    }

    private var agentCRMCommands: some View {
        HStack(spacing: LegendNextSpacing.sm) {
            Button {
                isLeadsPresented = true
            } label: {
                Label("Leads", systemImage: "person.crop.circle.badge.plus")
            }
            .buttonStyle(LegendNextButtonStyle(kind: .secondary))

            Button {
                isClientCreationPresented = true
            } label: {
                Label("Add Client", systemImage: "plus")
            }
            .buttonStyle(LegendNextButtonStyle(kind: .gold))
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
                LazyVStack(alignment: .leading, spacing: LegendNextSpacing.md) {
                    LegendNextHero(
                        eyebrow: "Journey Circles",
                        title: "Build your circle",
                        detail: "Confirm participation once to begin. Every additional detail makes your recommendations more precise."
                    ) {
                        HStack(spacing: LegendNextSpacing.xs) {
                            LegendNextBadge(
                                consentAffirmed
                                    ? "Matching active"
                                    : "1 confirmation needed",
                                tone: consentAffirmed ? .success : .gold,
                                systemImage: consentAffirmed
                                    ? "checkmark.circle.fill"
                                    : "checkmark.seal")

                            LegendNextBadge(
                                matchSignalCount == 1
                                    ? "1 match signal"
                                    : "\(matchSignalCount) match signals",
                                tone: matchSignalCount > 0 ? .information : .neutral,
                                systemImage: "sparkles")
                        }
                    }

                    JourneyParticipationSection(
                        consentAffirmed: $consentAffirmed,
                        isOptedIn: $isOptedIn,
                        isDiscoverable: $isDiscoverable,
                        allowSuggestions: $allowSuggestions,
                        allowConnectionRequests: $allowConnectionRequests)

                    JourneyIntroductionSection(introduction: $introduction)

                    JourneyMultiSelectSection(
                        title: "Goals",
                        detail: "The strongest starting signal for recommendations.",
                        options: dashboard.taxonomy.goals,
                        selections: $goals)
                    JourneyMultiSelectSection(
                        title: "Circles",
                        detail: "Choose communities that fit your current season.",
                        options: dashboard.taxonomy.circles,
                        selections: $circleCodes)
                    JourneyMultiSelectSection(
                        title: "Life stage",
                        detail: "Optional context that refines your matches.",
                        options: dashboard.taxonomy.lifeStages,
                        selections: $lifeStages)
                    JourneyMultiSelectSection(
                        title: "Location",
                        detail: "Optional regional relevance.",
                        options: dashboard.taxonomy.locations,
                        selections: $locations)
                    JourneyMultiSelectSection(
                        title: "Interests",
                        detail: "Add the subjects you want to explore together.",
                        options: dashboard.taxonomy.interests,
                        selections: $interests)
                    JourneyMultiSelectSection(
                        title: "Connection types",
                        detail: "Set the kind of connection you value.",
                        options: dashboard.taxonomy.connectionTypes,
                        selections: $connectionTypes)
                    JourneyMultiSelectSection(
                        title: "Communication style",
                        detail: "Help recommendations feel natural from the start.",
                        options: dashboard.taxonomy.communicationStyles,
                        selections: $communicationStyles)
                    JourneyMultiSelectSection(
                        title: "Accountability",
                        detail: "Optional cadence preferences for stronger fit.",
                        options: dashboard.taxonomy.accountabilityFrequencies,
                        selections: $accountabilityFrequencies)

                    LegendNextSurface(style: .brandBlue) {
                        Label {
                            VStack(alignment: .leading, spacing: LegendNextSpacing.micro) {
                                Text("Your match profile stays in your control")
                                    .font(LegendNextTypography.label)
                                    .foregroundStyle(LegendNextColor.textPrimary)
                                Text("One participation choice gets you started. Add or remove details whenever your season changes.")
                                    .font(LegendNextTypography.caption)
                                    .foregroundStyle(LegendNextColor.textSecondary)
                            }
                        } icon: {
                            Image(systemName: "slider.horizontal.3")
                                .font(.system(size: LegendNextSize.iconMedium, weight: .semibold))
                                .foregroundStyle(LegendNextColor.gold)
                        }
                    }
                }
                .padding(.horizontal, LegendNextSpacing.sm)
                .padding(.vertical, LegendNextSpacing.md)
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

    private var matchSignalCount: Int {
        lifeStages.count +
        locations.count +
        goals.count +
        interests.count +
        circleCodes.count +
        connectionTypes.count +
        communicationStyles.count +
        accountabilityFrequencies.count
    }
}

private struct JourneyParticipationSection: View {
    @Binding var consentAffirmed: Bool
    @Binding var isOptedIn: Bool
    @Binding var isDiscoverable: Bool
    @Binding var allowSuggestions: Bool
    @Binding var allowConnectionRequests: Bool

    var body: some View {
        LegendNextSurface(style: .elevated) {
            VStack(alignment: .leading, spacing: LegendNextSpacing.md) {
                LegendNextSectionHeader(
                    eyebrow: "Participation",
                    title: "Choose your starting point",
                    detail: "Confirm participation once to begin matching. Every other choice is optional and stays under your control.")

                VStack(spacing: 0) {
                    JourneyParticipationToggle(
                        title: "Confirm community participation",
                        detail: "Start a private, respectful matching profile.",
                        systemImage: "checkmark.seal",
                        isOn: $consentAffirmed)
                    LegendNextDivider()
                    JourneyParticipationToggle(
                        title: "Join Journey Circles",
                        detail: "Take part in the Legend matching community.",
                        systemImage: "person.3",
                        isOn: $isOptedIn)
                    LegendNextDivider()
                    JourneyParticipationToggle(
                        title: "Show my profile in Discover",
                        detail: "Let compatible members find your profile.",
                        systemImage: "magnifyingglass.circle",
                        isOn: $isDiscoverable)
                    LegendNextDivider()
                    JourneyParticipationToggle(
                        title: "Allow recommendations",
                        detail: "Receive tailored connection suggestions.",
                        systemImage: "sparkles",
                        isOn: $allowSuggestions)
                    LegendNextDivider()
                    JourneyParticipationToggle(
                        title: "Allow connection requests",
                        detail: "Let compatible members request a connection.",
                        systemImage: "person.crop.circle.badge.plus",
                        isOn: $allowConnectionRequests)
                }
            }
        }
    }
}

private struct JourneyParticipationToggle: View {
    let title: String
    let detail: String
    let systemImage: String
    @Binding var isOn: Bool

    var body: some View {
        Toggle(isOn: $isOn) {
            HStack(alignment: .top, spacing: LegendNextSpacing.sm) {
                Image(systemName: systemImage)
                    .font(.system(size: LegendNextSize.iconMedium, weight: .semibold))
                    .foregroundStyle(isOn ? LegendNextColor.gold : LegendNextColor.textSecondary)
                    .frame(width: LegendNextSize.minimumTapTarget, height: LegendNextSize.minimumTapTarget)
                    .background(
                        (isOn ? LegendNextColor.gold : LegendNextColor.navy)
                            .opacity(isOn ? 0.15 : 0.08),
                        in: Circle())

                VStack(alignment: .leading, spacing: LegendNextSpacing.micro) {
                    Text(title)
                        .font(LegendNextTypography.label)
                        .foregroundStyle(LegendNextColor.textPrimary)
                    Text(detail)
                        .font(LegendNextTypography.caption)
                        .foregroundStyle(LegendNextColor.textSecondary)
                        .fixedSize(horizontal: false, vertical: true)
                }
            }
        }
        .toggleStyle(SwitchToggleStyle(tint: LegendNextColor.gold))
        .padding(.vertical, LegendNextSpacing.xs)
    }
}

private struct JourneyIntroductionSection: View {
    @Binding var introduction: String

    var body: some View {
        LegendNextSurface(style: .elevated) {
            VStack(alignment: .leading, spacing: LegendNextSpacing.sm) {
                LegendNextSectionHeader(
                    eyebrow: "Optional",
                    title: "A little about your season",
                    detail: "Give future connections useful context in your own words.")

                TextField(
                    "What are you building, learning, or looking for?",
                    text: $introduction,
                    axis: .vertical)
                    .font(LegendNextTypography.body)
                    .foregroundStyle(LegendNextColor.textPrimary)
                    .lineLimit(3...6)
                    .textInputAutocapitalization(.sentences)
                    .padding(LegendNextSpacing.md)
                    .background(
                        LegendNextColor.surfaceInset,
                        in: RoundedRectangle(
                            cornerRadius: LegendNextRadius.control,
                            style: .continuous))
                    .overlay {
                        RoundedRectangle(
                            cornerRadius: LegendNextRadius.control,
                            style: .continuous)
                        .strokeBorder(LegendNextColor.separator, lineWidth: 1)
                    }
            }
        }
    }
}

struct JourneyMultiSelectSection: View {
    let title: String
    let detail: String
    let options: [String]
    @Binding var selections: Set<String>

    var body: some View {
        LegendNextSurface(style: .elevated) {
            VStack(alignment: .leading, spacing: LegendNextSpacing.sm) {
                LegendNextSectionHeader(
                    eyebrow: "Match signal",
                    title: title,
                    detail: selections.isEmpty
                        ? detail
                        : "\(selections.count) selected · \(detail)")
                LazyVGrid(
                    columns: [GridItem(.adaptive(minimum: 142), spacing: LegendNextSpacing.xs)],
                    alignment: .leading,
                    spacing: LegendNextSpacing.xs) {
                    ForEach(options, id: \.self) { option in
                        JourneySelectionPill(
                            title: option,
                            isSelected: selections.contains(option)) {
                                if selections.contains(option) {
                                    selections.remove(option)
                                } else {
                                    selections.insert(option)
                                }
                            }
                    }
                }
            }
        }
    }
}

private struct JourneySelectionPill: View {
    let title: String
    let isSelected: Bool
    let action: () -> Void

    var body: some View {
        Button(action: action) {
            HStack(spacing: LegendNextSpacing.xs) {
                Image(systemName: isSelected ? "checkmark.circle.fill" : "plus.circle")
                    .font(.system(size: 13, weight: .semibold))
                Text(title)
                    .lineLimit(2)
                    .multilineTextAlignment(.leading)
                Spacer(minLength: 0)
            }
            .font(LegendNextTypography.caption)
            .foregroundStyle(isSelected ? .white : LegendNextColor.textPrimary)
            .padding(.horizontal, LegendNextSpacing.sm)
            .padding(.vertical, LegendNextSpacing.xs)
            .frame(maxWidth: .infinity, minHeight: LegendNextSize.minimumTapTarget, alignment: .leading)
            .background(
                isSelected ? AnyShapeStyle(LegendNextGradient.hero) : AnyShapeStyle(LegendNextColor.surfaceInset),
                in: RoundedRectangle(cornerRadius: LegendNextRadius.compact, style: .continuous))
            .overlay {
                RoundedRectangle(cornerRadius: LegendNextRadius.compact, style: .continuous)
                    .strokeBorder(
                        isSelected
                            ? Color.white.opacity(0.18)
                            : LegendNextColor.separator,
                        lineWidth: 1)
            }
        }
        .buttonStyle(.plain)
        .accessibilityValue(isSelected ? "Selected" : "Not selected")
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
                    detail: "Current week and month"
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
            "\(title). \(status). View details."
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
                title: "Scheduled activity"
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
                title: "Week by week"
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
    @Environment(\.dismiss) private var dismiss

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
                financialStateContent {
                    LegendHomeSkeleton()
                        .accessibilityLabel("Loading financial intelligence")
                }

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
                financialStateContent {
                    LegendNextErrorState(
                        title: failure.title,
                        message: failure.message,
                        retryTitle: "Sign in again",
                        retry: { Task { await bootstrap.refreshFinancial() } }
                    )
                }

            case .retryableFailure(let failure):
                financialStateContent {
                    LegendNextErrorState(
                        title: failure.title,
                        message: failure.message,
                        retryTitle: "Retry",
                        retry: { Task { await bootstrap.refreshFinancial() } }
                    )
                }
            }
        }
        .background(
            LegendNextColor.canvas.ignoresSafeArea()
        )
        // The application shell already owns the Legend wordmark. Keeping a
        // second navigation title here pushes the financial journey below the
        // fold and repeats information the report itself already provides.
        .toolbar(.hidden, for: .navigationBar)
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
                financialReportExitControl()

                if let availability {
                    availabilityNotice(availability)
                }

                if financial.healthSnapshot == nil,
                   financial.position == nil {
                    LegendNextEmptyState(
                        title: "Financial snapshot incomplete",
                        message:
                            "Your saved financial health data will appear here after it is completed in your account workspace.",
                        systemImage:
                            "chart.line.uptrend.xyaxis"
                    )
                }

                financialDashboard(financial)

                lastUpdated(financial)
            }
            .padding(
                .horizontal,
                LegendNextSpacing.pageHorizontal
            )
            .padding(
                .top,
                LegendNextSpacing.xs
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
        if let amountCents = metric.amountCents,
           amountCents < 0 {
            return .danger
        }

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
                financialDetailNavigation()

                if destination.healthSectionKey == nil,
                   let section = financial.presentation?.prioritySections.first(
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
                case .assets,
                        .liabilities,
                        .cashFlow,
                        .protection,
                        .taxProfile:
                    if let section = financial.healthSnapshot?.section(
                        for: destination
                    ) {
                        financialHealthSectionDetail(section)
                    } else {
                        LegendNextEmptyState(
                            title: "Financial snapshot incomplete",
                            message: "The saved section detail is not available yet.",
                            systemImage: financialHealthSymbol(destination.rawValue)
                        )
                    }

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
                    if let healthSnapshot = financial.healthSnapshot {
                        if let position = financial.position {
                            financialHealth(position)
                        }
                        financialHealthSectionGrid(healthSnapshot)
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
                    if let section = financial.healthSnapshot?.section(
                        for: .protection
                    ) {
                        financialHealthSectionDetail(section)
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
            .padding(.top, LegendNextSpacing.xs)
            .padding(.bottom, LegendNextSpacing.xl)
        }
        .background(
            LegendNextGradient.pageWash(for: colorScheme)
                .ignoresSafeArea()
        )
        .navigationBarBackButtonHidden()
        .toolbar(.hidden, for: .navigationBar)
        .refreshable {
            await bootstrap.refreshFinancial()
        }
    }

    private func financialStateContent<Content: View>(
        @ViewBuilder content: () -> Content
    ) -> some View {
        LegendScrollView {
            VStack(alignment: .leading, spacing: LegendNextSpacing.md) {
                financialReportExitControl()
                content()
            }
            .padding(.horizontal, LegendNextSpacing.pageHorizontal)
            .padding(.top, LegendNextSpacing.xs)
            .padding(.bottom, LegendNextSpacing.xl)
        }
        .background(
            LegendNextGradient.pageWash(for: colorScheme)
                .ignoresSafeArea()
        )
    }

    private func financialReportExitControl() -> some View {
        Button {
            dismiss()
        } label: {
            Label("Profile", systemImage: "chevron.left")
                .font(.subheadline.weight(.bold))
                .foregroundStyle(LegendNextColor.navy)
                .padding(.horizontal, LegendNextSpacing.sm)
                .frame(minHeight: 40)
                .background(LegendNextColor.canvas, in: Capsule())
                .overlay {
                    Capsule()
                        .strokeBorder(
                            LegendNextColor.gold.opacity(0.38),
                            lineWidth: 1
                        )
                }
        }
        .buttonStyle(.plain)
        .accessibilityLabel("Back to Profile")
    }

    private func financialDetailNavigation() -> some View {
        HStack(spacing: LegendNextSpacing.sm) {
            Button {
                detailDestination = nil
            } label: {
                Label("Financial Intelligence", systemImage: "chevron.left")
                    .font(.subheadline.weight(.bold))
                    .foregroundStyle(LegendNextColor.navy)
                    .padding(.horizontal, LegendNextSpacing.sm)
                    .frame(minHeight: 40)
                    .background(LegendNextColor.canvas, in: Capsule())
                    .overlay {
                        Capsule()
                            .strokeBorder(
                                LegendNextColor.gold.opacity(0.38),
                                lineWidth: 1
                            )
                    }
            }
            .buttonStyle(.plain)
            .accessibilityLabel("Back to Financial Intelligence")

            Spacer(minLength: 0)

            financialReportExitControl()
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
                Text("BALANCE SHEET")
                    .font(LegendNextTypography.eyebrow)
                    .tracking(0.7)
                    .foregroundStyle(LegendNextColor.goldBright)

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



    private func financialHealthSectionGrid(
        _ snapshot: MobileFinancialHealthSnapshotResponse
    ) -> some View {
        LazyVGrid(
            columns: [
                GridItem(.flexible(), spacing: LegendNextSpacing.xs),
                GridItem(.flexible(), spacing: LegendNextSpacing.xs)
            ],
            spacing: LegendNextSpacing.xs
        ) {
            ForEach(snapshot.sections) { section in
                if let destination = MobileFinancialDetailDestination(
                    rawValue: section.key
                ) {
                    Button {
                        detailDestination = destination
                    } label: {
                        financialHealthSectionCard(section)
                    }
                    .buttonStyle(.plain)
                    .accessibilityLabel(
                        "\(section.title). \(sectionSummary(section)). Open details."
                    )
                }
            }
        }
    }

    private func financialHealthSectionCard(
        _ section: MobileFinancialHealthSectionResponse
    ) -> some View {
        let tone = LegendFinancialPresentation.sectionTone(
            for: section.semantic
        )
        let summaryTone = section.total.map {
            LegendFinancialPresentation.metricTone(
                $0,
                sectionSemantic: section.semantic
            )
        } ?? tone

        return LegendNextSurface(
            style: .navy,
            cornerRadius: LegendNextRadius.card,
            padding: LegendNextSpacing.xs
        ) {
            VStack(
                alignment: .leading,
                spacing: LegendNextSpacing.xs
            ) {
                HStack(spacing: LegendNextSpacing.micro) {
                    Image(systemName: financialHealthSymbol(section.key))
                        .font(.system(size: 13, weight: .semibold))
                        .foregroundStyle(tone.color)
                        .frame(width: 28, height: 28)
                        .background(tone.color.opacity(0.16), in: Circle())
                        .accessibilityHidden(true)

                    Spacer(minLength: 0)

                    Image(systemName: "chevron.right")
                        .font(.caption.weight(.bold))
                        .foregroundStyle(Color.white.opacity(0.58))
                        .accessibilityHidden(true)
                }

                Text(section.title.uppercased())
                    .font(LegendNextTypography.eyebrow)
                    .tracking(0.5)
                    .foregroundStyle(tone.color)
                    .lineLimit(1)
                    .minimumScaleFactor(0.72)

                Text(sectionSummary(section))
                    .font(LegendNextTypography.bodyEmphasis)
                    .foregroundStyle(summaryTone.color)
                    .monospacedDigit()
                    .lineLimit(1)
                    .minimumScaleFactor(0.68)

                if let period = section.period {
                    Text(period.uppercased())
                        .font(LegendNextTypography.eyebrow)
                        .foregroundStyle(Color.white.opacity(0.56))
                        .lineLimit(1)
                }
            }
            .frame(maxWidth: .infinity, minHeight: 106, alignment: .topLeading)
        }
    }

    private func financialHealthSectionDetail(
        _ section: MobileFinancialHealthSectionResponse
    ) -> some View {
        let tone = LegendFinancialPresentation.sectionTone(
            for: section.semantic
        )

        return VStack(
            alignment: .leading,
            spacing: LegendNextSpacing.sm
        ) {
            financialSnapshotSectionHeader(section, tone: tone)

            LegendNextSurface(
                style: .navy,
                cornerRadius: LegendNextRadius.prominentCard,
                padding: 0
            ) {
                VStack(spacing: 0) {
                    ForEach(Array(section.groups.enumerated()), id: \.element.id) {
                        groupIndex,
                        group in
                        if let title = group.title {
                            Text(title.uppercased())
                                .font(LegendNextTypography.eyebrow)
                                .tracking(0.7)
                                .foregroundStyle(tone.color)
                                .frame(maxWidth: .infinity, alignment: .leading)
                                .padding(.horizontal, LegendNextSpacing.sm)
                                .padding(.top, groupIndex == 0 ? LegendNextSpacing.sm : LegendNextSpacing.md)
                        }

                        ForEach(Array(group.metrics.enumerated()), id: \.element.id) {
                            metricIndex,
                            metric in
                            financialHealthMetricRow(
                                metric,
                                sectionSemantic: section.semantic
                            )

                            if metricIndex < group.metrics.count - 1 {
                                LegendNextDivider()
                                    .padding(.leading, LegendNextSpacing.sm)
                            }
                        }

                        if groupIndex < section.groups.count - 1 {
                            LegendNextDivider()
                                .padding(.top, LegendNextSpacing.sm)
                        }
                    }

                    if let total = section.total {
                        LegendNextDivider()
                            .padding(.top, LegendNextSpacing.sm)

                        financialHealthMetricRow(
                            total,
                            sectionSemantic: section.semantic,
                            isTotal: true
                        )
                    }
                }
            }
        }
    }

    private func financialHealthMetricRow(
        _ metric: MobileFinancialHealthMetricResponse,
        sectionSemantic: String,
        isTotal: Bool = false
    ) -> some View {
        let metricTone = LegendFinancialPresentation.metricTone(
            metric,
            sectionSemantic: sectionSemantic
        )

        return HStack(alignment: .firstTextBaseline, spacing: LegendNextSpacing.sm) {
            Text(metric.label)
                .font(isTotal ? LegendNextTypography.bodyEmphasis : LegendNextTypography.supporting)
                .foregroundStyle(isTotal ? .white : Color.white.opacity(0.78))
                .lineLimit(2)

            Spacer(minLength: LegendNextSpacing.xs)

            Text(metric.displayValue)
                .font(isTotal ? LegendNextTypography.bodyEmphasis : LegendNextTypography.supporting)
                .foregroundStyle(metricTone.color)
                .monospacedDigit()
                .multilineTextAlignment(.trailing)
                .lineLimit(2)
                .minimumScaleFactor(0.76)
        }
        .padding(.horizontal, LegendNextSpacing.sm)
        .padding(.vertical, isTotal ? LegendNextSpacing.sm : LegendNextSpacing.tiny)
        .accessibilityElement(children: .combine)
    }

    private func sectionSummary(
        _ section: MobileFinancialHealthSectionResponse
    ) -> String {
        section.total?.displayValue ?? "Open details"
    }

    private func financialSnapshotSectionHeader(
        _ section: MobileFinancialHealthSectionResponse,
        tone: LegendNextTone
    ) -> some View {
        LegendNextSurface(
            style: .navy,
            cornerRadius: LegendNextRadius.card,
            padding: LegendNextSpacing.sm
        ) {
            HStack(alignment: .center, spacing: LegendNextSpacing.sm) {
                Image(systemName: financialHealthSymbol(section.key))
                    .font(.system(size: 16, weight: .semibold))
                    .foregroundStyle(tone.color)
                    .frame(width: 40, height: 40)
                    .background(tone.color.opacity(0.16), in: Circle())
                    .accessibilityHidden(true)

                VStack(alignment: .leading, spacing: LegendNextSpacing.micro) {
                    Text("FINANCIAL JOURNEY")
                        .font(LegendNextTypography.eyebrow)
                        .tracking(0.8)
                        .foregroundStyle(LegendNextColor.goldBright)

                    LegendNextBadge(
                        section.title.uppercased(),
                        tone: tone
                    )
                }

                Spacer(minLength: LegendNextSpacing.xs)

                if let period = section.period {
                    Text(period.uppercased())
                        .font(LegendNextTypography.eyebrow)
                        .tracking(0.6)
                        .foregroundStyle(Color.white.opacity(0.58))
                        .multilineTextAlignment(.trailing)
                        .lineLimit(2)
                }
            }
        }
    }

    private func financialHealthSymbol(
        _ key: String
    ) -> String {
        switch key {
        case "assets":
            return "building.columns.fill"
        case "liabilities":
            return "creditcard.fill"
        case "cash-flow":
            return "arrow.left.arrow.right.circle.fill"
        case "protection":
            return "shield.lefthalf.filled"
        case "tax-profile":
            return "percent"
        default:
            return "chart.bar.fill"
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
                title: "Recurring Bills"
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
        title: String
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
    @EnvironmentObject private var pushNotifications: LegendPushNotificationDelegate
    let currentSession: MobileSession
    let openFinancialIntelligence: () -> Void
    let onSignOut: () -> Void

    @ObservedObject private var coordinator: MobileSessionCoordinator
    @ObservedObject private var account: MobileAccountStore
    @ObservedObject private var messages: MessagingStore
    @ObservedObject private var social: MobileSocialStore
    @ObservedObject private var bootstrap: LegendApplicationBootstrapCoordinator

    @State private var selectedContent: LegendProfileContentFilter = .posts
    @State private var isEditing = false
    @State private var isShowingSettings = false
    @State private var profileSettingsPresentation: ProfileSettingsPresentation?
    @State private var translationLanguageNames: [String: String] = [:]
    @State private var isConfirmingSignOut = false
    @State private var creationRoute: LegendSocialCreationRoute?
    @State private var selectedPost: MobileSocialPost?
    @State private var editingHac: MobileSocialPost?
    @State private var deletionTarget: MobileSocialPost?
    @State private var profilePage = 0
    @State private var controlledResourceRequestFeedback: LegendRequestSubmissionFeedback?
    @State private var selectedProfilePhoto: PhotosPickerItem?

    init(
        currentSession: MobileSession,
        coordinator: MobileSessionCoordinator,
        account: MobileAccountStore,
        messages: MessagingStore,
        social: MobileSocialStore,
        bootstrap: LegendApplicationBootstrapCoordinator,
        openFinancialIntelligence: @escaping () -> Void,
        onSignOut: @escaping () -> Void
    ) {
        self.currentSession = currentSession
        self.openFinancialIntelligence = openFinancialIntelligence
        self.onSignOut = onSignOut
        _coordinator = ObservedObject(wrappedValue: coordinator)
        _account = ObservedObject(wrappedValue: account)
        _messages = ObservedObject(wrappedValue: messages)
        _social = ObservedObject(wrappedValue: social)
        _bootstrap = ObservedObject(wrappedValue: bootstrap)
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
        .onChange(of: selectedProfilePhoto) { _, photo in
            guard let photo else { return }
            Task { await uploadProfileAvatar(photo) }
        }
        .sheet(isPresented: $isEditing) {
            if case .loaded(let profile) = account.state {
                LegendAccountEditor(
                    profile: profile,
                    store: account,
                    synchronizeProfile: { await bootstrap.synchronizeProfileIdentity() }
                )
            }
        }
        .sheet(isPresented: $isShowingSettings) {
            if case .loaded(let profile) = account.state {
                profileSettingsSheet(profile)
            }
        }
        .sheet(item: $creationRoute) { _ in
            LegendSocialCreationSheet(
                route: $creationRoute,
                social: social)
        }
        .sheet(item: $editingHac) { post in
            LegendSocialPostEditor(
                post: post,
                social: social,
                onSaved: { editingHac = nil })
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
                onSignOut()
            }

            Button("Cancel", role: .cancel) {}
        } message: {
            Text("You will need to securely sign in again to access your account.")
        }
        .confirmationDialog(
            "Delete this Hac?",
            isPresented: Binding(
                get: { deletionTarget != nil },
                set: { if !$0 { deletionTarget = nil } }
            ),
            titleVisibility: .visible
        ) {
            Button("Delete Hac", role: .destructive) {
                guard let post = deletionTarget else { return }
                deletionTarget = nil
                Task {
                    _ = await social.deletePost(postID: post.id)
                }
            }

            Button("Cancel", role: .cancel) {
                deletionTarget = nil
            }
        } message: {
            Text("This permanently removes the Hac and its media from Legend.")
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
                openFinancialIntelligence: openFinancialIntelligence
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
    }

    private func profileIdentityHeader(
        _ profile: MobileAccountProfile
    ) -> some View {
        let isUploadingAvatar = account.isSaving
        return LegendNextSurface(
            style: .elevated,
            cornerRadius: LegendNextRadius.prominentCard,
            padding: LegendNextSpacing.sm
        ) {
            VStack(alignment: .leading, spacing: LegendNextSpacing.xs) {
                HStack(alignment: .center, spacing: LegendNextSpacing.sm) {
                    PhotosPicker(
                        selection: $selectedProfilePhoto,
                        matching: .images,
                        photoLibrary: .shared()) {
                            ZStack(alignment: .bottomTrailing) {
                                LegendProfileAvatar(
                                    avatar: profile.avatar,
                                    displayName: profile.displayName,
                                    size: LegendNextSize.profileAvatar)

                                Image(systemName: isUploadingAvatar
                                      ? "arrow.triangle.2.circlepath"
                                      : "camera.fill")
                                    .font(.caption2.weight(.bold))
                                    .foregroundStyle(LegendNextColor.midnight)
                                    .frame(
                                        width: LegendNextSize.profileAvatarCamera,
                                        height: LegendNextSize.profileAvatarCamera)
                                    .background(LegendNextGradient.gold, in: Circle())
                                    .overlay {
                                        Circle().stroke(.white, lineWidth: 1.5)
                                    }
                            }
                        }
                        .disabled(isUploadingAvatar)
                        .accessibilityLabel("Change profile picture")
                        .accessibilityHint("Choose a picture from your photo library")

                    VStack(alignment: .leading, spacing: LegendNextSpacing.micro) {
                        profileAccountMenu

                        LegendVerifiedName(
                            profile.displayName,
                            isVerified: profile.isVerified,
                            font: .title2.weight(.bold),
                            badgePlacement: .alongsideProfileImage
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
                    .buttonStyle(LegendNextIconButtonStyle(
                        tone: .navy,
                        size: LegendNextSize.profileSettingsIcon))
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

    private func uploadProfileAvatar(_ item: PhotosPickerItem) async {
        defer { selectedProfilePhoto = nil }
        guard let imageData = try? await item.loadTransferable(type: Data.self),
              let update = legendMobileAvatarUpdate(from: imageData) else {
            return
        }

        guard await account.uploadAvatar(update) else { return }
        await bootstrap.synchronizeProfileIdentity()
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
            controlHeight: LegendNextSize.profileControlHeight
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
                .frame(
                    maxWidth: .infinity,
                    minHeight: LegendNextSize.profileControlHeight)
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
                        .contextMenu {
                            if post.isVideoHac {
                                Button {
                                    editingHac = post
                                } label: {
                                    Label("Edit Hac", systemImage: "pencil")
                                }

                                Button(role: .destructive) {
                                    deletionTarget = post
                                } label: {
                                    Label("Delete Hac", systemImage: "trash")
                                }
                            }
                        }
                        .accessibilityHint(
                            post.isVideoHac
                                ? "Open this Hac. Touch and hold to edit or delete it."
                                : "Open post options")
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


    private struct LegendLegalSection: Identifiable {
        let id: String
        let title: String
        let body: String

        init(_ title: String, _ body: String) {
            self.id = title
            self.title = title
            self.body = body
        }
    }

    private struct LegendLegalDocumentView: View {
        let eyebrow: String
        let title: String
        let subtitle: String
        let sections: [LegendLegalSection]
        let dismiss: () -> Void

        var body: some View {
            NavigationStack {
                ZStack {
                    LinearGradient(
                        colors: [
                            Color(red: 0.025, green: 0.075, blue: 0.145),
                            Color(red: 0.045, green: 0.125, blue: 0.220)
                        ],
                        startPoint: .topLeading,
                        endPoint: .bottomTrailing)
                        .ignoresSafeArea()

                    ScrollView {
                        VStack(alignment: .leading, spacing: 18) {
                            VStack(alignment: .leading, spacing: 8) {
                                Text("LEGEND®")
                                    .font(.system(size: 13, weight: .black, design: .rounded))
                                    .tracking(2.4)
                                    .foregroundStyle(
                                        Color(red: 0.835, green: 0.695, blue: 0.365))

                                Text(eyebrow.uppercased())
                                    .font(.caption.weight(.bold))
                                    .tracking(1.4)
                                    .foregroundStyle(.white.opacity(0.62))

                                Text(title)
                                    .font(.system(size: 30, weight: .bold, design: .rounded))
                                    .foregroundStyle(.white)

                                Text(subtitle)
                                    .font(.subheadline)
                                    .foregroundStyle(.white.opacity(0.76))
                                    .fixedSize(horizontal: false, vertical: true)

                                Rectangle()
                                    .fill(
                                        Color(red: 0.835, green: 0.695, blue: 0.365))
                                    .frame(width: 54, height: 2)
                                    .padding(.top, 3)
                            }
                            .padding(.bottom, 2)

                            ForEach(sections) { section in
                                VStack(alignment: .leading, spacing: 8) {
                                    Text(section.title)
                                        .font(.headline.weight(.bold))
                                        .foregroundStyle(
                                            Color(red: 0.835, green: 0.695, blue: 0.365))

                                    Text(section.body)
                                        .font(.subheadline)
                                        .foregroundStyle(.white.opacity(0.88))
                                        .lineSpacing(4)
                                        .fixedSize(horizontal: false, vertical: true)
                                        .textSelection(.enabled)
                                }
                                .frame(maxWidth: .infinity, alignment: .leading)
                                .padding(16)
                                .background(
                                    RoundedRectangle(cornerRadius: 18, style: .continuous)
                                        .fill(.white.opacity(0.075)))
                                .overlay(
                                    RoundedRectangle(cornerRadius: 18, style: .continuous)
                                        .stroke(.white.opacity(0.10), lineWidth: 1))
                            }

                            VStack(alignment: .leading, spacing: 5) {
                                Text("CONTACT LEGEND®")
                                    .font(.caption.weight(.bold))
                                    .tracking(1.2)
                                    .foregroundStyle(
                                        Color(red: 0.835, green: 0.695, blue: 0.365))

                                Text("connect@mylegnd.com")
                                    .font(.subheadline.weight(.semibold))
                                    .foregroundStyle(.white)

                                Text("Effective August 7, 2026")
                                    .font(.caption)
                                    .foregroundStyle(.white.opacity(0.56))
                            }
                            .padding(.top, 4)
                        }
                        .padding(.horizontal, 20)
                        .padding(.top, 20)
                        .padding(.bottom, 36)
                    }
                }
                .toolbar {
                    ToolbarItem(placement: .topBarTrailing) {
                        Button("Done", action: dismiss)
                            .fontWeight(.bold)
                            .tint(
                                Color(red: 0.835, green: 0.695, blue: 0.365))
                    }
                }
                .toolbarBackground(
                    Color(red: 0.025, green: 0.075, blue: 0.145),
                    for: .navigationBar)
                .toolbarBackground(.visible, for: .navigationBar)
            }
        }
    }

    private func legendPrivacyPolicyView(dismiss: @escaping () -> Void) -> some View {
        LegendLegalDocumentView(
            eyebrow: "Your Privacy",
            title: "Privacy Policy",
            subtitle: "Your trust is our priority.",
            sections: [
                LegendLegalSection(
                    "Privacy & Data Use",
                    """
                    LEGEND® collects and uses information necessary to provide, secure, personalize, and improve the services available through the app.

                    Depending on how you use LEGEND®, information associated with your account may include your name, contact information, profile information, authentication and account data, profile photo, messages, community activity, uploaded photos or videos, notification preferences, device registration information, and information you voluntarily provide through LEGEND® features.

                    Where financial-wellness or financial-planning features are available, LEGEND® may also process financial information you choose to provide for the purpose of delivering those features.

                    LEGEND® does not sell your personal information.
                    """),
                LegendLegalSection(
                    "Account & Authentication",
                    """
                    LEGEND® uses account and authentication information to identify you, protect your account, determine the features and permissions available to you, and maintain secure access to the platform.

                    Authentication may involve third-party identity infrastructure used by LEGEND® to securely manage sign-in, account access, and related security functions.
                    """),
                LegendLegalSection(
                    "Messaging & Communications",
                    """
                    LEGEND® allows eligible users to communicate through direct messages, conversations, and group messaging.

                    Messages and related conversation information may be stored so conversations can be delivered, synchronized, displayed, moderated where necessary, and preserved according to applicable business, legal, security, and retention requirements.

                    Where language-translation functionality is enabled and authorized for an account, eligible message content may be processed through an approved translation service to provide translated communications.
                    """),
                LegendLegalSection(
                    "Community, Hacs & User Content",
                    """
                    LEGEND® includes community and social features that may allow users to create profiles, publish posts or Hacs, upload photos or videos, comment, react, participate in discussions, and interact with other members.

                    Content you intentionally publish to community areas may be visible to other users according to the audience and privacy settings available within the app.

                    LEGEND® may review, restrict, remove, preserve, or take action on content when reasonably necessary to enforce community standards, respond to reports, protect users, comply with law, or maintain the safety and integrity of the platform.
                    """),
                LegendLegalSection(
                    "Photos, Videos & Device Permissions",
                    """
                    If you choose to upload a profile image, Hac, post, conversation image, or other media, LEGEND® may request access to your camera or photo library.

                    Permission is requested through iOS and remains subject to the controls available in your device settings. LEGEND® only receives media that you choose to provide through the app.
                    """),
                LegendLegalSection(
                    "Push Notifications",
                    """
                    If you permit notifications, LEGEND® may register your device with Apple Push Notification service (APNs) so the app can deliver alerts such as new messages, activity, or other relevant account notifications.

                    Device notification tokens and related delivery information may be processed to register your device, deliver notifications, diagnose delivery problems, and disable registrations that are no longer valid. Notification permissions remain under your control through iOS.
                    """),
                LegendLegalSection(
                    "Technical & Security Information",
                    """
                    LEGEND® may process limited technical information necessary to operate and protect the service, including device and application information, network requests, diagnostic information, security events, performance information, and service logs.

                    This information may be used to maintain reliability, investigate failures, prevent abuse, secure accounts, and improve application performance.
                    """),
                LegendLegalSection(
                    "Service Providers",
                    """
                    LEGEND® may use trusted technology and service providers to support functions such as cloud hosting, authentication, notifications, communications, scheduling, translation, media processing, security, and other operational services.

                    These providers may process limited information as necessary to perform services for LEGEND® and may also operate under their own applicable legal and privacy obligations.
                    """),
                LegendLegalSection(
                    "Information Retention",
                    """
                    LEGEND® retains information for as long as reasonably necessary to provide the service, protect the platform, satisfy legitimate business requirements, resolve disputes, enforce agreements, and comply with legal, regulatory, insurance, financial-services, recordkeeping, or security obligations.

                    Some information may therefore be retained after account closure when retention is legally or operationally required.
                    """),
                LegendLegalSection(
                    "Account Deletion",
                    """
                    Eligible users may request deletion of their LEGEND® account from within the application.

                    When an account-deletion request is confirmed, LEGEND® may immediately restrict account access while the deletion and closure process is completed.

                    Information that is not required to be retained may be deleted, anonymized, deactivated, or otherwise removed in accordance with LEGEND® policies and applicable requirements. Certain records may be retained when required for legal, regulatory, fraud-prevention, insurance, financial-services, security, dispute-resolution, or legitimate recordkeeping purposes.
                    """),
                LegendLegalSection(
                    "Your Choices",
                    """
                    You can manage certain privacy, profile, notification, community, and account settings from within LEGEND® or through your device settings.

                    Questions regarding privacy or account information may be directed to connect@mylegnd.com.
                    """)
            ],
            dismiss: dismiss)
    }

    private func legendTermsOfUseView(dismiss: @escaping () -> Void) -> some View {
        LegendLegalDocumentView(
            eyebrow: "LEGEND®",
            title: "Terms of Use",
            subtitle: "Using LEGEND® means agreeing to use the platform responsibly and in good faith.",
            sections: [
                LegendLegalSection(
                    "Using LEGEND®",
                    """
                    By accessing or using the LEGEND® mobile application, you agree to use the platform responsibly, lawfully, and in good faith.

                    LEGEND® provides a platform supporting personal growth, community, life coaching, education, financial wellness, and, where applicable, access to insurance-related guidance and services.

                    Features available to an individual user may vary based on account type, permissions, eligibility, location, licensing requirements, and services offered.
                    """),
                LegendLegalSection(
                    "Coaching & Educational Information",
                    """
                    Life-coaching, wellness, educational, community, and financial-wellness content provided through LEGEND® is intended to support personal development, organization, education, and informed decision-making.

                    Unless expressly stated otherwise in connection with a separately regulated professional service, content available through the app is not legal, tax, medical, or individualized investment advice.

                    LEGEND® does not guarantee specific personal, financial, health, career, relationship, business, investment, or other outcomes.
                    """),
                LegendLegalSection(
                    "Insurance-Related Services",
                    """
                    Insurance-related information or services available through LEGEND® are subject to applicable licensing requirements, eligibility, underwriting, carrier rules, product availability, approval, and applicable law.

                    Nothing displayed within the app guarantees issuance of an insurance policy, a particular premium, coverage amount, underwriting decision, or carrier approval.

                    Where regulated insurance services are provided, those services are governed by the applicable carrier documentation, disclosures, applications, policies, and regulatory requirements.
                    """),
                LegendLegalSection(
                    "User Content & Community Conduct",
                    """
                    You are responsible for content you create, upload, send, publish, or otherwise make available through LEGEND®.

                    You may not use LEGEND® to harass, threaten, impersonate, exploit, defraud, abuse, intimidate, or unlawfully target another person. You may not publish or transmit content that is unlawful, fraudulent, abusive, malicious, intentionally deceptive, or that violates another person's rights.

                    LEGEND® may investigate reports and may restrict content, communication, functionality, or account access when reasonably necessary to protect users or the platform. Reporting or blocking another user does not guarantee a particular moderation outcome.
                    """),
                LegendLegalSection(
                    "Messaging",
                    """
                    You are responsible for communications sent through your account.

                    Do not use LEGEND® messaging for spam, harassment, unlawful solicitation, fraud, threats, or prohibited activity.

                    Messages may be retained and reviewed when reasonably necessary for security, moderation, dispute resolution, legal compliance, or enforcement of these Terms.
                    """),
                LegendLegalSection(
                    "Account Security",
                    """
                    You are responsible for maintaining appropriate control of your account and authentication methods.

                    You may not intentionally access another person's account, misrepresent your identity, bypass security controls, interfere with application security, or attempt unauthorized access to LEGEND® systems or data.

                    You should promptly notify LEGEND® if you believe your account or credentials have been compromised.
                    """),
                LegendLegalSection(
                    "Availability & Changes",
                    """
                    LEGEND® continually develops and improves its services.

                    Features may be added, modified, restricted, suspended, or discontinued when reasonably necessary for security, compliance, reliability, product development, or business operations.

                    Temporary service interruptions may occur due to maintenance, technology providers, network conditions, platform updates, or circumstances outside LEGEND®'s reasonable control.
                    """),
                LegendLegalSection(
                    "Apple",
                    """
                    Your use of the LEGEND® iOS application is also subject to applicable Apple platform requirements and your agreements with Apple.

                    Apple is not responsible for providing LEGEND®'s coaching, community, insurance-related, financial-wellness, messaging, or other services.
                    """),
                LegendLegalSection(
                    "Termination & Account Closure",
                    """
                    LEGEND® may restrict, suspend, or terminate access when reasonably necessary because of unlawful activity, material violations of these Terms, threats to users or platform security, fraud, abuse, regulatory requirements, or misuse of the service.

                    Users may request eligible account closure through the account-management functionality provided within the application.
                    """),
                LegendLegalSection(
                    "Updates to These Terms",
                    """
                    LEGEND® may update these Terms as the application, services, legal obligations, or regulatory requirements evolve.

                    When material changes require additional notice or consent, LEGEND® may provide that notice through the application or another appropriate communication method.

                    Continued use of LEGEND® after an applicable update constitutes acceptance of the updated Terms to the extent permitted by law.
                    """),
                LegendLegalSection(
                    "Contact LEGEND®",
                    """
                    Questions regarding LEGEND®, your account, or these Terms may be directed to connect@mylegnd.com.
                    """)
            ],
            dismiss: dismiss)
    }

    private enum ProfileSettingsPresentation: String, Identifiable {
        case creatorInsights
        case founderManagement
        case followRequests
        case translationLanguage
        case dailyScriptureManagement
        case communitySafety
        case accountAccess
        case pushNotificationStatus
        case privacyPolicy
        case termsOfUse

        var id: String { rawValue }
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

                    if let controlledResourceRequestFeedback {
                        LegendRequestSubmissionPill(
                            feedback: controlledResourceRequestFeedback)
                    }

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
                                profileSettingsPresentation = .creatorInsights
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

                    if currentSession.actor.identity.participantType == .agent {
                        LegendProfileSettingsSection(title: "Agent portal") {
                            LegendAgentProfileSettingsRow()
                        }
                    }

                    if currentSession.capabilities.contains("founder") {
                        LegendProfileSettingsSection(title: "Founder management") {
                            Button {
                                profileSettingsPresentation = .founderManagement
                            } label: {
                                LegendProfileSettingsRow(
                                    title: "Founder management",
                                    detail: "Manage member access, leadership authority, and creator priority in one place.",
                                    systemImage: "crown.fill",
                                    showsChevron: true)
                            }
                            .buttonStyle(.plain)
                        }
                    }

                    if currentSession.capabilities.contains("scripture-management") {
                        LegendProfileSettingsSection(title: "Daily scripture") {
                            Button {
                                profileSettingsPresentation = .dailyScriptureManagement
                            } label: {
                                LegendProfileSettingsRow(
                                    title: "Manage Daily Scripture",
                                    detail: "Schedule and review the scripture shown across Legend.",
                                    systemImage: "book.closed",
                                    showsChevron: true)
                            }
                            .buttonStyle(.plain)
                        }
                    }

                    if currentSession.capabilities.contains("community-management") {
                        LegendProfileSettingsSection(title: "Community") {
                            Button {
                                profileSettingsPresentation = .communitySafety
                            } label: {
                                LegendProfileSettingsRow(
                                    title: "Community review",
                                    detail: "Review open member safety reports",
                                    systemImage: "shield.lefthalf.filled",
                                    showsChevron: true)
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
                                    profileSettingsPresentation = .followRequests
                                } label: {
                                    LegendProfileSettingsRow(
                                        title: "Follow requests",
                                        detail: "Approve or decline people waiting to follow you",
                                        systemImage: "person.badge.clock",
                                        showsChevron: true)
                                }
                                .buttonStyle(.plain)
                            }

                            LegendProfileSettingsDivider()

                            Button {
                                profileSettingsPresentation = .privacyPolicy
                            } label: {
                                LegendProfileSettingsRow(
                                    title: "Privacy Policy",
                                    detail: "How LEGEND® handles your information",
                                    systemImage: "hand.raised.fill",
                                    showsChevron: true)
                            }
                            .buttonStyle(.plain)
                            .accessibilityHint("Opens the LEGEND privacy policy")

                            LegendProfileSettingsDivider()

                            Button {
                                profileSettingsPresentation = .termsOfUse
                            } label: {
                                LegendProfileSettingsRow(
                                    title: "Terms of Use",
                                    detail: "Rules governing your use of LEGEND®",
                                    systemImage: "doc.text.fill",
                                    showsChevron: true)
                            }
                            .buttonStyle(.plain)
                            .accessibilityHint("Opens the LEGEND terms of use")
                        }
                    }

                    if !profile.isVerified {
                        LegendProfileSettingsSection(title: "Verification") {
                            Button {
                                submitControlledResourceRequest(.verificationBadge)
                            } label: {
                                LegendProfileSettingsRow(
                                    title: messages.isSubmittingControlledResourceRequest
                                        ? "Sending verification request…"
                                        : "Request verification",
                                    detail: "Submit your profile to the private Legend review team.",
                                    systemImage: "checkmark.seal",
                                    showsChevron: true)
                            }
                            .buttonStyle(.plain)
                            .disabled(messages.isSubmittingControlledResourceRequest)
                        }
                    }

                    LegendProfileSettingsSection(title: "Language translation") {
                        VStack(spacing: 0) {
                            if profile.translationAccess.isGranted {
                                Button {
                                    profileSettingsPresentation = .translationLanguage
                                } label: {
                                    LegendProfileSettingsRow(
                                        title: "Translation language",
                                        detail: legendLanguageName(profile.translationAccess.preferredCommunicationLanguage),
                                        systemImage: "character.bubble",
                                        showsChevron: true)
                                }
                                .buttonStyle(.plain)

                            } else {
                                Button {
                                    submitControlledResourceRequest(.languageTranslation)
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
                                .disabled(profile.translationAccess.isPending || messages.isSubmittingControlledResourceRequest)
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

                            LegendProfileSettingsDivider()

                            Button {
                                profileSettingsPresentation = .pushNotificationStatus
                                pushNotifications.refreshNotificationAuthorizationStatus()
                                Task { await bootstrap.stores.notifications.refreshPushDiagnostic() }
                            } label: {
                                LegendProfileSettingsRow(
                                    title: "Push notification status",
                                    detail: "Review this device's secure delivery state",
                                    systemImage: "bell.badge",
                                    showsChevron: true)
                            }
                            .buttonStyle(.plain)
                        }
                    }

                    LegendProfileSettingsSection(title: "Account access") {
                        Button {
                            profileSettingsPresentation = .accountAccess
                        } label: {
                            LegendProfileSettingsRow(
                                title: "Pause or delete account",
                                detail: "Review pause and account-deletion options",
                                systemImage: "person.crop.circle.badge.exclamationmark",
                                showsChevron: true)
                        }
                        .buttonStyle(.plain)
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
        .sheet(item: $profileSettingsPresentation) { presentation in
            switch presentation {
            case .creatorInsights:
                if case .loaded(let snapshot) = social.state {
                    LegendCreatorInsightsSheet(
                        insights: snapshot.creatorInsights,
                        profileMetrics: snapshot.currentProfileMetrics)
                }
            case .founderManagement:
                LegendFounderManagementModel(messages: messages)
            case .followRequests:
                LegendFollowRequestsSheet(social: social)
            case .translationLanguage:
                LegendTranslationLanguagePicker(
                    profile: profile,
                    store: account,
                    messages: messages)
            case .dailyScriptureManagement:
                LegendDailyScriptureManagementView(
                    store: bootstrap.stores.dailyScriptureManagement)
            case .communitySafety:
                LegendCommunitySafetyReview(
                    store: coordinator.makeCommunitySafetyStore(),
                    isFounder: currentSession.capabilities.contains("founder"))
            case .accountAccess:
                LegendAccountAccessSheet(account: account)
            case .pushNotificationStatus:
                LegendPushNotificationStatusSheet(
                    notifications: bootstrap.stores.notifications)
                .environmentObject(pushNotifications)
            case .privacyPolicy:
                legendPrivacyPolicyView {
                    profileSettingsPresentation = nil
                }
            case .termsOfUse:
                legendTermsOfUseView {
                    profileSettingsPresentation = nil
                }
            }
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

    private func submitControlledResourceRequest(_ resourceType: ControlledResourceType) {
        controlledResourceRequestFeedback = nil
        Task {
            switch await messages.submitControlledResourceRequest(resourceType) {
            case .sent:
                controlledResourceRequestFeedback = .sent(resourceType)
                await bootstrap.refreshProfile()
            case .failed(let failure):
                controlledResourceRequestFeedback = .failed(failure)
            }
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

/// A production-safe view of system and server APNs facts. The server response
/// is actor-scoped and redacted; this sheet never reads or renders a token.
private struct LegendPushNotificationStatusSheet: View {
    @Environment(\.dismiss) private var dismiss
    @EnvironmentObject private var pushNotifications: LegendPushNotificationDelegate
    @ObservedObject var notifications: MobileNotificationStore

    var body: some View {
        NavigationStack {
            LegendScrollView(tracksNavigationChrome: false) {
                VStack(alignment: .leading, spacing: LegendNextSpacing.md) {
                    LegendNextSheetHeader(
                        eyebrow: "Security",
                        title: "Push notification status",
                        detail: "This view reports only this signed-in device's notification state. Device tokens and credentials are never displayed.",
                        dismiss: { dismiss() })

                    LegendProfileSettingsSection(title: "This iPhone") {
                        VStack(spacing: 0) {
                            statusRow(
                                title: "Notification permission",
                                detail: notificationPermissionDetail,
                                systemImage: "bell")
                            LegendProfileSettingsDivider()
                            statusRow(
                                title: "APNs registration",
                                detail: pushNotifications.registrationState.displayName,
                                systemImage: "antenna.radiowaves.left.and.right")
                            LegendProfileSettingsDivider()
                            statusRow(
                                title: "Signed APNs environment",
                                detail: pushNotifications.signedEnvironment?.displayName ?? "Unavailable",
                                systemImage: "signature")
                            LegendProfileSettingsDivider()
                            statusRow(
                                title: "Device token",
                                detail: pushNotifications.deviceToken == nil ? "Missing" : "Registered",
                                systemImage: "checkmark.shield")
                        }
                    }

                    LegendProfileSettingsSection(title: "Legend server") {
                        VStack(spacing: 0) {
                            statusRow(
                                title: "Server registration",
                                detail: registrationDetail,
                                systemImage: "server.rack")
                            LegendProfileSettingsDivider()
                            statusRow(
                                title: "Server environment",
                                detail: environmentDetail,
                                systemImage: "network")
                            LegendProfileSettingsDivider()
                            statusRow(
                                title: "Latest registration timestamp",
                                detail: formatted(lastRegistrationUTC),
                                systemImage: "clock.arrow.circlepath")
                            LegendProfileSettingsDivider()
                            statusRow(
                                title: "Last registration result",
                                detail: registrationResultDetail,
                                systemImage: "checkmark.circle")
                        }
                    }

                    LegendProfileSettingsSection(title: "Last APNs delivery") {
                        VStack(spacing: 0) {
                            statusRow(
                                title: "Delivery result",
                                detail: deliveryDetail,
                                systemImage: "paperplane")
                            LegendProfileSettingsDivider()
                            statusRow(
                                title: "Delivery timestamp",
                                detail: formatted(notifications.pushDiagnostic?.lastDeliveryUTC),
                                systemImage: "clock")
                            LegendProfileSettingsDivider()
                            statusRow(
                                title: "APNs status",
                                detail: notifications.pushDiagnostic?.lastAPNSStatus.map(String.init) ?? "Unavailable",
                                systemImage: "number")
                            LegendProfileSettingsDivider()
                            statusRow(
                                title: "APNs reason",
                                detail: notifications.pushDiagnostic?.lastAPNSReason ?? "Unavailable",
                                systemImage: "exclamationmark.bubble")
                            if let attempts = notifications.pushDiagnostic?.deliveryAttemptCount {
                                LegendProfileSettingsDivider()
                                statusRow(
                                    title: "Delivery attempts",
                                    detail: attempts.formatted(),
                                    systemImage: "arrow.triangle.2.circlepath")
                            }
                        }
                    }

                    Button(notifications.isRefreshingPushDiagnostic ? "Refreshing…" : "Refresh status") {
                        pushNotifications.refreshNotificationAuthorizationStatus()
                        Task { await notifications.refreshPushDiagnostic() }
                    }
                    .buttonStyle(LegendNextButtonStyle(kind: .primary))
                    .disabled(notifications.isRefreshingPushDiagnostic)
                }
                .padding(LegendNextSpacing.sm)
                .padding(.bottom, LegendNextSpacing.xl)
            }
            .background(LegendNextCanvas())
            .toolbar(.hidden, for: .navigationBar)
        }
        .tint(LegendNextColor.gold)
        .legendNextSheetChrome(detents: [.medium, .large])
        .task {
            pushNotifications.refreshNotificationAuthorizationStatus()
            await notifications.refreshPushDiagnostic()
        }
    }

    private var notificationPermissionDetail: String {
        switch pushNotifications.authorizationStatus {
        case .authorized: "Authorized"
        case .denied: "Denied"
        case .provisional: "Provisional"
        case .ephemeral: "Ephemeral"
        case .notDetermined: "Not determined"
        @unknown default: "Unavailable"
        }
    }

    private var registrationDetail: String {
        switch notifications.pushRegistrationState {
        case .registering: return "Registering"
        case .registered: return "Registered"
        case .failed: return "Failed"
        case .unknown: break
        }

        switch notifications.pushDiagnostic?.registrationState {
        case "registered": return "Registered"
        case "inactive": return "Inactive"
        case "missing": return "Missing"
        default: return "Unknown"
        }
    }

    private var environmentDetail: String {
        switch notifications.pushDiagnostic?.environment {
        case "sandbox": "Development"
        case "production": "Production"
        default: "Unknown"
        }
    }

    private var registrationResultDetail: String {
        switch notifications.pushRegistrationState {
        case .registering: return "Registering"
        case .registered: return "Registered"
        case .failed: return "Failed"
        case .unknown: break
        }

        switch notifications.pushDiagnostic?.lastRegistrationResult {
        case "registered": return "Registered"
        case "inactive": return "Inactive"
        default: return "Unknown"
        }
    }

    private var lastRegistrationUTC: Date? {
        notifications.lastPushRegistrationAttemptUTC ??
            notifications.pushDiagnostic?.lastRegistrationUTC
    }

    private var deliveryDetail: String {
        switch notifications.pushDiagnostic?.deliveryState {
        case "delivered": "Delivered"
        case "failed": "Failed"
        case "pending": "Pending"
        case "suppressed": "Suppressed"
        default: "Unknown"
        }
    }

    private func formatted(_ value: Date?) -> String {
        guard let value else { return "Unavailable" }
        return value.formatted(date: .abbreviated, time: .shortened)
    }

    private func statusRow(title: String, detail: String, systemImage: String) -> some View {
        LegendProfileSettingsRow(
            title: title,
            detail: detail,
            systemImage: systemImage,
            showsChevron: false)
    }
}

/// Account deletion is intentionally placed inside profile settings and requires
/// a second, typed confirmation. Pausing is the clear reversible alternative.
private struct LegendAccountAccessSheet: View {
    @ObservedObject var account: MobileAccountStore
    @Environment(\.dismiss) private var dismiss
    @State private var isConfirmingPause = false
    @State private var isPresentingClosureConfirmation = false

    var body: some View {
        NavigationStack {
            LegendScrollView(tracksNavigationChrome: false) {
                VStack(alignment: .leading, spacing: LegendNextSpacing.md) {
                    LegendNextSheetHeader(
                        eyebrow: "Account access",
                        title: "Pause or delete your account",
                        detail: "Pausing is reversible. It stops access to Legend until you return here and resume your account.",
                        dismiss: { dismiss() })

                    LegendProfileSettingsSection(title: "Pause account") {
                        VStack(alignment: .leading, spacing: LegendNextSpacing.sm) {
                            Text("Choose this if you need time away. Your regular Legend access is disabled until you resume the account.")
                                .font(.subheadline)
                                .foregroundStyle(LegendNextColor.textSecondary)

                            Button(account.isUpdatingLifecycle ? "Pausing account…" : "Pause account") {
                                isConfirmingPause = true
                            }
                            .buttonStyle(LegendNextButtonStyle(kind: .primary))
                            .disabled(account.isUpdatingLifecycle)
                        }
                    }

                    LegendProfileSettingsSection(title: "Delete account") {
                        VStack(alignment: .leading, spacing: LegendNextSpacing.sm) {
                            Text("Deleting your account removes Legend access immediately. It is not the same as signing out. Account-owned data is handled through the applicable retention process; records required for legal, financial, insurance, security, or audit purposes may remain.")
                                .font(.subheadline)
                                .foregroundStyle(LegendNextColor.textSecondary)

                            Button("Continue to delete account") {
                                isPresentingClosureConfirmation = true
                            }
                            .buttonStyle(.plain)
                            .foregroundStyle(LegendNextColor.goldBright)
                            .accessibilityHint("Opens the account deletion confirmation")
                        }
                    }

                    if let failure = account.actionFailure {
                        LegendNextSurface(style: .profileSettings, padding: LegendNextSpacing.sm) {
                            Text(failure.message)
                                .font(.footnote)
                                .foregroundStyle(LegendNextColor.textSecondary)
                        }
                    }
                }
                .padding(LegendNextSpacing.sm)
                .padding(.bottom, LegendNextSpacing.xl)
            }
            .background(LegendNextCanvas())
            .toolbar(.hidden, for: .navigationBar)
        }
        .tint(LegendNextColor.gold)
        .legendNextSheetChrome(detents: [.medium, .large])
        .confirmationDialog(
            "Pause your Legend account?",
            isPresented: $isConfirmingPause,
            titleVisibility: .visible
        ) {
            Button("Pause account") {
                Task {
                    if await account.pauseAccount() {
                        dismiss()
                    }
                }
            }
            Button("Keep account active", role: .cancel) {}
        } message: {
            Text("You will lose regular Legend access until you return to Account access and resume the account.")
        }
        .sheet(isPresented: $isPresentingClosureConfirmation) {
            LegendAccountClosureConfirmationSheet(account: account)
        }
    }
}

private struct LegendAccountClosureConfirmationSheet: View {
    @ObservedObject var account: MobileAccountStore
    @Environment(\.dismiss) private var dismiss
    @State private var confirmation = ""

    private var hasConfirmedClosure: Bool {
        confirmation.trimmingCharacters(in: .whitespacesAndNewlines) == "DELETE"
    }

    var body: some View {
        NavigationStack {
            LegendScrollView(tracksNavigationChrome: false) {
                VStack(alignment: .leading, spacing: LegendNextSpacing.md) {
                    LegendNextSheetHeader(
                        eyebrow: "Final confirmation",
                        title: "Delete your Legend account",
                        detail: "This sends an account-deletion request and disables Legend access immediately.",
                        dismiss: { dismiss() })

                    LegendProfileSettingsSection(title: "Before you continue") {
                        Text("You will no longer be able to use Legend while deletion is in progress. This action cannot be undone from the app. If you only need time away, go back and pause your account instead.")
                            .font(.subheadline)
                            .foregroundStyle(LegendNextColor.textSecondary)
                    }

                    LegendProfileSettingsSection(title: "Confirm deletion") {
                        VStack(alignment: .leading, spacing: LegendNextSpacing.sm) {
                            Text("Type DELETE to confirm that you want to delete this account.")
                                .font(.subheadline)
                                .foregroundStyle(LegendNextColor.textSecondary)

                            TextField("DELETE", text: $confirmation)
                                .textInputAutocapitalization(.characters)
                                .autocorrectionDisabled()
                                .padding(.horizontal, LegendNextSpacing.sm)
                                .frame(minHeight: 48)
                                .background(
                                    LegendNextColor.brandBlueSurface,
                                    in: RoundedRectangle(
                                        cornerRadius: LegendNextRadius.control,
                                        style: .continuous))
                        }
                    }

                    Button(account.isUpdatingLifecycle ? "Sending deletion request…" : "Request account deletion") {
                        Task {
                            if await account.requestAccountDeletion(confirmation: confirmation) {
                                dismiss()
                            }
                        }
                    }
                    .buttonStyle(LegendNextButtonStyle(kind: .destructive))
                    .disabled(!hasConfirmedClosure || account.isUpdatingLifecycle)

                    if let failure = account.actionFailure {
                        Text(failure.message)
                            .font(.footnote)
                            .foregroundStyle(LegendNextColor.textSecondary)
                    }
                }
                .padding(LegendNextSpacing.sm)
                .padding(.bottom, LegendNextSpacing.xl)
            }
            .background(LegendNextCanvas())
            .toolbar(.hidden, for: .navigationBar)
        }
        .tint(LegendNextColor.gold)
        .legendNextSheetChrome(detents: [.large])
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

struct LegendCommunitySafetyReview: View {
    @StateObject private var store: MobileCommunitySafetyStore
    let isFounder: Bool
    @Environment(\.dismiss) private var dismiss

    init(store: MobileCommunitySafetyStore, isFounder: Bool) {
        _store = StateObject(wrappedValue: store)
        self.isFounder = isFounder
    }

    var body: some View {
        NavigationStack {
            LegendScrollView(tracksNavigationChrome: false) {
                VStack(alignment: .leading, spacing: LegendNextSpacing.md) {
                    LegendNextSheetHeader(
                        eyebrow: "Community",
                        title: "Safety review",
                        detail: "Open reports requiring a recorded decision.",
                        dismiss: { dismiss() })

                    content
                }
                .padding(LegendNextSpacing.sm)
                .padding(.bottom, LegendNextSpacing.xl)
            }
            .background(LegendNextCanvas())
            .toolbar(.hidden, for: .navigationBar)
        }
        .tint(LegendNextColor.gold)
        .legendNextSheetChrome(detents: [.large])
        .task { await store.load() }
        .alert(
            store.actionFailure?.title ?? "Community review unavailable",
            isPresented: Binding(
                get: { store.actionFailure != nil },
                set: { if !$0 { store.dismissActionFailure() } })
        ) {
            Button("OK", role: .cancel) { store.dismissActionFailure() }
        } message: {
            Text(store.actionFailure?.message ?? "The report could not be updated.")
        }
    }

    @ViewBuilder
    private var content: some View {
        switch store.state {
        case .idle, .loading:
            ProgressView("Loading community reports")
                .frame(maxWidth: .infinity, minHeight: 180)
        case .unavailable(let failure):
            LegendNextErrorState(
                title: failure.title,
                message: failure.message,
                retryTitle: "Retry",
                retry: { Task { await store.load() } })
        case .loaded(let reports):
            if reports.isEmpty {
                LegendNextEmptyState(
                    title: "All caught up",
                    message: "There are no open community reports.",
                    systemImage: "checkmark.shield")
            } else {
                LazyVStack(spacing: LegendNextSpacing.sm) {
                    ForEach(reports) { report in
                        reportRow(report)
                    }
                }
            }
        }
    }

    private func reportRow(_ report: MobileCommunitySafetyReport) -> some View {
        let isResolving = store.isResolvingReportID == report.id
        return VStack(alignment: .leading, spacing: LegendNextSpacing.xs) {
            HStack(alignment: .top, spacing: LegendNextSpacing.sm) {
                Image(systemName: "exclamationmark.shield.fill")
                    .foregroundStyle(LegendNextColor.gold)
                    .frame(width: 34, height: 34)
                    .background(LegendNextColor.gold.opacity(0.12), in: Circle())

                VStack(alignment: .leading, spacing: 2) {
                    Text(report.category)
                        .font(LegendNextTypography.section)
                        .foregroundStyle(LegendNextColor.textPrimary)
                    Text(report.targetKind)
                        .font(LegendNextTypography.caption)
                        .foregroundStyle(LegendNextColor.textSecondary)
                }

                Spacer(minLength: 0)

                Menu {
                    Button(MobileCommunitySafetyResolution.dismissed.title) {
                        Task { _ = await store.resolve(report, as: .dismissed) }
                    }
                    Button(MobileCommunitySafetyResolution.needsInvestigation.title) {
                        Task { _ = await store.resolve(report, as: .needsInvestigation) }
                    }
                    if isFounder, report.targetKind == "SocialPost" {
                        Button(
                            MobileCommunitySafetyResolution.actioned.title,
                            role: .destructive
                        ) {
                            Task { _ = await store.resolve(report, as: .actioned) }
                        }
                    }
                } label: {
                    if isResolving {
                        ProgressView()
                            .tint(LegendNextColor.gold)
                            .frame(width: 34, height: 34)
                    } else {
                        Image(systemName: "ellipsis.circle")
                            .font(.title3.weight(.semibold))
                            .foregroundStyle(LegendNextColor.gold)
                            .frame(width: 34, height: 34)
                    }
                }
                .disabled(isResolving)
            }

            if let detail = report.detail?.trimmingCharacters(in: .whitespacesAndNewlines),
               !detail.isEmpty {
                Text(detail)
                    .font(LegendNextTypography.caption)
                    .foregroundStyle(LegendNextColor.textSecondary)
                    .fixedSize(horizontal: false, vertical: true)
            }

            Text(report.createdUTC.formatted(date: .abbreviated, time: .shortened))
                .font(LegendNextTypography.caption)
                .foregroundStyle(LegendNextColor.textSecondary.opacity(0.8))
        }
        .padding(LegendNextSpacing.sm)
        .background(LegendNextColor.surface, in: RoundedRectangle(
            cornerRadius: LegendNextRadius.compact,
            style: .continuous))
        .overlay {
            RoundedRectangle(cornerRadius: LegendNextRadius.compact, style: .continuous)
                .strokeBorder(LegendNextColor.gold.opacity(0.30), lineWidth: 1)
        }
    }
}

/// The catalog of member authorities is defined once and rendered by the one
/// founder-management model from either Settings or a member profile.
private enum LegendFounderAuthority: CaseIterable, Identifiable {
    case languageTranslation
    case scriptureManagement
    case communityManagement
    case socialContentPriority

    var id: String { resourceType.rawValue }

    var resourceType: ControlledResourceType {
        switch self {
        case .languageTranslation: .languageTranslation
        case .scriptureManagement: .scriptureManagement
        case .communityManagement: .communityManagement
        case .socialContentPriority: .socialContentPriority
        }
    }

    var detail: String {
        switch self {
        case .languageTranslation:
            "Grant or revoke access to LEGEND language translation."
        case .scriptureManagement:
            "Delegate Daily Scripture scheduling and editorial management."
        case .communityManagement:
            "Delegate report triage while content removal remains Founder-only."
        case .socialContentPriority:
            "Prioritize eligible Posts and Hacs above the standard feed ranking."
        }
    }

    var systemImage: String {
        switch self {
        case .languageTranslation: "character.bubble.fill"
        case .scriptureManagement: "book.closed.fill"
        case .communityManagement: "person.badge.shield.checkmark"
        case .socialContentPriority: "sparkles.tv.fill"
        }
    }
}

/// The one founder-only authority surface. A Settings launch manages the
/// directory, while a profile launch opens the same control model scoped to
/// that member. Every grant writes through the existing typed server endpoint.
struct LegendFounderManagementModel: View {
    @ObservedObject var messages: MessagingStore
    let member: MobileSocialAuthor?
    @Binding private var verificationReview: VerificationReview?
    private let resolveVerification: ((VerificationReview, Bool, String?) async -> Bool)?
    @Environment(\.dismiss) private var dismiss
    @State private var selectedResource: ControlledResourceType?
    @State private var isPresentingAccountRemoval = false

    init(
        messages: MessagingStore,
        member: MobileSocialAuthor? = nil,
        verificationReview: Binding<VerificationReview?> = .constant(nil),
        resolveVerification: ((VerificationReview, Bool, String?) async -> Bool)? = nil
    ) {
        _messages = ObservedObject(wrappedValue: messages)
        self.member = member
        _verificationReview = verificationReview
        self.resolveVerification = resolveVerification
    }

    var body: some View {
        NavigationStack {
            LegendScrollView(tracksNavigationChrome: false) {
                VStack(alignment: .leading, spacing: LegendNextSpacing.md) {
                    LegendNextSheetHeader(
                        eyebrow: "Founder management",
                        title: member?.displayName ?? "Member authority",
                        detail: member == nil
                            ? "Manage Founder-only member authority from one place."
                            : "Review and update this member's Founder-managed access in one place.",
                        dismiss: { dismiss() })

                    if let member {
                        LegendFounderMemberAuthorityList(
                            profile: member,
                            messages: messages,
                            verificationReview: $verificationReview,
                            resolveVerification: resolveVerification)
                    } else {
                        ForEach(LegendFounderAuthority.allCases) { authority in
                            capabilityRow(authority)
                        }

                        Button {
                            isPresentingAccountRemoval = true
                        } label: {
                            LegendFounderAccountRemovalRow()
                        }
                        .buttonStyle(.plain)
                    }
                }
                .padding(LegendNextSpacing.sm)
                .padding(.bottom, LegendNextSpacing.xl)
            }
            .background(LegendNextCanvas())
            .toolbar(.hidden, for: .navigationBar)
        }
        .tint(LegendNextColor.gold)
        .legendNextSheetChrome(detents: [.medium, .large])
        .sheet(item: $selectedResource) { resourceType in
            LegendControlledResourceAccessManager(
                messages: messages,
                resourceType: resourceType)
        }
        .sheet(isPresented: $isPresentingAccountRemoval) {
            LegendFounderAccountRemovalManager(messages: messages)
        }
    }

    private func capabilityRow(
        _ authority: LegendFounderAuthority
    ) -> some View {
        Button {
            selectedResource = authority.resourceType
        } label: {
            HStack(spacing: LegendNextSpacing.sm) {
                Image(systemName: authority.systemImage)
                    .font(.title3.weight(.semibold))
                    .foregroundStyle(LegendNextColor.gold)
                    .frame(width: 38, height: 38)
                    .background(LegendNextColor.gold.opacity(0.12), in: Circle())

                VStack(alignment: .leading, spacing: 3) {
                    Text(authority.resourceType.displayName)
                        .font(LegendNextTypography.section)
                        .foregroundStyle(LegendNextColor.textPrimary)
                    Text(authority.detail)
                        .font(LegendNextTypography.caption)
                        .foregroundStyle(LegendNextColor.textSecondary)
                        .fixedSize(horizontal: false, vertical: true)
                }

                Spacer(minLength: 0)

                Image(systemName: "chevron.right")
                    .font(.caption.weight(.bold))
                    .foregroundStyle(LegendNextColor.gold)
            }
            .padding(LegendNextSpacing.sm)
            .background(LegendNextColor.surface, in: RoundedRectangle(
                cornerRadius: LegendNextRadius.compact,
                style: .continuous))
            .overlay {
                RoundedRectangle(cornerRadius: LegendNextRadius.compact, style: .continuous)
                    .strokeBorder(LegendNextColor.gold.opacity(0.35), lineWidth: 1)
            }
        }
        .buttonStyle(.plain)
    }
}

/// The member-specific content of the shared founder-management model. It
/// receives its authority definitions from the shared catalog so Settings and
/// profile launches cannot drift.
private struct LegendFounderMemberAuthorityList: View {
    private enum AccessState: Equatable {
        case loading
        case granted
        case notGranted
        case unavailable
    }

    let profile: MobileSocialAuthor
    @ObservedObject var messages: MessagingStore
    @Binding var verificationReview: VerificationReview?
    let resolveVerification: ((VerificationReview, Bool, String?) async -> Bool)?
    @State private var accessStates: [ControlledResourceType: AccessState] = [:]
    @State private var updatingResource: ControlledResourceType?
    @State private var isResolvingVerification = false
    @State private var verificationResolutionNote = ""

    var body: some View {
        VStack(alignment: .leading, spacing: LegendNextSpacing.xs) {
            verificationResolutionCard

            ForEach(LegendFounderAuthority.allCases) { authority in
                authorityRow(authority)
            }

            if let account = managedAccount {
                LegendFounderAccountRemovalAction(
                    account: account,
                    messages: messages)
            }
        }
        .task { await loadAccess() }
    }

    @ViewBuilder
    private var verificationResolutionCard: some View {
        if let verificationReview,
           verificationReview.canResolve,
           verificationReview.status == "Pending",
           resolveVerification != nil {
            LegendNextSurface(style: .navy) {
                VStack(alignment: .leading, spacing: LegendNextSpacing.sm) {
                    Label(
                        "\(verificationReview.resourceType.displayName) review",
                        systemImage: "checkmark.seal.fill"
                    )
                    .font(.headline.weight(.bold))
                    .foregroundStyle(.white)

                    Text("Resolve this member's pending request from Founder management.")
                        .font(LegendNextTypography.caption)
                        .foregroundStyle(.white.opacity(0.74))

                    TextField(
                        "Optional member update",
                        text: $verificationResolutionNote,
                        axis: .vertical
                    )
                    .font(LegendNextTypography.caption)
                    .foregroundStyle(.white)
                    .lineLimit(1...3)
                    .padding(.horizontal, LegendNextSpacing.sm)
                    .padding(.vertical, LegendNextSpacing.xs)
                    .background(.white.opacity(0.12), in: RoundedRectangle(
                        cornerRadius: LegendNextRadius.compact,
                        style: .continuous
                    ))

                    HStack(spacing: LegendNextSpacing.sm) {
                        Button(role: .destructive) {
                            Task { await resolve(verificationReview, approved: false) }
                        } label: {
                            Text("Decline")
                                .frame(maxWidth: .infinity)
                        }
                        .buttonStyle(LegendNextButtonStyle(
                            kind: .secondary,
                            controlHeight: 40
                        ))
                        .disabled(isResolvingVerification)

                        Button {
                            Task { await resolve(verificationReview, approved: true) }
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
                        .buttonStyle(LegendNextButtonStyle(
                            kind: .primary,
                            controlHeight: 40
                        ))
                        .disabled(isResolvingVerification)
                    }
                }
            }
        }
    }

    private func authorityRow(
        _ authority: LegendFounderAuthority
    ) -> some View {
        let resourceType = authority.resourceType
        let state = accessStates[resourceType] ?? .loading
        let isUpdating = updatingResource == resourceType
        let isGranted = state == .granted
        return HStack(alignment: .top, spacing: LegendNextSpacing.sm) {
            Image(systemName: authority.systemImage)
                .font(.title3.weight(.semibold))
                .foregroundStyle(LegendNextColor.gold)
                .frame(width: 38, height: 38)
                .background(LegendNextColor.gold.opacity(0.12), in: Circle())

            VStack(alignment: .leading, spacing: 3) {
                Text(resourceType.displayName)
                    .font(LegendNextTypography.section)
                    .foregroundStyle(LegendNextColor.textPrimary)
                Text(authority.detail)
                    .font(LegendNextTypography.caption)
                    .foregroundStyle(LegendNextColor.textSecondary)
                    .fixedSize(horizontal: false, vertical: true)
            }

            Spacer(minLength: 0)

            switch state {
            case .loading:
                ProgressView()
                    .tint(LegendNextColor.gold)
                    .frame(width: 68, height: 34)
            case .unavailable:
                Text("Unavailable")
                    .font(LegendNextTypography.caption.weight(.semibold))
                    .foregroundStyle(LegendNextColor.textSecondary)
            case .granted, .notGranted:
                Button(isUpdating ? "Saving…" : (isGranted ? "Revoke" : "Grant")) {
                    Task { await update(resourceType, isGranted: !isGranted) }
                }
                .buttonStyle(LegendNextButtonStyle(
                    kind: isGranted ? .secondary : .primary,
                    isFullWidth: false,
                    controlHeight: 34))
                .disabled(isUpdating)
            }
        }
        .padding(LegendNextSpacing.sm)
        .background(LegendNextColor.surface, in: RoundedRectangle(
            cornerRadius: LegendNextRadius.compact,
            style: .continuous))
        .overlay {
            RoundedRectangle(cornerRadius: LegendNextRadius.compact, style: .continuous)
                .strokeBorder(LegendNextColor.gold.opacity(0.35), lineWidth: 1)
        }
    }

    private var recipient: MessagingRecipient {
        MessagingRecipient(
            identity: profile.identity,
            profileID: profile.profileID,
            displayName: profile.displayName,
            email: profile.publicEmail,
            roleLabel: profile.roleLabel,
            relationshipLabel: nil,
            existingConversationID: nil,
            avatar: profile.avatar,
            isVerified: profile.isVerified)
    }

    private var managedAccount: FounderManagedAccount? {
        guard let profileID = UUID(uuidString: profile.profileID) else { return nil }
        return FounderManagedAccount(
            profileID: profileID,
            userID: profile.identity.userID,
            participantType: profile.identity.participantType,
            displayName: profile.displayName,
            email: profile.publicEmail,
            lifecycleState: "Active",
            hasCancelableSubscription: false,
            isActive: true)
    }

    private func loadAccess() async {
        for authority in LegendFounderAuthority.allCases {
            accessStates[authority.resourceType] = await state(
                for: authority.resourceType)
        }
    }

    private func state(for resourceType: ControlledResourceType) async -> AccessState {
        guard let members = await messages.controlledResourceRecipients(
            resourceType,
            search: profile.displayName),
              let member = members.first(where: { $0.identity == profile.identity }) else {
            return .unavailable
        }
        return member.resourceAccessState == "Granted" ? .granted : .notGranted
    }

    private func update(_ resourceType: ControlledResourceType, isGranted: Bool) async {
        updatingResource = resourceType
        defer { updatingResource = nil }
        guard await messages.setControlledResourceGrant(
            resourceType,
            recipient: recipient,
            isGranted: isGranted) else {
            return
        }
        accessStates[resourceType] = isGranted ? .granted : .notGranted
    }

    private func resolve(
        _ review: VerificationReview,
        approved: Bool
    ) async {
        guard let resolveVerification else { return }
        isResolvingVerification = true
        defer { isResolvingVerification = false }

        let note = verificationResolutionNote
            .trimmingCharacters(in: .whitespacesAndNewlines)
        guard await resolveVerification(
            review,
            approved,
            note.isEmpty ? nil : note
        ) else {
            return
        }

        verificationReview = nil
        verificationResolutionNote = ""
    }
}

struct LegendControlledResourceAccessManager: View {
    @ObservedObject var messages: MessagingStore
    let resourceType: ControlledResourceType
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
                        title: resourceType.displayName,
                        detail: "Grant or remove \(resourceType.displayName) for any active Legend profile.",
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
                LegendVerifiedName(
                    recipient.displayName,
                    isVerified: recipient.isVerified == true,
                    font: .subheadline.weight(.bold),
                    textColor: .white,
                    badgePlacement: .alongsideProfileImage)
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
            resourceType,
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
            resourceType,
            recipient: recipient,
            isGranted: isGranted) else {
            return
        }
        await reloadDirectory()
    }
}

/// Account removal is intentionally part of Founder Management, but its server
/// authority is separate from public member discovery. This lets the Founder
/// locate unsubscribed or inactive accounts without widening anyone else's
/// social-search visibility.
private struct LegendFounderAccountRemovalManager: View {
    @ObservedObject var messages: MessagingStore
    @Environment(\.dismiss) private var dismiss
    @State private var accounts: MobileDataLoadState<[FounderManagedAccount]> = .idle
    @State private var search = ""
    @State private var scope: FounderAccountDirectoryScope = .active
    @State private var selectedAccountKeys = Set<FounderAccountSelectionKey>()
    @State private var isPresentingBatchConfirmation = false

    var body: some View {
        NavigationStack {
            LegendScrollView(tracksNavigationChrome: false) {
                VStack(alignment: .leading, spacing: LegendNextSpacing.md) {
                    LegendNextSheetHeader(
                        eyebrow: "Founder management",
                        title: scope == .active ? "Account removal" : "Removed accounts",
                        detail: scope == .active
                            ? "Select accounts together, then close access through the protected workflow."
                            : "Permanently erase selected archived account records from Legend.",
                        dismiss: { dismiss() })

                    Picker("Account directory", selection: $scope) {
                        Text("Accounts").tag(FounderAccountDirectoryScope.active)
                        Text("Archive").tag(FounderAccountDirectoryScope.archive)
                    }
                    .pickerStyle(.segmented)

                    TextField("Search name, email, or account ID", text: $search)
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

                    content

                    if !selectedAccounts.isEmpty {
                        Button(role: .destructive) {
                            isPresentingBatchConfirmation = true
                        } label: {
                            Label(
                                scope == .active
                                    ? "Remove \(selectedAccounts.count) selected"
                                    : "Erase \(selectedAccounts.count) selected",
                                systemImage: scope == .active ? "archivebox.fill" : "trash.fill")
                                .frame(maxWidth: .infinity)
                        }
                        .buttonStyle(LegendNextButtonStyle(kind: .secondary))
                    }
                }
                .padding(LegendNextSpacing.sm)
                .padding(.bottom, LegendNextSpacing.xl)
            }
            .background(LegendNextCanvas())
            .toolbar(.hidden, for: .navigationBar)
        }
        .tint(LegendNextColor.gold)
        .legendNextSheetChrome(detents: [.large])
        .task { await reload() }
        .onChange(of: search) { _, _ in
            Task { await reload() }
        }
        .onChange(of: scope) { _, _ in
            selectedAccountKeys.removeAll()
            Task { await reload() }
        }
        .sheet(isPresented: $isPresentingBatchConfirmation) {
            LegendFounderAccountBatchConfirmation(
                accounts: selectedAccounts,
                operation: scope == .active ? .archive : .erase,
                messages: messages,
                completed: {
                    selectedAccountKeys.removeAll()
                    isPresentingBatchConfirmation = false
                    Task { await reload() }
                })
        }
    }

    @ViewBuilder
    private var content: some View {
        switch accounts {
        case .idle, .loading:
            ProgressView("Loading accounts")
                .frame(maxWidth: .infinity, minHeight: 144)

        case .unavailable(let failure):
            LegendNextErrorState(
                title: failure.title,
                message: failure.message,
                retryTitle: "Retry",
                retry: { Task { await reload() } })

        case .loaded(let directory):
            if directory.isEmpty {
                LegendNextEmptyState(
                    title: "No accounts found",
                    message: "Search by name, email address, or account ID.",
                    systemImage: "person.crop.circle.badge.questionmark")
            } else {
                LazyVStack(spacing: LegendNextSpacing.xs) {
                    ForEach(directory) { account in
                        Button {
                            toggle(account)
                        } label: {
                            accountRow(account, isSelected: isSelected(account))
                        }
                        .buttonStyle(.plain)
                    }
                }
            }
        }
    }

    private func accountRow(_ account: FounderManagedAccount, isSelected: Bool) -> some View {
        HStack(spacing: LegendNextSpacing.sm) {
            Image(systemName: account.participantType == .agent ? "briefcase.fill" : "person.fill")
                .font(.title3.weight(.semibold))
                .foregroundStyle(LegendNextColor.gold)
                .frame(width: 38, height: 38)
                .background(LegendNextColor.gold.opacity(0.12), in: Circle())

            VStack(alignment: .leading, spacing: 3) {
                Text(account.displayName)
                    .font(.subheadline.weight(.bold))
                    .foregroundStyle(LegendNextColor.textPrimary)
                    .lineLimit(1)
                Text(account.email ?? account.participantType.rawValue)
                    .font(LegendNextTypography.caption)
                    .foregroundStyle(LegendNextColor.textSecondary)
                    .lineLimit(1)
                Text(scope == .archive ? "Archived" : "Account active")
                    .font(.caption.weight(.semibold))
                    .foregroundStyle(scope == .archive ? LegendNextColor.textSecondary : LegendNextColor.gold)
            }

            Spacer(minLength: 0)

            Image(systemName: isSelected ? "checkmark.circle.fill" : "circle")
                .font(.subheadline.weight(.bold))
                .foregroundStyle(isSelected ? LegendNextColor.gold : LegendNextColor.textSecondary)
        }
        .padding(LegendNextSpacing.sm)
        .background(LegendNextColor.surface, in: RoundedRectangle(
            cornerRadius: LegendNextRadius.compact,
            style: .continuous))
        .overlay {
            RoundedRectangle(cornerRadius: LegendNextRadius.compact, style: .continuous)
                .strokeBorder(isSelected ? LegendNextColor.gold : LegendNextColor.gold.opacity(0.30), lineWidth: isSelected ? 2 : 1)
        }
    }

    private var selectedAccounts: [FounderManagedAccount] {
        guard case .loaded(let directory) = accounts else { return [] }
        return directory.filter { selectedAccountKeys.contains(FounderAccountSelectionKey(account: $0)) }
    }

    private func isSelected(_ account: FounderManagedAccount) -> Bool {
        selectedAccountKeys.contains(FounderAccountSelectionKey(account: account))
    }

    private func toggle(_ account: FounderManagedAccount) {
        let key = FounderAccountSelectionKey(account: account)
        if selectedAccountKeys.contains(key) {
            selectedAccountKeys.remove(key)
        } else {
            selectedAccountKeys.insert(key)
        }
    }

    private func reload() async {
        accounts = .loading
        guard let directory = await messages.founderAccounts(
            search: search.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty ? nil : search,
            scope: scope) else {
            accounts = .unavailable(UserFacingFailure(
                title: "Account directory unavailable",
                message: "Legend could not load the Founder account directory. Try again.",
                correlationID: nil))
            return
        }
        accounts = .loaded(directory)
    }
}

private struct FounderAccountSelectionKey: Hashable {
    let profileID: UUID
    let participantType: ParticipantType

    init(account: FounderManagedAccount) {
        profileID = account.profileID
        participantType = account.participantType
    }
}

private enum LegendFounderAccountBatchOperation: Equatable {
    case archive
    case erase

    var confirmationPhrase: String { self == .archive ? "DELETE" : "ERASE" }
    var title: String { self == .archive ? "Remove selected accounts" : "Erase archived accounts" }
    var buttonTitle: String { self == .archive ? "Remove selected" : "Erase permanently" }
    var detail: String {
        self == .archive
            ? "Active subscriptions are cancelled before each account enters the Archive."
            : "This permanently erases the selected archived account profiles and account-owned application data."
    }
}

private struct LegendFounderAccountBatchConfirmation: View {
    let accounts: [FounderManagedAccount]
    let operation: LegendFounderAccountBatchOperation
    @ObservedObject var messages: MessagingStore
    let completed: () -> Void
    @Environment(\.dismiss) private var dismiss
    @State private var confirmation = ""
    @State private var outcome: FounderAccountBatchOutcome?
    @State private var failure: UserFacingFailure?

    private var isConfirmed: Bool {
        confirmation.trimmingCharacters(in: .whitespacesAndNewlines) == operation.confirmationPhrase
    }

    var body: some View {
        NavigationStack {
            LegendScrollView(tracksNavigationChrome: false) {
                VStack(alignment: .leading, spacing: LegendNextSpacing.md) {
                    LegendNextSheetHeader(
                        eyebrow: "Founder action",
                        title: operation.title,
                        detail: operation.detail,
                        dismiss: { dismiss() })

                    LegendNextSurface(style: .navy) {
                        VStack(alignment: .leading, spacing: LegendNextSpacing.xs) {
                            Text("\(accounts.count) account\(accounts.count == 1 ? "" : "s") selected")
                                .font(.subheadline.weight(.bold))
                                .foregroundStyle(LegendNextColor.gold)
                            Text(accounts.map(\.displayName).joined(separator: " · "))
                                .font(LegendNextTypography.caption)
                                .foregroundStyle(.white.opacity(0.78))
                                .lineLimit(3)
                        }
                    }

                    TextField("Type \(operation.confirmationPhrase)", text: $confirmation)
                        .textInputAutocapitalization(.characters)
                        .autocorrectionDisabled()
                        .padding(.horizontal, LegendNextSpacing.sm)
                        .frame(minHeight: 48)
                        .background(LegendNextColor.brandBlueSurface, in: RoundedRectangle(
                            cornerRadius: LegendNextRadius.control,
                            style: .continuous))

                    if let outcome {
                        LegendNextSurface(style: .navy) {
                            Text("\(outcome.completedCount) completed · \(outcome.failedCount) not completed")
                                .font(LegendNextTypography.caption)
                                .foregroundStyle(.white.opacity(0.80))
                        }
                    }

                    if let failure {
                        LegendNextErrorState(
                            title: failure.title,
                            message: failure.message,
                            retryTitle: "Try again",
                            retry: { Task { await perform() } })
                    }

                    Button(role: .destructive) {
                        Task { await perform() }
                    } label: {
                        if messages.isRemovingFounderAccount {
                            ProgressView()
                                .tint(.white)
                                .frame(maxWidth: .infinity)
                        } else {
                            Text(operation.buttonTitle)
                                .frame(maxWidth: .infinity)
                        }
                    }
                    .buttonStyle(LegendNextButtonStyle(kind: .secondary))
                    .disabled(!isConfirmed || messages.isRemovingFounderAccount)
                }
                .padding(LegendNextSpacing.sm)
                .padding(.bottom, LegendNextSpacing.xl)
            }
            .background(LegendNextCanvas())
            .toolbar(.hidden, for: .navigationBar)
        }
        .tint(LegendNextColor.gold)
        .legendNextSheetChrome(detents: [.medium, .large])
    }

    private func perform() async {
        failure = nil
        outcome = nil
        let result = operation == .archive
            ? await messages.removeFounderAccounts(accounts, confirmation: confirmation)
            : await messages.purgeFounderAccounts(accounts, confirmation: confirmation)
        guard let result else {
            failure = messages.sendFailure ?? UserFacingFailure(
                title: "Founder action not completed",
                message: "Legend could not complete this Founder action. Try again.",
                correlationID: nil)
            return
        }

        outcome = result
        if result.failedCount == 0 {
            completed()
            dismiss()
        }
    }
}

private struct LegendFounderAccountRemovalAction: View {
    let account: FounderManagedAccount
    @ObservedObject var messages: MessagingStore
    @State private var isPresentingConfirmation = false

    var body: some View {
        Button {
            isPresentingConfirmation = true
        } label: {
            LegendFounderAccountRemovalRow()
        }
        .buttonStyle(.plain)
        .sheet(isPresented: $isPresentingConfirmation) {
            LegendFounderAccountRemovalConfirmation(
                account: account,
                messages: messages,
                completed: { isPresentingConfirmation = false })
        }
    }
}

private struct LegendFounderAccountRemovalRow: View {
    var body: some View {
        HStack(spacing: LegendNextSpacing.sm) {
            Image(systemName: "trash.fill")
                .font(.title3.weight(.semibold))
                .foregroundStyle(.red)
                .frame(width: 38, height: 38)
                .background(.red.opacity(0.10), in: Circle())

            VStack(alignment: .leading, spacing: 3) {
                Text("Account archive")
                    .font(LegendNextTypography.section)
                    .foregroundStyle(LegendNextColor.textPrimary)
                Text("Cancel access now, then permanently erase it from the Founder Archive when appropriate.")
                    .font(LegendNextTypography.caption)
                    .foregroundStyle(LegendNextColor.textSecondary)
                    .fixedSize(horizontal: false, vertical: true)
            }

            Spacer(minLength: 0)

            Image(systemName: "chevron.right")
                .font(.caption.weight(.bold))
                .foregroundStyle(.red)
        }
        .padding(LegendNextSpacing.sm)
        .background(LegendNextColor.surface, in: RoundedRectangle(
            cornerRadius: LegendNextRadius.compact,
            style: .continuous))
        .overlay {
            RoundedRectangle(cornerRadius: LegendNextRadius.compact, style: .continuous)
                .strokeBorder(.red.opacity(0.45), lineWidth: 1)
        }
    }
}

private struct LegendFounderAccountRemovalConfirmation: View {
    let account: FounderManagedAccount
    @ObservedObject var messages: MessagingStore
    let completed: () -> Void
    @Environment(\.dismiss) private var dismiss
    @State private var confirmation = ""
    @State private var outcome: FounderAccountRemovalOutcome?
    @State private var failure: UserFacingFailure?

    private var isConfirmed: Bool {
        confirmation.trimmingCharacters(in: .whitespacesAndNewlines) == "DELETE"
    }

    var body: some View {
        NavigationStack {
            LegendScrollView(tracksNavigationChrome: false) {
                VStack(alignment: .leading, spacing: LegendNextSpacing.md) {
                    LegendNextSheetHeader(
                        eyebrow: "Founder action",
                        title: "Archive \(account.displayName)",
                        detail: "Any active subscription is cancelled before this account is archived.",
                        dismiss: { dismiss() })

                    LegendNextSurface(style: .navy) {
                        VStack(alignment: .leading, spacing: LegendNextSpacing.xs) {
                            Label("Protected account archive", systemImage: "archivebox.fill")
                                .font(.subheadline.weight(.bold))
                                .foregroundStyle(LegendNextColor.gold)
                            Text("This closes sign-in access and removes the account's Legend content. The Archive provides the separate permanent erase action. Type DELETE to continue.")
                                .font(LegendNextTypography.caption)
                                .foregroundStyle(.white.opacity(0.78))
                        }
                    }

                    TextField("Type DELETE", text: $confirmation)
                        .textInputAutocapitalization(.characters)
                        .autocorrectionDisabled()
                        .padding(.horizontal, LegendNextSpacing.sm)
                        .frame(minHeight: 48)
                        .background(LegendNextColor.brandBlueSurface, in: RoundedRectangle(
                            cornerRadius: LegendNextRadius.control,
                            style: .continuous))

                    if let outcome {
                        LegendNextSurface(style: .navy) {
                            Text(outcome.message)
                                .font(LegendNextTypography.caption)
                                .foregroundStyle(.white.opacity(0.80))
                        }
                    }

                    if let failure {
                        LegendNextErrorState(
                            title: failure.title,
                            message: failure.message,
                            retryTitle: "Try again",
                            retry: { Task { await remove() } })
                    }

                    Button(role: .destructive) {
                        Task { await remove() }
                    } label: {
                        if messages.isRemovingFounderAccount {
                            ProgressView()
                                .tint(.white)
                                .frame(maxWidth: .infinity)
                        } else {
                            Text("Archive account")
                                .frame(maxWidth: .infinity)
                        }
                    }
                    .buttonStyle(LegendNextButtonStyle(kind: .secondary))
                    .disabled(!isConfirmed || messages.isRemovingFounderAccount)
                }
                .padding(LegendNextSpacing.sm)
                .padding(.bottom, LegendNextSpacing.xl)
            }
            .background(LegendNextCanvas())
            .toolbar(.hidden, for: .navigationBar)
        }
        .tint(LegendNextColor.gold)
        .legendNextSheetChrome(detents: [.medium, .large])
    }

    private func remove() async {
        failure = nil
        outcome = nil
        guard let result = await messages.removeFounderAccount(account, confirmation: confirmation) else {
            failure = messages.sendFailure ?? UserFacingFailure(
                title: "Account removal not completed",
                message: "Legend could not complete this account removal. Try again.",
                correlationID: nil)
            return
        }

        outcome = result
        if result.completed {
            completed()
            dismiss()
        }
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

    @Environment(\.dismiss) private var dismiss
    @Environment(\.legendMessagingStore) private var messaging
    @EnvironmentObject private var session: MobileSessionCoordinator
    @ObservedObject private var social: MobileSocialStore
    @State private var metricsState: MobileDataLoadState<MobileSocialProfileMetrics> = .idle
    @State private var postsState: MobileDataLoadState<[MobileSocialPost]> = .idle
    @State private var isFollowing: Bool
    @State private var isFollowRequestPending: Bool
    @State private var isUpdatingFollow = false
    @State private var isConnectionActive: Bool
    @State private var isRemovingConnection = false
    @State private var isSafetyActionInProgress = false
    @State private var isReportOptionsPresented = false
    @State private var isBlockConfirmationPresented = false
    @State private var verificationReview: VerificationReview?
    @State private var isPresentingFounderManagement = false
    private let journeyConnectionID: UUID?
    private let disconnectConnection: ((UUID) async -> Bool)?
    private let journeyClientProfileID: UUID?
    private let blockJourneyProfile: ((UUID) async -> Bool)?
    private let reportJourneyProfile: ((UUID, String) async -> Bool)?
    private let performVerificationResolution: ((VerificationReview, Bool, String?) async -> Bool)?

    init(
        profile: MobileSocialAuthor,
        currentIdentity: LogicalParticipantIdentity,
        social: MobileSocialStore,
        isFollowing: Bool,
        isFollowRequestPending: Bool = false,
        journeyConnectionID: UUID? = nil,
        disconnectConnection: ((UUID) async -> Bool)? = nil,
        journeyClientProfileID: UUID? = nil,
        blockJourneyProfile: ((UUID) async -> Bool)? = nil,
        reportJourneyProfile: ((UUID, String) async -> Bool)? = nil,
        verificationReview: VerificationReview? = nil,
        resolveVerification: ((VerificationReview, Bool, String?) async -> Bool)? = nil
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
        self.journeyClientProfileID = journeyClientProfileID
        self.blockJourneyProfile = blockJourneyProfile
        self.reportJourneyProfile = reportJourneyProfile
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

                journeySafetySection

                founderManagementEntry

                aboutSection
                updatesSection
            }
            .padding(LegendNextSpacing.md)
            .frame(maxWidth: .infinity, alignment: .leading)
        }
        .background(LegendNextCanvas())
        .navigationTitle(displayedProfile.displayName)
        .navigationBarTitleDisplayMode(.inline)
        .confirmationDialog(
            "Report \(displayedProfile.displayName)",
            isPresented: $isReportOptionsPresented,
            titleVisibility: .visible
        ) {
            Button("Harassment or hate") { submitProfileReport(category: "HarassmentOrHate") }
            Button("Threat or self-harm") { submitProfileReport(category: "ThreatOrSelfHarm") }
            Button("Sexual content") { submitProfileReport(category: "SexualContent") }
            Button("Spam or scam") { submitProfileReport(category: "SpamOrScam") }
            Button("Cancel", role: .cancel) {}
        } message: {
            Text("Choose the reason that best describes the concern.")
        }
        .confirmationDialog(
            "Block \(displayedProfile.displayName)?",
            isPresented: $isBlockConfirmationPresented,
            titleVisibility: .visible
        ) {
            Button("Block profile", role: .destructive) {
                Task { await blockProfile() }
            }
            Button("Cancel", role: .cancel) {}
        } message: {
            Text("This removes the Journey Circles connection and prevents client-to-client messaging with this profile.")
        }
        .sheet(isPresented: $isPresentingFounderManagement) {
            if let messaging {
                LegendFounderManagementModel(
                    messages: messaging,
                    member: displayedProfile,
                    verificationReview: $verificationReview,
                    resolveVerification: { review, approved, note in
                        guard let performVerificationResolution else {
                            return false
                        }
                        guard await performVerificationResolution(
                            review,
                            approved,
                            note
                        ) else {
                            return false
                        }
                        await refresh()
                        return true
                    })
            }
        }
        .onAppear {
            Task { await refresh() }
        }
        .refreshable {
            await refresh()
        }
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

    private var supportsJourneySafetyActions: Bool {
        currentIdentity.participantType == .client &&
            profile.identity.participantType == .client &&
            profile.identity != currentIdentity &&
            journeyClientProfileID != nil &&
            blockJourneyProfile != nil &&
            reportJourneyProfile != nil
    }

    private var canManageFounderControls: Bool {
        guard case .authenticated(let currentSession) = session.state else {
            return false
        }
        return currentSession.capabilities.contains("founder") &&
            profile.identity != currentIdentity
    }

    @ViewBuilder
    private var founderManagementEntry: some View {
        if canManageFounderControls, messaging != nil {
            Button {
                isPresentingFounderManagement = true
            } label: {
                Label("Founder management", systemImage: "crown.fill")
                    .frame(maxWidth: .infinity)
            }
            .buttonStyle(LegendNextButtonStyle(kind: .secondary, isFullWidth: true))
            .accessibilityHint("Manage \(displayedProfile.displayName)'s Founder-controlled access")
        }
    }

    @ViewBuilder
    private var journeySafetySection: some View {
        if supportsJourneySafetyActions {
            HStack(spacing: LegendNextSpacing.sm) {
                Button {
                    isReportOptionsPresented = true
                } label: {
                    Text("Report profile")
                        .frame(maxWidth: .infinity)
                }
                .buttonStyle(LegendNextButtonStyle(kind: .secondary, controlHeight: 40))
                .disabled(isSafetyActionInProgress)

                Button(role: .destructive) {
                    isBlockConfirmationPresented = true
                } label: {
                    if isSafetyActionInProgress {
                        ProgressView()
                            .frame(maxWidth: .infinity)
                    } else {
                        Text("Block profile")
                            .frame(maxWidth: .infinity)
                    }
                }
                .buttonStyle(LegendNextButtonStyle(kind: .destructive, controlHeight: 40))
                .disabled(isSafetyActionInProgress)
            }
        }
    }

    private func submitProfileReport(category: String) {
        Task { await reportProfile(category: category) }
    }

    private func reportProfile(category: String) async {
        guard let journeyClientProfileID, let reportJourneyProfile else { return }
        isSafetyActionInProgress = true
        defer { isSafetyActionInProgress = false }
        _ = await reportJourneyProfile(journeyClientProfileID, category)
    }

    private func blockProfile() async {
        guard let journeyClientProfileID, let blockJourneyProfile else { return }
        isSafetyActionInProgress = true
        defer { isSafetyActionInProgress = false }
        if await blockJourneyProfile(journeyClientProfileID) {
            dismiss()
        }
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
                    font: .title2.weight(.bold),
                    badgePlacement: .alongsideProfileImage
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
                            profilePosts: posts,
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
    let profilePosts: [MobileSocialPost]
    let currentIdentity: LogicalParticipantIdentity
    @ObservedObject var social: MobileSocialStore

    var body: some View {
        NavigationLink {
            if post.isVideoHac {
                LegendHacViewportFeed(
                    posts: profilePosts.filter(\.isVideoHac),
                    currentIdentity: currentIdentity,
                    social: social,
                    initialPostID: post.id,
                    presentsDismissControl: true)
            } else {
                LegendPostDetailView(
                    post: post,
                    currentIdentity: currentIdentity,
                    social: social,
                    profilePosts: profilePosts)
            }
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
                } else if let video = post.media.first(where: \.isVideo) {
                    LegendSocialVideoPoster(
                        media: video,
                        social: social,
                        contentMode: .fill,
                        usesRoundedCorners: false)
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
    let synchronizeProfile: @MainActor () async -> Void
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

    init(
        profile: MobileAccountProfile,
        store: MobileAccountStore,
        synchronizeProfile: @escaping @MainActor () async -> Void
    ) {
        self.profile = profile
        _store = ObservedObject(wrappedValue: store)
        self.synchronizeProfile = synchronizeProfile
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
                await synchronizeProfile()
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

struct LegendProfileSettingsSection<Content: View>: View {
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

/// The agent profile portal is a noncommercial profile-management handoff.
/// Client subscription and payment management deliberately have no native
/// link, price, or call-to-action surface.
private struct LegendAgentProfileSettingsRow: View {
    private var destination: URL? {
        MobileConfiguration.current.agentOnlineAccountURL
    }

    var body: some View {
        if let destination {
            Link(destination: destination) {
                LegendProfileSettingsRow(
                    title: "Manage agent profile",
                    detail: "Open your secure Legend profile in the browser",
                    systemImage: "safari",
                    showsChevron: true)
            }
            .buttonStyle(.plain)
            .accessibilityHint("Opens your Legend web account outside the app")
        } else {
            LegendProfileSettingsRow(
                title: "Agent profile unavailable",
                detail: "This build is missing its secure profile address",
                systemImage: "exclamationmark.triangle",
                showsChevron: false)
        }
    }
}

struct LegendProfileSettingsRow: View {
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

struct LegendProfileSettingsDivider: View {
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

struct LegendAvatarImageContent<Placeholder: View>: View {
    @EnvironmentObject private var session: MobileSessionCoordinator

    let avatar: ProfileAvatar?
    private let placeholder: Placeholder

    @State private var remoteData: Data?
    @State private var remoteResourcePath: String?

    init(
        avatar: ProfileAvatar?,
        @ViewBuilder placeholder: () -> Placeholder
    ) {
        self.avatar = avatar
        self.placeholder = placeholder()
    }

    var body: some View {
        Group {
            if let data = avatar?.imageData ?? currentRemoteData ?? cachedRemoteData,
               let image = UIImage(data: data) {
                Image(uiImage: image)
                    .resizable()
                    .scaledToFill()
            } else if avatar?.resourcePath != nil {
                // A resource path is authoritative proof that a profile image
                // exists. Never replace it with initials while the protected
                // cache/network path resolves; initials remain the true
                // no-profile-image fallback only.
                LegendSkeletonShape(cornerRadius: 999)
            } else {
                placeholder
            }
        }
        .task(id: avatar?.resourcePath) {
            guard avatar?.imageData == nil,
                  let resourcePath = avatar?.resourcePath else {
                remoteData = nil
                remoteResourcePath = nil
                return
            }

            if let cached = session.cachedProtectedImageData(resourcePath: resourcePath) {
                remoteData = cached
                remoteResourcePath = resourcePath
            } else if remoteResourcePath != resourcePath {
                remoteData = nil
                remoteResourcePath = nil
            }

            let fetched = await session.protectedImageData(
                resourcePath: resourcePath)
            guard !Task.isCancelled else { return }

            remoteData = fetched
            remoteResourcePath = fetched == nil ? nil : resourcePath
        }
    }

    private var currentRemoteData: Data? {
        guard remoteResourcePath == avatar?.resourcePath else { return nil }
        return remoteData
    }

    private var cachedRemoteData: Data? {
        guard avatar?.imageData == nil,
              let resourcePath = avatar?.resourcePath else { return nil }
        return session.cachedProtectedImageData(resourcePath: resourcePath)
    }
}

struct LegendProfileAvatar: View {
    let avatar: ProfileAvatar?
    let displayName: String
    let size: CGFloat

    var body: some View {
        LegendAvatarImageContent(avatar: avatar) {
            Text(initials)
                .font(.caption.weight(.bold))
                .foregroundStyle(.white)
                .background(LegendNextColor.navy)
        }
        .frame(width: size, height: size)
        .clipShape(Circle())
        .overlay {
            Circle()
                .stroke(
                    LegendNextColor.gold.opacity(0.7),
                    lineWidth: 1)
        }
        .accessibilityLabel(
            "Profile image for \(displayName)")
    }

    private var initials: String {
        let value = displayName
            .split(separator: " ")
            .prefix(2)
            .compactMap(\.first)
            .map(String.init)
            .joined()
            .uppercased()

        return value.isEmpty ? "L" : value
    }
}
