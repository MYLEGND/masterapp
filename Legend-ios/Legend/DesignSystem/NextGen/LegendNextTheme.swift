import SwiftUI

// MARK: - Next-generation Legend visual authority
//
// This system intentionally lives beside the legacy Legend design system.
// Screens can migrate individually without changing stores, networking,
// authentication, models, API contracts, or backend authority.

enum LegendNextColor {
    static let midnight = LegendSharedDesign.color("midnight")
    static let navy = LegendSharedDesign.color("navy")
    static let navyElevated = LegendSharedDesign.color("navyElevated")
    static let royal = LegendSharedDesign.color("royal")
    static let gold = LegendSharedDesign.color("gold")
    static let goldBright = LegendSharedDesign.color("goldBright")
    static let goldSoft = LegendSharedDesign.color("goldSoft")
    static let canvas = LegendSharedDesign.color("canvas")
    static let canvasSecondary = LegendSharedDesign.color("canvasSecondary")
    static let surface = LegendSharedDesign.color("surface")
    static let surfaceElevated = LegendSharedDesign.color("surfaceElevated")
    static let surfaceInset = LegendSharedDesign.color("surfaceInset")
    static let brandBlueSurface = LegendSharedDesign.color("brandBlueSurface")
    static let brandBlueInset = LegendSharedDesign.color("brandBlueInset")
    static let textPrimary = LegendSharedDesign.color("textPrimary")
    static let textSecondary = LegendSharedDesign.color("textSecondary")
    static let textTertiary = LegendSharedDesign.color("textTertiary")
    static let separator = LegendSharedDesign.color("separator")
    static let fill = LegendSharedDesign.color("fill")
    static let fillSecondary = LegendSharedDesign.color("fillSecondary")
    static let success = LegendSharedDesign.semanticColor("success")
    static let warning = LegendSharedDesign.semanticColor("warning")
    static let danger = LegendSharedDesign.semanticColor("danger")
    static let information = LegendSharedDesign.semanticColor("information")
    static let inactive = LegendSharedDesign.semanticColor("inactive")
    static let contactNavy = LegendSharedDesign.color("contactNavy")
    static let contactBorder = gold.opacity(LegendSharedDesign.opacity("contactBorder"))
    static let contactTitle = LegendSharedDesign.color("onNavy")
    static let contactSupporting = Color.white.opacity(LegendSharedDesign.opacity("contactSupporting"))
    static let contactDetail = Color.white.opacity(LegendSharedDesign.opacity("contactDetail"))
    static let contactAction = Color.white.opacity(LegendSharedDesign.opacity("contactAction"))
    static let contactConnected = LegendSharedDesign.color("contactConnected")
    static let verified = LegendSharedDesign.color("verified")

    static func premiumBorder(for colorScheme: ColorScheme) -> Color {
        colorScheme == .dark
            ? Color.white.opacity(LegendSharedDesign.opacity("premiumBorderDark"))
            : navy.opacity(LegendSharedDesign.opacity("premiumBorderLight"))
    }

    static func subtleBorder(for colorScheme: ColorScheme) -> Color {
        colorScheme == .dark
            ? Color.white.opacity(LegendSharedDesign.opacity("subtleBorderDark"))
            : midnight.opacity(LegendSharedDesign.opacity("subtleBorderLight"))
    }

    static func glassTint(for colorScheme: ColorScheme) -> Color {
        colorScheme == .dark
            ? Color.white.opacity(LegendSharedDesign.opacity("glassTintDark"))
            : Color.white.opacity(LegendSharedDesign.opacity("glassTintLight"))
    }

    static func elevatedShadow(for colorScheme: ColorScheme) -> Color {
        colorScheme == .dark
            ? midnight.opacity(LegendSharedDesign.opacity("elevatedShadowDark"))
            : navy.opacity(LegendSharedDesign.opacity("elevatedShadowLight"))
    }

    static func ambientShadow(for colorScheme: ColorScheme) -> Color {
        colorScheme == .dark
            ? midnight.opacity(LegendSharedDesign.opacity("ambientShadowDark"))
            : navy.opacity(LegendSharedDesign.opacity("ambientShadowLight"))
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
    static let hairline = LegendSharedDesign.scalar(.spacing, "hairline")
    static let micro = LegendSharedDesign.scalar(.spacing, "micro")
    static let tiny = LegendSharedDesign.scalar(.spacing, "tiny")
    static let xs = LegendSharedDesign.scalar(.spacing, "xs")
    static let sm = LegendSharedDesign.scalar(.spacing, "sm")
    static let md = LegendSharedDesign.scalar(.spacing, "md")
    static let intermediate = LegendSharedDesign.scalar(.spacing, "intermediate")
    static let lg = LegendSharedDesign.scalar(.spacing, "lg")
    static let xl = LegendSharedDesign.scalar(.spacing, "xl")
    static let xxl = LegendSharedDesign.scalar(.spacing, "xxl")
    static let display = LegendSharedDesign.scalar(.spacing, "display")
    static let section = LegendSharedDesign.scalar(.spacing, "section")
    static let scene = LegendSharedDesign.scalar(.spacing, "scene")
    static let pageHorizontal = LegendSharedDesign.scalar(.spacing, "pageHorizontal")
    static let pageTop = LegendSharedDesign.scalar(.spacing, "pageTop")
    static let pageBottom = LegendSharedDesign.scalar(.spacing, "pageBottom")
    static let cardContent = LegendSharedDesign.scalar(.spacing, "cardContent")
}

enum LegendNextRadius {
    static let compact = LegendSharedDesign.scalar(.radii, "compact")
    static let control = LegendSharedDesign.scalar(.radii, "control")
    static let card = LegendSharedDesign.scalar(.radii, "card")
    static let prominentCard = LegendSharedDesign.scalar(.radii, "prominentCard")
    static let hero = LegendSharedDesign.scalar(.radii, "hero")
    static let sheet = LegendSharedDesign.scalar(.radii, "sheet")
    static let capsule = LegendSharedDesign.scalar(.radii, "capsule")
}

enum LegendNextSize {
    static let minimumTapTarget = LegendSharedDesign.scalar(.sizes, "minimumTapTarget")
    static let compactControlHeight = LegendSharedDesign.scalar(.sizes, "compactControlHeight")
    static let controlHeight = LegendSharedDesign.scalar(.sizes, "controlHeight")
    static let prominentControlHeight = LegendSharedDesign.scalar(.sizes, "prominentControlHeight")
    static let avatarSmall = LegendSharedDesign.scalar(.sizes, "avatarSmall")
    static let avatarMedium = LegendSharedDesign.scalar(.sizes, "avatarMedium")
    static let avatarLarge = LegendSharedDesign.scalar(.sizes, "avatarLarge")
    static let avatarHero = LegendSharedDesign.scalar(.sizes, "avatarHero")
    static let profileAvatar = LegendSharedDesign.scalar(.sizes, "profileAvatar")
    static let profileAvatarCamera = LegendSharedDesign.scalar(.sizes, "profileAvatarCamera")
    static let profileSettingsIcon = LegendSharedDesign.scalar(.sizes, "profileSettingsIcon")
    static let profileControlHeight = LegendSharedDesign.scalar(.sizes, "profileControlHeight")
    static let hacActionSize = LegendSharedDesign.scalar(.sizes, "hacActionSize")
    static let iconSmall = LegendSharedDesign.scalar(.sizes, "iconSmall")
    static let iconMedium = LegendSharedDesign.scalar(.sizes, "iconMedium")
    static let iconLarge = LegendSharedDesign.scalar(.sizes, "iconLarge")
}

enum LegendNextTypography {
    static let display = LegendSharedDesign.font("display")
    static let wordmark = LegendSharedDesign.font("wordmark")
    static let hero = LegendSharedDesign.font("hero")
    static let title = LegendSharedDesign.font("title")
    static let section = LegendSharedDesign.font("section")
    static let cardTitle = LegendSharedDesign.font("cardTitle")
    static let body = LegendSharedDesign.font("body")
    static let bodyEmphasis = LegendSharedDesign.font("bodyEmphasis")
    static let supporting = LegendSharedDesign.font("supporting")
    static let label = LegendSharedDesign.font("label")
    static let caption = LegendSharedDesign.font("caption")
    static let eyebrow = LegendSharedDesign.font("eyebrow")
    static let metricLarge = LegendSharedDesign.font("metricLarge")
    static let metric = LegendSharedDesign.font("metric")
}

enum LegendNextMotion {
    static let quick = Animation.easeOut(duration: LegendSharedDesign.motion("quickSeconds"))
    static let standard = Animation.easeOut(duration: LegendSharedDesign.motion("standardSeconds"))
    static let entrance = Animation.easeOut(duration: LegendSharedDesign.motion("entranceSeconds"))

    static let responsive = Animation.spring(
        response: LegendSharedDesign.spring("responsive", .response),
        dampingFraction: LegendSharedDesign.spring("responsive", .dampingFraction),
        blendDuration: LegendSharedDesign.spring("responsive", .blendDuration)
    )

    static let expressive = Animation.spring(
        response: LegendSharedDesign.spring("expressive", .response),
        dampingFraction: LegendSharedDesign.spring("expressive", .dampingFraction),
        blendDuration: LegendSharedDesign.spring("expressive", .blendDuration)
    )

    static let tab = Animation.spring(
        response: LegendSharedDesign.spring("tab", .response),
        dampingFraction: LegendSharedDesign.spring("tab", .dampingFraction)
    )
}

enum LegendNextElevation {
    static let subtleRadius = LegendSharedDesign.elevation("subtle", .radius)
    static let subtleY = LegendSharedDesign.elevation("subtle", .y)
    static let cardRadius = LegendSharedDesign.elevation("card", .radius)
    static let cardY = LegendSharedDesign.elevation("card", .y)
    static let floatingRadius = LegendSharedDesign.elevation("floating", .radius)
    static let floatingY = LegendSharedDesign.elevation("floating", .y)
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
