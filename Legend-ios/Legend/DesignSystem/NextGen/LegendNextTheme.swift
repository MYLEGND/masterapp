import SwiftUI
import UIKit

// MARK: - Next-generation Legend visual authority
//
// This system intentionally lives beside the legacy Legend design system.
// Screens can migrate individually without changing stores, networking,
// authentication, models, API contracts, or backend authority.

enum LegendNextColor {
    // Brand foundation
    // ClientApp Home premium-blue authority
    // #0A162E — exact deep stop from rgba(10, 22, 46, 0.995)
    static let midnight = Color(
        red: 10.0 / 255.0,
        green: 22.0 / 255.0,
        blue: 46.0 / 255.0
    )

    // #10254C — exact top stop from rgba(16, 37, 76, 0.98)
    static let navy = Color(
        red: 16.0 / 255.0,
        green: 37.0 / 255.0,
        blue: 76.0 / 255.0
    )

    // #3159BF — exact ClientApp blue illumination source
    static let navyElevated = Color(
        red: 49.0 / 255.0,
        green: 89.0 / 255.0,
        blue: 191.0 / 255.0
    )

    static let royal = Color(
        red: 35.0 / 255.0,
        green: 66.0 / 255.0,
        blue: 132.0 / 255.0
    )

    static let gold = Color(
        red: 187.0 / 255.0,
        green: 145.0 / 255.0,
        blue: 48.0 / 255.0
    )

    static let goldBright = Color(
        red: 224.0 / 255.0,
        green: 184.0 / 255.0,
        blue: 83.0 / 255.0
    )

    static let goldSoft = Color(
        red: 246.0 / 255.0,
        green: 232.0 / 255.0,
        blue: 191.0 / 255.0
    )

    // Branded adaptive application surfaces
    static let canvas = adaptiveColor(
        light: .white,
        dark: UIColor(red: 5 / 255, green: 10 / 255, blue: 23 / 255, alpha: 1)
    )
    static let canvasSecondary = adaptiveColor(
        light: .white,
        dark: UIColor(red: 8 / 255, green: 16 / 255, blue: 34 / 255, alpha: 1)
    )
    static let surface = adaptiveColor(
        light: UIColor(red: 253 / 255, green: 252 / 255, blue: 248 / 255, alpha: 1),
        dark: UIColor(red: 12 / 255, green: 22 / 255, blue: 43 / 255, alpha: 1)
    )
    static let surfaceElevated = adaptiveColor(
        light: UIColor(red: 1, green: 254 / 255, blue: 250 / 255, alpha: 1),
        dark: UIColor(red: 17 / 255, green: 31 / 255, blue: 59 / 255, alpha: 1)
    )
    static let surfaceInset = adaptiveColor(
        light: UIColor(red: 241 / 255, green: 239 / 255, blue: 232 / 255, alpha: 1),
        dark: UIColor(red: 8 / 255, green: 17 / 255, blue: 35 / 255, alpha: 1)
    )

    // Branded adaptive content
    static let textPrimary = adaptiveColor(
        light: UIColor(red: 13 / 255, green: 25 / 255, blue: 49 / 255, alpha: 1),
        dark: UIColor(red: 244 / 255, green: 241 / 255, blue: 232 / 255, alpha: 1)
    )
    static let textSecondary = adaptiveColor(
        light: UIColor(red: 83 / 255, green: 91 / 255, blue: 108 / 255, alpha: 1),
        dark: UIColor(red: 176 / 255, green: 184 / 255, blue: 199 / 255, alpha: 1)
    )
    static let textTertiary = adaptiveColor(
        light: UIColor(red: 126 / 255, green: 132 / 255, blue: 145 / 255, alpha: 1),
        dark: UIColor(red: 127 / 255, green: 139 / 255, blue: 160 / 255, alpha: 1)
    )
    static let separator = adaptiveColor(
        light: UIColor(red: 20 / 255, green: 39 / 255, blue: 74 / 255, alpha: 0.12),
        dark: UIColor(white: 1, alpha: 0.11)
    )
    static let fill = adaptiveColor(
        light: UIColor(red: 20 / 255, green: 39 / 255, blue: 74 / 255, alpha: 0.08),
        dark: UIColor(white: 1, alpha: 0.08)
    )
    static let fillSecondary = adaptiveColor(
        light: UIColor(red: 20 / 255, green: 39 / 255, blue: 74 / 255, alpha: 0.055),
        dark: UIColor(white: 1, alpha: 0.055)
    )

    // Semantic state
    static let success = Color(uiColor: .systemGreen)
    static let warning = Color(uiColor: .systemOrange)
    static let danger = Color(uiColor: .systemRed)
    static let information = Color(uiColor: .systemBlue)
    static let inactive = Color(uiColor: .systemGray)

    static func premiumBorder(for colorScheme: ColorScheme) -> Color {
        colorScheme == .dark
            ? Color.white.opacity(0.12)
            : navy.opacity(0.09)
    }

    static func subtleBorder(for colorScheme: ColorScheme) -> Color {
        colorScheme == .dark
            ? Color.white.opacity(0.08)
            : Color.black.opacity(0.06)
    }

    static func glassTint(for colorScheme: ColorScheme) -> Color {
        colorScheme == .dark
            ? Color.white.opacity(0.045)
            : Color.white.opacity(0.62)
    }

    static func elevatedShadow(for colorScheme: ColorScheme) -> Color {
        colorScheme == .dark
            ? Color.black.opacity(0.34)
            : navy.opacity(0.11)
    }

    static func ambientShadow(for colorScheme: ColorScheme) -> Color {
        colorScheme == .dark
            ? Color.black.opacity(0.22)
            : navy.opacity(0.055)
    }

    private static func adaptiveColor(light: UIColor, dark: UIColor) -> Color {
        Color(uiColor: UIColor { traits in
            traits.userInterfaceStyle == .dark ? dark : light
        })
    }
}

enum LegendNextGradient {
    static let hero = LinearGradient(
        colors: [
            LegendNextColor.navy,
            LegendNextColor.midnight
        ],
        startPoint: .topLeading,
        endPoint: .bottomTrailing
    )

    static let heroGlow = RadialGradient(
        colors: [
            LegendNextColor.navyElevated.opacity(0.18),
            LegendNextColor.navyElevated.opacity(0.06),
            .clear
        ],
        center: .top,
        startRadius: 0,
        endRadius: 300
    )

    static let gold = LinearGradient(
        colors: [
            LegendNextColor.goldBright,
            LegendNextColor.gold
        ],
        startPoint: .topLeading,
        endPoint: .bottomTrailing
    )

    static let finance = LinearGradient(
        colors: [
            LegendNextColor.royal,
            LegendNextColor.navy
        ],
        startPoint: .topLeading,
        endPoint: .bottomTrailing
    )

    static let success = LinearGradient(
        colors: [
            LegendNextColor.success.opacity(0.96),
            LegendNextColor.success.opacity(0.72)
        ],
        startPoint: .topLeading,
        endPoint: .bottomTrailing
    )

    static let premiumStroke = LinearGradient(
        colors: [
            Color.white.opacity(0.32),
            LegendNextColor.gold.opacity(0.36),
            Color.white.opacity(0.06)
        ],
        startPoint: .topLeading,
        endPoint: .bottomTrailing
    )

    static func pageWash(for colorScheme: ColorScheme) -> LinearGradient {
        if colorScheme == .dark {
            return LinearGradient(
                colors: [
                    LegendNextColor.midnight,
                    Color.black
                ],
                startPoint: .top,
                endPoint: .bottom
            )
        }

        return LinearGradient(
            colors: [
                LegendNextColor.canvas,
                LegendNextColor.canvasSecondary.opacity(0.72),
                LegendNextColor.surface
            ],
            startPoint: .topLeading,
            endPoint: .bottomTrailing
        )
    }
}

enum LegendNextSpacing {
    static let hairline: CGFloat = 2
    static let micro: CGFloat = 4
    static let tiny: CGFloat = 6
    static let xs: CGFloat = 8
    static let sm: CGFloat = 12
    static let md: CGFloat = 16
    static let intermediate: CGFloat = 20
    static let lg: CGFloat = 24
    static let xl: CGFloat = 32
    static let xxl: CGFloat = 40
    static let display: CGFloat = 48
    static let section: CGFloat = 56
    static let scene: CGFloat = 64

    static let pageHorizontal: CGFloat = 20
    static let pageTop: CGFloat = 16
    static let pageBottom: CGFloat = 32
    static let cardContent: CGFloat = 18
}

enum LegendNextRadius {
    static let compact: CGFloat = 10
    static let control: CGFloat = 14
    static let card: CGFloat = 20
    static let prominentCard: CGFloat = 24
    static let hero: CGFloat = 28
    static let sheet: CGFloat = 32
    static let capsule: CGFloat = 999
}

enum LegendNextSize {
    static let minimumTapTarget: CGFloat = 44
    static let compactControlHeight: CGFloat = 38
    static let controlHeight: CGFloat = 50
    static let prominentControlHeight: CGFloat = 56

    static let avatarSmall: CGFloat = 32
    static let avatarMedium: CGFloat = 44
    static let avatarLarge: CGFloat = 64
    static let avatarHero: CGFloat = 88

    static let iconSmall: CGFloat = 16
    static let iconMedium: CGFloat = 20
    static let iconLarge: CGFloat = 26
}

enum LegendNextTypography {
    static let display = Font.system(
        size: 36,
        weight: .bold,
        design: .default
    )

    static let hero = Font.system(
        size: 29,
        weight: .bold,
        design: .default
    )

    static let title = Font.system(
        size: 24,
        weight: .bold,
        design: .default
    )

    static let section = Font.system(
        size: 19,
        weight: .semibold,
        design: .default
    )

    static let cardTitle = Font.system(
        size: 17,
        weight: .semibold,
        design: .default
    )

    static let body = Font.system(
        size: 16,
        weight: .regular,
        design: .default
    )

    static let bodyEmphasis = Font.system(
        size: 16,
        weight: .semibold,
        design: .default
    )

    static let supporting = Font.system(
        size: 14,
        weight: .regular,
        design: .default
    )

    static let label = Font.system(
        size: 13,
        weight: .semibold,
        design: .default
    )

    static let caption = Font.system(
        size: 12,
        weight: .medium,
        design: .default
    )

    static let eyebrow = Font.system(
        size: 11,
        weight: .bold,
        design: .default
    )

    static let metricLarge = Font.system(
        size: 28,
        weight: .bold,
        design: .default
    )

    static let metric = Font.system(
        size: 22,
        weight: .bold,
        design: .default
    )
}

enum LegendNextMotion {
    static let quick = Animation.easeOut(duration: 0.16)
    static let standard = Animation.easeOut(duration: 0.24)
    static let entrance = Animation.easeOut(duration: 0.34)

    static let responsive = Animation.spring(
        response: 0.32,
        dampingFraction: 0.82,
        blendDuration: 0.10
    )

    static let expressive = Animation.spring(
        response: 0.48,
        dampingFraction: 0.78,
        blendDuration: 0.12
    )

    static let tab = Animation.spring(
        response: 0.30,
        dampingFraction: 0.84
    )
}

enum LegendNextElevation {
    static let subtleRadius: CGFloat = 8
    static let subtleY: CGFloat = 3

    static let cardRadius: CGFloat = 18
    static let cardY: CGFloat = 8

    static let floatingRadius: CGFloat = 24
    static let floatingY: CGFloat = 12
}

enum LegendNextSurfaceStyle: Equatable {
    case plain
    case elevated
    case glass
    case navy
    case gold
    case success
}

enum LegendNextButtonKind: Equatable {
    case primary
    case secondary
    case gold
    case ghost
    case destructive
}

enum LegendNextTone: Equatable {
    case neutral
    case navy
    case gold
    case information
    case success
    case warning
    case danger
}

enum LegendNextAvatarStatus: Equatable {
    case none
    case online
    case away
    case busy
}

extension View {
    func legendNextPageBackground() -> some View {
        modifier(LegendNextPageBackgroundModifier())
    }

    func legendNextEntrance(delay: Double = 0) -> some View {
        modifier(LegendNextEntranceModifier(delay: delay))
    }
}

private struct LegendNextPageBackgroundModifier: ViewModifier {
    @Environment(\.colorScheme) private var colorScheme

    func body(content: Content) -> some View {
        content.background {
            LegendNextGradient.pageWash(for: colorScheme)
                .ignoresSafeArea()
        }
    }
}

private struct LegendNextEntranceModifier: ViewModifier {
    @Environment(\.accessibilityReduceMotion) private var reduceMotion
    let delay: Double

    @State private var hasAppeared = false

    func body(content: Content) -> some View {
        content
            .opacity(hasAppeared ? 1 : 0)
            .offset(y: reduceMotion || hasAppeared ? 0 : 10)
            .onAppear {
                guard !hasAppeared else {
                    return
                }

                if reduceMotion {
                    hasAppeared = true
                } else {
                    withAnimation(LegendNextMotion.entrance.delay(delay)) {
                        hasAppeared = true
                    }
                }
            }
    }
}
