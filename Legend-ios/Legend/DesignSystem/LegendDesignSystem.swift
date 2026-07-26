import SwiftUI
import UIKit

enum LegendPalette {
    static let primaryNavy = Color(red: 14.0 / 255.0, green: 27.0 / 255.0, blue: 61.0 / 255.0)
    static let secondaryNavy = Color(red: 11.0 / 255.0, green: 21.0 / 255.0, blue: 48.0 / 255.0)
    static let gold = Color(red: 166.0 / 255.0, green: 128.0 / 255.0, blue: 35.0 / 255.0)

    static let canvas = Color(uiColor: .systemGroupedBackground)
    static let elevatedSurface = Color(uiColor: .secondarySystemGroupedBackground)
    static let insetSurface = Color(uiColor: .tertiarySystemGroupedBackground)
    static let label = Color(uiColor: .label)
    static let secondaryLabel = Color(uiColor: .secondaryLabel)
    static let separator = Color(uiColor: .separator)
    static let success = Color(uiColor: .systemGreen)
    static let warning = Color(uiColor: .systemOrange)
    static let critical = Color(uiColor: .systemRed)
}

enum LegendSpacing {
    static let xxs: CGFloat = 4
    static let xs: CGFloat = 8
    static let sm: CGFloat = 12
    static let md: CGFloat = 16
    static let lg: CGFloat = 24
    static let xl: CGFloat = 32
}

enum LegendRadius {
    static let control: CGFloat = 12
    static let card: CGFloat = 20
    static let hero: CGFloat = 24
}

enum LegendElevation {
    static func shadowColor(for colorScheme: ColorScheme) -> Color {
        colorScheme == .dark ? .black.opacity(0.24) : .black.opacity(0.08)
    }

    static let cardShadowRadius: CGFloat = 16
    static let cardShadowOffset: CGFloat = 6
}

enum LegendTypography {
    static let brand = Font.system(.title2, design: .rounded).weight(.bold)
    static let hero = Font.system(.title2, design: .rounded).weight(.bold)
    static let section = Font.system(.headline, design: .rounded).weight(.semibold)
    static let body = Font.body
    static let metadata = Font.footnote
    static let metric = Font.system(.title3, design: .rounded).weight(.bold)
}

enum LegendMotion {
    static let standard = Animation.easeOut(duration: 0.22)
    static let entrance = Animation.easeOut(duration: 0.32)
}

enum LegendCardStyle: Equatable {
    case standard
    case navy
}

enum LegendButtonKind {
    case primary
    case secondary
    case gold
    case destructive
}

enum LegendBadgeTone {
    case neutral
    case gold
    case success
    case warning
    case critical
}
