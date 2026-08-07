import SwiftUI

struct LegendDailyScriptureManagementView: View {
    @ObservedObject var store: MobileDailyScriptureManagementStore
    @ObservedObject var messages: MessagingStore
    let isFounder: Bool

    @Environment(\.dismiss) private var dismiss
    @State private var editor: DailyScriptureOverrideEditorRoute?
    @State private var preview: MobileDailyScripture?
    @State private var removalTarget: MobileDailyScriptureOverride?
    @State private var isManagingAccess = false

    var body: some View {
        NavigationStack {
            Group {
                switch store.state {
                case .idle, .loading:
                    ProgressView("Loading Daily Scripture")
                        .frame(maxWidth: .infinity, maxHeight: .infinity)

                case .unavailable(let failure):
                    LegendNextErrorState(
                        title: failure.title,
                        message: failure.message,
                        retryTitle: "Retry",
                        retry: { Task { await store.load() } })
                    .padding(LegendNextSpacing.sm)

                case .loaded(let snapshot):
                    managerContent(snapshot)
                }
            }
            .background(LegendNextCanvas())
            .toolbar(.hidden, for: .navigationBar)
        }
        .tint(LegendNextColor.gold)
        .legendNextSheetChrome(detents: [.large])
        .task { await store.load() }
        .sheet(item: $editor) { route in
            LegendDailyScriptureOverrideEditor(
                route: route,
                store: store)
        }
        .sheet(item: $preview) { scripture in
            DailyScriptureSheet(scripture: scripture)
        }
        .sheet(isPresented: $isManagingAccess) {
            LegendControlledResourceAccessManager(
                messages: messages,
                resourceType: .scriptureManagement)
        }
        .confirmationDialog(
            "Remove this scheduled scripture?",
            isPresented: Binding(
                get: { removalTarget != nil },
                set: { if !$0 { removalTarget = nil } }),
            titleVisibility: .visible
        ) {
            Button("Remove override", role: .destructive) {
                guard let removalTarget else { return }
                self.removalTarget = nil
                Task { _ = await store.remove(id: removalTarget.id) }
            }
            Button("Cancel", role: .cancel) { removalTarget = nil }
        } message: {
            Text("Legend will return to its daily collection for this date unless another override is scheduled.")
        }
        .alert(
            store.actionFailure?.title ?? "Daily Scripture unavailable",
            isPresented: Binding(
                get: { store.actionFailure != nil },
                set: { if !$0 { store.dismissActionFailure() } }
            ),
            actions: {
                Button("OK", role: .cancel) { store.dismissActionFailure() }
            },
            message: {
                Text(store.actionFailure?.message ?? "Please try again.")
            }
        )
    }

    @ViewBuilder
    private func managerContent(
        _ snapshot: MobileDailyScriptureManagementSnapshot
    ) -> some View {
        LegendScrollView(tracksNavigationChrome: false) {
            VStack(alignment: .leading, spacing: LegendNextSpacing.md) {
                LegendNextSheetHeader(
                    eyebrow: "Content management",
                    title: "Daily Scripture",
                    detail: "The server resolves each Legend business day. Authored overrides win only on their scheduled date.",
                    dismiss: { dismiss() })

                todayCard(snapshot)

                LegendProfileSettingsSection(title: "Schedule") {
                    VStack(spacing: 0) {
                        Button {
                            editor = DailyScriptureOverrideEditorRoute(
                                existing: nil,
                                businessDate: nextScheduleDate(after: snapshot.businessDate))
                        } label: {
                            LegendProfileSettingsRow(
                                title: "Schedule scripture",
                                detail: "Choose a Legend date and paste the exact passage.",
                                systemImage: "calendar.badge.plus",
                                showsChevron: true)
                        }
                        .buttonStyle(.plain)

                        if !snapshot.upcoming.isEmpty {
                            LegendProfileSettingsDivider()
                            upcomingOverrides(snapshot)
                        }
                    }
                }

                if isFounder {
                    LegendProfileSettingsSection(title: "Access") {
                        Button {
                            isManagingAccess = true
                        } label: {
                            LegendProfileSettingsRow(
                                title: "Manage scripture access",
                                detail: "Grant or remove Daily Scripture Management.",
                                systemImage: "person.badge.key",
                                showsChevron: true)
                        }
                        .buttonStyle(.plain)
                    }
                }
            }
            .padding(LegendNextSpacing.sm)
            .padding(.bottom, LegendNextSpacing.xl)
        }
    }

    private func todayCard(
        _ snapshot: MobileDailyScriptureManagementSnapshot
    ) -> some View {
        let todayOverride = snapshot.upcoming.first {
            $0.displayDate == snapshot.businessDate
        }
        return LegendNextSurface(
            style: .elevated,
            cornerRadius: LegendNextRadius.prominentCard,
            padding: LegendNextSpacing.sm
        ) {
            VStack(alignment: .leading, spacing: LegendNextSpacing.sm) {
                HStack(alignment: .center, spacing: LegendNextSpacing.xs) {
                    VStack(alignment: .leading, spacing: LegendNextSpacing.micro) {
                        Text("TODAY")
                            .font(LegendNextTypography.eyebrow)
                            .tracking(1)
                            .foregroundStyle(LegendNextColor.gold)
                        Text(snapshot.current.reference)
                            .font(.headline.weight(.semibold))
                            .foregroundStyle(LegendNextColor.textPrimary)
                    }

                    Spacer(minLength: LegendNextSpacing.sm)

                    Text(sourceLabel(snapshot.current.source))
                        .font(LegendNextTypography.caption.weight(.semibold))
                        .foregroundStyle(LegendNextColor.gold)
                        .multilineTextAlignment(.trailing)
                }

                Text(snapshot.current.text)
                    .font(LegendNextTypography.caption)
                    .foregroundStyle(LegendNextColor.textSecondary)
                    .lineLimit(3)

                HStack(spacing: LegendNextSpacing.xs) {
                    Button("Preview") {
                        preview = snapshot.current
                    }
                    .buttonStyle(LegendNextButtonStyle(
                        kind: .secondary,
                        isFullWidth: false,
                        controlHeight: 36))

                    Button(todayOverride == nil ? "Override today" : "Edit today") {
                        editor = DailyScriptureOverrideEditorRoute(
                            existing: todayOverride,
                            businessDate: snapshot.businessDate)
                    }
                    .buttonStyle(LegendNextButtonStyle(
                        kind: .primary,
                        isFullWidth: false,
                        controlHeight: 36))
                }
            }
        }
    }

    private func upcomingOverrides(
        _ snapshot: MobileDailyScriptureManagementSnapshot
    ) -> some View {
        VStack(spacing: 0) {
            ForEach(snapshot.upcoming) { override in
                HStack(spacing: LegendNextSpacing.xs) {
                    VStack(alignment: .leading, spacing: LegendNextSpacing.micro) {
                        Text(override.displayDate)
                            .font(LegendNextTypography.caption.weight(.semibold))
                            .foregroundStyle(LegendNextColor.gold)
                        Text(override.reference)
                            .font(.subheadline.weight(.semibold))
                            .foregroundStyle(LegendNextColor.textPrimary)
                            .lineLimit(1)
                    }

                    Spacer(minLength: LegendNextSpacing.xs)

                    Button("Edit") {
                        editor = DailyScriptureOverrideEditorRoute(
                            existing: override,
                            businessDate: snapshot.businessDate)
                    }
                    .buttonStyle(.plain)
                    .font(LegendNextTypography.caption.weight(.semibold))
                    .foregroundStyle(LegendNextColor.gold)

                    Button(role: .destructive) {
                        removalTarget = override
                    } label: {
                        Image(systemName: "trash")
                    }
                    .buttonStyle(.plain)
                    .foregroundStyle(LegendNextColor.danger)
                    .accessibilityLabel("Remove \(override.reference) on \(override.displayDate)")
                }
                .padding(.vertical, LegendNextSpacing.xs)

                if override.id != snapshot.upcoming.last?.id {
                    LegendProfileSettingsDivider()
                }
            }
        }
    }

    private func sourceLabel(_ source: String) -> String {
        source == "ScheduledOverride" ? "Scheduled override" : "Daily collection"
    }

    private func nextScheduleDate(after businessDate: String) -> String {
        let date = LegendDailyScriptureDate.date(from: businessDate)
        return LegendDailyScriptureDate.string(
            from: LegendDailyScriptureDate.calendar.date(byAdding: .day, value: 1, to: date) ?? date)
    }
}

private struct DailyScriptureOverrideEditorRoute: Identifiable {
    let id = UUID()
    let existing: MobileDailyScriptureOverride?
    let businessDate: String
}

private struct LegendDailyScriptureOverrideEditor: View {
    let route: DailyScriptureOverrideEditorRoute
    @ObservedObject var store: MobileDailyScriptureManagementStore

    @Environment(\.dismiss) private var dismiss
    @State private var displayDate: Date
    @State private var reference: String
    @State private var translation: String
    @State private var passageText: String
    @State private var preview: MobileDailyScripture?

    init(
        route: DailyScriptureOverrideEditorRoute,
        store: MobileDailyScriptureManagementStore
    ) {
        self.route = route
        _store = ObservedObject(wrappedValue: store)
        _displayDate = State(initialValue: LegendDailyScriptureDate.date(
            from: route.existing?.displayDate ?? route.businessDate))
        _reference = State(initialValue: route.existing?.reference ?? "")
        _translation = State(initialValue: route.existing?.translation ?? "KJV")
        _passageText = State(initialValue: route.existing?.passageText ?? "")
    }

    var body: some View {
        NavigationStack {
            LegendScrollView(tracksNavigationChrome: false) {
                VStack(alignment: .leading, spacing: LegendNextSpacing.md) {
                    LegendNextSheetHeader(
                        eyebrow: route.existing == nil ? "Schedule scripture" : "Edit scripture",
                        title: route.existing == nil ? "New override" : "Scheduled override",
                        detail: "Legend uses America/Phoenix for this date. Your passage is stored exactly as entered.",
                        dismiss: { dismiss() })

                    LegendNextSurface(
                        style: .elevated,
                        cornerRadius: LegendNextRadius.prominentCard,
                        padding: LegendNextSpacing.sm
                    ) {
                        VStack(alignment: .leading, spacing: LegendNextSpacing.sm) {
                            DatePicker(
                                "Display date",
                                selection: $displayDate,
                                displayedComponents: .date)

                            TextField("Reference (for example, Psalm 121)", text: $reference)
                                .textInputAutocapitalization(.words)
                                .padding(.horizontal, LegendNextSpacing.sm)
                                .frame(minHeight: 44)
                                .background(LegendNextColor.brandBlueSurface, in: RoundedRectangle(
                                    cornerRadius: LegendNextRadius.control,
                                    style: .continuous))

                            TextField("Translation", text: $translation)
                                .textInputAutocapitalization(.characters)
                                .padding(.horizontal, LegendNextSpacing.sm)
                                .frame(minHeight: 44)
                                .background(LegendNextColor.brandBlueSurface, in: RoundedRectangle(
                                    cornerRadius: LegendNextRadius.control,
                                    style: .continuous))

                            Text("PASSAGE TEXT")
                                .font(LegendNextTypography.eyebrow)
                                .tracking(0.8)
                                .foregroundStyle(LegendNextColor.gold)

                            TextEditor(text: $passageText)
                                .font(LegendNextTypography.body)
                                .frame(minHeight: 220)
                                .padding(LegendNextSpacing.xs)
                                .scrollContentBackground(.hidden)
                                .background(LegendNextColor.brandBlueSurface, in: RoundedRectangle(
                                    cornerRadius: LegendNextRadius.control,
                                    style: .continuous))
                        }
                    }

                    HStack(spacing: LegendNextSpacing.xs) {
                        Button("Preview") {
                            preview = MobileDailyScripture(
                                date: LegendDailyScriptureDate.string(from: displayDate),
                                reference: reference,
                                translation: translation,
                                verses: [],
                                text: passageText,
                                source: "ScheduledOverride",
                                passageText: passageText)
                        }
                        .buttonStyle(LegendNextButtonStyle(kind: .secondary))
                        .disabled(reference.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty ||
                                  passageText.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty)

                        Button(store.isSaving ? "Saving…" : "Save") {
                            Task {
                                let draft = MobileDailyScriptureOverrideDraft(
                                    displayDate: LegendDailyScriptureDate.string(from: displayDate),
                                    reference: reference,
                                    translation: translation,
                                    passageText: passageText)
                                let saved = if let existing = route.existing {
                                    await store.update(id: existing.id, draft: draft)
                                } else {
                                    await store.create(draft)
                                }
                                if saved { dismiss() }
                            }
                        }
                        .buttonStyle(LegendNextButtonStyle(kind: .primary))
                        .disabled(store.isSaving || !isValid)
                    }
                }
                .padding(LegendNextSpacing.sm)
                .padding(.bottom, LegendNextSpacing.xl)
            }
            .background(LegendNextCanvas())
            .toolbar(.hidden, for: .navigationBar)
        }
        .tint(LegendNextColor.gold)
        .legendNextSheetChrome(detents: [.large])
        .sheet(item: $preview) { scripture in
            DailyScriptureSheet(scripture: scripture)
        }
    }

    private var isValid: Bool {
        !reference.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty &&
        !translation.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty &&
        !passageText.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty
    }
}

private enum LegendDailyScriptureDate {
    static var calendar: Calendar = {
        var calendar = Calendar(identifier: .gregorian)
        calendar.timeZone = TimeZone(identifier: "America/Phoenix") ?? .current
        return calendar
    }()

    static func date(from value: String) -> Date {
        formatter.date(from: value) ?? Date()
    }

    static func string(from value: Date) -> String {
        formatter.string(from: value)
    }

    private static let formatter: DateFormatter = {
        let formatter = DateFormatter()
        formatter.calendar = calendar
        formatter.locale = Locale(identifier: "en_US_POSIX")
        formatter.timeZone = calendar.timeZone
        formatter.dateFormat = "yyyy-MM-dd"
        return formatter
    }()
}
