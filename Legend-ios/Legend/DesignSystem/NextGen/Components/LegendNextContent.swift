import SwiftUI

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
        .foregroundStyle(foreground)
        .padding(.horizontal, LegendNextSpacing.xs)
        .padding(.vertical, LegendNextSpacing.tiny)
        .background(foreground.opacity(0.11), in: Capsule())
        .accessibilityLabel(title)
    }

    private var foreground: Color {
        switch tone {
        case .neutral:
            return LegendNextColor.textSecondary
        case .navy:
            return LegendNextColor.royal
        case .gold:
            return LegendNextColor.gold
        case .information:
            return LegendNextColor.information
        case .success:
            return LegendNextColor.success
        case .warning:
            return LegendNextColor.warning
        case .danger:
            return LegendNextColor.danger
        }
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
                            .foregroundStyle(accent)
                            .frame(width: 30, height: 30)
                            .background(accent.opacity(0.10), in: Circle())
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

    private var accent: Color {
        switch tone {
        case .neutral:
            return LegendNextColor.textSecondary
        case .navy:
            return LegendNextColor.royal
        case .gold:
            return LegendNextColor.gold
        case .information:
            return LegendNextColor.information
        case .success:
            return LegendNextColor.success
        case .warning:
            return LegendNextColor.warning
        case .danger:
            return LegendNextColor.danger
        }
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
                        .foregroundStyle(accent)
                        .frame(width: 38, height: 38)
                        .background(accent.opacity(0.11), in: Circle())
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

    private var accent: Color {
        switch tone {
        case .neutral:
            return LegendNextColor.textSecondary
        case .navy:
            return LegendNextColor.royal
        case .gold:
            return LegendNextColor.gold
        case .information:
            return LegendNextColor.information
        case .success:
            return LegendNextColor.success
        case .warning:
            return LegendNextColor.warning
        case .danger:
            return LegendNextColor.danger
        }
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
                .foregroundStyle(accent)
                .frame(width: 34, height: 34)
                .background(accent.opacity(0.11), in: Circle())
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
            accent.opacity(0.075),
            in: RoundedRectangle(
                cornerRadius: LegendNextRadius.control,
                style: .continuous
            )
        )
        .accessibilityElement(children: .combine)
    }

    private var accent: Color {
        switch tone {
        case .neutral:
            return LegendNextColor.textSecondary
        case .navy:
            return LegendNextColor.royal
        case .gold:
            return LegendNextColor.gold
        case .information:
            return LegendNextColor.information
        case .success:
            return LegendNextColor.success
        case .warning:
            return LegendNextColor.warning
        case .danger:
            return LegendNextColor.danger
        }
    }
}
