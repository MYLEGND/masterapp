import XCTest
@testable import Legend

final class LegendTests: XCTestCase {
    func testApplicationTargetLoads() {
        XCTAssertTrue(true)
    }

    func testTodayActivityProjectionOnlyReturnsDevicePlannerItems() throws {
        var calendar = Calendar(identifier: .gregorian)
        calendar.timeZone = try XCTUnwrap(TimeZone(secondsFromGMT: 0))

        func date(_ day: Int, _ hour: Int = 12) -> Date {
            calendar.date(from: DateComponents(
                year: 2026,
                month: 8,
                day: day,
                hour: hour))!
        }

        let today = date(2)

        let projection = LegendDailyActivityProjection.make(
            plannerItems: [
                LegendDevicePlannerItem(
                    id: "calendar-today", source: .calendar, title: "Prayer meeting", detail: "Personal", occursAt: date(2, 18), isPastDue: false, reminderIdentifier: nil),
                LegendDevicePlannerItem(
                    id: "reminder-overdue", source: .reminder, title: "Call client", detail: "Reminders", occursAt: date(1, 9), isPastDue: true, reminderIdentifier: "native-reminder"),
                LegendDevicePlannerItem(
                    id: "calendar-future", source: .calendar, title: "Tomorrow", detail: "Personal", occursAt: date(3, 9), isPastDue: false, reminderIdentifier: nil)
            ],
            now: today,
            calendar: calendar)

        XCTAssertEqual(
            Set(projection.today.map(\.source)),
            [.calendar])
        XCTAssertEqual(
            Set(projection.pastDue.map(\.source)),
            [.reminder])
        XCTAssertFalse(projection.today.contains { $0.title == "Tomorrow" })
    }

    func testInAppNotificationProjectionKeepsAllNotificationDatesOutOfTodayPlanner() throws {
        var calendar = Calendar(identifier: .gregorian)
        calendar.timeZone = try XCTUnwrap(TimeZone(secondsFromGMT: 0))
        let yesterday = calendar.date(from: DateComponents(
            year: 2026,
            month: 8,
            day: 1,
            hour: 9))!

        let notifications = LegendInAppNotificationProjection.make(
            social: nil,
            accountNotifications: [
                MobileActivityNotification(
                    id: UUID(),
                    kind: "ControlledResourceApproved",
                    title: "Request approved",
                    detail: "Translation enabled",
                    occurredUTC: yesterday,
                    controlledResourceRequestID: nil)
            ])

        XCTAssertEqual(notifications.map(\.source), [.account])
        XCTAssertEqual(notifications.map(\.title), ["Request approved"])
    }

    func testPlannerAlertPolicyUsesOneNativeAppleAlertWithoutAnAPNsMirror() throws {
        var calendar = Calendar(identifier: .gregorian)
        calendar.timeZone = try XCTUnwrap(TimeZone(secondsFromGMT: 0))
        let date = try XCTUnwrap(calendar.date(from: DateComponents(
            year: 2026,
            month: 8,
            day: 8,
            hour: 14)))

        XCTAssertEqual(
            LegendPlannerAlertPolicy.schedule(
                for: .reminder,
                scheduledFor: date,
                isAllDay: false,
                alertsEnabled: true,
                calendar: calendar),
            .absolute(date))
        XCTAssertEqual(
            LegendPlannerAlertPolicy.schedule(
                for: .event,
                scheduledFor: date,
                isAllDay: false,
                alertsEnabled: true,
                calendar: calendar),
            .relative(-15 * 60))
        XCTAssertEqual(
            LegendPlannerAlertPolicy.schedule(
                for: .event,
                scheduledFor: date,
                isAllDay: true,
                alertsEnabled: true,
                calendar: calendar),
            .absolute(try XCTUnwrap(calendar.date(
                byAdding: .hour,
                value: 9,
                to: calendar.startOfDay(for: date)))))
        XCTAssertEqual(
            LegendPlannerAlertPolicy.schedule(
                for: .reminder,
                scheduledFor: date,
                isAllDay: false,
                alertsEnabled: false,
                calendar: calendar),
            .none)
    }
}
