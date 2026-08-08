import SwiftUI
import WebKit

/// Hosts the existing AgentPortal client-creation Razor view in the native app.
/// No SwiftUI fields are defined here: the portal is the single owner of the
/// form, its styling, validation, and the CRM provisioning workflow.
struct LegendAgentClientCreationPortalView: View {
    @ObservedObject private var store: MobileAgentWorkspaceStore
    let onCreated: () -> Void

    @Environment(\.dismiss) private var dismiss
    @State private var state: LoadState = .loading
    @State private var hasRequestedLaunch = false

    init(store: MobileAgentWorkspaceStore, onCreated: @escaping () -> Void) {
        _store = ObservedObject(wrappedValue: store)
        self.onCreated = onCreated
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
                    onCreated: onCreated,
                    onCancelled: { dismiss() },
                    onFailure: { message in state = .failed(message) })
                    .ignoresSafeArea()
            case .failed(let message):
                LegendNextErrorState(
                    title: "Client intake unavailable",
                    message: message,
                    retryTitle: "Retry",
                    retry: { Task { await requestLaunch() } })
                    .padding(LegendNextSpacing.sm)
            }
        }
        .background(LegendNextCanvas())
        .task { await requestLaunch() }
    }

    private func requestLaunch() async {
        guard !hasRequestedLaunch else { return }
        hasRequestedLaunch = true
        state = .loading

        do {
            state = .ready(try await store.clientCreationPortalLaunch())
        } catch {
            state = .failed(error.localizedDescription)
            hasRequestedLaunch = false
        }
    }

    private enum LoadState {
        case loading
        case ready(URL)
        case failed(String)
    }
}

private struct LegendAgentClientCreationPortalWebView: UIViewRepresentable {
    let launchURL: URL
    let onCreated: () -> Void
    let onCancelled: () -> Void
    let onFailure: (String) -> Void

    func makeCoordinator() -> Coordinator {
        Coordinator(
            portalHost: launchURL.host,
            onCreated: onCreated,
            onCancelled: onCancelled,
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
        private let onFailure: (String) -> Void
        private var didSubmitClientCreation = false

        init(
            portalHost: String?,
            onCreated: @escaping () -> Void,
            onCancelled: @escaping () -> Void,
            onFailure: @escaping (String) -> Void
        ) {
            self.portalHost = portalHost
            self.onCreated = onCreated
            self.onCancelled = onCancelled
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

        private func reportFailure(_ error: Error) {
            let nsError = error as NSError
            guard nsError.code != NSURLErrorCancelled else { return }
            onFailure(error.localizedDescription)
        }
    }
}
