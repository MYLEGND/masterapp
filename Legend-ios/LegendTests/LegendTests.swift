import XCTest
@testable import Legend

final class LegendTests: XCTestCase {
    func testApplicationTargetLoads() {
        XCTAssertTrue(true)
    }

    func testDailyActivityProjectionKeepsTodayAndPastDueSeparate() throws {
        var calendar = Calendar(identifier: .gregorian)
        calendar.timeZone = try XCTUnwrap(TimeZone(secondsFromGMT: 0))

        func date(_ day: Int, _ hour: Int = 12) -> Date {
            calendar.date(from: DateComponents(
                year: 2026,
                month: 8,
                day: day,
                hour: hour))!
        }

        let identity = try LogicalParticipantIdentity(
            userID: "activity-user",
            participantType: .agent)
        let today = date(2)
        let home = MobileHomeResponse(
            identity: MobileHomeIdentity(
                userID: identity.userID,
                participantType: .agent,
                profileID: UUID(),
                displayName: "Activity User"),
            messaging: MobileMessagingSummary(unreadCount: 3, conversationCount: 3),
            journey: nil,
            upcomingAppointments: [
                MobileUpcomingAppointment(
                    id: UUID(), startUTC: date(2, 14), endUTC: nil, status: "Confirmed"),
                MobileUpcomingAppointment(
                    id: UUID(), startUTC: date(3, 9), endUTC: nil, status: "Scheduled")
            ],
            actions: [
                MobileActionItem(
                    id: UUID(), title: "Review policy", status: "Open", priority: "High", dueDateUTC: date(2, 10)),
                MobileActionItem(
                    id: UUID(), title: "Past due task", status: "Open", priority: "High", dueDateUTC: date(1, 10))
            ],
            dailyScripture: MobileDailyScripture(
                date: "2026-08-02", reference: "Psalm 23", translation: "KJV", verses: [], text: ""),
            activeClientCount: 0)

        let projection = LegendDailyActivityProjection.make(
            home: home,
            social: nil,
            accountNotifications: [
                MobileActivityNotification(
                    id: UUID(), kind: "ControlledResourceApproved", title: "Request approved", detail: "Translation enabled", occurredUTC: date(2, 9), controlledResourceRequestID: nil),
                MobileActivityNotification(
                    id: UUID(), kind: "ControlledResourceApproved", title: "Yesterday's update", detail: "Not today", occurredUTC: date(1, 9), controlledResourceRequestID: nil)
            ],
            plannerItems: [
                LegendApplePlannerItem(
                    id: "calendar-today", source: .calendar, title: "Prayer meeting", detail: "Personal", occursAt: date(2, 18), isPastDue: false, reminderIdentifier: nil),
                LegendApplePlannerItem(
                    id: "reminder-overdue", source: .reminder, title: "Call client", detail: "Reminders", occursAt: date(1, 9), isPastDue: true, reminderIdentifier: "native-reminder"),
                LegendApplePlannerItem(
                    id: "calendar-future", source: .calendar, title: "Tomorrow", detail: "Personal", occursAt: date(3, 9), isPastDue: false, reminderIdentifier: nil)
            ],
            now: today,
            calendar: calendar)

        XCTAssertEqual(
            Set(projection.today.map(\.source)),
            [.account, .action, .appointment, .calendar])
        XCTAssertEqual(
            Set(projection.pastDue.map(\.source)),
            [.action, .reminder])
        XCTAssertFalse(projection.today.contains { $0.title == "Yesterday's update" })
        XCTAssertFalse(projection.today.contains { $0.title == "Tomorrow" })
    }
}
