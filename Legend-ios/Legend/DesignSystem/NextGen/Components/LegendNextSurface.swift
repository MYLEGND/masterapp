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
            LinearGradient(
                colors: [
                    LegendNextColor.surface,
                    LegendNextColor.surfaceElevated.opacity(0.35)
                ],
                startPoint: .topLeading,
                endPoint: .bottomTrailing
            )

        case .elevated:
            ZStack {
                LinearGradient(
                    colors: [
                        LegendNextColor.surface,
                        LegendNextColor.surfaceElevated
                    ],
                    startPoint: .topLeading,
                    endPoint: .bottomTrailing
                )

                LinearGradient(
                    colors: [
                        Color.white.opacity(colorScheme == .dark ? 0.05 : 0.68),
                        .clear
                    ],
                    startPoint: .top,
                    endPoint: .center
                )
            }

        case .brandBlue:
            LinearGradient(
                colors: [
                    LegendNextColor.surface,
                    LegendNextColor.brandBlueSurface.opacity(0.38)
                ],
                startPoint: .topLeading,
                endPoint: .bottomTrailing
            )

        case .profileSettings:
            ZStack {
                LegendNextGradient.hero
                LegendNextGradient.heroGlow
            }

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
        case .brandBlue:
            return AnyShapeStyle(
                LegendNextColor.navy.opacity(colorScheme == .dark ? 0.45 : 0.18)
            )
        case .profileSettings:
            return AnyShapeStyle(LegendNextColor.gold.opacity(0.52))
        case .plain, .elevated:
            return AnyShapeStyle(
                LegendNextColor.subtleBorder(for: colorScheme)
            )
        }
    }

    private var borderWidth: CGFloat {
        style == .navy ? 1.15 : 1.05
    }

    private var shadowColor: Color {
        switch style {
        case .plain:
            return LegendNextColor.ambientShadow(for: colorScheme).opacity(0.72)
        case .glass:
            return LegendNextColor.ambientShadow(for: colorScheme)
        case .elevated, .brandBlue, .profileSettings, .navy, .gold, .success:
            return LegendNextColor.elevatedShadow(for: colorScheme).opacity(0.86)
        }
    }

    private var shadowRadius: CGFloat {
        switch style {
        case .plain:
            return LegendNextElevation.subtleRadius - 2
        case .glass:
            return LegendNextElevation.subtleRadius
        case .elevated, .brandBlue, .profileSettings, .navy, .gold, .success:
            return LegendNextElevation.cardRadius - 4
        }
    }

    private var shadowY: CGFloat {
        switch style {
        case .plain:
            return LegendNextElevation.subtleY - 2
        case .glass:
            return LegendNextElevation.subtleY
        case .elevated, .brandBlue, .profileSettings, .navy, .gold, .success:
            return LegendNextElevation.cardY - 3
        }
    }
}

struct LegendNextInsetSurface<Content: View>: View {
    @Environment(\.colorScheme) private var colorScheme

    private let style: LegendNextInsetStyle
    private let content: Content

    init(
        style: LegendNextInsetStyle = .standard,
        @ViewBuilder content: () -> Content
    ) {
        self.style = style
        self.content = content()
    }

    var body: some View {
        content
            .frame(maxWidth: .infinity, alignment: .leading)
            .padding(LegendNextSpacing.md)
            .background(
                background,
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
                    border,
                    lineWidth: 1
                )
            }
            .shadow(
                color: LegendNextColor.navy.opacity(
                    colorScheme == .dark ? 0.14 : 0.045
                ),
                radius: LegendNextElevation.subtleRadius,
                y: LegendNextElevation.subtleY
            )
    }

    private var background: AnyShapeStyle {
        switch style {
        case .standard:
            return AnyShapeStyle(LegendNextColor.surfaceInset)
        case .brandBlue:
            return AnyShapeStyle(LinearGradient(
                colors: [
                    LegendNextColor.surface,
                    LegendNextColor.brandBlueSurface.opacity(0.32)
                ],
                startPoint: .topLeading,
                endPoint: .bottomTrailing
            ))
        case .profileSettings:
            return AnyShapeStyle(LinearGradient(
                colors: [
                    LegendNextColor.surfaceInset,
                    LegendNextColor.navy
                ],
                startPoint: .topLeading,
                endPoint: .bottomTrailing
            ))
        }
    }

    private var border: Color {
        switch style {
        case .brandBlue:
            LegendNextColor.navy.opacity(colorScheme == .dark ? 0.48 : 0.18)
        case .profileSettings:
            LegendNextColor.gold.opacity(0.48)
        case .standard:
            LegendNextColor.subtleBorder(for: colorScheme)
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
