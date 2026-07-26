import SwiftUI

struct RootView: View {
    @EnvironmentObject private var session: MobileSessionCoordinator
    @EnvironmentObject private var diagnostics: LegendDiagnostics

    var body: some View {
        Group {
            switch session.state {
            case .loading:
                LegendLoadingView("Preparing secure access…")
            case .contractUnavailable(let validation):
                ConfigurationStateView(validation: validation)
            case .signedOut:
                SignInView()
            case .authenticating:
                LegendLoadingView("Opening secure sign-in…")
            case .roleSelection(let selection):
                RoleSelectionView(selection: selection)
            case .authenticated(let currentSession):
                AuthenticatedHomeView(currentSession: currentSession, coordinator: session)
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
                    Image(systemName: "person.2.badge.gearshape")
                        .font(.system(size: 38, weight: .semibold))
                        .foregroundStyle(LegendPalette.gold)
                        .accessibilityHidden(true)
                    VStack(spacing: LegendSpacing.xs) {
                        Text("Choose your Legend role")
                            .font(LegendTypography.hero)
                        Text("Your account has more than one authorized participant role. Choose the role for this secure mobile session.")
                            .font(LegendTypography.body)
                            .foregroundStyle(LegendPalette.secondaryLabel)
                            .multilineTextAlignment(.center)
                            .fixedSize(horizontal: false, vertical: true)
                    }
                    VStack(spacing: LegendSpacing.sm) {
                        ForEach(selection.permittedParticipantTypes, id: \.self) { role in
                            Button {
                                session.selectRole(role)
                            } label: {
                                Label(role == .agent ? "Continue as Agent" : "Continue as Client", systemImage: role == .agent ? "briefcase.fill" : "person.fill")
                            }
                            .buttonStyle(LegendButtonStyle(kind: .primary))
                        }
                    }
                    Button("Sign out", role: .destructive) {
                        session.signOut()
                    }
                    .buttonStyle(LegendButtonStyle(kind: .destructive))
                }
                .frame(maxWidth: 520)
                .padding(.horizontal, LegendSpacing.md)
                .padding(.vertical, LegendSpacing.xl)
                .frame(maxWidth: .infinity)
            }
            .background(LegendPalette.canvas.ignoresSafeArea())
            .navigationTitle("Secure role")
            .navigationBarTitleDisplayMode(.inline)
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
            .background(LegendPalette.canvas.ignoresSafeArea())
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
            .background(LegendPalette.canvas.ignoresSafeArea())
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
                    retry: session.signIn)
            }
            .padding(LegendSpacing.md)
            .frame(maxWidth: .infinity, maxHeight: .infinity)
            .background(LegendPalette.canvas.ignoresSafeArea())
            .navigationTitle("Secure access")
            .navigationBarTitleDisplayMode(.inline)
        }
    }
}

private struct AuthenticatedHomeView: View {
    let currentSession: MobileSession
    @ObservedObject private var coordinator: MobileSessionCoordinator

    init(currentSession: MobileSession, coordinator: MobileSessionCoordinator) {
        self.currentSession = currentSession
        _coordinator = ObservedObject(wrappedValue: coordinator)
    }

    var body: some View {
        LegendApplicationShell(currentSession: currentSession, coordinator: coordinator)
    }
}
