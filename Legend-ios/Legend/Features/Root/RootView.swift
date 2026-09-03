import SwiftUI

struct RootView: View {
    @EnvironmentObject private var session: MobileSessionCoordinator
    @EnvironmentObject private var diagnostics: LegendDiagnostics

    var body: some View {
        Group {
            switch session.state {
            case .loading:
                LegendSessionProgressView()
            case .contractUnavailable(let validation):
                ConfigurationStateView(validation: validation)
            case .signedOut:
                SignInView()
            case .authenticating:
                LegendSessionProgressView()
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
        .alert(
            "Use Face ID?",
            isPresented: Binding(
                get: { session.isOfferingBiometricSignIn },
                set: { isPresented in
                    if !isPresented {
                        session.declineBiometricSignInEnrollment()
                    }
                })
        ) {
            Button("Enable Face ID") {
                session.enableBiometricSignInFromEnrollment()
            }
            Button("Not now", role: .cancel) {
                session.declineBiometricSignInEnrollment()
            }
        } message: {
            Text("Optionally use Face ID to protect this Legend account on this device. You can change this any time in Profile settings.")
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

private struct LegendSessionProgressView: View {
    var body: some View {
        ProgressView()
            .tint(LegendNextColor.navyElevated)
            .frame(maxWidth: .infinity, maxHeight: .infinity)
            .background(LegendNextCanvas())
            .accessibilityLabel("Securing your Legend session")
    }
}

private struct RoleSelectionView: View {
    let selection: MobileRoleSelection
    @EnvironmentObject private var session: MobileSessionCoordinator

    var body: some View {
        NavigationStack {
            ScrollView {
                VStack(spacing: LegendNextSpacing.md) {
                    VStack(alignment: .leading, spacing: LegendNextSpacing.xs) {
                        HStack {
                            Text("LEGEND ACCOUNT")
                                .font(LegendNextTypography.eyebrow)
                                .foregroundStyle(LegendNextColor.goldBright)

                            Spacer()

                            LegendBrandLogo(maximumWidth: 52)
                                .frame(width: 46, height: 46)
                                .clipShape(Circle())
                                .accessibilityHidden(true)
                        }

                        Text("Choose your experience")
                            .font(.system(size: 27, weight: .bold))
                            .foregroundStyle(LegendNextColor.contactTitle)

                        Text("Choose the account you want to use. Legend will reopen it next time.")
                            .font(LegendNextTypography.supporting)
                            .foregroundStyle(LegendNextColor.contactTitle.opacity(0.76))
                            .fixedSize(horizontal: false, vertical: true)
                    }
                    .padding(.horizontal, LegendNextSpacing.lg)
                    .padding(.vertical, LegendNextSpacing.md)
                    .background {
                        ZStack {
                            LegendNextGradient.hero
                            LegendNextGradient.heroGlow
                        }
                        .clipShape(
                            RoundedRectangle(
                                cornerRadius: 24,
                                style: .continuous))
                    }
                    .overlay {
                        RoundedRectangle(cornerRadius: 24, style: .continuous)
                            .strokeBorder(LegendNextGradient.premiumStroke, lineWidth: 1)
                    }
                    .shadow(color: LegendNextColor.navy.opacity(0.16), radius: 18, y: 9)

                    LegendNextSurface(style: .elevated) {
                        VStack(alignment: .leading, spacing: LegendNextSpacing.xs) {
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
                                        .font(.system(size: 15, weight: .semibold))
                                        .foregroundStyle(LegendNextColor.gold)
                                        .frame(width: 31, height: 31)
                                        .background(LegendNextColor.goldSoft, in: Circle())

                                        Text(
                                            role == .agent
                                                ? "Continue as Agent"
                                                : "Continue as Client"
                                        )
                                        .font(.system(size: 15, weight: .semibold))
                                        .foregroundStyle(LegendNextColor.textPrimary)

                                        Spacer()

                                        Image(systemName: "chevron.right")
                                            .font(.caption.weight(.bold))
                                            .foregroundStyle(LegendNextColor.gold)
                                    }
                                    .padding(.horizontal, LegendNextSpacing.sm)
                                    .frame(maxWidth: .infinity, minHeight: 48)
                                    .background(
                                        LinearGradient(
                                            colors: [
                                                LegendNextColor.surface,
                                                LegendNextColor.surfaceInset
                                            ],
                                            startPoint: .topLeading,
                                            endPoint: .bottomTrailing))
                                    .overlay {
                                        RoundedRectangle(
                                            cornerRadius: LegendNextRadius.control,
                                            style: .continuous
                                        )
                                        .stroke(
                                            LegendNextColor.gold.opacity(0.58),
                                            lineWidth: 1
                                        )
                                    }
                                    .clipShape(
                                        RoundedRectangle(
                                            cornerRadius: LegendNextRadius.control,
                                            style: .continuous
                                        )
                                    )
                                    .shadow(
                                        color: LegendNextColor.gold.opacity(0.10),
                                        radius: 8,
                                        y: 3)
                                }
                                .buttonStyle(.plain)
                            }
                        }
                    }

                    Button("Sign out") {
                        session.signOut()
                    }
                    .font(.system(size: 14, weight: .semibold))
                    .foregroundStyle(LegendNextColor.danger)
                    .frame(maxWidth: .infinity, minHeight: 38)
                    .background(
                        LegendNextColor.danger.opacity(0.07),
                        in: RoundedRectangle(cornerRadius: 13, style: .continuous))
                    .overlay {
                        RoundedRectangle(cornerRadius: 13, style: .continuous)
                            .strokeBorder(LegendNextColor.danger.opacity(0.22), lineWidth: 1)
                    }
                    .buttonStyle(.plain)
                }
                .frame(maxWidth: 520)
                .padding(.horizontal, LegendNextSpacing.pageHorizontal)
                .padding(.top, LegendNextSpacing.lg)
                .padding(.bottom, LegendNextSpacing.xl)
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
                        Text("LEGEND®")
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
    @State private var username = ""
    @State private var password = ""
    @State private var showsProvidedCredentials = false

    private var normalizedUsername: String {
        username.trimmingCharacters(in: .whitespacesAndNewlines)
    }

    private var hasCompleteProvidedCredentials: Bool {
        !normalizedUsername.isEmpty && !password.isEmpty
    }

    var body: some View {
        NavigationStack {
            ScrollView {
                VStack(spacing: LegendNextSpacing.md) {
                    VStack(spacing: LegendNextSpacing.sm) {
                        LegendBrandLogo(maximumWidth: 78)
                            .frame(width: 68, height: 68)
                            .clipShape(Circle())
                            .overlay {
                                Circle()
                                    .strokeBorder(
                                        LegendNextColor.goldBright.opacity(0.72),
                                        lineWidth: 1)
                            }
                            .accessibilityHidden(true)

                        Text("LEGEND ACCOUNT")
                            .font(LegendNextTypography.eyebrow)
                            .foregroundStyle(LegendNextColor.goldBright)

                        Text("Secure sign in")
                            .font(.system(size: 27, weight: .bold))
                            .foregroundStyle(LegendNextColor.contactTitle)
                        Text("Tap Sign in securely to continue with your Legend account.")
                            .font(LegendNextTypography.supporting)
                            .foregroundStyle(LegendNextColor.contactTitle.opacity(0.76))
                            .multilineTextAlignment(.center)
                    }
                    .frame(maxWidth: .infinity)
                    .padding(.horizontal, LegendNextSpacing.lg)
                    .padding(.vertical, LegendNextSpacing.lg)
                    .background {
                        ZStack {
                            LegendNextGradient.hero
                            LegendNextGradient.heroGlow
                        }
                        .clipShape(
                            RoundedRectangle(
                                cornerRadius: 26,
                                style: .continuous))
                    }
                    .overlay {
                        RoundedRectangle(cornerRadius: 26, style: .continuous)
                            .strokeBorder(LegendNextGradient.premiumStroke, lineWidth: 1)
                    }
                    .shadow(color: LegendNextColor.navy.opacity(0.16), radius: 20, y: 10)

                    VStack(spacing: 0) {
                        Button(action: toggleProvidedCredentials) {
                            HStack(spacing: LegendNextSpacing.sm) {
                                VStack(alignment: .leading, spacing: 2) {
                                    Text("Were you given sign-in credentials?")
                                        .font(LegendNextTypography.supporting)
                                        .foregroundStyle(LegendNextColor.textPrimary)
                                    Text("Optional access method")
                                        .font(LegendNextTypography.caption)
                                        .foregroundStyle(LegendNextColor.textSecondary)
                                }
                                Spacer(minLength: LegendNextSpacing.sm)
                                Image(systemName: showsProvidedCredentials ? "chevron.up" : "chevron.down")
                                    .font(.system(size: 14, weight: .semibold))
                                    .foregroundStyle(LegendNextColor.navyElevated)
                            }
                            .contentShape(Rectangle())
                        }
                        .buttonStyle(.plain)
                        .accessibilityHint(
                            showsProvidedCredentials
                                ? "Hides the provided credential fields."
                                : "Shows fields for credentials supplied with access instructions.")

                        if showsProvidedCredentials {
                            Divider()
                                .padding(.vertical, LegendNextSpacing.sm)

                            VStack(alignment: .leading, spacing: LegendNextSpacing.sm) {
                                TextField("Username", text: $username)
                                    .textContentType(.username)
                                    .textInputAutocapitalization(.never)
                                    .autocorrectionDisabled()
                                    .submitLabel(.next)

                                Divider()

                                SecureField("Password", text: $password)
                                    .textContentType(.password)
                                    .submitLabel(.go)
                                    .onSubmit(signIn)

                                Text("Enter the username and password you were provided, then use the same Sign in securely button below.")
                                    .font(LegendNextTypography.caption)
                                    .foregroundStyle(LegendNextColor.textSecondary)
                                    .fixedSize(horizontal: false, vertical: true)
                            }
                            .textFieldStyle(.plain)
                        }
                    }
                    .padding(LegendNextSpacing.md)
                    .background(LegendNextColor.surface)
                    .clipShape(RoundedRectangle(cornerRadius: 18, style: .continuous))
                    .overlay {
                        RoundedRectangle(cornerRadius: 18, style: .continuous)
                            .strokeBorder(LegendNextColor.separator, lineWidth: 1)
                    }

                    Button("Sign in securely", action: signIn)
                        .buttonStyle(LegendNextButtonStyle(kind: .primary))
                        .frame(maxHeight: 48)
                        .disabled(showsProvidedCredentials && !hasCompleteProvidedCredentials)
                        .accessibilityHint(
                            hasCompleteProvidedCredentials
                                ? "Signs in with the provided Legend credentials."
                                : "Opens secure Legend sign-in and verification.")

                    Text("Face ID is optional and can be enabled after sign in in Profile settings.")
                        .font(LegendNextTypography.caption)
                        .foregroundStyle(LegendNextColor.textSecondary)
                        .multilineTextAlignment(.center)
                        .padding(.horizontal, LegendNextSpacing.xl)
                }
                .frame(maxWidth: 520)
                .padding(.horizontal, LegendNextSpacing.pageHorizontal)
                .padding(.top, LegendNextSpacing.xl)
                .padding(.bottom, LegendNextSpacing.lg)
                .frame(maxWidth: .infinity)
            }
            .scrollDismissesKeyboard(.interactively)
            .background(LegendNextCanvas())
            .toolbar(.hidden, for: .navigationBar)
        }
    }

    private func signIn() {
        if showsProvidedCredentials && hasCompleteProvidedCredentials {
            let submittedUsername = normalizedUsername
            let submittedPassword = password
            password = ""
            session.signInForAppReview(
                username: submittedUsername,
                password: submittedPassword)
        } else if !showsProvidedCredentials {
            session.signIn()
        }
    }

    private func toggleProvidedCredentials() {
        showsProvidedCredentials.toggle()
        if !showsProvidedCredentials {
            username = ""
            password = ""
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
    @Environment(\.scenePhase) private var scenePhase
    @EnvironmentObject private var pushNotifications: LegendPushNotificationDelegate
    let currentSession: MobileSession
    @ObservedObject private var coordinator: MobileSessionCoordinator
    @StateObject private var bootstrap: LegendApplicationBootstrapCoordinator
    @StateObject private var legendFounderAi: LegendFounderAiStore
    @State private var notificationSynchronizationInFlight = false
    @State private var pushDeviceRegistrationInFlight = false

    init(currentSession: MobileSession, coordinator: MobileSessionCoordinator) {
        self.currentSession = currentSession
        _coordinator = ObservedObject(wrappedValue: coordinator)
        _bootstrap = StateObject(wrappedValue: LegendApplicationBootstrapCoordinator(
            currentSession: currentSession,
            coordinator: coordinator))

        _legendFounderAi = StateObject(
            wrappedValue:
                coordinator.makeLegendFounderAiStore(
                    participantType:
                        currentSession.actor.identity.participantType))
    }

    var body: some View {
        Group {
            switch bootstrap.state {
            case .idle, .loading:
                LegendSessionProgressView()
            case .ready, .partiallyReady:
                LegendApplicationShell(
                    currentSession: currentSession,
                    coordinator: coordinator,
                    bootstrap: bootstrap,
                    legendFounderAi: legendFounderAi,
                    onSignOut: {
                        Task { await signOut() }
                    })
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
                    .navigationTitle("LEGEND®")
                    .navigationBarTitleDisplayMode(.inline)
                }
            }
        }
        .task(id: scenePhase) {
            await bootstrap.bootstrapIfNeeded()
            guard scenePhase == .active else { return }
            await synchronizeNotifications()
        }
        .onChange(of: pushNotifications.deviceToken) { _, _ in
            Task { await registerPushDeviceIfAvailable() }
        }
        .onChange(of: pushNotifications.signedEnvironment) { _, _ in
            Task { await registerPushDeviceIfAvailable() }
        }
    }

    private func synchronizeNotifications() async {
        guard !notificationSynchronizationInFlight else {
            return
        }

        notificationSynchronizationInFlight = true
        defer {
            notificationSynchronizationInFlight = false
        }

        await bootstrap.stores.notifications.sync()
        await registerPushDeviceIfAvailable()
    }

    private func registerPushDeviceIfAvailable() async {
        guard !pushDeviceRegistrationInFlight,
              let token = pushNotifications.deviceToken,
              let environment = pushNotifications.signedEnvironment else {
            return
        }

        pushDeviceRegistrationInFlight = true
        defer {
            pushDeviceRegistrationInFlight = false
        }

        await bootstrap.stores.notifications.registerAPNSDevice(
            token: token,
            environment: environment.rawValue)
    }

    private func signOut() async {
        await bootstrap.stores.notifications.deactivateAPNSDevice(
            token: pushNotifications.deviceToken)
        coordinator.signOut()
    }
}
