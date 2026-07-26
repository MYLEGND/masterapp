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
            VStack(spacing: 24) {
                Image(systemName: "lock.shield")
                    .font(.system(size: 54, weight: .semibold))
                    .foregroundStyle(.tint)

                VStack(spacing: 10) {
                    Text("Mobile access requires server configuration")
                        .font(.title2.bold())
                        .multilineTextAlignment(.center)
                    Text("This native build will stay disconnected until the approved PKCE and bearer API contract is supplied.")
                        .foregroundStyle(.secondary)
                        .multilineTextAlignment(.center)
                }

                if !validation.missingKeys.isEmpty {
                    VStack(alignment: .leading, spacing: 8) {
                        Text("Missing build settings")
                            .font(.headline)
                        ForEach(validation.missingKeys, id: \.self) { key in
                            Label(key, systemImage: "exclamationmark.circle")
                                .font(.footnote.monospaced())
                        }
                    }
                    .frame(maxWidth: .infinity, alignment: .leading)
                    .padding()
                    .background(.thinMaterial, in: RoundedRectangle(cornerRadius: 16))
                }

                Button("Check configuration") {
                    session.restore()
                }
                .buttonStyle(.borderedProminent)

                Spacer()
            }
            .padding(24)
            .navigationTitle("Legend")
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
