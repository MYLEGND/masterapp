import SwiftUI

private enum LegendAppTab: Hashable {
    case home
    case messages
    case clients
    case finance
    case activity
    case settings
}

struct LegendApplicationShell: View {
    let currentSession: MobileSession
    @ObservedObject private var coordinator: MobileSessionCoordinator
    @State private var selectedTab: LegendAppTab = .home

    init(currentSession: MobileSession, coordinator: MobileSessionCoordinator) {
        self.currentSession = currentSession
        _coordinator = ObservedObject(wrappedValue: coordinator)
    }

    var body: some View {
        TabView(selection: $selectedTab) {
            NavigationStack {
                LegendDashboardHomeView(currentSession: currentSession)
            }
            .tabItem { Label("Home", systemImage: "house") }
            .tag(LegendAppTab.home)

            LegendMessagesTab(coordinator: coordinator)
                .tabItem { Label("Messages", systemImage: "message") }
                .tag(LegendAppTab.messages)

            NavigationStack {
                LegendFeatureFoundationView(
                    title: "Clients",
                    detail: "Your secure client workspace will be available here.",
                    symbolName: "person.2")
            }
            .tabItem { Label("Clients", systemImage: "person.2") }
            .tag(LegendAppTab.clients)

            NavigationStack {
                LegendFeatureFoundationView(
                    title: "Finance",
                    detail: "Your financial workspace will be available here.",
                    symbolName: "chart.line.uptrend.xyaxis")
            }
            .tabItem { Label("Finance", systemImage: "chart.line.uptrend.xyaxis") }
            .tag(LegendAppTab.finance)

            NavigationStack {
                LegendFeatureFoundationView(
                    title: "Activity",
                    detail: "Your secure activity timeline will be available here.",
                    symbolName: "clock.arrow.circlepath")
            }
            .tabItem { Label("Activity", systemImage: "clock.arrow.circlepath") }
            .tag(LegendAppTab.activity)

            NavigationStack {
                LegendSettingsView(currentSession: currentSession, coordinator: coordinator)
            }
            .tabItem { Label("Settings", systemImage: "gearshape") }
            .tag(LegendAppTab.settings)
        }
        .tint(LegendPalette.gold)
    }
}

private struct LegendMessagesTab: View {
    @StateObject private var messages: MessagingStore

    init(coordinator: MobileSessionCoordinator) {
        _messages = StateObject(wrappedValue: coordinator.makeMessagingStore())
    }

    var body: some View {
        NavigationStack {
            MessagingHomeView(store: messages)
        }
    }
}

private struct LegendDashboardHomeView: View {
    let currentSession: MobileSession

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: LegendSpacing.lg) {
                LegendNavigationBar(
                    title: "Good day, \(currentSession.actor.displayName)",
                    detail: "Your secure Legend workspace",
                    symbolName: "shield.lefthalf.filled")

                LegendHero(
                    eyebrow: "Today",
                    title: "One clear place for your next steps.",
                    detail: "Your Legend mobile workspace brings protection, finance, and relationships together.",
                    symbolName: "sparkles")

                LegendSectionHeader("Today's Agenda", detail: "Foundation")
                agenda

                LegendSectionHeader("Your workspace")
                LazyVGrid(
                    columns: [GridItem(.flexible(), spacing: LegendSpacing.sm), GridItem(.flexible(), spacing: LegendSpacing.sm)],
                    spacing: LegendSpacing.sm
                ) {
                    LegendMetric(title: "Messages", value: "Secure", detail: "Private conversations")
                    LegendMetric(title: "Clients", value: "Connected", detail: "Relationship workspace")
                    LegendMetric(title: "Financial Snapshot", value: "Ready", detail: "Financial intelligence")
                    LegendMetric(title: "Recent Activity", value: "Current", detail: "Timeline foundation")
                }
                .padding(LegendSpacing.md)
                .background(LegendPalette.elevatedSurface, in: RoundedRectangle(cornerRadius: LegendRadius.card, style: .continuous))

                LegendSectionHeader("Recent Activity")
                LegendCard {
                    HStack(spacing: LegendSpacing.sm) {
                        Image(systemName: "lock.shield")
                            .foregroundStyle(LegendPalette.gold)
                            .accessibilityHidden(true)
                        Text("Your secure mobile workspace is ready when you are.")
                            .font(LegendTypography.body)
                            .foregroundStyle(LegendPalette.secondaryLabel)
                    }
                }
            }
            .padding(.horizontal, LegendSpacing.md)
            .padding(.vertical, LegendSpacing.lg)
        }
        .background(LegendPalette.canvas.ignoresSafeArea())
        .navigationBarTitleDisplayMode(.inline)
        .toolbar {
            ToolbarItem(placement: .principal) {
                Text("Home")
                    .font(.headline.weight(.semibold))
            }
        }
    }

    private var agenda: some View {
        LegendCard {
            VStack(alignment: .leading, spacing: LegendSpacing.sm) {
                LegendStatusBanner(
                    title: "Secure workspace",
                    detail: "Your mobile dashboard foundation is ready for connected information.",
                    tone: .success)
                Text("Dashboard information will appear here as approved mobile capabilities are connected.")
                    .font(LegendTypography.metadata)
                    .foregroundStyle(LegendPalette.secondaryLabel)
                    .fixedSize(horizontal: false, vertical: true)
            }
        }
    }
}

private struct LegendFeatureFoundationView: View {
    let title: String
    let detail: String
    let symbolName: String

    var body: some View {
        LegendEmptyState(title: title, message: detail, symbolName: symbolName)
            .background(LegendPalette.canvas.ignoresSafeArea())
            .navigationTitle(title)
            .navigationBarTitleDisplayMode(.inline)
    }
}

private struct LegendSettingsView: View {
    let currentSession: MobileSession
    @ObservedObject private var coordinator: MobileSessionCoordinator

    init(currentSession: MobileSession, coordinator: MobileSessionCoordinator) {
        self.currentSession = currentSession
        _coordinator = ObservedObject(wrappedValue: coordinator)
    }

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: LegendSpacing.lg) {
                LegendNavigationBar(
                    title: "Settings",
                    detail: "Secure mobile session",
                    symbolName: "gearshape")

                LegendCard {
                    VStack(alignment: .leading, spacing: LegendSpacing.sm) {
                        Text(currentSession.actor.displayName)
                            .font(LegendTypography.section)
                        LegendBadge(
                            title: currentSession.actor.identity.participantType.rawValue.capitalized,
                            tone: .gold)
                        Text("Session identity is verified by Legend's secure mobile authorization.")
                            .font(LegendTypography.metadata)
                            .foregroundStyle(LegendPalette.secondaryLabel)
                            .fixedSize(horizontal: false, vertical: true)
                    }
                }

                Button("Sign out") {
                    coordinator.signOut()
                }
                .buttonStyle(LegendButtonStyle(kind: .destructive))
            }
            .padding(.horizontal, LegendSpacing.md)
            .padding(.vertical, LegendSpacing.lg)
        }
        .background(LegendPalette.canvas.ignoresSafeArea())
        .navigationBarTitleDisplayMode(.inline)
    }
}
