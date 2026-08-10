import Foundation
import SwiftUI
import UIKit

/// Native SwiftUI mapping for the platform-neutral LEGEND® token resource.
/// Values deliberately live only in `Legend-Design/legend-design.tokens.json`.
enum LegendSharedDesign {
    private static let specification: Specification = {
        guard let url = Bundle.main.url(
            forResource: "legend-design.tokens",
            withExtension: "json"
        ) else {
            preconditionFailure("Missing bundled LEGEND design specification.")
        }

        do {
            return try JSONDecoder().decode(Specification.self, from: Data(contentsOf: url))
        } catch {
            preconditionFailure("Invalid bundled LEGEND design specification: \(error)")
        }
    }()

    static func color(_ name: String) -> Color {
        let token = required(specification.colors[name], named: "color \(name)")
        return Color(uiColor: UIColor { traits in
            let isDark = traits.userInterfaceStyle == .dark
            return UIColor(
                legendHex: isDark ? (token.dark ?? token.light) : token.light,
                alpha: isDark ? (token.darkOpacity ?? token.lightOpacity ?? 1) : (token.lightOpacity ?? 1)
            )
        })
    }

    static func opacity(_ name: String) -> Double {
        required(specification.opacity[name], named: "opacity \(name)")
    }

    static func scalar(_ group: ScalarGroup, _ name: String) -> CGFloat {
        CGFloat(required(group.values(in: specification)[name], named: "\(group.rawValue) \(name)"))
    }

    static func font(_ name: String) -> Font {
        let token = required(specification.typography[name], named: "typography \(name)")
        return .system(size: CGFloat(token.size), weight: fontWeight(token.weight), design: .default)
    }

    static func motion(_ name: String) -> Double {
        switch name {
        case "quickSeconds": return specification.motion.quickSeconds
        case "standardSeconds": return specification.motion.standardSeconds
        case "entranceSeconds": return specification.motion.entranceSeconds
        default: preconditionFailure("Missing LEGEND motion \(name) token.")
        }
    }

    static func elevation(_ name: String, _ metric: ElevationMetric) -> CGFloat {
        let token = required(specification.elevation[name], named: "elevation \(name)")
        return CGFloat(metric == .radius ? token.radius : token.y)
    }

    static func spring(_ name: String, _ metric: SpringMetric) -> Double {
        let token: SpringToken
        switch name {
        case "responsive": token = specification.motion.responsive
        case "expressive": token = specification.motion.expressive
        case "tab": token = specification.motion.tab
        default: preconditionFailure("Missing LEGEND spring \(name) token.")
        }
        switch metric {
        case .response: return token.response
        case .dampingFraction: return token.dampingFraction
        case .blendDuration: return token.blendDuration ?? 0
        }
    }

    static func semanticColor(_ name: String) -> Color {
        switch required(specification.platformSemanticColors[name], named: "semantic color \(name)").ios {
        case "systemGreen": return Color(uiColor: .systemGreen)
        case "systemOrange": return Color(uiColor: .systemOrange)
        case "systemRed": return Color(uiColor: .systemRed)
        case "systemBlue": return Color(uiColor: .systemBlue)
        case "systemGray": return Color(uiColor: .systemGray)
        default: preconditionFailure("Unsupported native LEGEND semantic color.")
        }
    }

    private static func fontWeight(_ value: String) -> Font.Weight {
        switch value {
        case "regular": return .regular
        case "medium": return .medium
        case "semibold": return .semibold
        case "bold": return .bold
        default: preconditionFailure("Unsupported LEGEND font weight.")
        }
    }

    private static func required<T>(_ value: T?, named name: String) -> T {
        guard let value else { preconditionFailure("Missing LEGEND \(name) token.") }
        return value
    }

    enum ScalarGroup: String {
        case spacing
        case radii
        case sizes

        fileprivate func values(in specification: Specification) -> [String: Double] {
            switch self {
            case .spacing: return specification.spacing
            case .radii: return specification.radii
            case .sizes: return specification.sizes
            }
        }
    }

    enum ElevationMetric {
        case radius
        case y
    }

    enum SpringMetric {
        case response
        case dampingFraction
        case blendDuration
    }

    fileprivate struct Specification: Decodable {
        fileprivate let colors: [String: ColorToken]
        fileprivate let platformSemanticColors: [String: SemanticColorToken]
        fileprivate let opacity: [String: Double]
        fileprivate let spacing: [String: Double]
        fileprivate let radii: [String: Double]
        fileprivate let sizes: [String: Double]
        fileprivate let typography: [String: TypographyToken]
        fileprivate let motion: MotionToken
        fileprivate let elevation: [String: ElevationToken]
    }

    fileprivate struct ColorToken: Decodable {
        let light: String
        let dark: String?
        let lightOpacity: Double?
        let darkOpacity: Double?
    }

    fileprivate struct TypographyToken: Decodable {
        let size: Double
        let weight: String
    }

    fileprivate struct SemanticColorToken: Decodable {
        let ios: String
    }

    fileprivate struct ElevationToken: Decodable {
        let radius: Double
        let y: Double
    }

    fileprivate struct MotionToken: Decodable {
        let quickSeconds: Double
        let standardSeconds: Double
        let entranceSeconds: Double
        let responsive: SpringToken
        let expressive: SpringToken
        let tab: SpringToken
    }

    fileprivate struct SpringToken: Decodable {
        let response: Double
        let dampingFraction: Double
        let blendDuration: Double?
    }
}

private extension UIColor {
    convenience init(legendHex: String, alpha: Double) {
        let value = legendHex.drop(while: { $0 == "#" })
        guard value.count == 6, let rgb = UInt64(value, radix: 16) else {
            preconditionFailure("Invalid LEGEND color token.")
        }
        self.init(
            red: CGFloat((rgb >> 16) & 0xFF) / 255,
            green: CGFloat((rgb >> 8) & 0xFF) / 255,
            blue: CGFloat(rgb & 0xFF) / 255,
            alpha: alpha
        )
    }
}
