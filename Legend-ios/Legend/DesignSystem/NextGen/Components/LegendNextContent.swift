import SwiftUI

/// One navigation-chrome authority for the native app. A downward movement hides
/// the bottom action bar; even a small upward movement immediately restores it.
/// Screens report their content offset through `LegendScrollView` rather than
/// carrying their own visibility flags.
@MainActor
final class LegendScrollChrome: ObservableObject {
    @Published private(set) var isBottomNavigationVisible = true

    private var lastContentOffset: CGFloat?
    private let downwardThreshold: CGFloat = 1
    private let upwardThreshold: CGFloat = 0.5

    func beginTracking() {
        lastContentOffset = nil
    }

    func reset() {
        lastContentOffset = nil
        isBottomNavigationVisible = true
    }

    func record(contentOffset: CGFloat) {
        guard let previous = lastContentOffset else {
            lastContentOffset = contentOffset
            return
        }

        lastContentOffset = contentOffset
        let movement = contentOffset - previous

        if movement <= -downwardThreshold {
            isBottomNavigationVisible = false
        } else if movement >= upwardThreshold {
            isBottomNavigationVisible = true
        }
    }
}

/// The shared vertical scrolling surface. It hides the system indicator and
/// reports only real content movement to the single navigation-chrome authority.
struct LegendScrollView<Content: View>: View {
    private let axes: Axis.Set
    private let tracksNavigationChrome: Bool
    private let content: Content

    @EnvironmentObject private var scrollChrome: LegendScrollChrome
    @State private var coordinateSpaceID = UUID()

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
            if axes.contains(.vertical) {
                GeometryReader { proxy in
                    Color.clear.preference(
                        key: LegendScrollOffsetPreferenceKey.self,
                        value: proxy.frame(in: .named(coordinateSpaceID)).minY
                    )
                }
                .frame(height: 0)
            }

            content
        }
        .coordinateSpace(name: coordinateSpaceID)
        .scrollIndicators(.hidden)
        .onAppear {
            if tracksNavigationChrome && axes.contains(.vertical) {
                scrollChrome.beginTracking()
            }
        }
        .onPreferenceChange(LegendScrollOffsetPreferenceKey.self) { offset in
            guard tracksNavigationChrome, axes.contains(.vertical) else { return }
            scrollChrome.record(contentOffset: offset)
        }
    }
}

private enum LegendScrollOffsetPreferenceKey: PreferenceKey {
    static var defaultValue: CGFloat = .zero

    static func reduce(value: inout CGFloat, nextValue: () -> CGFloat) {
        value = nextValue()
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
