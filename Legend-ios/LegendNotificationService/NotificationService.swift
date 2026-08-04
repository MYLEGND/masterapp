import UserNotifications

/// Runs immediately before an APNs alert is presented. The backend has already
/// reconciled its database ledger just before publishing this payload, so the
/// extension only applies that exact server value while preserving the alert
/// that APNs authenticated and delivered.
final class NotificationService: UNNotificationServiceExtension {
    private var contentHandler: ((UNNotificationContent) -> Void)?
    private var bestAttemptContent: UNMutableNotificationContent?

    override func didReceive(
        _ request: UNNotificationRequest,
        withContentHandler contentHandler: @escaping (UNNotificationContent) -> Void
    ) {
        self.contentHandler = contentHandler
        guard let content = request.content.mutableCopy() as? UNMutableNotificationContent else {
            contentHandler(request.content)
            return
        }

        if let unreadCount = request.content.userInfo["unreadCount"] as? NSNumber {
            content.badge = NSNumber(value: max(0, unreadCount.intValue))
        }
        bestAttemptContent = content
        contentHandler(content)
    }

    override func serviceExtensionTimeWillExpire() {
        if let contentHandler, let bestAttemptContent {
            contentHandler(bestAttemptContent)
        }
    }
}
