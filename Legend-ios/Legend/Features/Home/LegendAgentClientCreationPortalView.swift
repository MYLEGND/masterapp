import SwiftUI
import WebKit

/// Hosts the existing AgentPortal client-creation Razor view in the native app.
/// No SwiftUI fields are defined here: the portal is the single owner of the
/// form, its styling, validation, and the CRM provisioning workflow.
struct LegendAgentClientCreationPortalView: View {
    @ObservedObject private var store: MobileAgentWorkspaceStore
    let onClose: () -> Void

    @Environment(\.dismiss) private var dismiss
    @EnvironmentObject private var session: MobileSessionCoordinator
    @State private var state: LoadState = .loading
    @State private var hasRequestedLaunch = false
    @State private var isRecoveringExpiredPortalSession = false
    @State private var hasRecoveredExpiredPortalSession = false

    init(store: MobileAgentWorkspaceStore, onClose: @escaping () -> Void) {
        _store = ObservedObject(wrappedValue: store)
        self.onClose = onClose
    }

    var body: some View {
        Group {
            switch state {
            case .loading:
                LegendScreenSkeleton(accessibilityMessage: "Opening client intake") {
                    LegendListSkeleton(rows: 6)
                }
            case .ready(let launchURL):
                LegendAgentClientCreationPortalWebView(
                    launchURL: launchURL,
                    onCreated: finishClientCreation,
                    onCancelled: dismissPortal,
                    onPortalSessionExpired: {
                        Task { await recoverExpiredPortalSession() }
                    },
                    onPortalLoaded: {
                        hasRecoveredExpiredPortalSession = false
                    },
                    onFailure: { message in
                        hasRequestedLaunch = false
                        state = .failed(message)
                    })
                    .ignoresSafeArea(edges: .bottom)
            case .failed(let message):
                LegendNextErrorState(
                    title: "Client intake unavailable",
                    message: message,
                    retryTitle: "Retry",
                    retry: { Task { await requestLaunch() } })
                    .padding(LegendNextSpacing.sm)
            case .authenticationRequired(let message):
                LegendAgentClientCreationAuthenticationState(
                    message: message,
                    retry: { Task { await requestLaunch() } },
                    signIn: signInAgain)
                    .padding(LegendNextSpacing.sm)
            }
        }
        .background(LegendNextCanvas())
        .safeAreaInset(edge: .top, spacing: 0) {
            portalHeader
        }
        .task { await requestLaunch() }
    }

    private func requestLaunch() async {
        guard !hasRequestedLaunch else { return }
        hasRequestedLaunch = true
        state = .loading

        do {
            state = .ready(try await store.clientCreationPortalLaunch())
        } catch {
            state = requiresInteractiveSignIn(for: error)
                ? .authenticationRequired(error.localizedDescription)
                : .failed(error.localizedDescription)
            hasRequestedLaunch = false
        }
    }

    private func recoverExpiredPortalSession() async {
        guard !isRecoveringExpiredPortalSession else { return }
        guard !hasRecoveredExpiredPortalSession else {
            hasRequestedLaunch = false
            state = .authenticationRequired(
                "Your secure session could not be restored. Sign in again to continue.")
            return
        }
        isRecoveringExpiredPortalSession = true
        defer { isRecoveringExpiredPortalSession = false }

        // The AgentPortal ticket is intentionally short-lived. Requesting a new
        // ticket through the authenticated mobile API keeps the native session
        // intact and never leaves a stale embedded page on screen.
        hasRecoveredExpiredPortalSession = true
        hasRequestedLaunch = false
        await requestLaunch()
    }

    private func finishClientCreation() {
        onClose()
        dismiss()
    }

    private func dismissPortal() {
        // The presentation binding is the source of truth for this full-screen
        // cover. Update it as well as using the environment dismissal so Close
        // remains reliable if the sheet is nested by a future container.
        onClose()
        dismiss()
    }

    private func signInAgain() {
        dismissPortal()
        session.signIn()
    }

    private func requiresInteractiveSignIn(for error: Error) -> Bool {
        switch error as? MobileAPIError {
        case .unauthorized, .apiUnauthorized:
            return true
        default:
            return false
        }
    }

    private var portalHeader: some View {
        HStack(spacing: LegendNextSpacing.sm) {
            VStack(alignment: .leading, spacing: 2) {
                Text("CLIENT CRM")
                    .font(LegendNextTypography.eyebrow)
                    .foregroundStyle(LegendNextColor.gold)
                Text("Create client")
                    .font(LegendNextTypography.section)
                    .foregroundStyle(LegendNextColor.textPrimary)
            }

            Spacer(minLength: 0)

            Button(action: dismissPortal) {
                Label("Close", systemImage: "xmark")
            }
            .buttonStyle(LegendNextButtonStyle(
                kind: .secondary,
                isFullWidth: false,
                controlHeight: 38
            ))
            .accessibilityHint("Closes client intake and returns to your clients.")
        }
        .padding(.horizontal, LegendNextSpacing.sm)
        .padding(.vertical, LegendNextSpacing.xs)
        .background(LegendNextCanvas())
        .overlay(alignment: .bottom) {
            Rectangle()
                .fill(LegendNextColor.separator)
                .frame(height: 1)
        }
    }

    private enum LoadState {
        case loading
        case ready(URL)
        case failed(String)
        case authenticationRequired(String)
    }
}

private struct LegendAgentClientCreationAuthenticationState: View {
    let message: String
    let retry: () -> Void
    let signIn: () -> Void

    var body: some View {
        VStack(alignment: .leading, spacing: LegendNextSpacing.md) {
            Image(systemName: "lock.trianglebadge.exclamationmark")
                .font(.system(size: 28, weight: .semibold))
                .foregroundStyle(LegendNextColor.gold)

            Text("Sign in to continue")
                .font(LegendNextTypography.section)
                .foregroundStyle(LegendNextColor.textPrimary)

            Text(message)
                .font(LegendNextTypography.body)
                .foregroundStyle(LegendNextColor.textSecondary)

            Button("Try secure session again", action: retry)
                .buttonStyle(LegendNextButtonStyle(kind: .secondary))

            Button("Sign in again", action: signIn)
                .buttonStyle(LegendNextButtonStyle(kind: .primary))
        }
        .padding(LegendNextSpacing.md)
        .frame(maxWidth: .infinity, alignment: .leading)
        .background(LegendNextColor.surface)
        .clipShape(RoundedRectangle(cornerRadius: LegendNextRadius.card, style: .continuous))
        .overlay {
            RoundedRectangle(cornerRadius: LegendNextRadius.card, style: .continuous)
                .stroke(LegendNextColor.separator, lineWidth: 1)
        }
    }
}

private struct LegendAgentClientCreationPortalWebView: UIViewRepresentable {
    let launchURL: URL
    let onCreated: () -> Void
    let onCancelled: () -> Void
    let onPortalSessionExpired: () -> Void
    let onPortalLoaded: () -> Void
    let onFailure: (String) -> Void

    func makeCoordinator() -> Coordinator {
        Coordinator(
            portalHost: launchURL.host,
            onCreated: onCreated,
            onCancelled: onCancelled,
            onPortalSessionExpired: onPortalSessionExpired,
            onPortalLoaded: onPortalLoaded,
            onFailure: onFailure)
    }

    func makeUIView(context: Context) -> WKWebView {
        let configuration = WKWebViewConfiguration()
        configuration.websiteDataStore = .nonPersistent()
        configuration.defaultWebpagePreferences.allowsContentJavaScript = true

        let webView = WKWebView(frame: .zero, configuration: configuration)
        webView.navigationDelegate = context.coordinator
        webView.allowsBackForwardNavigationGestures = false
        webView.scrollView.contentInsetAdjustmentBehavior = .never
        webView.load(URLRequest(url: launchURL))
        return webView
    }

    func updateUIView(_ webView: WKWebView, context: Context) {}

    final class Coordinator: NSObject, WKNavigationDelegate {
        private let portalHost: String?
        private let onCreated: () -> Void
        private let onCancelled: () -> Void
        private let onPortalSessionExpired: () -> Void
        private let onPortalLoaded: () -> Void
        private let onFailure: (String) -> Void
        private var didSubmitClientCreation = false

        init(
            portalHost: String?,
            onCreated: @escaping () -> Void,
            onCancelled: @escaping () -> Void,
            onPortalSessionExpired: @escaping () -> Void,
            onPortalLoaded: @escaping () -> Void,
            onFailure: @escaping (String) -> Void
        ) {
            self.portalHost = portalHost
            self.onCreated = onCreated
            self.onCancelled = onCancelled
            self.onPortalSessionExpired = onPortalSessionExpired
            self.onPortalLoaded = onPortalLoaded
            self.onFailure = onFailure
        }

        func webView(
            _ webView: WKWebView,
            decidePolicyFor navigationAction: WKNavigationAction,
            decisionHandler: @escaping (WKNavigationActionPolicy) -> Void
        ) {
            guard let url = navigationAction.request.url else {
                decisionHandler(.cancel)
                return
            }

            // WKWebView begins with its own about:blank document before the
            // requested portal page. It is not an external navigation.
            if url.scheme?.lowercased() == "about" {
                decisionHandler(.allow)
                return
            }

            let isPortalURL = url.scheme?.lowercased() == "https" && url.host == portalHost
            if isPortalURL && url.path == "/mobile/agent/clients/create-complete" {
                decisionHandler(.cancel)
                if didSubmitClientCreation {
                    onCreated()
                } else {
                    onCancelled()
                }
                return
            }

            if navigationAction.navigationType == .formSubmitted &&
                (url.path == "/Clients/Create" || url.path == "/mobile/agent/clients/create") {
                didSubmitClientCreation = true
            }

            guard isPortalURL else {
                decisionHandler(.cancel)
                return
            }

            decisionHandler(.allow)
        }

        func webView(
            _ webView: WKWebView,
            decidePolicyFor navigationResponse: WKNavigationResponse,
            decisionHandler: @escaping (WKNavigationResponsePolicy) -> Void
        ) {
            guard navigationResponse.isForMainFrame,
                  let response = navigationResponse.response as? HTTPURLResponse else {
                decisionHandler(.allow)
                return
            }

            switch response.statusCode {
            case 401, 403:
                decisionHandler(.cancel)
                onPortalSessionExpired()
            case 500...599:
                decisionHandler(.cancel)
                onFailure("The client intake could not be opened. Please try again.")
            default:
                decisionHandler(.allow)
            }
        }

        func webView(
            _ webView: WKWebView,
            didFail navigation: WKNavigation!,
            withError error: Error
        ) {
            reportFailure(error)
        }

        func webView(
            _ webView: WKWebView,
            didFailProvisionalNavigation navigation: WKNavigation!,
            withError error: Error
        ) {
            reportFailure(error)
        }

        func webView(_ webView: WKWebView, didFinish navigation: WKNavigation!) {
            guard webView.url?.host == portalHost,
                  webView.url?.path == "/mobile/agent/clients/create" else {
                return
            }

            onPortalLoaded()
        }

        private func reportFailure(_ error: Error) {
            let nsError = error as NSError
            guard nsError.code != NSURLErrorCancelled else { return }
            onFailure(error.localizedDescription)
        }
    }
}
