import SwiftUI

struct LegendCard<Content: View>: View {
    @Environment(\.colorScheme) private var colorScheme

    private let style: LegendCardStyle
    private let content: Content

    init(style: LegendCardStyle = .standard, @ViewBuilder content: () -> Content) {
        self.style = style
        self.content = content()
    }

    var body: some View {
        content
            .padding(LegendSpacing.md)
            .background(background, in: RoundedRectangle(cornerRadius: cornerRadius, style: .continuous))
            .overlay {
                RoundedRectangle(cornerRadius: cornerRadius, style: .continuous)
                    .stroke(borderColor, lineWidth: 1)
            }
            .shadow(
                color: LegendElevation.shadowColor(for: colorScheme),
                radius: LegendElevation.cardShadowRadius,
                y: LegendElevation.cardShadowOffset)
    }

    private var background: Color {
        style == .navy ? LegendPalette.primaryNavy : LegendPalette.elevatedSurface
    }

    private var borderColor: Color {
        style == .navy ? LegendPalette.gold.opacity(0.45) : LegendPalette.separator.opacity(0.35)
    }

    private var cornerRadius: CGFloat {
        style == .navy ? LegendRadius.hero : LegendRadius.card
    }
}

struct LegendButtonStyle: ButtonStyle {
    let kind: LegendButtonKind

    func makeBody(configuration: Configuration) -> some View {
        configuration.label
            .font(.headline.weight(.semibold))
            .foregroundStyle(foreground)
            .frame(maxWidth: .infinity)
            .padding(.vertical, 13)
            .padding(.horizontal, LegendSpacing.md)
            .background(background, in: RoundedRectangle(cornerRadius: LegendRadius.control, style: .continuous))
            .overlay {
                RoundedRectangle(cornerRadius: LegendRadius.control, style: .continuous)
                    .stroke(border, lineWidth: 1)
            }
            .opacity(configuration.isPressed ? 0.88 : 1)
            .scaleEffect(configuration.isPressed ? 0.985 : 1)
            .animation(LegendMotion.standard, value: configuration.isPressed)
    }

    private var background: Color {
        switch kind {
        case .primary: LegendPalette.primaryNavy
        case .secondary: LegendPalette.insetSurface
        case .gold: LegendPalette.gold
        case .destructive: LegendPalette.critical.opacity(0.12)
        }
    }

    private var foreground: Color {
        switch kind {
        case .primary, .gold: .white
        case .secondary: LegendPalette.label
        case .destructive: LegendPalette.critical
        }
    }

    private var border: Color {
        switch kind {
        case .primary: LegendPalette.primaryNavy
        case .secondary: LegendPalette.separator.opacity(0.5)
        case .gold: LegendPalette.gold
        case .destructive: LegendPalette.critical.opacity(0.35)
        }
    }
}

struct LegendSectionHeader: View {
    let title: String
    let detail: String?

    init(_ title: String, detail: String? = nil) {
        self.title = title
        self.detail = detail
    }

    var body: some View {
        HStack(alignment: .firstTextBaseline, spacing: LegendSpacing.sm) {
            Text(title)
                .font(LegendTypography.section)
                .foregroundStyle(LegendPalette.label)
            Spacer(minLength: LegendSpacing.sm)
            if let detail {
                Text(detail)
                    .font(LegendTypography.metadata.weight(.medium))
                    .foregroundStyle(LegendPalette.secondaryLabel)
                    .multilineTextAlignment(.trailing)
            }
        }
    }
}

struct LegendBadge: View {
    let title: String
    let tone: LegendBadgeTone

    var body: some View {
        Text(title)
            .font(.caption.weight(.semibold))
            .foregroundStyle(foreground)
            .padding(.horizontal, 9)
            .padding(.vertical, 5)
            .background(background, in: Capsule())
            .accessibilityLabel(title)
    }

    private var background: Color {
        switch tone {
        case .neutral: LegendPalette.insetSurface
        case .gold: LegendPalette.gold.opacity(0.16)
        case .success: LegendPalette.success.opacity(0.15)
        case .warning: LegendPalette.warning.opacity(0.15)
        case .critical: LegendPalette.critical.opacity(0.15)
        }
    }

    private var foreground: Color {
        switch tone {
        case .neutral: LegendPalette.secondaryLabel
        case .gold: LegendPalette.gold
        case .success: LegendPalette.success
        case .warning: LegendPalette.warning
        case .critical: LegendPalette.critical
        }
    }
}

struct LegendMetric: View {
    let title: String
    let value: String
    let detail: String?

    init(title: String, value: String, detail: String? = nil) {
        self.title = title
        self.value = value
        self.detail = detail
    }

    var body: some View {
        VStack(alignment: .leading, spacing: LegendSpacing.xs) {
            Text(title.uppercased())
                .font(.caption2.weight(.semibold))
                .foregroundStyle(LegendPalette.secondaryLabel)
            Text(value)
                .font(LegendTypography.metric)
                .foregroundStyle(LegendPalette.label)
            if let detail {
                Text(detail)
                    .font(LegendTypography.metadata)
                    .foregroundStyle(LegendPalette.secondaryLabel)
                    .lineLimit(2)
            }
        }
        .frame(maxWidth: .infinity, alignment: .leading)
        .accessibilityElement(children: .combine)
    }
}

struct LegendNavigationBar: View {
    let title: String
    let detail: String?
    let symbolName: String?

    init(title: String, detail: String? = nil, symbolName: String? = nil) {
        self.title = title
        self.detail = detail
        self.symbolName = symbolName
    }

    var body: some View {
        HStack(alignment: .center, spacing: LegendSpacing.sm) {
            if let symbolName {
                Image(systemName: symbolName)
                    .font(.headline)
                    .foregroundStyle(LegendPalette.gold)
                    .accessibilityHidden(true)
            }
            VStack(alignment: .leading, spacing: LegendSpacing.xxs) {
                Text(title)
                    .font(LegendTypography.section)
                    .foregroundStyle(LegendPalette.label)
                if let detail {
                    Text(detail)
                        .font(LegendTypography.metadata)
                        .foregroundStyle(LegendPalette.secondaryLabel)
                }
            }
            Spacer(minLength: LegendSpacing.sm)
        }
        .accessibilityElement(children: .combine)
    }
}

struct LegendLoadingView: View {
    let title: String

    init(_ title: String = "Loading…") {
        self.title = title
    }

    var body: some View {
        VStack(spacing: LegendSpacing.sm) {
            ProgressView()
                .controlSize(.regular)
            Text(title)
                .font(LegendTypography.metadata)
                .foregroundStyle(LegendPalette.secondaryLabel)
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity)
        .padding(LegendSpacing.lg)
        .accessibilityElement(children: .combine)
    }
}

struct LegendEmptyState: View {
    let title: String
    let message: String
    let symbolName: String

    var body: some View {
        VStack(spacing: LegendSpacing.sm) {
            Image(systemName: symbolName)
                .font(.system(size: 30, weight: .medium))
                .foregroundStyle(LegendPalette.gold)
                .accessibilityHidden(true)
            Text(title)
                .font(LegendTypography.section)
            Text(message)
                .font(LegendTypography.body)
                .foregroundStyle(LegendPalette.secondaryLabel)
                .multilineTextAlignment(.center)
                .fixedSize(horizontal: false, vertical: true)
        }
        .frame(maxWidth: .infinity)
        .padding(LegendSpacing.xl)
        .accessibilityElement(children: .combine)
    }
}

struct LegendErrorCard: View {
    let title: String
    let message: String
    let retryTitle: String?
    let retry: (() -> Void)?

    init(title: String, message: String, retryTitle: String? = nil, retry: (() -> Void)? = nil) {
        self.title = title
        self.message = message
        self.retryTitle = retryTitle
        self.retry = retry
    }

    var body: some View {
        LegendCard {
            VStack(alignment: .leading, spacing: LegendSpacing.sm) {
                Label(title, systemImage: "exclamationmark.triangle.fill")
                    .font(LegendTypography.section)
                    .foregroundStyle(LegendPalette.critical)
                Text(message)
                    .font(LegendTypography.body)
                    .foregroundStyle(LegendPalette.secondaryLabel)
                    .fixedSize(horizontal: false, vertical: true)
                if let retryTitle, let retry {
                    Button(retryTitle, action: retry)
                        .buttonStyle(LegendButtonStyle(kind: .secondary))
                        .padding(.top, LegendSpacing.xs)
                }
            }
        }
        .accessibilityElement(children: .contain)
    }
}

struct LegendStatusBanner: View {
    let title: String
    let detail: String
    let tone: LegendBadgeTone

    var body: some View {
        HStack(alignment: .top, spacing: LegendSpacing.sm) {
            Circle()
                .fill(indicator)
                .frame(width: 9, height: 9)
                .padding(.top, 5)
                .accessibilityHidden(true)
            VStack(alignment: .leading, spacing: LegendSpacing.xxs) {
                Text(title)
                    .font(.subheadline.weight(.semibold))
                Text(detail)
                    .font(LegendTypography.metadata)
                    .foregroundStyle(LegendPalette.secondaryLabel)
                    .fixedSize(horizontal: false, vertical: true)
            }
            Spacer(minLength: 0)
        }
        .padding(LegendSpacing.sm)
        .background(indicator.opacity(0.11), in: RoundedRectangle(cornerRadius: LegendRadius.control, style: .continuous))
        .accessibilityElement(children: .combine)
    }

    private var indicator: Color {
        switch tone {
        case .neutral: LegendPalette.secondaryLabel
        case .gold: LegendPalette.gold
        case .success: LegendPalette.success
        case .warning: LegendPalette.warning
        case .critical: LegendPalette.critical
        }
    }
}

struct LegendHero: View {
    let eyebrow: String?
    let title: String
    let detail: String
    let symbolName: String?

    init(eyebrow: String? = nil, title: String, detail: String, symbolName: String? = nil) {
        self.eyebrow = eyebrow
        self.title = title
        self.detail = detail
        self.symbolName = symbolName
    }

    var body: some View {
        LegendCard(style: .navy) {
            HStack(alignment: .top, spacing: LegendSpacing.md) {
                VStack(alignment: .leading, spacing: LegendSpacing.xs) {
                    if let eyebrow {
                        Text(eyebrow.uppercased())
                            .font(.caption.weight(.semibold))
                            .foregroundStyle(LegendPalette.gold)
                    }
                    Text(title)
                        .font(LegendTypography.hero)
                        .foregroundStyle(.white)
                    Text(detail)
                        .font(LegendTypography.body)
                        .foregroundStyle(.white.opacity(0.76))
                        .fixedSize(horizontal: false, vertical: true)
                }
                Spacer(minLength: 0)
                if let symbolName {
                    Image(systemName: symbolName)
                        .font(.title2.weight(.semibold))
                        .foregroundStyle(LegendPalette.gold)
                        .accessibilityHidden(true)
                }
            }
        }
        .accessibilityElement(children: .combine)
    }
}

struct LegendBrandHeader: View {
    var body: some View {
        VStack(spacing: LegendSpacing.sm) {
            LegendBrandLogo(maximumWidth: 108)
                .accessibilityHidden(true)
            Text("Legend")
                .font(LegendTypography.brand)
                .foregroundStyle(LegendPalette.label)
            Text("One secure platform.")
                .font(.subheadline.weight(.medium))
                .foregroundStyle(LegendPalette.secondaryLabel)
            Text("Insurance  •  Finance  •  Business  •  Legacy")
                .font(LegendTypography.metadata)
                .foregroundStyle(LegendPalette.secondaryLabel)
                .multilineTextAlignment(.center)
        }
        .accessibilityElement(children: .combine)
        .accessibilityLabel("Legend. One secure platform for insurance, finance, business, and legacy.")
    }
}

struct LegendListCell<Leading: View, Content: View, Trailing: View>: View {
    let leading: Leading
    let content: Content
    let trailing: Trailing

    init(
        @ViewBuilder leading: () -> Leading,
        @ViewBuilder content: () -> Content,
        @ViewBuilder trailing: () -> Trailing
    ) {
        self.leading = leading()
        self.content = content()
        self.trailing = trailing()
    }

    var body: some View {
        HStack(spacing: LegendSpacing.sm) {
            leading
            content
            Spacer(minLength: LegendSpacing.xs)
            trailing
        }
        .padding(.vertical, LegendSpacing.xs)
    }
}
