import SwiftUI

struct LegendNextSurface<Content: View>: View {
    @Environment(\.colorScheme) private var colorScheme

    private let style: LegendNextSurfaceStyle
    private let cornerRadius: CGFloat
    private let padding: CGFloat
    private let content: Content

    init(
        style: LegendNextSurfaceStyle = .elevated,
        cornerRadius: CGFloat = LegendNextRadius.card,
        padding: CGFloat = LegendNextSpacing.cardContent,
        @ViewBuilder content: () -> Content
    ) {
        self.style = style
        self.cornerRadius = cornerRadius
        self.padding = padding
        self.content = content()
    }

    var body: some View {
        content
            .frame(maxWidth: .infinity, alignment: .leading)
            .padding(padding)
            .background {
                background
            }
            .clipShape(shape)
            .overlay {
                shape.strokeBorder(border, lineWidth: borderWidth)
            }
            .shadow(
                color: shadowColor,
                radius: shadowRadius,
                y: shadowY
            )
    }

    private var shape: RoundedRectangle {
        RoundedRectangle(
            cornerRadius: cornerRadius,
            style: .continuous
        )
    }

    @ViewBuilder
    private var background: some View {
        switch style {
        case .plain:
            LegendNextColor.surface

        case .elevated:
            LegendNextColor.surfaceElevated

        case .glass:
            Rectangle()
                .fill(.ultraThinMaterial)
                .overlay(LegendNextColor.glassTint(for: colorScheme))

        case .navy:
            ZStack {
                LegendNextGradient.hero
                LegendNextGradient.heroGlow
            }

        case .gold:
            LegendNextGradient.gold

        case .success:
            LegendNextGradient.success
        }
    }

    private var border: AnyShapeStyle {
        switch style {
        case .navy:
            return AnyShapeStyle(LegendNextGradient.premiumStroke)
        case .gold, .success:
            return AnyShapeStyle(Color.white.opacity(0.20))
        case .glass:
            return AnyShapeStyle(
                LegendNextColor.premiumBorder(for: colorScheme)
            )
        case .plain, .elevated:
            return AnyShapeStyle(
                LegendNextColor.subtleBorder(for: colorScheme)
            )
        }
    }

    private var borderWidth: CGFloat {
        style == .navy ? 1.15 : 1
    }

    private var shadowColor: Color {
        switch style {
        case .plain:
            return .clear
        case .glass:
            return LegendNextColor.ambientShadow(for: colorScheme)
        case .elevated, .navy, .gold, .success:
            return LegendNextColor.elevatedShadow(for: colorScheme)
        }
    }

    private var shadowRadius: CGFloat {
        switch style {
        case .plain:
            return 0
        case .glass:
            return LegendNextElevation.subtleRadius
        case .elevated, .navy, .gold, .success:
            return LegendNextElevation.cardRadius
        }
    }

    private var shadowY: CGFloat {
        switch style {
        case .plain:
            return 0
        case .glass:
            return LegendNextElevation.subtleY
        case .elevated, .navy, .gold, .success:
            return LegendNextElevation.cardY
        }
    }
}

struct LegendNextInsetSurface<Content: View>: View {
    @Environment(\.colorScheme) private var colorScheme

    private let content: Content

    init(@ViewBuilder content: () -> Content) {
        self.content = content()
    }

    var body: some View {
        content
            .frame(maxWidth: .infinity, alignment: .leading)
            .padding(LegendNextSpacing.md)
            .background(
                LegendNextColor.fillSecondary,
                in: RoundedRectangle(
                    cornerRadius: LegendNextRadius.control,
                    style: .continuous
                )
            )
            .overlay {
                RoundedRectangle(
                    cornerRadius: LegendNextRadius.control,
                    style: .continuous
                )
                .strokeBorder(
                    LegendNextColor.subtleBorder(for: colorScheme),
                    lineWidth: 1
                )
            }
    }
}

struct LegendNextDivider: View {
    var body: some View {
        Rectangle()
            .fill(LegendNextColor.separator.opacity(0.42))
            .frame(height: 0.5)
            .accessibilityHidden(true)
    }
}
