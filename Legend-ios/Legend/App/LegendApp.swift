import SwiftUI

@main
struct LegendApp: App {
    @StateObject private var diagnostics: LegendDiagnostics
    @StateObject private var session: MobileSessionCoordinator
    @State private var isPresentingLaunch = true

    init() {
        let diagnostics = LegendDiagnostics()
        _diagnostics = StateObject(wrappedValue: diagnostics)
        _session = StateObject(wrappedValue: MobileSessionCoordinator(diagnostics: diagnostics))
    }

    var body: some Scene {
        WindowGroup {
            ZStack {
                RootView()
                    .environmentObject(session)
                    .environmentObject(diagnostics)

                if isPresentingLaunch {
                    LegendLaunchView()
                        .transition(.opacity)
                        .zIndex(1)
                }
            }
            .task {
                NativeUnreadBadge.prepare()
                try? await Task.sleep(for: .milliseconds(550))
                withAnimation(LegendMotion.entrance) {
                    isPresentingLaunch = false
                }
            }
        }
    }
}
