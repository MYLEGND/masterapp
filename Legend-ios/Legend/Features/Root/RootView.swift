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
        // Legend's standard canvas is white. Discover opts into its blue
        // treatment locally, making that choice explicit rather than allowing
        // each individual page to choose a competing color scheme.
        .preferredColorScheme(.light)
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
                VStack(spacing: LegendNextSpacing.lg) {
                    LegendNextHero(
                        eyebrow: "Legend membership",
                        title: "Choose your experience",
                        detail: "Your account includes every authorized Legend workspace."
                    ) {
                        LegendBrandLogo(maximumWidth: 86)
                            .frame(maxWidth: .infinity, alignment: .trailing)
                            .accessibilityLabel("Legend")
                    }

                    LegendNextSurface(style: .elevated) {
                        VStack(alignment: .leading, spacing: LegendNextSpacing.sm) {
                            Text("Available workspaces")
                                .font(LegendNextTypography.section)
                                .foregroundStyle(LegendNextColor.textPrimary)

                            ForEach(selection.permittedParticipantTypes, id: \.self) { role in
                                Button {
                                    session.selectRole(role)
                                } label: {
                                    HStack(spacing: LegendNextSpacing.sm) {
                                        Image(
                                            systemName: role == .agent
                                                ? "briefcase.fill"
                                                : "person.fill"
                                        )
                                        .font(.system(size: 18, weight: .semibold))
                                        .foregroundStyle(LegendNextColor.gold)
                                        .frame(width: 30)

                                        Text(
                                            role == .agent
                                                ? "Continue as Agent"
                                                : "Continue as Client"
                                        )
                                        .font(LegendNextTypography.cardTitle)
                                        .foregroundStyle(LegendNextColor.textPrimary)

                                        Spacer()

                                        Image(systemName: "chevron.right")
                                            .font(.caption.weight(.bold))
                                            .foregroundStyle(LegendNextColor.gold)
                                    }
                                    .padding(.horizontal, LegendNextSpacing.md)
                                    .frame(maxWidth: .infinity, minHeight: 56)
                                    .background(LegendNextColor.surfaceInset)
                                    .overlay {
                                        RoundedRectangle(
                                            cornerRadius: LegendNextRadius.control,
                                            style: .continuous
                                        )
                                        .stroke(
                                            LegendNextColor.gold.opacity(0.42),
                                            lineWidth: 1
                                        )
                                    }
                                    .clipShape(
                                        RoundedRectangle(
                                            cornerRadius: LegendNextRadius.control,
                                            style: .continuous
                                        )
                                    )
                                }
                                .buttonStyle(.plain)
                            }
                        }
                    }

                    Button("Sign out") {
                        session.signOut()
                    }
                    .buttonStyle(LegendNextButtonStyle(kind: .destructive))
                }
                .frame(maxWidth: 520)
                .padding(.horizontal, LegendNextSpacing.pageHorizontal)
                .padding(.top, LegendNextSpacing.xxl)
                .padding(.bottom, LegendNextSpacing.xxl)
                .frame(maxWidth: .infinity)
            }
            .background(LegendNextCanvas())
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
                VStack(spacing: LegendNextSpacing.lg) {
                    VStack(spacing: LegendNextSpacing.xs) {
                        LegendBrandLogo(maximumWidth: 96)
                            .accessibilityHidden(true)
                        Text("Legend")
                            .font(.system(.title2, design: .rounded).weight(.bold))
                            .foregroundStyle(LegendNextColor.textPrimary)
                    }

                    VStack(spacing: LegendNextSpacing.sm) {
                        Text("Native Mobile Configuration Required")
                            .font(.system(.title, design: .rounded).weight(.bold))
                            .multilineTextAlignment(.center)
                            .foregroundStyle(LegendNextColor.textPrimary)
                            .fixedSize(horizontal: false, vertical: true)
                        Text("This build is waiting for administrator configuration before secure sign-in can begin.")
                            .font(LegendNextTypography.body)
                            .foregroundStyle(LegendNextColor.textSecondary)
                            .multilineTextAlignment(.center)
                            .fixedSize(horizontal: false, vertical: true)
                    }

                    LegendNextSurface {
                        VStack(alignment: .leading, spacing: LegendNextSpacing.sm) {
                            Label("Required configuration", systemImage: "checklist")
                                .font(LegendNextTypography.section)
                                .foregroundStyle(LegendNextColor.textPrimary)
                            Text(validation.summary)
                                .font(LegendNextTypography.supporting)
                                .foregroundStyle(LegendNextColor.textSecondary)
                                .fixedSize(horizontal: false, vertical: true)
                            Divider()
                            ForEach(validation.missingKeys) { key in
                                Label(key.buildSetting, systemImage: "exclamationmark.circle")
                                    .font(.footnote.monospaced().weight(.medium))
                                    .foregroundStyle(LegendNextColor.textPrimary)
                                    .accessibilityLabel("Missing administrator configuration: \(key.buildSetting)")
                            }
                        }
                    }

                    Button("Check configuration") {
                        session.restore()
                    }
                    .buttonStyle(LegendNextButtonStyle(kind: .primary))
                    .accessibilityHint("Checks whether the required administrator configuration is now available.")
                }
                .frame(maxWidth: 520)
                .padding(.horizontal, LegendNextSpacing.md)
                .padding(.vertical, LegendNextSpacing.xl)
                .frame(maxWidth: .infinity)
            }
            .background(LegendNextCanvas())
            .toolbar(.hidden, for: .navigationBar)
        }
    }
}

private struct SignInView: View {
    @EnvironmentObject private var session: MobileSessionCoordinator

    var body: some View {
        NavigationStack {
            ScrollView {
                VStack(spacing: LegendNextSpacing.lg) {
                    Spacer(minLength: LegendNextSpacing.section)

                    LegendNextSurface(
                        style: .navy,
                        cornerRadius: LegendNextRadius.hero,
                        padding: LegendNextSpacing.xxl
                    ) {
                        VStack(spacing: LegendNextSpacing.md) {
                            Text("LEGEND")
                                .font(.system(size: 13, weight: .bold, design: .default))
                                .tracking(4.2)
                                .foregroundStyle(LegendNextColor.goldBright)

                            Image("LegendLogo")
                                .resizable()
                                .scaledToFit()
                                .frame(maxWidth: 154)
                                .accessibilityLabel("Legend")

                            Text("Your legacy, in motion.")
                                .font(LegendNextTypography.hero)
                                .foregroundStyle(.white)
                                .multilineTextAlignment(.center)

                            Text("Life, finances, protection, and community—elevated in one secure experience.")
                                .font(LegendNextTypography.supporting)
                                .foregroundStyle(.white.opacity(0.74))
                                .multilineTextAlignment(.center)
                                .fixedSize(horizontal: false, vertical: true)
                        }
                        .frame(maxWidth: .infinity)
                    }

                    LegendNextSurface(style: .elevated) {
                        VStack(alignment: .leading, spacing: LegendNextSpacing.md) {
                            Label("Private member access", systemImage: "lock.shield.fill")
                                .font(LegendNextTypography.label)
                                .foregroundStyle(LegendNextColor.gold)

                            Text("Continue securely to your personalized Legend workspace.")
                                .font(LegendNextTypography.supporting)
                                .foregroundStyle(LegendNextColor.textSecondary)
                                .fixedSize(horizontal: false, vertical: true)

                            Button("Continue securely") {
                                session.signIn()
                            }
                            .buttonStyle(LegendNextButtonStyle(kind: .primary))
                            .accessibilityHint("Opens secure sign-in.")
                        }
                    }

                    Text("Built for the life you are leading and the legacy you are building.")
                        .font(LegendNextTypography.caption)
                        .foregroundStyle(LegendNextColor.textSecondary)
                        .multilineTextAlignment(.center)
                        .padding(.horizontal, LegendNextSpacing.xl)
                }
                .frame(maxWidth: 520)
                .padding(.horizontal, LegendNextSpacing.pageHorizontal)
                .padding(.vertical, LegendNextSpacing.xxl)
                .frame(maxWidth: .infinity, minHeight: 640)
            }
            .background(LegendNextCanvas())
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
                LegendNextErrorState(
                    title: failure.title,
                    message: failure.message,
                    retryTitle: "Try again",
                    retry: session.retrySessionEntry)
            }
            .padding(LegendNextSpacing.md)
            .frame(maxWidth: .infinity, maxHeight: .infinity)
            .background(LegendNextCanvas())
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
                    LegendNextErrorState(
                        title: failure.title,
                        message: failure.message,
                        retryTitle: "Try again",
                        retry: {
                            Task { await bootstrap.retryBootstrap() }
                        })
                    .padding(LegendNextSpacing.md)
                    .frame(maxWidth: .infinity, maxHeight: .infinity)
                    .background(LegendNextCanvas())
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
