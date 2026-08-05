import UIKit
import UserNotifications

/// The app icon has one writer: a response from the database-backed
/// notification engine (or its server-sent snapshot). Feature stores never
/// calculate a badge from their own local projections.
@MainActor
enum NativeUnreadBadge {
    static func prepare() {
        UNUserNotificationCenter.current().requestAuthorization(
            options: [.alert, .badge, .sound]
        ) { _, _ in
            // APNs registration is independent from alert permission. Register
            // on every launch so token rotation is reported even when the user
            // has denied visible notifications or changes Settings later.
            DispatchQueue.main.async {
                UIApplication.shared.registerForRemoteNotifications()
            }
        }
    }

    static func update(with unreadCount: Int) {
        let count = max(0, unreadCount)
        Task {
            try? await UNUserNotificationCenter.current().setBadgeCount(count)
        }
    }

    static func clear() {
        update(with: 0)
    }
}
