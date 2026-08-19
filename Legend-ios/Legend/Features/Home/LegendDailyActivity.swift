import Combine
import EventKit
import SwiftUI
import UIKit

/// Activity intentionally has two destinations: in-app notifications and the
/// member's private device planner. They share row presentation only; their
/// data never mixes into one feed.
enum LegendDailyActivitySource: String, CaseIterable, Hashable, Sendable {
    case account
    case network
    case calendar
    case reminder

    var title: String {
        switch self {
        case .account: return "Account"
        case .network: return "Network"
        case .calendar: return "Calendar"
        case .reminder: return "Reminders"
        }
    }

    var systemImage: String {
        switch self {
        case .account: return "checkmark.seal.fill"
        case .network: return "heart.fill"
        case .calendar: return "calendar"
        case .reminder: return "checklist"
        }
    }

    var tone: LegendNextTone {
        switch self {
        case .account: return .information
        case .network: return .gold
        case .calendar: return .information
        case .reminder: return .success
        }
    }
}

struct LegendDailyActivityItem: Identifiable, Equatable, Sendable {
    let id: String
    let source: LegendDailyActivitySource
    let title: String
    let detail: String
    let occurredAt: Date
    let isCompletable: Bool
    let isPastDue: Bool
    let nativeReminderIdentifier: String?
    let actor: MobileSocialAuthor?
    let sourcePostID: UUID?

    init(
        id: String,
        source: LegendDailyActivitySource,
        title: String,
        detail: String,
        occurredAt: Date,
        isCompletable: Bool = false,
        isPastDue: Bool = false,
        nativeReminderIdentifier: String? = nil,
        actor: MobileSocialAuthor? = nil,
        sourcePostID: UUID? = nil
    ) {
        self.id = id
        self.source = source
        self.title = title
        self.detail = detail
        self.occurredAt = occurredAt
        self.isCompletable = isCompletable
        self.isPastDue = isPastDue
        self.nativeReminderIdentifier = nativeReminderIdentifier
        self.actor = actor
        self.sourcePostID = sourcePostID
    }
}

struct LegendDailyActivityCategoryCount: Identifiable, Equatable, Sendable {
    let source: LegendDailyActivitySource
    let count: Int

    var id: LegendDailyActivitySource { source }
}

/// The native device-planner payload is intentionally small and value-based.
/// Nothing from a device calendar or reminders service is posted to Legend's
/// backend. On iOS, EventKit exposes every account the member has enabled on
/// the phone, including iCloud, Google, Outlook, and Exchange calendars.
struct LegendDevicePlannerItem: Identifiable, Equatable, Sendable {
    let id: String
    let source: LegendDailyActivitySource
    let title: String
    let detail: String
    let occursAt: Date
    let isPastDue: Bool
    let reminderIdentifier: String?
}

enum LegendDevicePlannerAuthorization: Equatable {
    case notDetermined
    case authorized
    case denied
    case restricted

    var isAuthorized: Bool { self == .authorized }

    var statusTitle: String {
        switch self {
        case .notDetermined: return "Not connected"
        case .authorized: return "Connected"
        case .denied: return "Access denied"
        case .restricted: return "Unavailable"
        }
    }
}

/// These capabilities intentionally describe the user-facing planner model,
/// rather than an operating-system vendor. Each native client maps them to its
/// device provider while Activity keeps one source of truth.
enum LegendDevicePlannerCapability: String, CaseIterable, Hashable, Sendable {
    case calendar
    case reminders

    var title: String {
        switch self {
        case .calendar: return "Calendar"
        case .reminders: return "Reminders"
        }
    }

    var activitySource: LegendDailyActivitySource {
        switch self {
        case .calendar: return .calendar
        case .reminders: return .reminder
        }
    }
}

/// A value-only representation of a device calendar or reminders list. It
/// prevents EventKit objects from leaking outside the planner authority.
struct LegendDevicePlannerCalendar: Identifiable, Equatable, Sendable {
    let id: String
    let title: String
}

enum LegendPlannerEntryKind: String, CaseIterable, Identifiable, Sendable {
    case reminder
    case event

    var id: String { rawValue }

    var title: String {
        switch self {
        case .reminder: return "Reminder"
        case .event: return "Event"
        }
    }

    var capability: LegendDevicePlannerCapability {
        switch self {
        case .reminder: return .reminders
        case .event: return .calendar
        }
    }
}

enum LegendPlannerRepeat: String, CaseIterable, Identifiable, Sendable {
    case never
    case daily
    case weekly
    case monthly
    case yearly

    var id: String { rawValue }

    var title: String { rawValue.capitalized }

    var eventKitRule: EKRecurrenceRule? {
        let frequency: EKRecurrenceFrequency
        switch self {
        case .never: return nil
        case .daily: frequency = .daily
        case .weekly: frequency = .weekly
        case .monthly: frequency = .monthly
        case .yearly: frequency = .yearly
        }
        return EKRecurrenceRule(recurrenceWith: frequency, interval: 1, end: nil)
    }
}

struct LegendPlannerEntryDraft: Sendable {
    let kind: LegendPlannerEntryKind
    let title: String
    let notes: String
    let startDate: Date?
    let endDate: Date?
    let isAllDay: Bool
    let alertsEnabled: Bool
    let priority: Int
    let repeatRule: LegendPlannerRepeat
    let calendarIdentifier: String?
}

/// Today’s Activity writes private planner items directly to EventKit. This
/// policy determines the single LEGEND-owned local alert for each enabled item;
/// EventKit stores the planner record but never owns its lock-screen alert.
enum LegendPlannerAlertSchedule: Equatable, Sendable {
    case none
    case absolute(Date)
    case relative(TimeInterval)
}

enum LegendPlannerAlertPolicy {
    static func schedule(
        for kind: LegendPlannerEntryKind,
        scheduledFor date: Date?,
        isAllDay: Bool,
        alertsEnabled: Bool,
        calendar: Calendar = .autoupdatingCurrent
    ) -> LegendPlannerAlertSchedule {
        guard alertsEnabled, let date else {
            return .none
        }

        switch kind {
        case .reminder:
            return .absolute(date)

        case .event where isAllDay:
            let dayStart = calendar.startOfDay(for: date)
            let morning = calendar.date(byAdding: .hour, value: 9, to: dayStart) ?? dayStart
            return .absolute(morning)

        case .event:
            return .relative(-15 * 60)
        }
    }

}

/// Owns the device-planner access boundary. The system permission and the
/// member's in-app connection are deliberately separate: system access may
/// remain granted while a member disconnects Activity at any time.
@MainActor
final class LegendDevicePlannerStore: ObservableObject {
    @Published private(set) var calendarAuthorization: LegendDevicePlannerAuthorization
    @Published private(set) var remindersAuthorization: LegendDevicePlannerAuthorization
    @Published private(set) var isCalendarConnected: Bool
    @Published private(set) var isRemindersConnected: Bool
    @Published private(set) var items: [LegendDevicePlannerItem] = []
    @Published private(set) var failureMessage: String?

    private let eventStore: EKEventStore
    private let calendar: Calendar
    private let storageScope: String
    private let defaults: UserDefaults
    private let notificationScheduler: any LegendTodayActivityNotificationScheduling

    init(
        eventStore: EKEventStore = EKEventStore(),
        calendar: Calendar = .autoupdatingCurrent,
        storageScope: String = "default",
        defaults: UserDefaults = .standard,
        notificationScheduler: (any LegendTodayActivityNotificationScheduling)? = nil
    ) {
        let initialCalendarAuthorization = Self.authorization(for: .event)
        let initialRemindersAuthorization = Self.authorization(for: .reminder)

        self.eventStore = eventStore
        self.calendar = calendar
        self.storageScope = storageScope
        self.defaults = defaults
        self.notificationScheduler = notificationScheduler ?? LegendTodayActivityNotificationScheduler()

        self.calendarAuthorization = initialCalendarAuthorization
        self.remindersAuthorization = initialRemindersAuthorization

        self.isCalendarConnected = Self.initialConnectionState(
            for: .calendar,
            authorization: initialCalendarAuthorization,
            storageScope: storageScope,
            defaults: defaults
        )

        self.isRemindersConnected = Self.initialConnectionState(
            for: .reminders,
            authorization: initialRemindersAuthorization,
            storageScope: storageScope,
            defaults: defaults
        )
    }

    func authorization(for capability: LegendDevicePlannerCapability) -> LegendDevicePlannerAuthorization {
        switch capability {
        case .calendar: return calendarAuthorization
        case .reminders: return remindersAuthorization
        }
    }

    func isConnected(_ capability: LegendDevicePlannerCapability) -> Bool {
        switch capability {
        case .calendar: return isCalendarConnected
        case .reminders: return isRemindersConnected
        }
    }

    func connect(_ capability: LegendDevicePlannerCapability) async {
        failureMessage = nil
        refreshAuthorizations()
        let currentAuthorization = authorization(for: capability)

        switch currentAuthorization {
        case .authorized:
            setConnection(true, for: capability)
            await refresh()

        case .notDetermined:
            await requestSystemAccess(for: capability)
            refreshAuthorizations()
            guard authorization(for: capability).isAuthorized else {
                failureMessage = "Allow \(capability.title) in your device settings, then connect it to Activity."
                return
            }
            setConnection(true, for: capability)
            await refresh()

        case .denied:
            failureMessage = "Allow \(capability.title) in your device settings, then return to Activity to connect it."

        case .restricted:
            failureMessage = "\(capability.title) access is unavailable on this device."
        }
    }

    func disconnect(_ capability: LegendDevicePlannerCapability) {
        failureMessage = nil
        setConnection(false, for: capability)
        items.removeAll { $0.source == capability.activitySource }
    }

    func openDeviceSettings() {
        guard let settingsURL = URL(string: UIApplication.openSettingsURLString) else {
            return
        }
        UIApplication.shared.open(settingsURL)
    }

    func refresh() async {
        refreshAuthorizations()

        let range = todayRange
        var refreshed: [LegendDevicePlannerItem] = []

        if isCalendarConnected {
            let predicate = eventStore.predicateForEvents(
                withStart: range.start,
                end: range.end,
                calendars: nil)
            refreshed.append(contentsOf: eventStore.events(matching: predicate).map {
                event in
                LegendDevicePlannerItem(
                    id: "calendar:\(event.eventIdentifier ?? event.calendarItemIdentifier)",
                    source: .calendar,
                    title: sanitized(event.title, fallback: "Calendar event"),
                    detail: event.isAllDay
                        ? "All-day event · \(calendarDetail(for: event.calendar))"
                        : calendarDetail(for: event.calendar),
                    occursAt: event.startDate,
                    isPastDue: false,
                    reminderIdentifier: nil)
            })
        }

        if isRemindersConnected {
            let predicate = eventStore.predicateForIncompleteReminders(
                withDueDateStarting: nil,
                ending: range.end,
                calendars: nil)
            let reminders = await incompleteReminders(matching: predicate)
            refreshed.append(contentsOf: reminders.compactMap { reminder in
                guard let dueDate = dueDate(for: reminder) else { return nil }
                return LegendDevicePlannerItem(
                    id: "reminder:\(reminder.calendarItemIdentifier)",
                    source: .reminder,
                    title: sanitized(reminder.title, fallback: "Reminder"),
                    detail: calendarDetail(for: reminder.calendar),
                    occursAt: dueDate,
                    isPastDue: dueDate < range.start,
                    reminderIdentifier: reminder.calendarItemIdentifier)
            })
        }

        items = refreshed.sorted { $0.occursAt < $1.occursAt }
    }

    func calendars(
        for capability: LegendDevicePlannerCapability
    ) -> [LegendDevicePlannerCalendar] {
        let entityType: EKEntityType = capability == .calendar ? .event : .reminder
        guard authorization(for: capability).isAuthorized else { return [] }

        return eventStore.calendars(for: entityType)
            .map {
                LegendDevicePlannerCalendar(
                    id: $0.calendarIdentifier,
                    title: $0.title.trimmingCharacters(in: .whitespacesAndNewlines))
            }
            .filter { !$0.title.isEmpty }
            .sorted { $0.title.localizedCaseInsensitiveCompare($1.title) == .orderedAscending }
    }

    func create(_ draft: LegendPlannerEntryDraft) async throws {
        let title = sanitized(draft.title, fallback: "")
        guard !title.isEmpty else {
            throw LegendDevicePlannerError.titleRequired
        }

        let capability = draft.kind.capability
        if !isConnected(capability) {
            await connect(capability)
        }
        guard isConnected(capability) else {
            throw LegendDevicePlannerError.plannerDisconnected(capability)
        }

        let notes = draft.notes.trimmingCharacters(in: .whitespacesAndNewlines)
        let destinationCalendar = try resolvedCalendar(
            identifier: draft.calendarIdentifier,
            capability: capability)

        let alertSchedule = LegendPlannerAlertPolicy.schedule(
            for: draft.kind,
            scheduledFor: draft.startDate,
            isAllDay: draft.isAllDay,
            alertsEnabled: draft.alertsEnabled,
            calendar: calendar)
        if alertSchedule != .none {
            try await notificationScheduler.verifyAuthorization()
        }

        let savedItem: EKCalendarItem
        switch draft.kind {
        case .reminder:
            let reminder = EKReminder(eventStore: eventStore)
            reminder.calendar = destinationCalendar
            reminder.title = title
            reminder.notes = notes.isEmpty ? nil : notes
            reminder.priority = draft.priority

            if let startDate = draft.startDate {
                reminder.dueDateComponents = self.calendar.dateComponents(
                    [.calendar, .timeZone, .year, .month, .day, .hour, .minute],
                    from: startDate)
            }
            if let recurrenceRule = draft.repeatRule.eventKitRule {
                reminder.addRecurrenceRule(recurrenceRule)
            }
            try eventStore.save(reminder, commit: true)
            savedItem = reminder

        case .event:
            guard let startDate = draft.startDate else {
                throw LegendDevicePlannerError.eventStartRequired
            }
            let eventStartDate = draft.isAllDay
                ? calendar.startOfDay(for: startDate)
                : startDate
            let requestedEndDate = draft.endDate ?? startDate.addingTimeInterval(60 * 60)
            let eventEndDate: Date
            if draft.isAllDay {
                let dayAfterStart = calendar.date(
                    byAdding: .day,
                    value: 1,
                    to: eventStartDate) ?? eventStartDate.addingTimeInterval(24 * 60 * 60)
                eventEndDate = max(
                    calendar.startOfDay(for: requestedEndDate),
                    dayAfterStart)
            } else {
                eventEndDate = requestedEndDate
            }
            guard eventEndDate > eventStartDate else {
                throw LegendDevicePlannerError.eventEndMustFollowStart
            }

            let event = EKEvent(eventStore: eventStore)
            event.calendar = destinationCalendar
            event.title = title
            event.notes = notes.isEmpty ? nil : notes
            event.startDate = eventStartDate
            event.endDate = eventEndDate
            event.isAllDay = draft.isAllDay
            if let recurrenceRule = draft.repeatRule.eventKitRule {
                event.addRecurrenceRule(recurrenceRule)
            }
            try eventStore.save(event, span: .thisEvent, commit: true)
            savedItem = event
        }

        if let plan = LegendTodayActivityNotificationPlan.make(
            itemIdentifier: savedItem.calendarItemIdentifier,
            kind: draft.kind,
            entryTitle: title,
            scheduledFor: draft.startDate,
            alertSchedule: alertSchedule,
            repeatRule: draft.repeatRule) {
            do {
                try await notificationScheduler.schedule(plan)
            } catch {
                try? remove(savedItem, for: draft.kind)
                throw error
            }
        }

        await refresh()
    }

    func setReminder(
        identifier: String,
        completed: Bool
    ) throws {
        guard isRemindersConnected else {
            throw LegendDevicePlannerError.remindersDisconnected
        }
        guard let reminder = eventStore.calendarItem(
            withIdentifier: identifier) as? EKReminder else {
            throw LegendDevicePlannerError.reminderNotFound
        }

        reminder.isCompleted = completed
        try eventStore.save(reminder, commit: true)
        if completed {
            notificationScheduler.cancel(
                kind: .reminder,
                itemIdentifier: reminder.calendarItemIdentifier)
        }
    }

    func dismissFailure() {
        failureMessage = nil
    }

    private func requestSystemAccess(for capability: LegendDevicePlannerCapability) async {
        do {
            switch capability {
            case .calendar:
                _ = try await eventStore.requestFullAccessToEvents()
            case .reminders:
                _ = try await eventStore.requestFullAccessToReminders()
            }
        } catch {
            failureMessage = "Legend could not request \(capability.title) access. Please try again in device settings."
        }
    }

    private func refreshAuthorizations() {
        calendarAuthorization = Self.authorization(for: .event)
        remindersAuthorization = Self.authorization(for: .reminder)
        if !calendarAuthorization.isAuthorized {
            isCalendarConnected = false
        }
        if !remindersAuthorization.isAuthorized {
            isRemindersConnected = false
        }
    }

    private func setConnection(
        _ isConnected: Bool,
        for capability: LegendDevicePlannerCapability
    ) {
        defaults.set(isConnected, forKey: connectionKey(for: capability))
        switch capability {
        case .calendar:
            isCalendarConnected = isConnected && calendarAuthorization.isAuthorized
        case .reminders:
            isRemindersConnected = isConnected && remindersAuthorization.isAuthorized
        }
    }

    private var todayRange: DateInterval {
        let start = calendar.startOfDay(for: Date())
        let end = calendar.date(byAdding: .day, value: 1, to: start) ?? start
        return DateInterval(start: start, end: end)
    }

    private func dueDate(for reminder: EKReminder) -> Date? {
        guard let components = reminder.dueDateComponents else { return nil }
        return calendar.date(from: components)
    }

    private func incompleteReminders(
        matching predicate: NSPredicate
    ) async -> [EKReminder] {
        await withCheckedContinuation { continuation in
            eventStore.fetchReminders(matching: predicate) { reminders in
                continuation.resume(returning: reminders ?? [])
            }
        }
    }

    private func resolvedCalendar(
        identifier: String?,
        capability: LegendDevicePlannerCapability
    ) throws -> EKCalendar {
        if let identifier,
           let calendar = eventStore.calendar(withIdentifier: identifier) {
            return calendar
        }

        let defaultCalendar: EKCalendar?
        switch capability {
        case .calendar:
            defaultCalendar = eventStore.defaultCalendarForNewEvents
        case .reminders:
            defaultCalendar = eventStore.defaultCalendarForNewReminders()
        }

        guard let defaultCalendar else {
            throw LegendDevicePlannerError.defaultCalendarUnavailable(capability)
        }
        return defaultCalendar
    }

    private func remove(
        _ item: EKCalendarItem,
        for kind: LegendPlannerEntryKind
    ) throws {
        switch kind {
        case .reminder:
            guard let reminder = item as? EKReminder else { return }
            try eventStore.remove(reminder, commit: true)
        case .event:
            guard let event = item as? EKEvent else { return }
            try eventStore.remove(event, span: .thisEvent, commit: true)
        }
    }

    private static func authorization(
        for entityType: EKEntityType
    ) -> LegendDevicePlannerAuthorization {
        switch EKEventStore.authorizationStatus(for: entityType) {
        case .fullAccess, .authorized:
            return .authorized
        case .denied, .writeOnly:
            return .denied
        case .restricted:
            return .restricted
        case .notDetermined:
            return .notDetermined
        @unknown default:
            return .restricted
        }
    }

    private static func initialConnectionState(
        for capability: LegendDevicePlannerCapability,
        authorization: LegendDevicePlannerAuthorization,
        storageScope: String,
        defaults: UserDefaults
    ) -> Bool {
        let key = "legend.device-planner.connection.\(storageScope).\(capability.rawValue)"
        guard let storedValue = defaults.object(forKey: key) as? Bool else {
            // An existing system authorization means the member had already
            // opted in before this explicit disconnect control was introduced.
            return authorization.isAuthorized
        }
        return storedValue && authorization.isAuthorized
    }

    private func connectionKey(for capability: LegendDevicePlannerCapability) -> String {
        "legend.device-planner.connection.\(storageScope).\(capability.rawValue)"
    }

    private func calendarDetail(for calendar: EKCalendar) -> String {
        let source = calendar.source.title.trimmingCharacters(in: .whitespacesAndNewlines)
        let title = calendar.title.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !source.isEmpty,
              !title.isEmpty,
              source.caseInsensitiveCompare(title) != .orderedSame else {
            return title.isEmpty ? "Device planner" : title
        }
        return "\(source) · \(title)"
    }

    private func sanitized(_ value: String?, fallback: String) -> String {
        let trimmed = value?.trimmingCharacters(in: .whitespacesAndNewlines) ?? ""
        return trimmed.isEmpty ? fallback : trimmed
    }
}

private enum LegendDevicePlannerError: LocalizedError {
    case reminderNotFound
    case remindersDisconnected
    case plannerDisconnected(LegendDevicePlannerCapability)
    case titleRequired
    case eventStartRequired
    case eventEndMustFollowStart
    case defaultCalendarUnavailable(LegendDevicePlannerCapability)

    var errorDescription: String? {
        switch self {
        case .reminderNotFound:
            return "The device reminder is no longer available."
        case .remindersDisconnected:
            return "Connect device reminders before updating a reminder."
        case .plannerDisconnected(let capability):
            return "Connect \(capability.title) before saving this item."
        case .titleRequired:
            return "Add a title before saving."
        case .eventStartRequired:
            return "Choose when this event starts."
        case .eventEndMustFollowStart:
            return "The event must end after it starts."
        case .defaultCalendarUnavailable(let capability):
            return "Choose or create a \(capability.title.lowercased()) list on this device, then try again."
        }
    }
}

/// The Today destination is intentionally limited to the private device
/// planner. Social/account notifications have their own in-app destination.
enum LegendDailyActivityProjection {
    static func make(
        plannerItems: [LegendDevicePlannerItem],
        now: Date = Date(),
        calendar: Calendar = .autoupdatingCurrent
    ) -> (today: [LegendDailyActivityItem], pastDue: [LegendDailyActivityItem]) {
        let start = calendar.startOfDay(for: now)
        let end = calendar.date(byAdding: .day, value: 1, to: start) ?? start
        let todayRange = DateInterval(start: start, end: end)
        var today: [LegendDailyActivityItem] = []
        var pastDue: [LegendDailyActivityItem] = []

        for item in plannerItems {
            let activity = LegendDailyActivityItem(
                id: item.id,
                source: item.source,
                title: item.title,
                detail: item.detail,
                occurredAt: item.occursAt,
                isCompletable: item.source == .reminder,
                isPastDue: item.isPastDue,
                nativeReminderIdentifier: item.reminderIdentifier)
            if item.isPastDue {
                pastDue.append(activity)
            } else if todayRange.contains(item.occursAt) {
                today.append(activity)
            }
        }

        return (
            today: today.sorted(by: { $0.occurredAt < $1.occurredAt }),
            pastDue: pastDue.sorted(by: { $0.occurredAt < $1.occurredAt }))
    }
}

/// The heart opens this projection only. It is ordered newest-first and keeps
/// all in-app interaction kinds together without exposing device-planner data.
enum LegendInAppNotificationProjection {
    static func make(
        social: MobileSocialSnapshot?,
        accountNotifications: [MobileActivityNotification]
    ) -> [LegendDailyActivityItem] {
        var notifications = accountNotifications.map {
            LegendDailyActivityItem(
                id: "account:\($0.id.uuidString)",
                source: .account,
                title: $0.title,
                detail: $0.detail,
                occurredAt: $0.occurredUTC)
        }

        if let social {
            notifications.append(contentsOf: social.activity.map {
                LegendDailyActivityItem(
                    id: "network:\($0.id.uuidString)",
                    source: .network,
                    title: $0.actor.displayName,
                    detail: $0.summary,
                    occurredAt: $0.occurredUTC,
                    actor: $0.actor,
                    sourcePostID: $0.postID)
            })
        }

        return notifications.sorted { $0.occurredAt > $1.occurredAt }
    }
}

/// Shared, account-scoped Activity authority. It observes the existing protected
/// stores rather than maintaining duplicate copies of their data.
@MainActor
final class LegendDailyActivityStore: ObservableObject {
    @Published private(set) var today: [LegendDailyActivityItem] = []
    @Published private(set) var pastDue: [LegendDailyActivityItem] = []
    @Published private(set) var inAppNotifications: [LegendDailyActivityItem] = []
    @Published private(set) var unreadBadgeCount = 0
    @Published private(set) var categoryCounts: [LegendDailyActivityCategoryCount] = []
    @Published private(set) var completionFailure: String?

    let planner: LegendDevicePlannerStore

    private let identity: LogicalParticipantIdentity
    private let social: MobileSocialStore
    private let messages: MessagingStore
    private let calendar: Calendar
    private var cancellables = Set<AnyCancellable>()

    init(
        identity: LogicalParticipantIdentity,
        social: MobileSocialStore,
        messages: MessagingStore,
        planner: LegendDevicePlannerStore? = nil,
        calendar: Calendar = .autoupdatingCurrent
    ) {
        self.identity = identity
        self.social = social
        self.messages = messages
        self.planner = planner ?? LegendDevicePlannerStore(
            storageScope: "\(identity.participantType.rawValue).\(identity.userID)")
        self.calendar = calendar
        observeSources()
        rebuild()
    }

    var hasDevicePlannerConnection: Bool {
        planner.isConnected(.calendar) || planner.isConnected(.reminders)
    }

    func refreshDevicePlanner() async {
        await planner.refresh()
        rebuild()
    }

    func connectPlanner(_ capability: LegendDevicePlannerCapability) async {
        await planner.connect(capability)
        rebuild()
    }

    func disconnectPlanner(_ capability: LegendDevicePlannerCapability) {
        planner.disconnect(capability)
        rebuild()
    }

    func openDevicePlannerSettings() {
        planner.openDeviceSettings()
    }

    func markNotificationsViewed() {
        let identifiers = inAppNotifications.map(\.id)
        var seen = viewedIdentifiers
        seen.formUnion(identifiers)
        viewedIdentifiers = seen
        rebuild()
    }

    func createPlannerEntry(_ draft: LegendPlannerEntryDraft) async throws {
        try await planner.create(draft)
        rebuild()
    }

    func toggleCompletion(for item: LegendDailyActivityItem) {
        guard item.isCompletable else { return }
        let shouldComplete = !isCompleted(item)

        if let reminderIdentifier = item.nativeReminderIdentifier {
            do {
                try planner.setReminder(
                    identifier: reminderIdentifier,
                    completed: shouldComplete)
            } catch {
                completionFailure = error.localizedDescription
                return
            }
        }

        var completed = completedIdentifiers
        if shouldComplete {
            completed.insert(item.id)
        } else {
            completed.remove(item.id)
        }
        completedIdentifiers = completed
        rebuild()

        if item.nativeReminderIdentifier != nil {
            Task { [weak self] in
                await self?.refreshDevicePlanner()
            }
        }
    }

    func isCompleted(_ item: LegendDailyActivityItem) -> Bool {
        completedIdentifiers.contains(item.id)
    }

    var plannerFailure: String? {
        completionFailure ?? planner.failureMessage
    }

    func dismissPlannerFailure() {
        completionFailure = nil
        planner.dismissFailure()
    }

    /// Resolve Activity links from the same loaded social snapshot that created
    /// the item. The Activity layer does not create a second post or profile
    /// cache merely to support navigation.
    func post(for id: UUID) -> MobileSocialPost? {
        guard let snapshot = socialSnapshot else { return nil }
        return (snapshot.posts + snapshot.hacs + snapshot.stories)
            .first { $0.id == id }
    }

    func profileRoute(for author: MobileSocialAuthor) -> LegendPublicProfileRoute {
        let matchingPost = socialSnapshot.flatMap { snapshot in
            (snapshot.posts + snapshot.hacs + snapshot.stories)
                .first { $0.author.identity == author.identity }
        }
        return LegendPublicProfileRoute(
            profile: author,
            isFollowing: matchingPost?.followedByCurrentActor ?? false,
            isFollowRequestPending: matchingPost?.followRequestPending ?? false)
    }

    private func observeSources() {
        social.$state
            .sink { [weak self] _ in self?.rebuild() }
            .store(in: &cancellables)
        messages.$activityNotifications
            .sink { [weak self] _ in self?.rebuild() }
            .store(in: &cancellables)
        planner.$items
            .sink { [weak self] _ in self?.rebuild() }
            .store(in: &cancellables)
    }

    private func rebuild() {
        let plannerProjection = LegendDailyActivityProjection.make(
            plannerItems: planner.items,
            calendar: calendar)
        today = plannerProjection.today
        pastDue = plannerProjection.pastDue
        inAppNotifications = LegendInAppNotificationProjection.make(
            social: socialSnapshot,
            accountNotifications: messages.activityNotifications)

        let incompleteToday = today.filter { !isCompleted($0) }
        categoryCounts = [
            LegendDailyActivitySource.calendar,
            .reminder
        ].compactMap { source in
            let count = incompleteToday.count { $0.source == source }
            return count > 0
                ? LegendDailyActivityCategoryCount(source: source, count: count)
                : nil
        }

        unreadBadgeCount = inAppNotifications.count {
            !viewedIdentifiers.contains($0.id)
        }
    }

    private var socialSnapshot: MobileSocialSnapshot? {
        guard case .loaded(let snapshot) = social.state else { return nil }
        return snapshot
    }

    private var completedIdentifiers: Set<String> {
        get { Set(UserDefaults.standard.stringArray(forKey: completedKey) ?? []) }
        set { UserDefaults.standard.set(Array(newValue), forKey: completedKey) }
    }

    private var viewedIdentifiers: Set<String> {
        get { Set(UserDefaults.standard.stringArray(forKey: viewedKey) ?? []) }
        set { UserDefaults.standard.set(Array(newValue), forKey: viewedKey) }
    }

    private var completedKey: String {
        storageKey("completed")
    }

    private var viewedKey: String {
        storageKey("notifications-viewed")
    }

    private func storageKey(_ suffix: String) -> String {
        "legend.daily-activity.\(suffix).\(identity.participantType.rawValue).\(identity.userID)"
    }
}

struct LegendTodayActivitySummaryPill: View {
    @ObservedObject var activity: LegendDailyActivityStore
    let openActivity: () -> Void

    var body: some View {
        Button(action: openActivity) {
            HStack(spacing: LegendNextSpacing.sm) {
                Image(systemName: "checklist")
                    .font(.system(size: 17, weight: .bold))
                    .foregroundStyle(LegendNextColor.goldBright)
                    .frame(width: 40, height: 40)
                    .background(
                        LinearGradient(
                            colors: [
                                Color.white.opacity(0.15),
                                LegendNextColor.gold.opacity(0.10)
                            ],
                            startPoint: .topLeading,
                            endPoint: .bottomTrailing
                        ),
                        in: Circle()
                    )
                    .overlay {
                        Circle()
                            .stroke(
                                LegendNextColor.goldBright.opacity(0.18),
                                lineWidth: 1
                            )
                    }
                    .shadow(
                        color: LegendNextColor.midnight.opacity(0.20),
                        radius: 5,
                        y: 3
                    )

                VStack(alignment: .leading, spacing: 2) {
                    Text("TODAY'S ACTIVITY")
                        .font(LegendNextTypography.eyebrow)
                        .tracking(0.8)
                        .foregroundStyle(LegendNextColor.goldBright)

                    categorySummary
                }

                Spacer(minLength: LegendNextSpacing.xs)

                Text("\(activity.today.count)")
                    .font(.title3.weight(.bold))
                    .foregroundStyle(LegendNextColor.danger)
                    .accessibilityHidden(true)

                Image(systemName: "chevron.right")
                    .font(.caption.weight(.bold))
                    .foregroundStyle(.white.opacity(0.70))
            }
            .padding(.horizontal, LegendNextSpacing.sm)
            .padding(.vertical, LegendNextSpacing.xs)
            .background(LegendNextGradient.hero, in: Capsule())
            .overlay {
                Capsule()
                    .strokeBorder(LegendNextGradient.premiumStroke, lineWidth: 1)
            }
        }
        .overlay {
            RoundedRectangle(
                cornerRadius: LegendNextRadius.prominentCard,
                style: .continuous
            )
            .stroke(
                LinearGradient(
                    colors: [
                        LegendNextColor.goldBright.opacity(0.34),
                        LegendNextColor.gold.opacity(0.10),
                        Color.white.opacity(0.05)
                    ],
                    startPoint: .topLeading,
                    endPoint: .bottomTrailing
                ),
                lineWidth: 1
            )
            .allowsHitTesting(false)
        }
        .shadow(
            color: LegendNextColor.midnight.opacity(0.18),
            radius: 7,
            x: 0,
            y: 4
        )
        .contentShape(
            RoundedRectangle(
                cornerRadius: LegendNextRadius.prominentCard,
                style: .continuous
            )
        )
        .buttonStyle(.plain)
        .accessibilityLabel("Open today's activity. \(summary)")
    }

    private var categorySummary: some View {
        Group {
            if activity.categoryCounts.isEmpty {
                Text("Your day is clear")
                    .foregroundStyle(.white.opacity(0.84))
            } else {
                HStack(spacing: 5) {
                    ForEach(
                        Array(activity.categoryCounts.prefix(3).enumerated()),
                        id: \.element.id
                    ) { index, category in
                        if index > 0 {
                            Text("·")
                                .foregroundStyle(.white.opacity(0.46))
                        }

                        Text("\(category.count)")
                            .foregroundStyle(category.source.tone.color)

                        Text(category.source.title.lowercased())
                            .foregroundStyle(.white.opacity(0.84))
                    }
                }
            }
        }
        .font(LegendNextTypography.supporting)
        .lineLimit(1)
        .minimumScaleFactor(0.82)
        .allowsTightening(true)
    }

    private var summary: String {
        guard !activity.categoryCounts.isEmpty else {
            return "Your day is clear"
        }

        return activity.categoryCounts.prefix(3).map {
            "\($0.count) \($0.source.title.lowercased())"
        }
        .joined(separator: " · ")
    }
}

struct LegendDailyActivitySheet: View {
    @ObservedObject var activity: LegendDailyActivityStore
    @Environment(\.dismiss) private var dismiss
    @Environment(\.scenePhase) private var scenePhase
    @State private var showsPastDue = false
    @State private var selectedDetail: LegendDailyActivityItem?
    @State private var isPresentingPlannerComposer = false

    var body: some View {
        NavigationStack {
            LegendScrollView(tracksNavigationChrome: false) {
                VStack(alignment: .leading, spacing: LegendNextSpacing.md) {
                    LegendNextSheetHeader(
                        eyebrow: "Your Legend",
                        title: showsPastDue ? "Past due" : "Today's activity",
                        detail: showsPastDue
                            ? "Incomplete items from before today."
                            : Date().formatted(.dateTime.weekday(.wide).month(.wide).day()),
                        dismiss: { dismiss() }
                    )

                    activityModePicker

                    if !showsPastDue {
                        setReminderButton
                        activitySummary
                        devicePlannerConnection
                    }

                    let items = showsPastDue ? activity.pastDue : activity.today
                    if items.isEmpty {
                        LegendNextEmptyState(
                            title: showsPastDue ? "Nothing past due" : "Your day is clear",
                            message: showsPastDue
                                ? "Completed or rescheduled activities will not appear here."
                                : "Your connected calendar events and reminders will appear here.",
                            systemImage: showsPastDue
                                ? "checkmark.circle"
                                : "sun.max")
                    } else {
                        ForEach(items) { item in
                            LegendDailyActivityRow(
                                item: item,
                                isCompleted: activity.isCompleted(item),
                                toggleCompletion: {
                                    activity.toggleCompletion(for: item)
                                },
                                openEvent: { selectedDetail = item },
                                openProfile: nil)
                        }
                    }
                }
                .padding(LegendNextSpacing.md)
                .padding(.bottom, LegendNextSpacing.xl)
            }
            .background(LegendNextCanvas())
            .toolbar(.hidden, for: .navigationBar)
            .navigationDestination(
                isPresented: Binding(
                    get: { selectedDetail != nil },
                    set: { if !$0 { selectedDetail = nil } }
                )
            ) {
                if let selectedDetail {
                    LegendDailyActivityDetailView(item: selectedDetail)
                }
            }
        }
        .sheet(isPresented: $isPresentingPlannerComposer) {
            LegendPlannerEntryComposer(activity: activity)
        }
        .alert(
            "Device planner unavailable",
            isPresented: Binding(
                get: { activity.plannerFailure != nil },
                set: { if !$0 { activity.dismissPlannerFailure() } }
            ),
            actions: {
                Button("OK", role: .cancel) { activity.dismissPlannerFailure() }
            },
            message: {
                Text(activity.plannerFailure ?? "Please try again.")
            }
        )
        .task {
            await activity.refreshDevicePlanner()
        }
        .onChange(of: scenePhase) { _, phase in
            guard phase == .active else { return }
            Task { await activity.refreshDevicePlanner() }
        }
        .legendNextSheetChrome(detents: [.large])
    }

    private var setReminderButton: some View {
        Button {
            isPresentingPlannerComposer = true
        } label: {
            Label("Set reminder", systemImage: "plus.circle.fill")
        }
        .buttonStyle(LegendNextButtonStyle(kind: .primary))
        .accessibilityHint("Create a reminder or calendar event on this device")
    }

    private var activityModePicker: some View {
        HStack(spacing: LegendNextSpacing.xs) {
            modeButton(title: "Today", isSelected: !showsPastDue) {
                showsPastDue = false
            }
            modeButton(title: "Past due", isSelected: showsPastDue) {
                showsPastDue = true
            }
            Spacer()
        }
    }

    private func modeButton(
        title: String,
        isSelected: Bool,
        action: @escaping () -> Void
    ) -> some View {
        Button(action: action) {
            Text(title)
                .font(.subheadline.weight(.semibold))
                .foregroundStyle(isSelected ? .white : LegendNextColor.textSecondary)
                .padding(.horizontal, LegendNextSpacing.sm)
                .padding(.vertical, LegendNextSpacing.xs)
                .background {
                    if isSelected {
                        Capsule().fill(LegendNextGradient.hero)
                    } else {
                        Capsule().fill(LegendNextColor.fill)
                    }
                }
        }
        .buttonStyle(.plain)
    }

    private var activitySummary: some View {
        LegendNextSurface(style: .navy, padding: LegendNextSpacing.sm) {
            VStack(alignment: .leading, spacing: LegendNextSpacing.xs) {
                Text("AT A GLANCE")
                    .font(LegendNextTypography.eyebrow)
                    .tracking(0.8)
                    .foregroundStyle(LegendNextColor.goldBright)

                if activity.categoryCounts.isEmpty {
                    Text("Nothing requires your attention right now.")
                        .font(LegendNextTypography.supporting)
                        .foregroundStyle(.white.opacity(0.80))
                } else {
                    HStack(spacing: LegendNextSpacing.xs) {
                        ForEach(activity.categoryCounts.prefix(4)) { category in
                            LegendNextBadge(
                                "\(category.count) \(category.source.title)",
                                tone: category.source.tone,
                                systemImage: category.source.systemImage)
                        }
                    }
                }
            }
        }
    }

    private var devicePlannerConnection: some View {
        LegendNextSurface(style: .elevated, padding: LegendNextSpacing.sm) {
            VStack(alignment: .leading, spacing: LegendNextSpacing.sm) {
                HStack(alignment: .firstTextBaseline) {
                    VStack(alignment: .leading, spacing: 2) {
                        Text("DEVICE PLANNER")
                            .font(LegendNextTypography.eyebrow)
                            .tracking(0.8)
                            .foregroundStyle(LegendNextColor.gold)
                        Text("Optional, private calendar and reminders connection")
                            .font(LegendNextTypography.caption)
                            .foregroundStyle(LegendNextColor.textSecondary)
                    }
                    Spacer()
                }

                Text("Connect the calendar and reminders available on this phone. Apple, Google, Outlook, Exchange, and other device accounts stay local and are never sent to Legend.")
                    .font(LegendNextTypography.caption)
                    .foregroundStyle(LegendNextColor.textSecondary)

                plannerAccessRow(
                    capability: .calendar,
                    title: "Calendar",
                    symbol: "calendar",
                    authorization: activity.planner.calendarAuthorization,
                    isConnected: activity.planner.isConnected(.calendar),
                    connect: { Task { await activity.connectPlanner(.calendar) } },
                    disconnect: { activity.disconnectPlanner(.calendar) },
                    openSettings: activity.openDevicePlannerSettings
                )
                plannerAccessRow(
                    capability: .reminders,
                    title: "Reminders",
                    symbol: "checklist",
                    authorization: activity.planner.remindersAuthorization,
                    isConnected: activity.planner.isConnected(.reminders),
                    connect: { Task { await activity.connectPlanner(.reminders) } },
                    disconnect: { activity.disconnectPlanner(.reminders) },
                    openSettings: activity.openDevicePlannerSettings
                )
            }
        }
    }

    private func plannerAccessRow(
        capability: LegendDevicePlannerCapability,
        title: String,
        symbol: String,
        authorization: LegendDevicePlannerAuthorization,
        isConnected: Bool,
        connect: @escaping () -> Void,
        disconnect: @escaping () -> Void,
        openSettings: @escaping () -> Void
    ) -> some View {
        HStack(spacing: LegendNextSpacing.xs) {
            Image(systemName: symbol)
                .foregroundStyle(LegendNextColor.royal)
                .frame(width: 30, height: 30)
                .background(LegendNextColor.information.opacity(0.10), in: Circle())

            VStack(alignment: .leading, spacing: 1) {
                Text(title)
                    .font(.subheadline.weight(.semibold))
                    .foregroundStyle(LegendNextColor.textPrimary)
                Text(connectionStatus(
                    for: capability,
                    authorization: authorization,
                    isConnected: isConnected))
                    .font(.caption)
                    .foregroundStyle(isConnected
                        ? LegendNextColor.success
                        : LegendNextColor.textSecondary)
            }

            Spacer()

            if isConnected {
                Button("Disconnect", action: disconnect)
                    .buttonStyle(LegendNextButtonStyle(
                        kind: .secondary,
                        isFullWidth: false,
                        controlHeight: 34))
            } else if authorization == .denied {
                Button("Settings", action: openSettings)
                    .buttonStyle(LegendNextButtonStyle(
                        kind: .secondary,
                        isFullWidth: false,
                        controlHeight: 34))
            } else if authorization == .restricted {
                Text("Unavailable")
                    .font(.caption.weight(.semibold))
                    .foregroundStyle(LegendNextColor.textTertiary)
            } else {
                Button("Connect", action: connect)
                    .buttonStyle(LegendNextButtonStyle(
                        kind: .secondary,
                        isFullWidth: false,
                        controlHeight: 34))
            }
        }
    }

    private func connectionStatus(
        for capability: LegendDevicePlannerCapability,
        authorization: LegendDevicePlannerAuthorization,
        isConnected: Bool
    ) -> String {
        if isConnected { return "Connected" }
        if authorization.isAuthorized { return "Not connected" }
        return authorization.statusTitle
    }
}

/// The heart's dedicated surface. It contains only Legend notifications, never
/// personal calendar or reminders data.
struct LegendInAppNotificationsSheet: View {
    @ObservedObject var activity: LegendDailyActivityStore
    let currentIdentity: LogicalParticipantIdentity
    @ObservedObject var social: MobileSocialStore
    @Environment(\.dismiss) private var dismiss
    @State private var selectedPost: MobileSocialPost?
    @State private var selectedProfile: LegendPublicProfileRoute?

    var body: some View {
        NavigationStack {
            LegendScrollView(tracksNavigationChrome: false) {
                VStack(alignment: .leading, spacing: LegendNextSpacing.md) {
                    LegendNextSheetHeader(
                        eyebrow: "Your Legend",
                        title: "Notifications",
                        detail: "Follows, reactions, comments, reposts, and account updates.",
                        dismiss: { dismiss() }
                    )

                    LegendNextSurface(style: .navy, padding: LegendNextSpacing.sm) {
                        Label(
                            "IN-APP ACTIVITY",
                            systemImage: "heart.fill"
                        )
                        .font(LegendNextTypography.eyebrow)
                        .tracking(0.8)
                        .foregroundStyle(LegendNextColor.goldBright)
                    }

                    if activity.inAppNotifications.isEmpty {
                        LegendNextEmptyState(
                            title: "You're all caught up",
                            message: "New follows, reactions, comments, reposts, and account updates will appear here.",
                            systemImage: "heart")
                    } else {
                        ForEach(activity.inAppNotifications) { notification in
                            LegendDailyActivityRow(
                                item: notification,
                                isCompleted: false,
                                toggleCompletion: {},
                                openEvent: { openNotification(notification) },
                                openProfile: notification.actor.map { author in
                                    { openProfile(author) }
                                },
                                usesRelativeTimestamp: true)
                        }
                    }
                }
                .padding(LegendNextSpacing.md)
                .padding(.bottom, LegendNextSpacing.xl)
            }
            .background(LegendNextCanvas())
            .toolbar(.hidden, for: .navigationBar)
            .navigationDestination(
                isPresented: Binding(
                    get: { selectedPost != nil },
                    set: { if !$0 { selectedPost = nil } }
                )
            ) {
                if let selectedPost {
                    LegendPostDetailView(
                        post: selectedPost,
                        currentIdentity: currentIdentity,
                        social: social)
                }
            }
            .navigationDestination(
                isPresented: Binding(
                    get: { selectedProfile != nil },
                    set: { if !$0 { selectedProfile = nil } }
                )
            ) {
                if let selectedProfile {
                    LegendPublicProfileView(
                        profile: selectedProfile.profile,
                        currentIdentity: currentIdentity,
                        social: social,
                        isFollowing: selectedProfile.isFollowing,
                        isFollowRequestPending: selectedProfile.isFollowRequestPending)
                }
            }
        }
        .task {
            activity.markNotificationsViewed()
        }
        .legendNextSheetChrome(detents: [.large])
    }

    private func openNotification(_ notification: LegendDailyActivityItem) {
        if let sourcePostID = notification.sourcePostID,
           let post = activity.post(for: sourcePostID) {
            selectedPost = post
            return
        }
        if let author = notification.actor {
            openProfile(author)
        }
    }

    private func openProfile(_ author: MobileSocialAuthor) {
        selectedProfile = activity.profileRoute(for: author)
    }
}

/// A compact LEGEND treatment of the core Apple Reminders/Calendar questions.
/// Saving uses the EventKit planner authority; no planner item is mirrored to
/// the Legend backend.
private struct LegendPlannerEntryComposer: View {
    @ObservedObject var activity: LegendDailyActivityStore
    @Environment(\.dismiss) private var dismiss

    @State private var kind: LegendPlannerEntryKind = .reminder
    @State private var title = ""
    @State private var notes = ""
    @State private var includesDate = true
    @State private var includesTime = true
    @State private var startDate = Date()
    @State private var endDate = Date().addingTimeInterval(60 * 60)
    @State private var isAllDay = false
    @State private var alertsEnabled = true
    @State private var priority = 0
    @State private var repeatRule: LegendPlannerRepeat = .never
    @State private var selectedCalendarID: String?
    @State private var isSaving = false
    @State private var failureMessage: String?

    var body: some View {
        NavigationStack {
            LegendScrollView(tracksNavigationChrome: false) {
                VStack(alignment: .leading, spacing: LegendNextSpacing.md) {
                    LegendNextSheetHeader(
                        eyebrow: "Today's activity",
                        title: kind == .reminder ? "Set reminder" : "Add event",
                        detail: nil,
                        dismiss: { dismiss() }
                    )

                    Picker("Item type", selection: $kind) {
                        ForEach(LegendPlannerEntryKind.allCases) {
                            Text($0.title).tag($0)
                        }
                    }
                    .pickerStyle(.segmented)
                    .tint(LegendNextColor.gold)
                    .onChange(of: kind) { _, _ in
                        selectedCalendarID = nil
                        failureMessage = nil
                    }

                    LegendNextSurface(style: .elevated, padding: LegendNextSpacing.sm) {
                        VStack(alignment: .leading, spacing: LegendNextSpacing.xs) {
                            sectionTitle("DETAILS")

                            plannerTextField(
                                title: "Title",
                                placeholder: kind == .reminder
                                    ? "What do you need to remember?"
                                    : "What is happening?",
                                text: $title)

                            VStack(alignment: .leading, spacing: LegendNextSpacing.micro) {
                                Text("Notes")
                                    .font(.caption.weight(.semibold))
                                    .foregroundStyle(LegendNextColor.textSecondary)
                                TextEditor(text: $notes)
                                    .font(.subheadline)
                                    .frame(height: 72)
                                    .scrollContentBackground(.hidden)
                                    .padding(LegendNextSpacing.xs)
                                    .background(
                                        LegendNextColor.surfaceInset,
                                        in: RoundedRectangle(
                                            cornerRadius: LegendNextRadius.compact,
                                            style: .continuous))
                            }
                        }
                    }

                    timingSection
                    preferencesSection

                    if let failureMessage {
                        LegendNextSurface(style: .elevated, padding: LegendNextSpacing.sm) {
                            Label(failureMessage, systemImage: "exclamationmark.triangle.fill")
                                .font(LegendNextTypography.caption)
                                .foregroundStyle(LegendNextColor.danger)
                        }
                    }

                    Button(isSaving
                           ? "Saving…"
                           : kind == .reminder ? "Save reminder" : "Save event") {
                        save()
                    }
                    .buttonStyle(LegendNextButtonStyle(kind: .primary))
                    .disabled(isSaving || title.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty)
                }
                .padding(LegendNextSpacing.md)
                .padding(.bottom, LegendNextSpacing.xl)
            }
            .background(LegendNextCanvas())
            .toolbar(.hidden, for: .navigationBar)
        }
        .legendNextSheetChrome(detents: [.large])
    }

    @ViewBuilder
    private var timingSection: some View {
        LegendNextSurface(style: .elevated, padding: LegendNextSpacing.sm) {
            VStack(alignment: .leading, spacing: LegendNextSpacing.xs) {
                sectionTitle("WHEN")

                if kind == .reminder {
                    Toggle("Date", isOn: $includesDate)
                        .tint(LegendNextColor.gold)
                    if includesDate {
                        DatePicker(
                            "Due date",
                            selection: $startDate,
                            displayedComponents: .date)

                        Toggle("Time", isOn: $includesTime)
                            .tint(LegendNextColor.gold)
                        if includesTime {
                            DatePicker(
                                "Time",
                                selection: $startDate,
                                displayedComponents: .hourAndMinute)
                        }
                    }
                } else {
                    Toggle("All-day", isOn: $isAllDay)
                        .tint(LegendNextColor.gold)
                    DatePicker(
                        "Starts",
                        selection: $startDate,
                        displayedComponents: isAllDay ? .date : [.date, .hourAndMinute])
                    DatePicker(
                        "Ends",
                        selection: $endDate,
                        in: startDate...,
                        displayedComponents: isAllDay ? .date : [.date, .hourAndMinute])
                }
            }
            .font(.subheadline.weight(.semibold))
            .foregroundStyle(LegendNextColor.textPrimary)
        }
    }

    private var preferencesSection: some View {
        LegendNextSurface(style: .elevated, padding: LegendNextSpacing.sm) {
            VStack(alignment: .leading, spacing: LegendNextSpacing.xs) {
                sectionTitle("SCHEDULE")

                Picker("Repeat", selection: $repeatRule) {
                    ForEach(LegendPlannerRepeat.allCases) {
                        Text($0.title).tag($0)
                    }
                }
                .pickerStyle(.menu)

                if kind == .reminder {
                    Picker("Priority", selection: $priority) {
                        Text("None").tag(0)
                        Text("Low").tag(1)
                        Text("Medium").tag(5)
                        Text("High").tag(9)
                    }
                    .pickerStyle(.menu)
                }

                Toggle(kind == .reminder ? "Remind me" : "Alert 15 minutes before", isOn: $alertsEnabled)
                    .tint(LegendNextColor.gold)

                Divider()
                    .overlay(LegendNextColor.separator)
                    .padding(.vertical, LegendNextSpacing.micro)

                sectionTitle(kind == .reminder ? "REMINDERS LIST" : "CALENDAR")

                Picker(kind == .reminder ? "List" : "Calendar", selection: $selectedCalendarID) {
                    Text(primaryDestinationTitle).tag(Optional<String>.none)
                    ForEach(availableCalendars) { calendar in
                        Text(calendar.title).tag(Optional(calendar.id))
                    }
                }
                .pickerStyle(.menu)
            }
            .font(.subheadline.weight(.semibold))
            .foregroundStyle(LegendNextColor.textPrimary)
        }
    }

    private var availableCalendars: [LegendDevicePlannerCalendar] {
        activity.planner.calendars(for: kind.capability)
    }

    private var primaryDestinationTitle: String {
        kind == .reminder ? "Primary list" : "Primary calendar"
    }

    private func sectionTitle(_ title: String) -> some View {
        Text(title)
            .font(LegendNextTypography.eyebrow)
            .tracking(0.8)
            .foregroundStyle(LegendNextColor.gold)
    }

    private func plannerTextField(
        title: String,
        placeholder: String,
        text: Binding<String>
    ) -> some View {
        VStack(alignment: .leading, spacing: LegendNextSpacing.micro) {
            Text(title)
                .font(.caption.weight(.semibold))
                .foregroundStyle(LegendNextColor.textSecondary)
            TextField(placeholder, text: text)
                .font(.body)
                .textInputAutocapitalization(.sentences)
                .padding(LegendNextSpacing.sm)
                .background(
                    LegendNextColor.surfaceInset,
                    in: RoundedRectangle(
                        cornerRadius: LegendNextRadius.compact,
                        style: .continuous))
        }
    }

    private func save() {
        let scheduledDate = resolvedStartDate
        let draft = LegendPlannerEntryDraft(
            kind: kind,
            title: title,
            notes: notes,
            startDate: scheduledDate,
            endDate: kind == .event ? resolvedEndDate : nil,
            isAllDay: kind == .event && isAllDay,
            alertsEnabled: alertsEnabled,
            priority: kind == .reminder ? priority : 0,
            repeatRule: repeatRule,
            calendarIdentifier: selectedCalendarID)

        Task {
            isSaving = true
            failureMessage = nil
            do {
                try await activity.createPlannerEntry(draft)
                dismiss()
            } catch {
                failureMessage = error.localizedDescription
            }
            isSaving = false
        }
    }

    private var resolvedStartDate: Date? {
        if kind == .reminder && !includesDate {
            return nil
        }
        guard kind == .reminder && !includesTime else {
            return startDate
        }
        return Calendar.autoupdatingCurrent.startOfDay(for: startDate)
    }

    private var resolvedEndDate: Date {
        if isAllDay {
            let start = Calendar.autoupdatingCurrent.startOfDay(for: startDate)
            return max(endDate, start.addingTimeInterval(24 * 60 * 60))
        }
        return endDate
    }
}

private struct LegendDailyActivityRow: View {
    let item: LegendDailyActivityItem
    let isCompleted: Bool
    let toggleCompletion: () -> Void
    let openEvent: () -> Void
    let openProfile: (() -> Void)?
    var usesRelativeTimestamp = false

    private static let relativeDateFormatter: RelativeDateTimeFormatter = {
        let formatter = RelativeDateTimeFormatter()
        formatter.unitsStyle = .full
        formatter.dateTimeStyle = .numeric
        return formatter
    }()

    private func relativeTimestamp(
        for date: Date
    ) -> String {
        Self.relativeDateFormatter.localizedString(
            for: date,
            relativeTo: Date()
        )
    }

    var body: some View {
        LegendNextSurface(style: .elevated, padding: LegendNextSpacing.sm) {
            HStack(alignment: .top, spacing: LegendNextSpacing.sm) {
                sourceAvatar

                Button(action: openEvent) {
                    VStack(alignment: .leading, spacing: 3) {
                        HStack(spacing: LegendNextSpacing.micro) {
                            Text(item.source.title.uppercased())
                                .font(.caption2.weight(.bold))
                                .tracking(0.7)
                                .foregroundStyle(toneColor)
                            if item.isPastDue {
                                Text("PAST DUE")
                                    .font(.caption2.weight(.bold))
                                    .foregroundStyle(LegendNextColor.danger)
                            }
                        }

                        Text(item.title)
                            .font(LegendNextTypography.bodyEmphasis)
                            .foregroundStyle(LegendNextColor.textPrimary)
                            .strikethrough(isCompleted, color: LegendNextColor.textSecondary)
                            .lineLimit(2)

                        Text(item.detail)
                            .font(LegendNextTypography.caption)
                            .foregroundStyle(LegendNextColor.textSecondary)
                            .lineLimit(2)

                        if usesRelativeTimestamp {
                            Text(
                                relativeTimestamp(
                                    for: item.occurredAt
                                )
                            )
                            .font(.caption2)
                            .foregroundStyle(
                                LegendNextColor.textTertiary
                            )
                        } else {
                            Text(
                                item.occurredAt,
                                format: .dateTime.hour().minute()
                            )
                            .font(.caption2)
                            .foregroundStyle(
                                LegendNextColor.textTertiary
                            )
                        }
                    }
                    .frame(maxWidth: .infinity, alignment: .leading)
                }
                .buttonStyle(.plain)
                .accessibilityLabel("Open \(item.title)")

                if item.isCompletable {
                    Button(action: toggleCompletion) {
                        Image(systemName: isCompleted
                            ? "checkmark.circle.fill"
                            : "circle")
                            .font(.title3.weight(.semibold))
                            .foregroundStyle(isCompleted
                                ? LegendNextColor.success
                                : LegendNextColor.textSecondary)
                    }
                    .buttonStyle(.plain)
                    .accessibilityLabel(isCompleted
                        ? "Mark \(item.title) incomplete"
                        : "Mark \(item.title) complete")
                } else {
                    Image(systemName: "chevron.right")
                        .font(.caption.weight(.bold))
                        .foregroundStyle(LegendNextColor.textTertiary)
                        .padding(.top, 4)
                }
            }
        }
        .opacity(isCompleted ? 0.66 : 1)
    }

    @ViewBuilder
    private var sourceAvatar: some View {
        if let actor = item.actor, let openProfile {
            Button(action: openProfile) {
                LegendProfileAvatar(
                    avatar: actor.avatar,
                    displayName: actor.displayName,
                    size: 38)
            }
            .buttonStyle(.plain)
            .accessibilityLabel("Open \(actor.displayName)'s profile")
        } else {
            Image(systemName: item.source.systemImage)
                .font(.system(size: 16, weight: .semibold))
                .foregroundStyle(toneColor)
                .frame(width: 38, height: 38)
                .background(toneColor.opacity(0.12), in: Circle())
        }
    }

    private var toneColor: Color {
        switch item.source.tone {
        case .neutral: return LegendNextColor.textSecondary
        case .navy: return LegendNextColor.royal
        case .gold: return LegendNextColor.gold
        case .information: return LegendNextColor.information
        case .success: return LegendNextColor.success
        case .warning: return LegendNextColor.warning
        case .danger: return LegendNextColor.danger
        }
    }
}

private struct LegendDailyActivityDetailView: View {
    let item: LegendDailyActivityItem
    @Environment(\.dismiss) private var dismiss

    var body: some View {
        LegendScrollView(tracksNavigationChrome: false) {
            VStack(alignment: .leading, spacing: LegendNextSpacing.md) {
                Button {
                    dismiss()
                } label: {
                    Label("Back to activity", systemImage: "chevron.left")
                        .font(.subheadline.weight(.semibold))
                        .foregroundStyle(LegendNextColor.royal)
                }
                .buttonStyle(.plain)

                LegendNextSurface(style: .elevated, padding: LegendNextSpacing.md) {
                    VStack(alignment: .leading, spacing: LegendNextSpacing.sm) {
                        Label(item.source.title.uppercased(), systemImage: item.source.systemImage)
                            .font(LegendNextTypography.eyebrow)
                            .tracking(0.8)
                            .foregroundStyle(LegendNextColor.gold)

                        Text(item.title)
                            .font(LegendNextTypography.title)
                            .foregroundStyle(LegendNextColor.textPrimary)

                        Text(item.detail)
                            .font(LegendNextTypography.body)
                            .foregroundStyle(LegendNextColor.textSecondary)
                            .fixedSize(horizontal: false, vertical: true)

                        LegendNextDivider()

                        Label(
                            item.occurredAt.formatted(
                                .dateTime.weekday(.wide).month(.wide).day().hour().minute()),
                            systemImage: "clock")
                            .font(LegendNextTypography.caption)
                            .foregroundStyle(LegendNextColor.textSecondary)
                    }
                }
            }
            .padding(LegendNextSpacing.md)
            .padding(.bottom, LegendNextSpacing.xl)
        }
        .background(LegendNextCanvas())
        .toolbar(.hidden, for: .navigationBar)
    }
}
