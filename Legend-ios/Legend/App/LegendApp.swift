import SwiftUI

@main
struct LegendApp: App {
    @StateObject private var diagnostics: LegendDiagnostics
    @StateObject private var session: MobileSessionCoordinator
    @StateObject private var scrollChrome: LegendScrollChrome
    @StateObject private var localization: LegendApplicationLocalization
    @UIApplicationDelegateAdaptor(LegendPushNotificationDelegate.self) private var pushNotifications

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
        _localization = StateObject(wrappedValue: LegendApplicationLocalization())
    }

    var body: some Scene {
        WindowGroup {
            RootView()
                .environmentObject(session)
                .environmentObject(diagnostics)
                .environmentObject(scrollChrome)
                .environmentObject(localization)
                .environmentObject(pushNotifications)
                .scrollIndicators(.hidden)
                .task {
                    NativeUnreadBadge.prepare()
                }
        }
    }
}
