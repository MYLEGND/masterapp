import Foundation
import UserNotifications

/// A private, device-owned alert prepared by LEGEND for an item created in
/// Today's Activity. It intentionally contains no EventKit type, calendar
/// name, account information, or server data.
struct LegendTodayActivityNotificationPlan: Equatable, Sendable {
    let identifier: String
    let title: String
    let subtitle: String
    let body: String
    let fireDate: Date
    let repeatRule: LegendPlannerRepeat

    static func make(
        itemIdentifier: String,
        kind: LegendPlannerEntryKind,
        entryTitle: String,
        scheduledFor entryDate: Date?,
        alertSchedule: LegendPlannerAlertSchedule,
        repeatRule: LegendPlannerRepeat,
        now: Date = Date()
    ) -> Self? {
        let fireDate: Date
        switch alertSchedule {
        case .none:
            return nil
        case .absolute(let date):
            fireDate = date
        case .relative(let offset):
            guard let entryDate else { return nil }
            fireDate = entryDate.addingTimeInterval(offset)
        }

        // A new event that starts within its alert window should still notify
        // immediately instead of silently losing the alert to a past trigger.
        let resolvedFireDate = fireDate > now
            ? fireDate
            : now.addingTimeInterval(1)
        let normalizedTitle = entryTitle.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !itemIdentifier.isEmpty, !normalizedTitle.isEmpty else { return nil }

        return Self(
            identifier: "legend.today.\(kind.rawValue).\(itemIdentifier)",
            title: LegendLocalized("LEGEND®"),
            subtitle: kind == .reminder ? LegendLocalized("Reminder") : LegendLocalized("Event"),
            body: normalizedTitle,
            fireDate: resolvedFireDate,
            repeatRule: repeatRule)
    }

    func trigger(calendar: Calendar = .autoupdatingCurrent) -> UNCalendarNotificationTrigger {
        let components: DateComponents
        switch repeatRule {
        case .never:
            components = calendar.dateComponents(
                [.calendar, .timeZone, .year, .month, .day, .hour, .minute, .second],
                from: fireDate)
        case .daily:
            components = calendar.dateComponents(
                [.calendar, .timeZone, .hour, .minute],
                from: fireDate)
        case .weekly:
            components = calendar.dateComponents(
                [.calendar, .timeZone, .weekday, .hour, .minute],
                from: fireDate)
        case .monthly:
            components = calendar.dateComponents(
                [.calendar, .timeZone, .day, .hour, .minute],
                from: fireDate)
        case .yearly:
            components = calendar.dateComponents(
                [.calendar, .timeZone, .month, .day, .hour, .minute],
                from: fireDate)
        }

        return UNCalendarNotificationTrigger(
            dateMatching: components,
            repeats: repeatRule != .never)
    }
}

@MainActor
protocol LegendTodayActivityNotificationScheduling {
    func verifyAuthorization() async throws
    func schedule(_ plan: LegendTodayActivityNotificationPlan) async throws
    func cancel(kind: LegendPlannerEntryKind, itemIdentifier: String)
}

/// The sole alert authority for Today’s Activity. Local notifications are
/// delivered by iOS while LEGEND is backgrounded or terminated and display the
/// signed LEGEND® app name and app icon rather than Apple Calendar or Reminders.
@MainActor
final class LegendTodayActivityNotificationScheduler: LegendTodayActivityNotificationScheduling {
    private let center: UNUserNotificationCenter
    private let calendar: Calendar

    init(
        center: UNUserNotificationCenter = .current(),
        calendar: Calendar = .autoupdatingCurrent
    ) {
        self.center = center
        self.calendar = calendar
    }

    func verifyAuthorization() async throws {
        var settings = await center.notificationSettings()
        if settings.authorizationStatus == .notDetermined {
            _ = try await center.requestAuthorization(options: [.alert, .badge, .sound])
            settings = await center.notificationSettings()
        }

        guard settings.authorizationStatus == .authorized,
              settings.alertSetting == .enabled else {
            throw LegendTodayActivityNotificationError.authorizationRequired
        }
    }

    func schedule(_ plan: LegendTodayActivityNotificationPlan) async throws {
        try await verifyAuthorization()
        center.removePendingNotificationRequests(withIdentifiers: [plan.identifier])

        let content = UNMutableNotificationContent()
        content.title = plan.title
        content.subtitle = plan.subtitle
        content.body = plan.body
        content.sound = .default
        content.categoryIdentifier = "LEGEND_TODAY_ACTIVITY"
        content.userInfo = [
            "legendTodayActivity": true,
            "legendPlannerNotificationID": plan.identifier
        ]

        try await center.add(UNNotificationRequest(
            identifier: plan.identifier,
            content: content,
            trigger: plan.trigger(calendar: calendar)))
    }

    func cancel(kind: LegendPlannerEntryKind, itemIdentifier: String) {
        center.removePendingNotificationRequests(withIdentifiers: [
            Self.identifier(kind: kind, itemIdentifier: itemIdentifier)
        ])
    }

    private static func identifier(
        kind: LegendPlannerEntryKind,
        itemIdentifier: String
    ) -> String {
        "legend.today.\(kind.rawValue).\(itemIdentifier)"
    }
}

enum LegendTodayActivityNotificationError: LocalizedError {
    case authorizationRequired

    var errorDescription: String? {
        switch self {
        case .authorizationRequired:
            return LegendLocalized("Allow LEGEND notifications in Settings to schedule this alert.")
        }
    }
}
