import SwiftUI
import UIKit

// MARK: - Next-generation Legend visual authority
//
// This system intentionally lives beside the legacy Legend design system.
// Screens can migrate individually without changing stores, networking,
// authentication, models, API contracts, or backend authority.

enum LegendNextColor {
    // Brand foundation
    // Legend premium-blue authority. This is the shared source for the only
    // deep-color treatments used by the app, including Discover.
    static let midnight = Color(
        red: 10.0 / 255.0,
        green: 22.0 / 255.0,
        blue: 46.0 / 255.0
    )

    // #10254C — the elevated premium-blue brand stop.
    static let navy = Color(
        red: 16.0 / 255.0,
        green: 37.0 / 255.0,
        blue: 76.0 / 255.0
    )

    // #3159BF — premium blue illumination.
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

    // Primary Legend brand gold — #A68023
    static let gold = Color(
        red: 166.0 / 255.0,
        green: 128.0 / 255.0,
        blue: 35.0 / 255.0
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
        light: UIColor(red: 246 / 255, green: 249 / 255, blue: 255 / 255, alpha: 1),
        dark: UIColor(red: 8 / 255, green: 16 / 255, blue: 34 / 255, alpha: 1)
    )
    static let surface = adaptiveColor(
        light: .white,
        dark: UIColor(red: 10 / 255, green: 22 / 255, blue: 46 / 255, alpha: 1)
    )
    static let surfaceElevated = adaptiveColor(
        light: UIColor(red: 246 / 255, green: 249 / 255, blue: 255 / 255, alpha: 1),
        dark: UIColor(red: 16 / 255, green: 37 / 255, blue: 76 / 255, alpha: 1)
    )
    static let surfaceInset = adaptiveColor(
        light: UIColor(red: 237 / 255, green: 243 / 255, blue: 253 / 255, alpha: 1),
        dark: UIColor(red: 7 / 255, green: 18 / 255, blue: 40 / 255, alpha: 1)
    )
    static let brandBlueSurface = adaptiveColor(
        light: UIColor(red: 224 / 255, green: 235 / 255, blue: 255 / 255, alpha: 1),
        dark: UIColor(red: 26 / 255, green: 56 / 255, blue: 112 / 255, alpha: 1)
    )
    static let brandBlueInset = adaptiveColor(
        light: UIColor(red: 207 / 255, green: 224 / 255, blue: 252 / 255, alpha: 1),
        dark: UIColor(red: 20 / 255, green: 45 / 255, blue: 92 / 255, alpha: 1)
    )

    // Branded adaptive content
    static let textPrimary = adaptiveColor(
        light: UIColor(red: 10 / 255, green: 22 / 255, blue: 46 / 255, alpha: 1),
        dark: UIColor(red: 248 / 255, green: 250 / 255, blue: 252 / 255, alpha: 1)
    )
    static let textSecondary = adaptiveColor(
        light: UIColor(red: 71 / 255, green: 85 / 255, blue: 105 / 255, alpha: 1),
        dark: UIColor(red: 203 / 255, green: 213 / 255, blue: 225 / 255, alpha: 1)
    )
    static let textTertiary = adaptiveColor(
        light: UIColor(red: 100 / 255, green: 116 / 255, blue: 139 / 255, alpha: 1),
        dark: UIColor(red: 148 / 255, green: 163 / 255, blue: 184 / 255, alpha: 1)
    )
    static let separator = adaptiveColor(
        light: UIColor(red: 16 / 255, green: 37 / 255, blue: 76 / 255, alpha: 0.10),
        dark: UIColor(white: 1, alpha: 0.10)
    )
    static let fill = adaptiveColor(
        light: UIColor(red: 16 / 255, green: 37 / 255, blue: 76 / 255, alpha: 0.055),
        dark: UIColor(white: 1, alpha: 0.07)
    )
    static let fillSecondary = adaptiveColor(
        light: UIColor(red: 16 / 255, green: 37 / 255, blue: 76 / 255, alpha: 0.035),
        dark: UIColor(white: 1, alpha: 0.045)
    )

    // Semantic state
    static let success = Color(uiColor: .systemGreen)
    static let warning = Color(uiColor: .systemOrange)
    static let danger = Color(uiColor: .systemRed)
    static let information = Color(uiColor: .systemBlue)
    static let inactive = Color(uiColor: .systemGray)

    // Contact cards are intentionally distinct from generic surfaces. This is
    // their one visual contract everywhere in the app; it does not vary with
    // the device's Light/Dark setting or with the screen that happens to host
    // the card.
    static let contactNavy = Color(
        red: 20.0 / 255.0,
        green: 46.0 / 255.0,
        blue: 91.0 / 255.0
    )

    static let contactBorder = gold.opacity(0.92)
    static let contactTitle = Color.white
    static let contactSupporting = Color.white.opacity(0.78)
    static let contactDetail = Color.white.opacity(0.62)
    static let contactAction = Color.white.opacity(0.72)
    static let contactConnected = Color(
        red: 74 / 255,
        green: 226 / 255,
        blue: 139 / 255
    )

    static let verified = Color(
        red: 31 / 255,
        green: 122 / 255,
        blue: 235 / 255
    )

    static func premiumBorder(for colorScheme: ColorScheme) -> Color {
        colorScheme == .dark
            ? Color.white.opacity(0.10)
            : navy.opacity(0.075)
    }

    static func subtleBorder(for colorScheme: ColorScheme) -> Color {
        colorScheme == .dark
            ? Color.white.opacity(0.08)
            : midnight.opacity(0.06)
    }

    static func glassTint(for colorScheme: ColorScheme) -> Color {
        colorScheme == .dark
            ? Color.white.opacity(0.045)
            : Color.white.opacity(0.62)
    }

    static func elevatedShadow(for colorScheme: ColorScheme) -> Color {
        colorScheme == .dark
            ? midnight.opacity(0.22)
            : navy.opacity(0.065)
    }

    static func ambientShadow(for colorScheme: ColorScheme) -> Color {
        colorScheme == .dark
            ? midnight.opacity(0.15)
            : navy.opacity(0.035)
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

    /// The host and content background for a full-screen financial sheet.
    /// Keeping this here prevents client and agent financial views from
    /// drifting into separate modal treatments.
    static let financialSheet = LinearGradient(
        colors: [
            LegendNextColor.navy,
            LegendNextColor.midnight,
            LegendNextColor.midnight
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
                    LegendNextColor.navy
                ],
                startPoint: .top,
                endPoint: .bottom
            )
        }

        return LinearGradient(
            colors: [
                LegendNextColor.canvas,
                LegendNextColor.canvas
            ],
            startPoint: .top,
            endPoint: .bottom
        )
    }
}

enum LegendNextSpacing {
    static let hairline: CGFloat = 1
    static let micro: CGFloat = 3
    static let tiny: CGFloat = 5
    static let xs: CGFloat = 7
    static let sm: CGFloat = 10
    static let md: CGFloat = 14
    static let intermediate: CGFloat = 18
    static let lg: CGFloat = 20
    static let xl: CGFloat = 26
    static let xxl: CGFloat = 32
    static let display: CGFloat = 40
    static let section: CGFloat = 44
    static let scene: CGFloat = 52

    static let pageHorizontal: CGFloat = 16
    static let pageTop: CGFloat = 12
    static let pageBottom: CGFloat = 24
    static let cardContent: CGFloat = 14
}

enum LegendNextRadius {
    static let compact: CGFloat = 10
    static let control: CGFloat = 16
    static let card: CGFloat = 20
    static let prominentCard: CGFloat = 24
    static let hero: CGFloat = 28
    static let sheet: CGFloat = 28
    static let capsule: CGFloat = 999
}

enum LegendNextSize {
    static let minimumTapTarget: CGFloat = 44
    static let compactControlHeight: CGFloat = 36
    static let controlHeight: CGFloat = 46
    static let prominentControlHeight: CGFloat = 50

    static let avatarSmall: CGFloat = 30
    static let avatarMedium: CGFloat = 40
    static let avatarLarge: CGFloat = 56
    static let avatarHero: CGFloat = 76

    static let iconSmall: CGFloat = 15
    static let iconMedium: CGFloat = 19
    static let iconLarge: CGFloat = 23
}

enum LegendNextTypography {
    static let display = Font.system(
        size: 32,
        weight: .bold,
        design: .default
    )

    static let hero = Font.system(
        size: 27,
        weight: .bold,
        design: .default
    )

    static let title = Font.system(
        size: 22,
        weight: .bold,
        design: .default
    )

    static let section = Font.system(
        size: 18,
        weight: .semibold,
        design: .default
    )

    static let cardTitle = Font.system(
        size: 16,
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
    static let subtleRadius: CGFloat = 10
    static let subtleY: CGFloat = 4

    static let cardRadius: CGFloat = 22
    static let cardY: CGFloat = 10

    static let floatingRadius: CGFloat = 28
    static let floatingY: CGFloat = 14
}

enum LegendNextSurfaceStyle: Equatable {
    case plain
    case elevated
    case brandBlue
    /// The only full-strength blue surface. It is reserved for Profile settings
    /// so that private account controls read as Legend blue, never a washed tint.
    case profileSettings
    case glass
    case navy
    case gold
    case success
}

enum LegendNextInsetStyle: Equatable {
    case standard
    case brandBlue
    case profileSettings
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

extension LegendNextTone {
    var color: Color {
        switch self {
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

/// The only standard application canvas. It is intentionally quiet and
/// deterministic: standard pages use a clean near-white field while branded
/// surfaces explicitly use the Discover navy system.
struct LegendNextCanvas: View {
    @Environment(\.colorScheme) private var colorScheme

    var body: some View {
        ZStack {
            LegendNextGradient.pageWash(for: colorScheme)

            if colorScheme == .dark {
                Circle()
                    .fill(LegendNextColor.navyElevated.opacity(0.12))
                    .frame(width: 320, height: 320)
                    .blur(radius: 72)
                    .offset(x: 140, y: -260)
            }
        }
        .ignoresSafeArea()
        .accessibilityHidden(true)
    }
}

private struct LegendNextPageBackgroundModifier: ViewModifier {
    func body(content: Content) -> some View {
        content.background {
            LegendNextCanvas()
        }
    }
}

private struct LegendNextBrandedSheetAppearance<Background: ShapeStyle>: ViewModifier {
    let background: Background

    func body(content: Content) -> some View {
        content
            .preferredColorScheme(.dark)
            .presentationBackground(background)
    }
}

extension View {
    /// The shared sheet appearance. Sheets never inherit a system material or
    /// device color-mode treatment; they always use the Discover navy language.
    func legendNextBrandedSheetAppearance() -> some View {
        legendNextBrandedSheetAppearance(background: LegendNextColor.midnight)
    }

    func legendNextBrandedSheetAppearance<Background: ShapeStyle>(
        background: Background
    ) -> some View {
        modifier(LegendNextBrandedSheetAppearance(background: background))
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
