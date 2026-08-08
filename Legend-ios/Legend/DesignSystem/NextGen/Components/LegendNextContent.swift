import SwiftUI

struct LegendHomeChromeActionRequest: Equatable, Identifiable {
    enum Kind: Equatable {
        case create
        case notifications
        case todayActivity
    }

    let id = UUID()
    let kind: Kind
}

/// One navigation-chrome authority for the native app. A downward movement hides
/// action chrome; even a small upward movement immediately restores it. Screens
/// report their vertical drag direction through `LegendScrollView`, so the bottom
/// navigation and the Home actions cannot get out of sync.
@MainActor
final class LegendScrollChrome: ObservableObject {
    @Published private(set) var isBottomNavigationVisible = true
    @Published private(set) var pendingHomeAction: LegendHomeChromeActionRequest?

    private let downwardThreshold: CGFloat = 1
    private let upwardThreshold: CGFloat = 0.5

    func beginTracking() {
        // A new scroll surface inherits the current chrome visibility. This
        // prevents a tab switch from flashing the navigation back on screen.
    }

    func reset() {
        isBottomNavigationVisible = true
    }

    func record(verticalDragTranslation: CGFloat) {
        if verticalDragTranslation <= -downwardThreshold {
            isBottomNavigationVisible = false
        } else if verticalDragTranslation >= upwardThreshold {
            isBottomNavigationVisible = true
        }
    }

    func requestHomeAction(_ kind: LegendHomeChromeActionRequest.Kind) {
        pendingHomeAction = LegendHomeChromeActionRequest(kind: kind)
    }

    func completeHomeAction(_ request: LegendHomeChromeActionRequest) {
        guard pendingHomeAction?.id == request.id else { return }
        pendingHomeAction = nil
    }
}

/// The shared vertical scrolling surface. It hides the system indicator and
/// reports the user's actual drag direction to the single navigation-chrome
/// authority. Gesture direction works consistently for every supported iOS 17
/// scroll surface, including nested content where geometry offsets can be stale.
struct LegendScrollView<Content: View>: View {
    private let axes: Axis.Set
    private let tracksNavigationChrome: Bool
    private let content: Content

    @EnvironmentObject private var scrollChrome: LegendScrollChrome
    init(
        _ axes: Axis.Set = .vertical,
        tracksNavigationChrome: Bool = true,
        @ViewBuilder content: () -> Content
    ) {
        self.axes = axes
        self.tracksNavigationChrome = tracksNavigationChrome
        self.content = content()
    }

    var body: some View {
        ScrollView(axes, showsIndicators: false) {
            content
        }
        .scrollIndicators(.hidden)
        .onAppear {
            if tracksNavigationChrome && axes.contains(.vertical) {
                scrollChrome.beginTracking()
            }
        }
        .simultaneousGesture(
            DragGesture(minimumDistance: 1)
                .onChanged { value in
                    guard tracksNavigationChrome, axes.contains(.vertical) else {
                        return
                    }

                    scrollChrome.record(
                        verticalDragTranslation: value.translation.height)
                }
        )
    }
}

struct LegendNextSectionHeader<Trailing: View>: View {
    let eyebrow: String?
    let title: String
    let detail: String?
    private let trailing: Trailing

    init(
        eyebrow: String? = nil,
        title: String,
        detail: String? = nil,
        @ViewBuilder trailing: () -> Trailing
    ) {
        self.eyebrow = eyebrow
        self.title = title
        self.detail = detail
        self.trailing = trailing()
    }

    var body: some View {
        HStack(alignment: .bottom, spacing: LegendNextSpacing.md) {
            VStack(alignment: .leading, spacing: LegendNextSpacing.micro) {
                if let eyebrow, !eyebrow.isEmpty {
                    Text(eyebrow.uppercased())
                        .font(LegendNextTypography.eyebrow)
                        .tracking(0.9)
                        .foregroundStyle(LegendNextColor.gold)
                }

                Text(title)
                    .font(LegendNextTypography.section)
                    .foregroundStyle(LegendNextColor.textPrimary)

                if let detail, !detail.isEmpty {
                    Text(detail)
                        .font(LegendNextTypography.supporting)
                        .foregroundStyle(LegendNextColor.textSecondary)
                        .fixedSize(horizontal: false, vertical: true)
                }
            }

            Spacer(minLength: LegendNextSpacing.sm)
            trailing
        }
        .accessibilityElement(children: .contain)
    }
}

extension LegendNextSectionHeader where Trailing == EmptyView {
    init(
        eyebrow: String? = nil,
        title: String,
        detail: String? = nil
    ) {
        self.init(
            eyebrow: eyebrow,
            title: title,
            detail: detail
        ) {
            EmptyView()
        }
    }
}

struct LegendNextHero<Accessory: View>: View {
    let eyebrow: String?
    let title: String
    let detail: String
    private let accessory: Accessory

    init(
        eyebrow: String? = nil,
        title: String,
        detail: String,
        @ViewBuilder accessory: () -> Accessory
    ) {
        self.eyebrow = eyebrow
        self.title = title
        self.detail = detail
        self.accessory = accessory()
    }

    var body: some View {
        LegendNextSurface(
            style: .navy,
            cornerRadius: LegendNextRadius.prominentCard,
            padding: LegendNextSpacing.intermediate
        ) {
            VStack(alignment: .leading, spacing: LegendNextSpacing.md) {
                HStack(alignment: .top, spacing: LegendNextSpacing.md) {
                    VStack(alignment: .leading, spacing: LegendNextSpacing.xs) {
                        if let eyebrow, !eyebrow.isEmpty {
                            Text(eyebrow.uppercased())
                                .font(LegendNextTypography.eyebrow)
                                .tracking(1)
                                .foregroundStyle(LegendNextColor.goldBright)
                        }

                        Text(title)
                            .font(LegendNextTypography.hero)
                            .foregroundStyle(.white)
                            .fixedSize(horizontal: false, vertical: true)

                        Text(detail)
                            .font(LegendNextTypography.supporting)
                            .foregroundStyle(.white.opacity(0.72))
                            .lineSpacing(2)
                            .fixedSize(horizontal: false, vertical: true)
                    }

                    Spacer(minLength: 0)
                }

                accessory
            }
        }
        .accessibilityElement(children: .contain)
    }
}

extension LegendNextHero where Accessory == EmptyView {
    init(
        eyebrow: String? = nil,
        title: String,
        detail: String
    ) {
        self.init(
            eyebrow: eyebrow,
            title: title,
            detail: detail
        ) {
            EmptyView()
        }
    }
}

/// A shared title treatment for white Legend screens. It keeps navigation
/// hierarchy, editorial spacing, and the brand accent consistent without
/// forcing a dark full-page treatment onto ordinary content views.
struct LegendNextScreenHeader<Accessory: View>: View {
    let eyebrow: String?
    let title: String
    let detail: String?
    private let accessory: Accessory

    init(
        eyebrow: String? = nil,
        title: String,
        detail: String? = nil,
        @ViewBuilder accessory: () -> Accessory
    ) {
        self.eyebrow = eyebrow
        self.title = title
        self.detail = detail
        self.accessory = accessory()
    }

    var body: some View {
        HStack(alignment: .top, spacing: LegendNextSpacing.md) {
            VStack(alignment: .leading, spacing: LegendNextSpacing.xs) {
                if let eyebrow, !eyebrow.isEmpty {
                    HStack(spacing: LegendNextSpacing.xs) {
                        Capsule()
                            .fill(LegendNextGradient.gold)
                            .frame(width: 22, height: 3)

                        Text(eyebrow.uppercased())
                            .font(LegendNextTypography.eyebrow)
                            .tracking(1.05)
                            .foregroundStyle(LegendNextColor.gold)
                    }
                }

                Text(title)
                    .font(LegendNextTypography.hero)
                    .foregroundStyle(LegendNextColor.textPrimary)
                    .fixedSize(horizontal: false, vertical: true)

                if let detail, !detail.isEmpty {
                    Text(detail)
                        .font(LegendNextTypography.supporting)
                        .foregroundStyle(LegendNextColor.textSecondary)
                        .fixedSize(horizontal: false, vertical: true)
                }
            }

            Spacer(minLength: LegendNextSpacing.sm)
            accessory
        }
        .accessibilityElement(children: .contain)
    }
}

extension LegendNextScreenHeader where Accessory == EmptyView {
    init(
        eyebrow: String? = nil,
        title: String,
        detail: String? = nil
    ) {
        self.init(eyebrow: eyebrow, title: title, detail: detail) {
            EmptyView()
        }
    }
}

/// Purpose-built sheet chrome keeps social and account sheets recognizably
/// Legend instead of inheriting a generic system-modal header.
struct LegendNextSheetHeader: View {
    let eyebrow: String
    let title: String
    let detail: String?
    let dismiss: () -> Void

    var body: some View {
        HStack(alignment: .top, spacing: LegendNextSpacing.md) {
            VStack(alignment: .leading, spacing: LegendNextSpacing.xs) {
                HStack(spacing: LegendNextSpacing.xs) {
                    Capsule()
                        .fill(LegendNextGradient.gold)
                        .frame(width: 20, height: 3)
                    Text(eyebrow.uppercased())
                        .font(LegendNextTypography.eyebrow)
                        .tracking(1)
                        .foregroundStyle(LegendNextColor.gold)
                }

                Text(title)
                    .font(LegendNextTypography.title)
                    .foregroundStyle(LegendNextColor.textPrimary)

                if let detail, !detail.isEmpty {
                    Text(detail)
                        .font(LegendNextTypography.caption)
                        .foregroundStyle(LegendNextColor.textSecondary)
                        .lineLimit(3)
                }
            }

            Spacer(minLength: LegendNextSpacing.xs)

            Button(action: dismiss) {
                Image(systemName: "xmark")
                    .font(.system(size: 14, weight: .bold))
                    .foregroundStyle(.white)
                    .frame(width: 38, height: 38)
                    .background(LegendNextGradient.finance, in: Circle())
                    .overlay {
                        Circle().strokeBorder(
                            Color.white.opacity(0.16),
                            lineWidth: 1
                        )
                    }
            }
            .buttonStyle(.plain)
            .accessibilityLabel("Close")
        }
    }
}

struct LegendNextBadge: View {
    let title: String
    let tone: LegendNextTone
    let systemImage: String?

    init(
        _ title: String,
        tone: LegendNextTone = .neutral,
        systemImage: String? = nil
    ) {
        self.title = title
        self.tone = tone
        self.systemImage = systemImage
    }

    var body: some View {
        HStack(spacing: LegendNextSpacing.micro) {
            if let systemImage {
                Image(systemName: systemImage)
                    .font(.system(size: 10, weight: .bold))
                    .accessibilityHidden(true)
            }

            Text(title)
                .lineLimit(1)
        }
        .font(LegendNextTypography.caption)
        .foregroundStyle(tone.color)
        .padding(.horizontal, LegendNextSpacing.xs)
        .padding(.vertical, LegendNextSpacing.tiny)
        .background(tone.color.opacity(0.11), in: Capsule())
        .overlay {
            Capsule().strokeBorder(tone.color.opacity(0.19), lineWidth: 1)
        }
        .shadow(
            color: tone.color.opacity(0.12),
            radius: LegendNextElevation.subtleRadius - 3,
            y: 2
        )
        .accessibilityLabel(title)
    }

}

/// One feedback treatment for every founder-controlled request. A successful
/// submission stays in the caller's current context; it never navigates the
/// requester into the private staff review conversation.
enum LegendRequestSubmissionFeedback: Equatable {
    case sent(ControlledResourceType)
    case failed(UserFacingFailure)

    var title: String {
        switch self {
        case .sent:
            return "Request Sent"
        case .failed:
            return "Request Not Sent"
        }
    }

    var detail: String {
        switch self {
        case .sent(let resourceType):
            return "Your \(resourceType.displayName) request is with the private Legend review team."
        case .failed(let failure):
            return failure.message
        }
    }

    var tone: LegendNextTone {
        switch self {
        case .sent: .success
        case .failed: .danger
        }
    }

    var systemImage: String {
        switch self {
        case .sent: "checkmark.circle.fill"
        case .failed: "exclamationmark.circle.fill"
        }
    }
}

struct LegendRequestSubmissionPill: View {
    let feedback: LegendRequestSubmissionFeedback

    var body: some View {
        HStack(alignment: .top, spacing: LegendNextSpacing.xs) {
            Image(systemName: feedback.systemImage)
                .font(.subheadline.weight(.bold))

            VStack(alignment: .leading, spacing: 2) {
                Text(feedback.title)
                    .font(LegendNextTypography.caption.weight(.bold))
                Text(feedback.detail)
                    .font(.caption2)
                    .fixedSize(horizontal: false, vertical: true)
            }
        }
        .foregroundStyle(feedback.tone.color)
        .padding(.horizontal, LegendNextSpacing.sm)
        .padding(.vertical, LegendNextSpacing.xs)
        .frame(maxWidth: .infinity, alignment: .leading)
        .background(feedback.tone.color.opacity(0.13), in: Capsule())
        .overlay {
            Capsule()
                .strokeBorder(feedback.tone.color.opacity(0.35), lineWidth: 1)
        }
        .accessibilityElement(children: .combine)
    }
}

struct LegendNextMetricTile: View {
    let title: String
    let value: String
    let detail: String?
    let systemImage: String?
    let tone: LegendNextTone

    init(
        title: String,
        value: String,
        detail: String? = nil,
        systemImage: String? = nil,
        tone: LegendNextTone = .navy
    ) {
        self.title = title
        self.value = value
        self.detail = detail
        self.systemImage = systemImage
        self.tone = tone
    }

    var body: some View {
        LegendNextSurface(style: .elevated) {
            VStack(alignment: .leading, spacing: LegendNextSpacing.sm) {
                HStack(spacing: LegendNextSpacing.xs) {
                    if let systemImage {
                        Image(systemName: systemImage)
                            .font(.system(size: 14, weight: .semibold))
                            .foregroundStyle(tone.color)
                            .frame(width: 30, height: 30)
                            .background(tone.color.opacity(0.10), in: Circle())
                            .accessibilityHidden(true)
                    }

                    Text(title.uppercased())
                        .font(LegendNextTypography.eyebrow)
                        .tracking(0.65)
                        .foregroundStyle(LegendNextColor.textSecondary)
                        .lineLimit(2)
                }

                Text(value)
                    .font(LegendNextTypography.metric)
                    .foregroundStyle(LegendNextColor.textPrimary)
                    .monospacedDigit()
                    .lineLimit(1)
                    .minimumScaleFactor(0.62)
                    .allowsTightening(true)

                if let detail, !detail.isEmpty {
                    Text(detail)
                        .font(LegendNextTypography.caption)
                        .foregroundStyle(LegendNextColor.textSecondary)
                        .lineLimit(2)
                }
            }
        }
        .accessibilityElement(children: .combine)
    }

}

/// Shared analytical row used anywhere Legend presents a protected metric.
/// Keeping it here prevents sheets and dashboards from drifting into their own
/// competing label/value layouts.
struct LegendNextKeyValueRow: View {
    let label: String
    let value: String

    var body: some View {
        HStack(alignment: .firstTextBaseline, spacing: LegendNextSpacing.sm) {
            Text(label)
                .font(LegendNextTypography.supporting)
                .foregroundStyle(LegendNextColor.textSecondary)

            Spacer(minLength: LegendNextSpacing.sm)

            Text(value)
                .font(LegendNextTypography.bodyEmphasis)
                .foregroundStyle(LegendNextColor.textPrimary)
                .multilineTextAlignment(.trailing)
        }
        .accessibilityElement(children: .combine)
    }
}

struct LegendNextQuickAction: View {
    let title: String
    let detail: String?
    let systemImage: String
    let tone: LegendNextTone
    let action: () -> Void

    init(
        title: String,
        detail: String? = nil,
        systemImage: String,
        tone: LegendNextTone = .navy,
        action: @escaping () -> Void
    ) {
        self.title = title
        self.detail = detail
        self.systemImage = systemImage
        self.tone = tone
        self.action = action
    }

    var body: some View {
        Button(action: action) {
            LegendNextSurface(
                style: .elevated,
                padding: LegendNextSpacing.md
            ) {
                HStack(spacing: LegendNextSpacing.sm) {
                    Image(systemName: systemImage)
                        .font(.system(size: 17, weight: .semibold))
                        .foregroundStyle(tone.color)
                        .frame(width: 38, height: 38)
                        .background(tone.color.opacity(0.11), in: Circle())
                        .accessibilityHidden(true)

                    VStack(alignment: .leading, spacing: LegendNextSpacing.micro) {
                        Text(title)
                            .font(LegendNextTypography.cardTitle)
                            .foregroundStyle(LegendNextColor.textPrimary)

                        if let detail, !detail.isEmpty {
                            Text(detail)
                                .font(LegendNextTypography.caption)
                                .foregroundStyle(LegendNextColor.textSecondary)
                                .lineLimit(2)
                        }
                    }

                    Spacer(minLength: LegendNextSpacing.xs)

                    Image(systemName: "chevron.right")
                        .font(.caption.weight(.bold))
                        .foregroundStyle(LegendNextColor.textTertiary)
                        .accessibilityHidden(true)
                }
            }
        }
        .buttonStyle(.plain)
        .accessibilityLabel(detail.map { "\(title), \($0)" } ?? title)
    }

}

struct LegendNextStatusBanner: View {
    let title: String
    let detail: String
    let tone: LegendNextTone
    let systemImage: String

    init(
        title: String,
        detail: String,
        tone: LegendNextTone,
        systemImage: String
    ) {
        self.title = title
        self.detail = detail
        self.tone = tone
        self.systemImage = systemImage
    }

    var body: some View {
        HStack(alignment: .top, spacing: LegendNextSpacing.sm) {
            Image(systemName: systemImage)
                .font(.system(size: 15, weight: .semibold))
                .foregroundStyle(tone.color)
                .frame(width: 34, height: 34)
                .background(tone.color.opacity(0.11), in: Circle())
                .accessibilityHidden(true)

            VStack(alignment: .leading, spacing: LegendNextSpacing.micro) {
                Text(title)
                    .font(LegendNextTypography.bodyEmphasis)
                    .foregroundStyle(LegendNextColor.textPrimary)

                Text(detail)
                    .font(LegendNextTypography.supporting)
                    .foregroundStyle(LegendNextColor.textSecondary)
                    .fixedSize(horizontal: false, vertical: true)
            }

            Spacer(minLength: 0)
        }
        .padding(LegendNextSpacing.md)
        .background(
            tone.color.opacity(0.075),
            in: RoundedRectangle(
                cornerRadius: LegendNextRadius.control,
                style: .continuous
            )
        )
        .accessibilityElement(children: .combine)
    }

}

private struct LegendNextSheetChrome: ViewModifier {
    let detents: Set<PresentationDetent>
    let showsDragIndicator: Bool

    func body(content: Content) -> some View {
        content
            .presentationDetents(detents)
            .presentationDragIndicator(showsDragIndicator ? .visible : .hidden)
            .presentationCornerRadius(LegendNextRadius.sheet)
            .legendNextBrandedSheetAppearance()
    }
}

extension View {
    func legendNextSheetChrome(
        detents: Set<PresentationDetent> = [.medium, .large],
        showsDragIndicator: Bool = true
    ) -> some View {
        modifier(
            LegendNextSheetChrome(
                detents: detents,
                showsDragIndicator: showsDragIndicator
            )
        )
    }
}
