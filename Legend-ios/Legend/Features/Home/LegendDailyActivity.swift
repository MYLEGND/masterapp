import Combine
import EventKit
import SwiftUI
import UIKit

/// The one presentation model for the Activity experience. Sources remain
/// authoritative in their own domains; this projection is the only place they
/// are combined for a member's local day.
enum LegendDailyActivitySource: String, CaseIterable, Hashable, Sendable {
    case account
    case network
    case action
    case appointment
    case calendar
    case reminder

    var title: String {
        switch self {
        case .account: return "Account"
        case .network: return "Network"
        case .action: return "Actions"
        case .appointment: return "Appointments"
        case .calendar: return "Calendar"
        case .reminder: return "Reminders"
        }
    }

    var systemImage: String {
        switch self {
        case .account: return "checkmark.seal.fill"
        case .network: return "heart.fill"
        case .action: return "checkmark.circle.fill"
        case .appointment: return "calendar.badge.clock"
        case .calendar: return "calendar"
        case .reminder: return "checklist"
        }
    }

    var tone: LegendNextTone {
        switch self {
        case .account: return .information
        case .network: return .gold
        case .action: return .warning
        case .appointment: return .navy
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

    init(
        eventStore: EKEventStore = EKEventStore(),
        calendar: Calendar = .autoupdatingCurrent,
        storageScope: String = "default",
        defaults: UserDefaults = .standard
    ) {
        let initialCalendarAuthorization = Self.authorization(for: .event)
        let initialRemindersAuthorization = Self.authorization(for: .reminder)

        self.eventStore = eventStore
        self.calendar = calendar
        self.storageScope = storageScope
        self.defaults = defaults

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

    var errorDescription: String? {
        switch self {
        case .reminderNotFound:
            return "The device reminder is no longer available."
        case .remindersDisconnected:
            return "Connect device reminders before updating a reminder."
        }
    }
}

/// A deterministic projection used by the store and tests. Its single local-day
/// range is deliberate: future and prior network events never appear in the
/// Today view, while incomplete tasks and reminders are available in Past Due.
enum LegendDailyActivityProjection {
    static func make(
        home: MobileHomeResponse?,
        social: MobileSocialSnapshot?,
        accountNotifications: [MobileActivityNotification],
        plannerItems: [LegendDevicePlannerItem],
        now: Date = Date(),
        calendar: Calendar = .autoupdatingCurrent
    ) -> (today: [LegendDailyActivityItem], pastDue: [LegendDailyActivityItem]) {
        let start = calendar.startOfDay(for: now)
        let end = calendar.date(byAdding: .day, value: 1, to: start) ?? start
        let todayRange = DateInterval(start: start, end: end)
        var today: [LegendDailyActivityItem] = []
        var pastDue: [LegendDailyActivityItem] = []

        for notification in accountNotifications where todayRange.contains(notification.occurredUTC) {
            today.append(LegendDailyActivityItem(
                id: "account:\(notification.id.uuidString)",
                source: .account,
                title: notification.title,
                detail: notification.detail,
                occurredAt: notification.occurredUTC))
        }

        if let social {
            for item in social.activity where todayRange.contains(item.occurredUTC) {
                today.append(LegendDailyActivityItem(
                    id: "network:\(item.id.uuidString)",
                    source: .network,
                    title: item.actor.displayName,
                    detail: item.summary,
                    occurredAt: item.occurredUTC,
                    actor: item.actor,
                    sourcePostID: item.postID))
            }
        }

        if let home {
            for action in home.actions {
                let dueDate = action.dueDateUTC
                let occurredAt = dueDate ?? start
                let item = LegendDailyActivityItem(
                    id: "action:\(action.id.uuidString)",
                    source: .action,
                    title: action.title,
                    detail: actionDetail(action),
                    occurredAt: occurredAt,
                    isCompletable: true,
                    isPastDue: dueDate.map { $0 < start } ?? false)
                if item.isPastDue {
                    pastDue.append(item)
                } else if dueDate == nil || todayRange.contains(occurredAt) {
                    today.append(item)
                }
            }

            for appointment in home.upcomingAppointments where todayRange.contains(appointment.startUTC) {
                today.append(LegendDailyActivityItem(
                    id: "appointment:\(appointment.id.uuidString)",
                    source: .appointment,
                    title: "Appointment",
                    detail: appointment.status.capitalized,
                    occurredAt: appointment.startUTC,
                    isCompletable: true))
            }

        }

        for item in plannerItems {
            let activity = LegendDailyActivityItem(
                id: item.id,
                source: item.source,
                title: item.title,
                detail: item.detail,
                occurredAt: item.occursAt,
                isCompletable: true,
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

    private static func actionDetail(_ action: MobileActionItem) -> String {
        let priority = action.priority.trimmingCharacters(in: .whitespacesAndNewlines)
        return priority.isEmpty ? "Legend action" : "\(priority.capitalized) priority"
    }
}

/// Shared, account-scoped Activity authority. It observes the existing protected
/// stores rather than maintaining duplicate copies of their data.
@MainActor
final class LegendDailyActivityStore: ObservableObject {
    @Published private(set) var today: [LegendDailyActivityItem] = []
    @Published private(set) var pastDue: [LegendDailyActivityItem] = []
    @Published private(set) var unreadBadgeCount = 0
    @Published private(set) var categoryCounts: [LegendDailyActivityCategoryCount] = []
    @Published private(set) var completionFailure: String?

    let planner: LegendDevicePlannerStore

    private let identity: LogicalParticipantIdentity
    private let home: MobileHomeStore
    private let social: MobileSocialStore
    private let messages: MessagingStore
    private let calendar: Calendar
    private var cancellables = Set<AnyCancellable>()

    init(
        identity: LogicalParticipantIdentity,
        home: MobileHomeStore,
        social: MobileSocialStore,
        messages: MessagingStore,
        planner: LegendDevicePlannerStore? = nil,
        calendar: Calendar = .autoupdatingCurrent
    ) {
        self.identity = identity
        self.home = home
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

    func markTodayViewed() {
        let identifiers = today
            .filter { !$0.isCompletable }
            .map(\.id)
        var seen = viewedIdentifiers
        seen.formUnion(identifiers)
        viewedIdentifiers = seen
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
        home.$state
            .sink { [weak self] _ in self?.rebuild() }
            .store(in: &cancellables)
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
        let projection = LegendDailyActivityProjection.make(
            home: homeSnapshot,
            social: socialSnapshot,
            accountNotifications: messages.activityNotifications,
            plannerItems: planner.items,
            calendar: calendar)
        today = projection.today
        pastDue = projection.pastDue

        let incompleteToday = today.filter { !isCompleted($0) }
        categoryCounts = LegendDailyActivitySource.allCases.compactMap { source in
            let count = incompleteToday.count { $0.source == source }
            return count > 0
                ? LegendDailyActivityCategoryCount(source: source, count: count)
                : nil
        }

        let unreadNotifications = incompleteToday.count {
            ($0.source == .account || $0.source == .network)
                && !viewedIdentifiers.contains($0.id)
        }
        let incompleteWork = incompleteToday.count { $0.isCompletable }
        unreadBadgeCount = unreadNotifications + incompleteWork
    }

    private var homeSnapshot: MobileHomeResponse? {
        guard case .loaded(let home) = home.state else { return nil }
        return home
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
        storageKey("viewed")
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
                    .font(.system(size: 17, weight: .semibold))
                    .foregroundStyle(LegendNextColor.goldBright)
                    .frame(width: 38, height: 38)
                    .background(Color.white.opacity(0.10), in: Circle())

                VStack(alignment: .leading, spacing: 2) {
                    Text("TODAY'S ACTIVITY")
                        .font(LegendNextTypography.eyebrow)
                        .tracking(0.8)
                        .foregroundStyle(LegendNextColor.goldBright)

                    Text(summary)
                        .font(LegendNextTypography.supporting)
                        .foregroundStyle(.white.opacity(0.84))
                        .lineLimit(1)
                }

                Spacer(minLength: LegendNextSpacing.xs)

                Text("\(activity.today.count)")
                    .font(.title3.weight(.bold))
                    .foregroundStyle(.white)
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
        .buttonStyle(.plain)
        .accessibilityLabel("Open today's activity. \(summary)")
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
    let currentIdentity: LogicalParticipantIdentity
    @ObservedObject var social: MobileSocialStore
    @Environment(\.dismiss) private var dismiss
    @Environment(\.scenePhase) private var scenePhase
    @State private var showsPastDue = false
    @State private var selectedPost: MobileSocialPost?
    @State private var selectedProfile: LegendPublicProfileRoute?
    @State private var selectedDetail: LegendDailyActivityItem?

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
                        activitySummary
                        devicePlannerConnection
                    }

                    let items = showsPastDue ? activity.pastDue : activity.today
                    if items.isEmpty {
                        LegendNextEmptyState(
                            title: showsPastDue ? "Nothing past due" : "Your day is clear",
                            message: showsPastDue
                                ? "Completed or rescheduled activities will not appear here."
                                : "Today's account updates, appointments, actions, and connected device planner items appear here.",
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
                                openEvent: { openEvent(item) },
                                openProfile: item.actor.map { actor in
                                    { openProfile(actor) }
                                })
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

    private func openProfile(_ author: MobileSocialAuthor) {
        selectedProfile = activity.profileRoute(for: author)
    }

    private func openEvent(_ item: LegendDailyActivityItem) {
        if let sourcePostID = item.sourcePostID,
           let post = activity.post(for: sourcePostID) {
            selectedPost = post
            return
        }

        if let actor = item.actor {
            openProfile(actor)
            return
        }

        selectedDetail = item
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

private struct LegendDailyActivityRow: View {
    let item: LegendDailyActivityItem
    let isCompleted: Bool
    let toggleCompletion: () -> Void
    let openEvent: () -> Void
    let openProfile: (() -> Void)?

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

                        Text(item.occurredAt, format: .dateTime.hour().minute())
                            .font(.caption2)
                            .foregroundStyle(LegendNextColor.textTertiary)
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
