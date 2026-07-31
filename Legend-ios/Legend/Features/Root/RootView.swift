import SwiftUI

struct RootView: View {
    @EnvironmentObject private var session: MobileSessionCoordinator
    @EnvironmentObject private var diagnostics: LegendDiagnostics

    var body: some View {
        Group {
            switch session.state {
            case .loading:
                LegendLaunchSkeleton()
            case .contractUnavailable(let validation):
                ConfigurationStateView(validation: validation)
            case .signedOut:
                SignInView()
            case .authenticating:
                LegendLaunchSkeleton()
            case .roleSelection(let selection):
                RoleSelectionView(selection: selection)
            case .authenticated(let currentSession):
                AuthenticatedHomeView(currentSession: currentSession, coordinator: session)
                    .id(currentSession.actor.identity)
            case .failed(let failure):
                SessionFailureView(failure: failure)
            }
        }
        .task {
            session.restore()
        }
        .onAppear(perform: recordSelectedBranch)
        .onChange(of: session.state.diagnosticName) { _, _ in
            recordSelectedBranch()
        }
    }

    private func recordSelectedBranch() {
        #if DEBUG
        diagnostics.record(
            category: .authentication,
            summary: "Root view branch selected: \(session.state.diagnosticName).")
        #endif
    }
}

private struct RoleSelectionView: View {
    let selection: MobileRoleSelection
    @EnvironmentObject private var session: MobileSessionCoordinator

    var body: some View {
        NavigationStack {
            ScrollView {
                VStack(spacing: LegendSpacing.lg) {
                    LegendBrandLogo(maximumWidth: 150)
                        .accessibilityLabel("Legend")

                    VStack(spacing: LegendSpacing.xs) {
                        Text("Choose your Legend role")
                            .font(LegendTypography.hero)
                            .foregroundStyle(LegendPalette.label)
                            .multilineTextAlignment(.center)

                        Text("Your account includes both Legend experiences.")
                            .font(LegendTypography.body)
                            .foregroundStyle(LegendPalette.secondaryLabel)
                            .multilineTextAlignment(.center)
                    }

                    VStack(spacing: LegendSpacing.sm) {
                        ForEach(selection.permittedParticipantTypes, id: \.self) { role in
                            Button {
                                session.selectRole(role)
                            } label: {
                                HStack(spacing: LegendSpacing.sm) {
                                    Image(
                                        systemName: role == .agent
                                            ? "briefcase.fill"
                                            : "person.fill"
                                    )
                                    .font(.system(size: 19, weight: .semibold))
                                    .foregroundStyle(LegendPalette.gold)
                                    .frame(width: 28)

                                    Text(
                                        role == .agent
                                            ? "Continue as Agent"
                                            : "Continue as Client"
                                    )
                                    .font(LegendTypography.section)
                                    .foregroundStyle(LegendPalette.label)

                                    Spacer()

                                    Image(systemName: "chevron.right")
                                        .font(.system(size: 15, weight: .bold))
                                        .foregroundStyle(LegendPalette.gold)
                                }
                                .padding(.horizontal, LegendSpacing.md)
                                .frame(maxWidth: .infinity)
                                .frame(minHeight: 58)
                                .background(Color.white)
                                .overlay {
                                    RoundedRectangle(cornerRadius: 14)
                                        .stroke(LegendPalette.gold, lineWidth: 1.5)
                                }
                                .clipShape(RoundedRectangle(cornerRadius: 14))
                            }
                            .buttonStyle(.plain)
                        }
                    }

                    Button {
                        session.signOut()
                    } label: {
                        Text("Sign out")
                            .font(LegendTypography.section)
                            .foregroundStyle(Color.white)
                            .frame(maxWidth: .infinity)
                            .frame(minHeight: 54)
                            .background(
                                LinearGradient(
                                    colors: [
                                        Color(red: 0.50, green: 0.10, blue: 0.14),
                                        Color(red: 0.38, green: 0.07, blue: 0.10)
                                    ],
                                    startPoint: .top,
                                    endPoint: .bottom
                                )
                            )
                            .clipShape(RoundedRectangle(cornerRadius: 14))
                    }
                    .buttonStyle(.plain)
                }
                .frame(maxWidth: 520)
                .padding(.horizontal, LegendSpacing.md)
                .padding(.top, LegendSpacing.xl)
                .padding(.bottom, LegendSpacing.lg)
                .frame(maxWidth: .infinity)
            }
            .background(Color.white.ignoresSafeArea())
            .toolbar(.hidden, for: .navigationBar)
        }
    }
}

private struct ConfigurationStateView: View {
    let validation: MobileConfigurationValidation
    @EnvironmentObject private var session: MobileSessionCoordinator

    var body: some View {
        NavigationStack {
            ScrollView {
                VStack(spacing: LegendSpacing.lg) {
                    VStack(spacing: LegendSpacing.xs) {
                        LegendBrandLogo(maximumWidth: 96)
                            .accessibilityHidden(true)
                        Text("Legend")
                            .font(.system(.title2, design: .rounded).weight(.bold))
                            .foregroundStyle(LegendPalette.label)
                    }

                    VStack(spacing: LegendSpacing.sm) {
                        Text("Native Mobile Configuration Required")
                            .font(.system(.title, design: .rounded).weight(.bold))
                            .multilineTextAlignment(.center)
                            .foregroundStyle(LegendPalette.label)
                            .fixedSize(horizontal: false, vertical: true)
                        Text("This build is waiting for administrator configuration before secure sign-in can begin.")
                            .font(LegendTypography.body)
                            .foregroundStyle(LegendPalette.secondaryLabel)
                            .multilineTextAlignment(.center)
                            .fixedSize(horizontal: false, vertical: true)
                    }

                    LegendCard {
                        VStack(alignment: .leading, spacing: LegendSpacing.sm) {
                            Label("Required configuration", systemImage: "checklist")
                                .font(LegendTypography.section)
                                .foregroundStyle(LegendPalette.label)
                            Text(validation.summary)
                                .font(LegendTypography.metadata)
                                .foregroundStyle(LegendPalette.secondaryLabel)
                                .fixedSize(horizontal: false, vertical: true)
                            Divider()
                            ForEach(validation.missingKeys) { key in
                                Label(key.buildSetting, systemImage: "exclamationmark.circle")
                                    .font(.footnote.monospaced().weight(.medium))
                                    .foregroundStyle(LegendPalette.label)
                                    .accessibilityLabel("Missing administrator configuration: \(key.buildSetting)")
                            }
                        }
                    }

                    Button("Check configuration") {
                        session.restore()
                    }
                    .buttonStyle(LegendButtonStyle(kind: .primary))
                    .accessibilityHint("Checks whether the required administrator configuration is now available.")
                }
                .frame(maxWidth: 520)
                .padding(.horizontal, LegendSpacing.md)
                .padding(.vertical, LegendSpacing.xl)
                .frame(maxWidth: .infinity)
            }
            .background(Color.white.ignoresSafeArea())
            .toolbar(.hidden, for: .navigationBar)
        }
    }
}

private struct SignInView: View {
    @EnvironmentObject private var session: MobileSessionCoordinator

    var body: some View {
        NavigationStack {
            ScrollView {
                VStack(spacing: LegendSpacing.xl) {
                    Spacer(minLength: LegendSpacing.xl)

                    Image("LegendLogo")
                        .resizable()
                        .scaledToFit()
                        .frame(maxWidth: 220)
                        .accessibilityLabel("Legend")

                    VStack(spacing: LegendSpacing.sm) {
                        Text("Welcome to Legend")
                            .font(LegendTypography.hero)
                            .foregroundStyle(LegendPalette.label)
                            .multilineTextAlignment(.center)

                        Text("Life, finances, protection, and community—connected in one place.")
                            .font(LegendTypography.body)
                            .foregroundStyle(LegendPalette.secondaryLabel)
                            .multilineTextAlignment(.center)
                            .fixedSize(horizontal: false, vertical: true)
                    }

                    Button("Continue") {
                        session.signIn()
                    }
                    .buttonStyle(LegendButtonStyle(kind: .primary))
                    .accessibilityHint("Opens sign-in.")

                    Spacer(minLength: LegendSpacing.xl)
                }
                .frame(maxWidth: 520)
                .padding(.horizontal, LegendSpacing.md)
                .padding(.vertical, LegendSpacing.lg)
                .frame(maxWidth: .infinity, minHeight: 640)
            }
            .background(Color.white.ignoresSafeArea())
            .toolbar(.hidden, for: .navigationBar)
        }
    }
}

private struct SessionFailureView: View {
    let failure: UserFacingFailure
    @EnvironmentObject private var session: MobileSessionCoordinator

    var body: some View {
        NavigationStack {
            VStack {
                LegendErrorCard(
                    title: failure.title,
                    message: failure.message,
                    retryTitle: "Try again",
                    retry: session.retrySessionEntry)
            }
            .padding(LegendSpacing.md)
            .frame(maxWidth: .infinity, maxHeight: .infinity)
            .background(Color.white.ignoresSafeArea())
            .navigationTitle("Secure access")
            .navigationBarTitleDisplayMode(.inline)
        }
    }
}

private struct AuthenticatedHomeView: View {
    let currentSession: MobileSession
    @ObservedObject private var coordinator: MobileSessionCoordinator
    @StateObject private var bootstrap: LegendApplicationBootstrapCoordinator

    init(currentSession: MobileSession, coordinator: MobileSessionCoordinator) {
        self.currentSession = currentSession
        _coordinator = ObservedObject(wrappedValue: coordinator)
        _bootstrap = StateObject(wrappedValue: LegendApplicationBootstrapCoordinator(
            currentSession: currentSession,
            coordinator: coordinator))
    }

    var body: some View {
        Group {
            switch bootstrap.state {
            case .idle, .loading:
                LegendLaunchSkeleton()
            case .ready, .partiallyReady:
                LegendApplicationShell(
                    currentSession: currentSession,
                    coordinator: coordinator,
                    bootstrap: bootstrap)
            case .failed(let failure):
                NavigationStack {
                    LegendErrorCard(
                        title: failure.title,
                        message: failure.message,
                        retryTitle: "Try again",
                        retry: {
                            Task { await bootstrap.retryBootstrap() }
                        })
                    .padding(LegendSpacing.md)
                    .frame(maxWidth: .infinity, maxHeight: .infinity)
                    .background(Color.white.ignoresSafeArea())
                    .navigationTitle("Legend")
                    .navigationBarTitleDisplayMode(.inline)
                }
            }
        }
        .task {
            await bootstrap.bootstrapIfNeeded()
        }
    }
}
