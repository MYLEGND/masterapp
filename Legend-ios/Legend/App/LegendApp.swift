import SwiftUI

@main
struct LegendApp: App {
    @StateObject private var diagnostics: LegendDiagnostics
    @StateObject private var session: MobileSessionCoordinator
    @StateObject private var scrollChrome: LegendScrollChrome

    init() {
        let diagnostics = LegendDiagnostics()

        _diagnostics = StateObject(
            wrappedValue: diagnostics
        )

        _session = StateObject(
            wrappedValue: MobileSessionCoordinator(
                diagnostics: diagnostics
            )
        )
        _scrollChrome = StateObject(wrappedValue: LegendScrollChrome())
    }

    var body: some Scene {
        WindowGroup {
            RootView()
                .environmentObject(session)
                .environmentObject(diagnostics)
                .environmentObject(scrollChrome)
                .scrollIndicators(.hidden)
                .task {
                    NativeUnreadBadge.prepare()
                }
        }
    }
}
