import UIKit
import UserNotifications

/// The app-icon badge is always derived from the protected mobile messaging
/// projection. It never attempts to infer a count from local state.
@MainActor
enum NativeUnreadBadge {
    static func prepare() {
        UNUserNotificationCenter.current().requestAuthorization(options: [.badge]) { _, _ in }
    }

    static func update(with unreadCount: Int) {
        let count = max(0, unreadCount)
        UIApplication.shared.applicationIconBadgeNumber = count
        Task {
            try? await UNUserNotificationCenter.current().setBadgeCount(count)
        }
    }

    static func clear() {
        update(with: 0)
    }
}
