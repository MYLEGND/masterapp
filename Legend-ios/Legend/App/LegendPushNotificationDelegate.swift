import Combine
import Foundation
import UIKit
import UserNotifications

/// The backend's two APNs routes. This mapping uses the single
/// `APS_ENVIRONMENT` build setting that also expands the app entitlement.
enum LegendAPNSEnvironment: String, Equatable, Sendable {
    case sandbox
    case production

    static func fromSignedEntitlement(_ value: String?) -> Self? {
        switch value?.trimmingCharacters(in: .whitespacesAndNewlines).lowercased() {
        case "development": .sandbox
        case "production": .production
        default: nil
        }
    }

    var displayName: String {
        switch self {
        case .sandbox: LegendLocalized("Development")
        case .production: LegendLocalized("Production")
        }
    }
}

enum LegendAPNSRegistrationState: String, Equatable, Sendable {
    case waiting
    case registered
    case failed

    var displayName: String {
        LegendLocalized(rawValue.capitalized)
    }
}

/// Captures only the opaque APNs device token. The authenticated notification
/// store registers it against the current server actor after sign-in; no token
/// is ever associated with an anonymous or stale account session.
final class LegendPushNotificationDelegate: NSObject, UIApplicationDelegate, UNUserNotificationCenterDelegate, ObservableObject {
    @Published private(set) var deviceToken: String?
    @Published private(set) var registrationState: LegendAPNSRegistrationState = .waiting
    @Published private(set) var signedEnvironment: LegendAPNSEnvironment?
    @Published private(set) var authorizationStatus: UNAuthorizationStatus = .notDetermined

    func application(
        _ application: UIApplication,
        didFinishLaunchingWithOptions launchOptions: [UIApplication.LaunchOptionsKey: Any]? = nil
    ) -> Bool {
        UNUserNotificationCenter.current().delegate = self
        signedEnvironment = Self.signedEnvironment()
        refreshNotificationAuthorizationStatus()
        return true
    }

    func application(
        _ application: UIApplication,
        didRegisterForRemoteNotificationsWithDeviceToken deviceToken: Data
    ) {
        self.deviceToken = deviceToken.map { String(format: "%02x", $0) }.joined()
        registrationState = .registered
    }

    func application(
        _ application: UIApplication,
        didFailToRegisterForRemoteNotificationsWithError error: Error
    ) {
        // Registration is optional. The app remains fully usable and will retry
        // on the next activation after system settings change.
        deviceToken = nil
        registrationState = .failed
    }

    /// Reads the system-owned authorization state instead of inferring it from
    /// a prior prompt. This is used by the production-safe Settings diagnostic.
    func refreshNotificationAuthorizationStatus() {
        UNUserNotificationCenter.current().getNotificationSettings { [weak self] settings in
            DispatchQueue.main.async {
                self?.authorizationStatus = settings.authorizationStatus
            }
        }
    }

    func userNotificationCenter(
        _ center: UNUserNotificationCenter,
        willPresent notification: UNNotification,
        withCompletionHandler completionHandler: @escaping (UNNotificationPresentationOptions) -> Void
    ) {
        completionHandler([.banner, .list, .sound, .badge])
    }

    private static func signedEnvironment() -> LegendAPNSEnvironment? {
        // SecTaskCopyValueForEntitlement is unavailable on iOS. This Info.plist
        // value is code-signed with the app and is expanded from the same single
        // APS_ENVIRONMENT setting as Legend.entitlements, so runtime code cannot
        // independently choose a different APNs route.
        LegendAPNSEnvironment.fromSignedEntitlement(
            Bundle.main.object(forInfoDictionaryKey: "LegendAPNSEnvironment") as? String)
    }
}
