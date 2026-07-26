import SwiftUI
import UIKit

private enum LegendAppTab: String, CaseIterable, Identifiable {
    case home
    case circles
    case create
    case messages
    case you

    var id: Self { self }

    var title: String {
        switch self {
        case .home:
            return "Home"
        case .circles:
            return "Circles"
        case .create:
            return "Create"
        case .messages:
            return "Messages"
        case .you:
            return "You"
        }
    }

    var symbolName: String {
        switch self {
        case .home:
            return "house"
        case .circles:
            return "circle.grid.2x2"
        case .create:
            return "plus"
        case .messages:
            return "message"
        case .you:
            return "person"
        }
    }

    var selectedSymbolName: String {
        switch self {
        case .home:
            return "house.fill"
        case .circles:
            return "circle.grid.2x2.fill"
        case .create:
            return "plus"
        case .messages:
            return "message.fill"
        case .you:
            return "person.fill"
        }
    }
}

struct LegendApplicationShell: View {
    let currentSession: MobileSession

    @ObservedObject private var coordinator: MobileSessionCoordinator
    @State private var selectedTab: LegendAppTab = .home

    init(
        currentSession: MobileSession,
        coordinator: MobileSessionCoordinator
    ) {
        self.currentSession = currentSession
        _coordinator = ObservedObject(wrappedValue: coordinator)
    }

    var body: some View {
        TabView(selection: $selectedTab) {
            NavigationStack {
                LegendHomeView(
                    currentSession: currentSession,
                    selectedTab: $selectedTab
                )
            }
            .tag(LegendAppTab.home)

            NavigationStack {
                LegendCirclesView()
            }
            .tag(LegendAppTab.circles)

            NavigationStack {
                LegendCreateView()
            }
            .tag(LegendAppTab.create)

            LegendMessagesTab(coordinator: coordinator)
                .tag(LegendAppTab.messages)

            NavigationStack {
                LegendYouView(
                    currentSession: currentSession,
                    coordinator: coordinator
                )
            }
            .tag(LegendAppTab.you)
        }
        .toolbar(.hidden, for: .tabBar)
        .background(LegendPalette.canvas.ignoresSafeArea())
        .safeAreaInset(edge: .bottom, spacing: 0) {
            LegendTabBar(selection: $selectedTab)
        }
        .tint(LegendPalette.gold)
    }
}

private struct LegendTabBar: View {
    @Binding var selection: LegendAppTab

    var body: some View {
        HStack(alignment: .bottom, spacing: 0) {
            tabButton(.home)
            tabButton(.circles)
            createButton
            tabButton(.messages)
            tabButton(.you)
        }
        .padding(.horizontal, LegendSpacing.sm)
        .padding(.top, LegendSpacing.xs)
        .padding(.bottom, LegendSpacing.xs)
        .background(.ultraThinMaterial)
        .overlay(alignment: .top) {
            Rectangle()
                .fill(LegendPalette.separator.opacity(0.45))
                .frame(height: 0.5)
        }
        .accessibilityElement(children: .contain)
    }

    private func tabButton(_ tab: LegendAppTab) -> some View {
        Button {
            select(tab)
        } label: {
            VStack(spacing: LegendSpacing.xxs) {
                Image(
                    systemName:
                        selection == tab
                        ? tab.selectedSymbolName
                        : tab.symbolName
                )
                .font(.system(size: 21, weight: .semibold))

                Text(tab.title)
                    .font(.caption2.weight(.semibold))
                    .lineLimit(1)
            }
            .foregroundStyle(
                selection == tab
                ? LegendPalette.primaryNavy
                : LegendPalette.secondaryLabel
            )
            .frame(maxWidth: .infinity)
            .frame(minHeight: 48)
            .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .accessibilityLabel(tab.title)
        .accessibilityAddTraits(
            selection == tab ? .isSelected : []
        )
    }

    private var createButton: some View {
        Button {
            select(.create)
        } label: {
            VStack(spacing: LegendSpacing.xxs) {
                ZStack {
                    Circle()
                        .fill(LegendPalette.primaryNavy)
                        .frame(width: 48, height: 48)

                    Circle()
                        .stroke(
                            LegendPalette.gold.opacity(0.75),
                            lineWidth: 1
                        )
                        .frame(width: 48, height: 48)

                    Image(systemName: "plus")
                        .font(.system(size: 21, weight: .bold))
                        .foregroundStyle(.white)
                }

                Text(LegendAppTab.create.title)
                    .font(.caption2.weight(.semibold))
                    .foregroundStyle(
                        selection == .create
                        ? LegendPalette.primaryNavy
                        : LegendPalette.secondaryLabel
                    )
            }
            .frame(maxWidth: .infinity)
            .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .accessibilityLabel("Create")
        .accessibilityHint("Opens Legend creation actions.")
        .accessibilityAddTraits(
            selection == .create ? .isSelected : []
        )
    }

    private func select(_ tab: LegendAppTab) {
        guard selection != tab else {
            return
        }

        UISelectionFeedbackGenerator().selectionChanged()

        withAnimation(.easeOut(duration: 0.18)) {
            selection = tab
        }
    }
}

private struct LegendMessagesTab: View {
    @StateObject private var messages: MessagingStore

    init(coordinator: MobileSessionCoordinator) {
        _messages = StateObject(
            wrappedValue: coordinator.makeMessagingStore()
        )
    }

    var body: some View {
        NavigationStack {
            MessagingHomeView(store: messages)
        }
    }
}

private struct LegendHomeView: View {
    let currentSession: MobileSession
    @Binding var selectedTab: LegendAppTab

    var body: some View {
        ScrollView {
            LazyVStack(
                alignment: .leading,
                spacing: LegendSpacing.lg
            ) {
                homeHeader

                journeyCard

                LegendSectionHeader(
                    "Continue building",
                    detail: "Your connected Legend journey"
                )

                actionGrid

                LegendSectionHeader(
                    "Your relationships",
                    detail: "Stay connected to the people walking with you"
                )

                relationshipCard

                LegendSectionHeader(
                    "Your financial picture",
                    detail: "One connected view of what matters"
                )

                financialFoundationCard
            }
            .padding(.horizontal, LegendSpacing.md)
            .padding(.top, LegendSpacing.sm)
            .padding(.bottom, LegendSpacing.lg)
        }
        .background(Color.clear)
        .navigationBarHidden(true)
    }

    private var homeHeader: some View {
        HStack(spacing: LegendSpacing.sm) {
            VStack(alignment: .leading, spacing: LegendSpacing.xxs) {
                Text(greeting)
                    .font(LegendTypography.metadata)
                    .foregroundStyle(LegendPalette.secondaryLabel)

                Text(currentSession.actor.displayName)
                    .font(LegendTypography.hero)
                    .foregroundStyle(LegendPalette.label)
                    .lineLimit(1)
            }

            Spacer(minLength: LegendSpacing.sm)

            Button {
                selectedTab = .messages
            } label: {
                Image(systemName: "message.fill")
                    .font(.system(size: 17, weight: .semibold))
                    .foregroundStyle(LegendPalette.primaryNavy)
                    .frame(width: 42, height: 42)
                    .background(
                        LegendPalette.elevatedSurface,
                        in: Circle()
                    )
                    .overlay {
                        Circle()
                            .stroke(
                                LegendPalette.separator.opacity(0.35),
                                lineWidth: 1
                            )
                    }
            }
            .buttonStyle(.plain)
            .accessibilityLabel("Open messages")

            Button {
                selectedTab = .you
            } label: {
                Text(initials)
                    .font(.caption.weight(.bold))
                    .foregroundStyle(.white)
                    .frame(width: 42, height: 42)
                    .background(
                        LegendPalette.primaryNavy,
                        in: Circle()
                    )
                    .overlay {
                        Circle()
                            .stroke(
                                LegendPalette.gold.opacity(0.75),
                                lineWidth: 1
                            )
                    }
            }
            .buttonStyle(.plain)
            .accessibilityLabel("Open your profile")
        }
        .padding(.top, LegendSpacing.xs)
    }

    private var journeyCard: some View {
        LegendCard(style: .navy) {
            VStack(alignment: .leading, spacing: LegendSpacing.md) {
                HStack {
                    Text("YOUR JOURNEY")
                        .font(.caption.weight(.bold))
                        .foregroundStyle(LegendPalette.gold)

                    Spacer()

                    Image(systemName: "sparkles")
                        .font(.headline)
                        .foregroundStyle(LegendPalette.gold)
                        .accessibilityHidden(true)
                }

                Text("Build with clarity. Protect what matters. Keep moving forward.")
                    .font(LegendTypography.hero)
                    .foregroundStyle(.white)
                    .fixedSize(horizontal: false, vertical: true)

                Text(
                    "Legend brings your relationships, protection, finances, and next steps into one connected experience."
                )
                .font(LegendTypography.body)
                .foregroundStyle(.white.opacity(0.76))
                .fixedSize(horizontal: false, vertical: true)

                Button {
                    selectedTab = .create
                } label: {
                    Label(
                        "Share your next step",
                        systemImage: "plus.circle.fill"
                    )
                }
                .buttonStyle(LegendButtonStyle(kind: .gold))
            }
        }
    }

    private var actionGrid: some View {
        LazyVGrid(
            columns: [
                GridItem(
                    .flexible(),
                    spacing: LegendSpacing.sm
                ),
                GridItem(
                    .flexible(),
                    spacing: LegendSpacing.sm
                )
            ],
            spacing: LegendSpacing.sm
        ) {
            LegendHomeActionCard(
                title: "Journey circles",
                detail: "Walk with people in a similar season.",
                symbolName: "circle.grid.2x2.fill"
            ) {
                selectedTab = .circles
            }

            LegendHomeActionCard(
                title: "Ask for guidance",
                detail: "Start a secure conversation.",
                symbolName: "message.fill"
            ) {
                selectedTab = .messages
            }

            LegendHomeActionCard(
                title: "Capture a milestone",
                detail: "Record progress worth remembering.",
                symbolName: "flag.checkered"
            ) {
                selectedTab = .create
            }

            LegendHomeActionCard(
                title: "Your profile",
                detail: "See your connected Legend identity.",
                symbolName: "person.crop.circle.fill"
            ) {
                selectedTab = .you
            }
        }
    }

    private var relationshipCard: some View {
        LegendCard {
            HStack(spacing: LegendSpacing.md) {
                Image(systemName: "person.2.fill")
                    .font(.title2)
                    .foregroundStyle(LegendPalette.gold)
                    .frame(width: 46, height: 46)
                    .background(
                        LegendPalette.gold.opacity(0.12),
                        in: Circle()
                    )
                    .accessibilityHidden(true)

                VStack(alignment: .leading, spacing: LegendSpacing.xxs) {
                    Text("Your Legend network")
                        .font(LegendTypography.section)
                        .foregroundStyle(LegendPalette.label)

                    Text(
                        "Your agent partnership, conversations, and journey-circle relationships live together here."
                    )
                    .font(LegendTypography.metadata)
                    .foregroundStyle(LegendPalette.secondaryLabel)
                    .fixedSize(horizontal: false, vertical: true)
                }

                Spacer(minLength: 0)
            }
        }
    }

    private var financialFoundationCard: some View {
        LegendCard {
            VStack(alignment: .leading, spacing: LegendSpacing.sm) {
                HStack {
                    Label(
                        "Financial operating system",
                        systemImage: "chart.line.uptrend.xyaxis"
                    )
                    .font(LegendTypography.section)
                    .foregroundStyle(LegendPalette.label)

                    Spacer()
                }

                Text(
                    "Your protection profile, financial picture, documents, goals, and progress will remain connected to the same Legend relationship."
                )
                .font(LegendTypography.body)
                .foregroundStyle(LegendPalette.secondaryLabel)
                .fixedSize(horizontal: false, vertical: true)

                Button {
                    selectedTab = .you
                } label: {
                    Label(
                        "View your foundation",
                        systemImage: "arrow.right"
                    )
                }
                .buttonStyle(LegendButtonStyle(kind: .secondary))
            }
        }
    }

    private var greeting: String {
        let hour = Calendar.current.component(
            .hour,
            from: Date()
        )

        switch hour {
        case 5..<12:
            return "Good morning"
        case 12..<17:
            return "Good afternoon"
        default:
            return "Good evening"
        }
    }

    private var initials: String {
        currentSession.actor.displayName
            .split(separator: " ")
            .prefix(2)
            .compactMap(\.first)
            .map(String.init)
            .joined()
            .uppercased()
    }
}

private struct LegendHomeActionCard: View {
    let title: String
    let detail: String
    let symbolName: String
    let action: () -> Void

    var body: some View {
        Button(action: action) {
            VStack(alignment: .leading, spacing: LegendSpacing.sm) {
                Image(systemName: symbolName)
                    .font(.title3.weight(.semibold))
                    .foregroundStyle(LegendPalette.gold)
                    .accessibilityHidden(true)

                Text(title)
                    .font(.headline)
                    .foregroundStyle(LegendPalette.label)
                    .multilineTextAlignment(.leading)

                Text(detail)
                    .font(LegendTypography.metadata)
                    .foregroundStyle(LegendPalette.secondaryLabel)
                    .multilineTextAlignment(.leading)
                    .fixedSize(horizontal: false, vertical: true)

                Spacer(minLength: 0)

                Image(systemName: "arrow.up.right")
                    .font(.caption.weight(.bold))
                    .foregroundStyle(LegendPalette.primaryNavy)
                    .frame(maxWidth: .infinity, alignment: .trailing)
            }
            .frame(maxWidth: .infinity, minHeight: 150, alignment: .topLeading)
            .padding(LegendSpacing.md)
            .background(
                LegendPalette.elevatedSurface,
                in: RoundedRectangle(
                    cornerRadius: LegendRadius.card,
                    style: .continuous
                )
            )
            .overlay {
                RoundedRectangle(
                    cornerRadius: LegendRadius.card,
                    style: .continuous
                )
                .stroke(
                    LegendPalette.separator.opacity(0.35),
                    lineWidth: 1
                )
            }
        }
        .buttonStyle(.plain)
        .accessibilityElement(children: .combine)
        .accessibilityAddTraits(.isButton)
    }
}

private struct LegendCirclesView: View {
    var body: some View {
        ScrollView {
            LazyVStack(
                alignment: .leading,
                spacing: LegendSpacing.lg
            ) {
                LegendPageHeader(
                    eyebrow: "COMMUNITY",
                    title: "Journey Circles",
                    detail:
                        "Connect with people walking through similar financial, spiritual, physical, and life seasons."
                )

                LegendSectionHeader(
                    "Your circles",
                    detail: "Community built around the walk of life"
                )

                LegendCircleFoundationCard(
                    title: "Building financial strength",
                    detail:
                        "Debt payoff, emergency savings, income growth, and stronger daily stewardship.",
                    symbolName: "chart.line.uptrend.xyaxis"
                )

                LegendCircleFoundationCard(
                    title: "Family and legacy",
                    detail:
                        "Protection, family continuity, estate preparation, and generational impact.",
                    symbolName: "figure.2.and.child.holdinghands"
                )

                LegendCircleFoundationCard(
                    title: "Faith and purpose",
                    detail:
                        "Encouragement, accountability, and stewardship through every season.",
                    symbolName: "heart.fill"
                )
            }
            .padding(.horizontal, LegendSpacing.md)
            .padding(.top, LegendSpacing.sm)
            .padding(.bottom, LegendSpacing.lg)
        }
        .background(Color.clear)
        .navigationBarHidden(true)
    }
}

private struct LegendCircleFoundationCard: View {
    let title: String
    let detail: String
    let symbolName: String

    var body: some View {
        LegendCard {
            HStack(alignment: .top, spacing: LegendSpacing.md) {
                Image(systemName: symbolName)
                    .font(.title3.weight(.semibold))
                    .foregroundStyle(LegendPalette.gold)
                    .frame(width: 44, height: 44)
                    .background(
                        LegendPalette.gold.opacity(0.12),
                        in: Circle()
                    )
                    .accessibilityHidden(true)

                VStack(alignment: .leading, spacing: LegendSpacing.xs) {
                    Text(title)
                        .font(LegendTypography.section)
                        .foregroundStyle(LegendPalette.label)

                    Text(detail)
                        .font(LegendTypography.body)
                        .foregroundStyle(LegendPalette.secondaryLabel)
                        .fixedSize(horizontal: false, vertical: true)

                    Text("Circle foundation")
                        .font(.caption.weight(.semibold))
                        .foregroundStyle(LegendPalette.gold)
                }

                Spacer(minLength: 0)
            }
        }
    }
}

private struct LegendCreateView: View {
    var body: some View {
        ScrollView {
            VStack(
                alignment: .leading,
                spacing: LegendSpacing.lg
            ) {
                LegendPageHeader(
                    eyebrow: "CREATE",
                    title: "What is happening in your journey?",
                    detail:
                        "Capture a milestone, ask a question, share progress, or add something important to your Legend."
                )

                LegendCreateAction(
                    title: "Share an update",
                    detail: "Post progress to your connected journey.",
                    symbolName: "square.and.pencil"
                )

                LegendCreateAction(
                    title: "Capture a milestone",
                    detail: "Record a moment that moved your life forward.",
                    symbolName: "flag.checkered"
                )

                LegendCreateAction(
                    title: "Ask a question",
                    detail: "Request guidance from your trusted Legend relationship.",
                    symbolName: "questionmark.bubble"
                )

                LegendCreateAction(
                    title: "Add a document",
                    detail: "Prepare an important document for secure storage.",
                    symbolName: "doc.badge.plus"
                )

                LegendCreateAction(
                    title: "Set a goal",
                    detail: "Define the next outcome you are working toward.",
                    symbolName: "target"
                )
            }
            .padding(.horizontal, LegendSpacing.md)
            .padding(.top, LegendSpacing.sm)
            .padding(.bottom, LegendSpacing.lg)
        }
        .background(Color.clear)
        .navigationBarHidden(true)
    }
}

private struct LegendCreateAction: View {
    let title: String
    let detail: String
    let symbolName: String

    var body: some View {
        Button {
            UIImpactFeedbackGenerator(
                style: .light
            ).impactOccurred()
        } label: {
            LegendCard {
                HStack(spacing: LegendSpacing.md) {
                    Image(systemName: symbolName)
                        .font(.title3.weight(.semibold))
                        .foregroundStyle(LegendPalette.gold)
                        .frame(width: 44, height: 44)
                        .background(
                            LegendPalette.gold.opacity(0.12),
                            in: Circle()
                        )
                        .accessibilityHidden(true)

                    VStack(alignment: .leading, spacing: LegendSpacing.xxs) {
                        Text(title)
                            .font(LegendTypography.section)
                            .foregroundStyle(LegendPalette.label)

                        Text(detail)
                            .font(LegendTypography.metadata)
                            .foregroundStyle(LegendPalette.secondaryLabel)
                            .fixedSize(horizontal: false, vertical: true)
                    }

                    Spacer(minLength: LegendSpacing.sm)

                    Image(systemName: "chevron.right")
                        .font(.caption.weight(.bold))
                        .foregroundStyle(LegendPalette.secondaryLabel)
                        .accessibilityHidden(true)
                }
            }
        }
        .buttonStyle(.plain)
        .accessibilityElement(children: .combine)
        .accessibilityAddTraits(.isButton)
    }
}

private struct LegendYouView: View {
    let currentSession: MobileSession

    @ObservedObject private var coordinator: MobileSessionCoordinator

    init(
        currentSession: MobileSession,
        coordinator: MobileSessionCoordinator
    ) {
        self.currentSession = currentSession
        _coordinator = ObservedObject(wrappedValue: coordinator)
    }

    var body: some View {
        ScrollView {
            VStack(
                alignment: .leading,
                spacing: LegendSpacing.lg
            ) {
                profileHeader

                LegendSectionHeader(
                    "Your Legend",
                    detail: "One connected profile across your journey"
                )

                LegendProfileRow(
                    title: "Journey",
                    detail: "Milestones, goals, and progress",
                    symbolName: "map.fill"
                )

                LegendProfileRow(
                    title: "Financial foundation",
                    detail: "Protection, finances, and planning",
                    symbolName: "chart.pie.fill"
                )

                LegendProfileRow(
                    title: "Documents",
                    detail: "Your secure document foundation",
                    symbolName: "folder.fill"
                )

                LegendProfileRow(
                    title: "Settings",
                    detail: "Security and mobile preferences",
                    symbolName: "gearshape.fill"
                )

                Button("Sign out") {
                    coordinator.signOut()
                }
                .buttonStyle(
                    LegendButtonStyle(kind: .destructive)
                )
            }
            .padding(.horizontal, LegendSpacing.md)
            .padding(.top, LegendSpacing.sm)
            .padding(.bottom, LegendSpacing.lg)
        }
        .background(Color.clear)
        .navigationBarHidden(true)
    }

    private var profileHeader: some View {
        LegendCard(style: .navy) {
            VStack(
                alignment: .leading,
                spacing: LegendSpacing.md
            ) {
                HStack(spacing: LegendSpacing.md) {
                    Text(initials)
                        .font(.title2.weight(.bold))
                        .foregroundStyle(.white)
                        .frame(width: 64, height: 64)
                        .background(
                            LegendPalette.secondaryNavy,
                            in: Circle()
                        )
                        .overlay {
                            Circle()
                                .stroke(
                                    LegendPalette.gold,
                                    lineWidth: 1.5
                                )
                        }

                    VStack(alignment: .leading, spacing: LegendSpacing.xxs) {
                        Text(currentSession.actor.displayName)
                            .font(LegendTypography.hero)
                            .foregroundStyle(.white)
                            .lineLimit(2)

                        Text(
                            currentSession.actor.identity
                                .participantType.rawValue
                                .capitalized
                        )
                        .font(.subheadline.weight(.semibold))
                        .foregroundStyle(LegendPalette.gold)
                    }
                }

                Text(
                    "Your verified Legend identity connects your relationships, financial foundation, documents, and journey."
                )
                .font(LegendTypography.body)
                .foregroundStyle(.white.opacity(0.76))
                .fixedSize(horizontal: false, vertical: true)
            }
        }
    }

    private var initials: String {
        currentSession.actor.displayName
            .split(separator: " ")
            .prefix(2)
            .compactMap(\.first)
            .map(String.init)
            .joined()
            .uppercased()
    }
}

private struct LegendProfileRow: View {
    let title: String
    let detail: String
    let symbolName: String

    var body: some View {
        LegendCard {
            HStack(spacing: LegendSpacing.md) {
                Image(systemName: symbolName)
                    .font(.headline)
                    .foregroundStyle(LegendPalette.gold)
                    .frame(width: 40, height: 40)
                    .background(
                        LegendPalette.gold.opacity(0.12),
                        in: Circle()
                    )
                    .accessibilityHidden(true)

                VStack(alignment: .leading, spacing: LegendSpacing.xxs) {
                    Text(title)
                        .font(LegendTypography.section)
                        .foregroundStyle(LegendPalette.label)

                    Text(detail)
                        .font(LegendTypography.metadata)
                        .foregroundStyle(LegendPalette.secondaryLabel)
                }

                Spacer(minLength: LegendSpacing.sm)

                Image(systemName: "chevron.right")
                    .font(.caption.weight(.bold))
                    .foregroundStyle(LegendPalette.secondaryLabel)
                    .accessibilityHidden(true)
            }
        }
        .accessibilityElement(children: .combine)
    }
}

private struct LegendPageHeader: View {
    let eyebrow: String
    let title: String
    let detail: String

    var body: some View {
        VStack(alignment: .leading, spacing: LegendSpacing.xs) {
            Text(eyebrow)
                .font(.caption.weight(.bold))
                .foregroundStyle(LegendPalette.gold)

            Text(title)
                .font(LegendTypography.hero)
                .foregroundStyle(LegendPalette.label)
                .fixedSize(horizontal: false, vertical: true)

            Text(detail)
                .font(LegendTypography.body)
                .foregroundStyle(LegendPalette.secondaryLabel)
                .fixedSize(horizontal: false, vertical: true)
        }
        .padding(.top, LegendSpacing.xs)
    }
}
