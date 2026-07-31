import SwiftUI

struct LegendNextButtonStyle: ButtonStyle {
    @Environment(\.isEnabled) private var isEnabled
    @Environment(\.colorScheme) private var colorScheme
    @Environment(\.accessibilityReduceMotion) private var reduceMotion

    let kind: LegendNextButtonKind
    var isFullWidth: Bool = true
    var controlHeight: CGFloat = LegendNextSize.controlHeight

    func makeBody(configuration: Configuration) -> some View {
        configuration.label
            .font(LegendNextTypography.bodyEmphasis)
            .foregroundStyle(foreground)
            .frame(
                maxWidth: isFullWidth ? .infinity : nil,
                minHeight: controlHeight
            )
            .padding(.horizontal, LegendNextSpacing.md)
            .background {
                background
            }
            .clipShape(shape)
            .overlay {
                shape.strokeBorder(border, lineWidth: 1)
            }
            .shadow(
                color: shadowColor(configuration),
                radius: isFullWidth
                    ? LegendNextElevation.subtleRadius
                    : LegendNextElevation.subtleRadius - 2,
                y: isFullWidth
                    ? LegendNextElevation.subtleY
                    : LegendNextElevation.subtleY - 1
            )
            .contentShape(shape)
            .opacity(isEnabled ? pressedOpacity(configuration) : 0.48)
            .scaleEffect(pressedScale(configuration))
            .animation(
                reduceMotion ? nil : LegendNextMotion.quick,
                value: configuration.isPressed
            )
    }

    private var shape: RoundedRectangle {
        RoundedRectangle(
            cornerRadius: LegendNextRadius.control,
            style: .continuous
        )
    }

    @ViewBuilder
    private var background: some View {
        switch kind {
        case .primary:
            LegendNextGradient.hero

        case .secondary:
            LegendNextGradient.finance

        case .gold:
            LegendNextGradient.gold

        case .ghost:
            Color.clear

        case .destructive:
            LegendNextColor.danger.opacity(
                colorScheme == .dark ? 0.18 : 0.10
            )
        }
    }

    private var foreground: Color {
        switch kind {
        case .primary:
            return .white

        case .secondary:
            return .white

        case .ghost:
            return LegendNextColor.textPrimary

        case .gold:
            return LegendNextColor.midnight

        case .destructive:
            return LegendNextColor.danger
        }
    }

    private var border: Color {
        switch kind {
        case .primary:
            return Color.white.opacity(0.10)

        case .secondary:
            return Color.white.opacity(0.16)

        case .gold:
            return LegendNextColor.goldBright.opacity(0.42)

        case .ghost:
            return LegendNextColor.subtleBorder(for: colorScheme)

        case .destructive:
            return LegendNextColor.danger.opacity(0.30)
        }
    }

    private func pressedOpacity(_ configuration: Configuration) -> Double {
        configuration.isPressed ? 0.88 : 1
    }

    private func pressedScale(_ configuration: Configuration) -> CGFloat {
        guard !reduceMotion else {
            return 1
        }

        return configuration.isPressed ? 0.975 : 1
    }

    private func shadowColor(_ configuration: Configuration) -> Color {
        let opacity = configuration.isPressed ? 0.08 : 0.16
        switch kind {
        case .primary:
            return LegendNextColor.navy.opacity(opacity)
        case .gold:
            return LegendNextColor.gold.opacity(opacity)
        case .secondary:
            return LegendNextColor.navy.opacity(opacity)
        case .ghost:
            return LegendNextColor.navy.opacity(opacity * 0.45)
        case .destructive:
            return LegendNextColor.danger.opacity(opacity * 0.55)
        }
    }
}

struct LegendNextIconButtonStyle: ButtonStyle {
    @Environment(\.isEnabled) private var isEnabled
    @Environment(\.colorScheme) private var colorScheme
    @Environment(\.accessibilityReduceMotion) private var reduceMotion

    let tone: LegendNextTone
    var size: CGFloat = LegendNextSize.minimumTapTarget

    func makeBody(configuration: Configuration) -> some View {
        configuration.label
            .font(.system(size: 17, weight: .semibold))
            .foregroundStyle(foreground)
            .frame(width: size, height: size)
            .background(background, in: Circle())
            .overlay {
                Circle().strokeBorder(
                    LegendNextColor.subtleBorder(for: colorScheme),
                    lineWidth: 1
                )
            }
            .shadow(
                color: LegendNextColor.navy.opacity(
                    colorScheme == .dark ? 0.15 : 0.09
                ),
                radius: LegendNextElevation.subtleRadius,
                y: LegendNextElevation.subtleY
            )
            .contentShape(Circle())
            .opacity(isEnabled ? (configuration.isPressed ? 0.78 : 1) : 0.42)
            .scaleEffect(
                reduceMotion || !configuration.isPressed ? 1 : 0.94
            )
            .animation(
                reduceMotion ? nil : LegendNextMotion.quick,
                value: configuration.isPressed
            )
    }

    private var foreground: Color {
        tone == .navy
            ? .white
            : tone == .neutral ? LegendNextColor.textPrimary : tone.color
    }

    private var background: Color {
        tone == .navy
            ? LegendNextColor.navy
            : foreground.opacity(colorScheme == .dark ? 0.16 : 0.09)
    }
}

/// Use for content rows that need the same elevated interaction language as
/// primary screens without pretending to be a primary action button.
struct LegendNextSurfaceButtonStyle: ButtonStyle {
    @Environment(\.accessibilityReduceMotion) private var reduceMotion

    func makeBody(configuration: Configuration) -> some View {
        LegendNextSurface(style: .elevated, padding: LegendNextSpacing.md) {
            configuration.label
        }
        .contentShape(RoundedRectangle(
            cornerRadius: LegendNextRadius.card,
            style: .continuous
        ))
        .opacity(configuration.isPressed ? 0.86 : 1)
        .scaleEffect(reduceMotion || !configuration.isPressed ? 1 : 0.985)
        .animation(
            reduceMotion ? nil : LegendNextMotion.quick,
            value: configuration.isPressed
        )
    }
}

struct LegendNextFloatingActionButton: View {
    let title: String
    let systemImage: String
    let action: () -> Void

    init(
        _ title: String,
        systemImage: String,
        action: @escaping () -> Void
    ) {
        self.title = title
        self.systemImage = systemImage
        self.action = action
    }

    var body: some View {
        Button(action: action) {
            Label(title, systemImage: systemImage)
                .font(LegendNextTypography.bodyEmphasis)
                .foregroundStyle(LegendNextColor.midnight)
                .padding(.horizontal, LegendNextSpacing.intermediate)
                .frame(minHeight: LegendNextSize.prominentControlHeight)
                .background(LegendNextGradient.gold, in: Capsule())
                .shadow(
                    color: LegendNextColor.gold.opacity(0.28),
                    radius: LegendNextElevation.floatingRadius,
                    y: LegendNextElevation.floatingY
                )
        }
        .buttonStyle(.plain)
        .accessibilityLabel(title)
    }
}
