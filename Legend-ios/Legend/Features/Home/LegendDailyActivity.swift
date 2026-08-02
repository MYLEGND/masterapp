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

/// The native EventKit payload is intentionally small and value-based. Nothing
/// from Apple Calendar or Reminders is posted to Legend's backend.
struct LegendApplePlannerItem: Identifiable, Equatable, Sendable {
    let id: String
    let source: LegendDailyActivitySource
    let title: String
    let detail: String
    let occursAt: Date
    let isPastDue: Bool
    let reminderIdentifier: String?
}

enum LegendApplePlannerAuthorization: Equatable {
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

/// Owns the Apple-only access boundary. It is separate from the Activity
/// projection because EventKit is a device permission, not a Legend account
/// credential. The resulting values are fed into the same projection as every
/// other activity source.
@MainActor
final class LegendApplePlannerStore: ObservableObject {
    @Published private(set) var calendarAuthorization: LegendApplePlannerAuthorization
    @Published private(set) var remindersAuthorization: LegendApplePlannerAuthorization
    @Published private(set) var items: [LegendApplePlannerItem] = []
    @Published private(set) var failureMessage: String?

    private let eventStore: EKEventStore
    private let calendar: Calendar

    init(
        eventStore: EKEventStore = EKEventStore(),
        calendar: Calendar = .autoupdatingCurrent
    ) {
        self.eventStore = eventStore
        self.calendar = calendar
        calendarAuthorization = Self.authorization(for: .event)
        remindersAuthorization = Self.authorization(for: .reminder)
    }

    func requestCalendarAccess() async {
        do {
            _ = try await eventStore.requestFullAccessToEvents()
            calendarAuthorization = Self.authorization(for: .event)
            await refresh()
        } catch {
            failureMessage = "Legend could not connect to Apple Calendar. Please try again in Settings."
        }
    }

    func requestRemindersAccess() async {
        do {
            _ = try await eventStore.requestFullAccessToReminders()
            remindersAuthorization = Self.authorization(for: .reminder)
            await refresh()
        } catch {
            failureMessage = "Legend could not connect to Apple Reminders. Please try again in Settings."
        }
    }

    func refresh() async {
        calendarAuthorization = Self.authorization(for: .event)
        remindersAuthorization = Self.authorization(for: .reminder)

        let range = todayRange
        var refreshed: [LegendApplePlannerItem] = []

        if calendarAuthorization.isAuthorized {
            let predicate = eventStore.predicateForEvents(
                withStart: range.start,
                end: range.end,
                calendars: nil)
            refreshed.append(contentsOf: eventStore.events(matching: predicate).map {
                event in
                LegendApplePlannerItem(
                    id: "calendar:\(event.eventIdentifier ?? event.calendarItemIdentifier)",
                    source: .calendar,
                    title: sanitized(event.title, fallback: "Calendar event"),
                    detail: event.isAllDay
                        ? "All-day event · \(event.calendar.title)"
                        : event.calendar.title,
                    occursAt: event.startDate,
                    isPastDue: false,
                    reminderIdentifier: nil)
            })
        }

        if remindersAuthorization.isAuthorized {
            let predicate = eventStore.predicateForIncompleteReminders(
                withDueDateStarting: nil,
                ending: range.end,
                calendars: nil)
            let reminders = await incompleteReminders(matching: predicate)
            refreshed.append(contentsOf: reminders.compactMap { reminder in
                guard let dueDate = dueDate(for: reminder) else { return nil }
                return LegendApplePlannerItem(
                    id: "reminder:\(reminder.calendarItemIdentifier)",
                    source: .reminder,
                    title: sanitized(reminder.title, fallback: "Reminder"),
                    detail: reminder.calendar.title,
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
        guard let reminder = eventStore.calendarItem(
            withIdentifier: identifier) as? EKReminder else {
            throw LegendApplePlannerError.reminderNotFound
        }

        reminder.isCompleted = completed
        try eventStore.save(reminder, commit: true)
    }

    func dismissFailure() {
        failureMessage = nil
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
    ) -> LegendApplePlannerAuthorization {
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

    private func sanitized(_ value: String?, fallback: String) -> String {
        let trimmed = value?.trimmingCharacters(in: .whitespacesAndNewlines) ?? ""
        return trimmed.isEmpty ? fallback : trimmed
    }
}

private enum LegendApplePlannerError: LocalizedError {
    case reminderNotFound

    var errorDescription: String? {
        switch self {
        case .reminderNotFound:
            return "The Apple Reminder is no longer available."
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
        plannerItems: [LegendApplePlannerItem],
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

    let planner: LegendApplePlannerStore

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
        planner: LegendApplePlannerStore? = nil,
        calendar: Calendar = .autoupdatingCurrent
    ) {
        self.identity = identity
        self.home = home
        self.social = social
        self.messages = messages
        self.planner = planner ?? LegendApplePlannerStore()
        self.calendar = calendar
        observeSources()
        rebuild()
    }

    var hasApplePlannerConnection: Bool {
        planner.calendarAuthorization.isAuthorized || planner.remindersAuthorization.isAuthorized
    }

    func refreshApplePlanner() async {
        await planner.refresh()
        rebuild()
    }

    func requestCalendarAccess() async {
        await planner.requestCalendarAccess()
        rebuild()
    }

    func requestRemindersAccess() async {
        await planner.requestRemindersAccess()
        rebuild()
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
                await self?.refreshApplePlanner()
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
                        plannerConnection
                    }

                    let items = showsPastDue ? activity.pastDue : activity.today
                    if items.isEmpty {
                        LegendNextEmptyState(
                            title: showsPastDue ? "Nothing past due" : "Your day is clear",
                            message: showsPastDue
                                ? "Completed or rescheduled activities will not appear here."
                                : "Today's account updates, appointments, actions, and connected Apple planner items appear here.",
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
            "Apple planner unavailable",
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
            await activity.refreshApplePlanner()
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

    private var plannerConnection: some View {
        LegendNextSurface(style: .elevated, padding: LegendNextSpacing.sm) {
            VStack(alignment: .leading, spacing: LegendNextSpacing.sm) {
                HStack(alignment: .firstTextBaseline) {
                    VStack(alignment: .leading, spacing: 2) {
                        Text("APPLE PLANNER")
                            .font(LegendNextTypography.eyebrow)
                            .tracking(0.8)
                            .foregroundStyle(LegendNextColor.gold)
                        Text("Optional, private device connection")
                            .font(LegendNextTypography.caption)
                            .foregroundStyle(LegendNextColor.textSecondary)
                    }
                    Spacer()
                }

                Text("Calendar events and incomplete reminders stay on this device and are never sent to Legend.")
                    .font(LegendNextTypography.caption)
                    .foregroundStyle(LegendNextColor.textSecondary)

                plannerAccessRow(
                    title: "Apple Calendar",
                    symbol: "calendar",
                    authorization: activity.planner.calendarAuthorization,
                    connect: { Task { await activity.requestCalendarAccess() } }
                )
                plannerAccessRow(
                    title: "Apple Reminders",
                    symbol: "checklist",
                    authorization: activity.planner.remindersAuthorization,
                    connect: { Task { await activity.requestRemindersAccess() } }
                )
            }
        }
    }

    private func plannerAccessRow(
        title: String,
        symbol: String,
        authorization: LegendApplePlannerAuthorization,
        connect: @escaping () -> Void
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
                Text(authorization.statusTitle)
                    .font(.caption)
                    .foregroundStyle(authorization.isAuthorized
                        ? LegendNextColor.success
                        : LegendNextColor.textSecondary)
            }

            Spacer()

            if authorization.isAuthorized {
                Image(systemName: "checkmark.circle.fill")
                    .foregroundStyle(LegendNextColor.success)
            } else {
                Button("Connect", action: connect)
                    .buttonStyle(LegendNextButtonStyle(
                        kind: .secondary,
                        isFullWidth: false,
                        controlHeight: 34))
            }
        }
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
