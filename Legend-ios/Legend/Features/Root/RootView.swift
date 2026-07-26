import SwiftUI

struct RootView: View {
    @EnvironmentObject private var session: MobileSessionCoordinator

    var body: some View {
        Group {
            switch session.state {
            case .loading:
                ProgressView("Preparing secure access…")
            case .contractUnavailable(let validation):
                MobileContractUnavailableView(validation: validation)
            case .signedOut:
                SignInView()
            case .authenticating:
                ProgressView("Opening secure sign-in…")
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
    }
}

private struct RoleSelectionView: View {
    let selection: MobileRoleSelection
    @EnvironmentObject private var session: MobileSessionCoordinator

    var body: some View {
        NavigationStack {
            VStack(spacing: 20) {

            LegendBrandLogo()
                .padding(.bottom, 8)

                Image(systemName: "person.2.badge.gearshape")
                    .font(.system(size: 46, weight: .semibold))
                    .foregroundStyle(.tint)
                VStack(spacing: 8) {
                    Text("Choose your Legend role")
                        .font(.title2.bold())
                    Text("Your account has more than one authorized participant role. Choose the role for this secure mobile session.")
                        .foregroundStyle(.secondary)
                        .multilineTextAlignment(.center)
                }
                VStack(spacing: 10) {
                    ForEach(selection.permittedParticipantTypes, id: \.self) { role in
                        Button {
                            session.selectRole(role)
                        } label: {
                            Label(role == .agent ? "Continue as Agent" : "Continue as Client", systemImage: role == .agent ? "briefcase.fill" : "person.fill")
                                .frame(maxWidth: .infinity)
                        }
                        .buttonStyle(.borderedProminent)
                    }
                }
                Button("Sign out", role: .destructive) {
                    session.signOut()
                }
                Spacer()
            }
            .padding(24)
            .navigationTitle("Legend")
        }
    }
}

private struct MobileContractUnavailableView: View {
    let validation: MobileConfigurationValidation
    @EnvironmentObject private var session: MobileSessionCoordinator

    var body: some View {
        NavigationStack {
            ScrollView {
                VStack(spacing: 24) {
                    Spacer(minLength: 12)

                    LegendBrandLogo(maximumWidth: 136)
                        .accessibilityLabel("Legend logo")

                    VStack(spacing: 10) {
                        Text("Legend")
                            .font(.title2.weight(.bold))
                        Text("Native Mobile Configuration Required")
                            .font(.headline.weight(.semibold))
                            .multilineTextAlignment(.center)
                        Text("This build is waiting for administrator configuration before secure sign-in can begin.")
                            .font(.body)
                            .foregroundStyle(.secondary)
                            .multilineTextAlignment(.center)
                            .fixedSize(horizontal: false, vertical: true)
                    }
                    .accessibilityElement(children: .combine)

                    VStack(alignment: .leading, spacing: 16) {
                        HStack(spacing: 10) {
                            Image(systemName: "checklist.checked")
                                .foregroundStyle(.tint)
                                .accessibilityHidden(true)
                            Text("Required administrator settings")
                                .font(.headline)
                        }

                        Text(validation.summary)
                            .font(.subheadline)
                            .foregroundStyle(.secondary)
                            .fixedSize(horizontal: false, vertical: true)

                        Divider()

                        VStack(alignment: .leading, spacing: 10) {
                            ForEach(validation.missingKeys) { key in
                                Label(key.buildSetting, systemImage: "minus.circle.fill")
                                    .font(.footnote.monospaced())
                                    .foregroundStyle(.primary)
                                    .accessibilityLabel("Missing build setting \(key.buildSetting)")
                            }
                        }
                    }
                    .frame(maxWidth: .infinity, alignment: .leading)
                    .padding(20)
                    .background(Color(uiColor: .secondarySystemGroupedBackground), in: RoundedRectangle(cornerRadius: 22, style: .continuous))
                    .overlay {
                        RoundedRectangle(cornerRadius: 22, style: .continuous)
                            .stroke(Color.primary.opacity(0.08), lineWidth: 1)
                    }
                    .accessibilityElement(children: .contain)

                    Button("Check configuration") {
                        session.restore()
                    }
                    .buttonStyle(.borderedProminent)
                    .controlSize(.large)
                    .accessibilityHint("Checks whether the required administrator configuration is now available.")
                }
                .frame(maxWidth: 520)
                .padding(.horizontal, 24)
                .padding(.vertical, 32)
                .frame(maxWidth: .infinity)
            }
            .background(Color(uiColor: .systemGroupedBackground).ignoresSafeArea())
            .navigationTitle("Legend")
            .navigationBarTitleDisplayMode(.inline)
        }
    }
}

private struct SignInView: View {
    @EnvironmentObject private var session: MobileSessionCoordinator

    var body: some View {
        NavigationStack {
            VStack(spacing: 24) {
                Image(systemName: "person.badge.key")
                    .font(.system(size: 54, weight: .semibold))
                    .foregroundStyle(.tint)
                Text("Secure Legend access")
                    .font(.title2.bold())
                Text("Sign in in the system browser. Legend does not collect or retain your password.")
                    .foregroundStyle(.secondary)
                    .multilineTextAlignment(.center)
                Button("Continue securely") {
                    session.signIn()
                }
                .buttonStyle(.borderedProminent)
                Spacer()
            }
            .padding(24)
            .navigationTitle("Legend")
        }
    }
}

private struct SessionFailureView: View {
    let failure: UserFacingFailure
    @EnvironmentObject private var session: MobileSessionCoordinator

    var body: some View {
        NavigationStack {
            ContentUnavailableView {
                Label(failure.title, systemImage: "exclamationmark.triangle")
            } description: {
                Text(failure.message)
            } actions: {
                Button("Try again") {
                    session.signIn()
                }
                .buttonStyle(.borderedProminent)
            }
            .navigationTitle("Legend")
        }
    }
}

private struct AuthenticatedHomeView: View {
    let currentSession: MobileSession
    @ObservedObject private var coordinator: MobileSessionCoordinator
    @StateObject private var messages: MessagingStore

    init(currentSession: MobileSession, coordinator: MobileSessionCoordinator) {
        self.currentSession = currentSession
        _coordinator = ObservedObject(wrappedValue: coordinator)
        _messages = StateObject(wrappedValue: coordinator.makeMessagingStore())
    }

    var body: some View {
        NavigationStack {
            MessagingHomeView(store: messages, currentSession: currentSession)
                .toolbar {
                    ToolbarItem(placement: .topBarTrailing) {
                        Menu {
                            Button("Sign out", role: .destructive) {
                                coordinator.signOut()
                            }
                        } label: {
                            Label("Account", systemImage: "person.crop.circle")
                        }
                    }
                }
        }
    }
}
