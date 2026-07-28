import SwiftUI

@main
struct LegendApp: App {
    @StateObject private var diagnostics: LegendDiagnostics
    @StateObject private var session: MobileSessionCoordinator

    init() {
        let diagnostics = LegendDiagnostics()
        _diagnostics = StateObject(wrappedValue: diagnostics)
        _session = StateObject(
            wrappedValue: MobileSessionCoordinator(
                diagnostics: diagnostics
            )
        )
    }

    var body: some Scene {
        WindowGroup {
            RootView()
                .environmentObject(session)
                .environmentObject(diagnostics)
                .task {
                    NativeUnreadBadge.prepare()
                }
        }
    }
}
