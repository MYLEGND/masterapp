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

    static func tracking(_ name: String) -> CGFloat {
        CGFloat(required(specification.typography[name], named: "typography \(name)").tracking ?? 0)
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

    static func copy(_ key: String) -> String {
        LegendLocalized(required(specification.copy[key], named: "copy \(key)"))
    }

    static func socialFormat(_ name: String) -> SocialFormatToken {
        required(specification.socialFormats[name], named: "social format \(name)")
    }

    /// Shared account-retention rules used by each native mobile client.
    static var accountSession: AccountSessionToken {
        specification.accountSession
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
        fileprivate let socialFormats: [String: SocialFormatToken]
        fileprivate let motion: MotionToken
        fileprivate let elevation: [String: ElevationToken]
        fileprivate let copy: [String: String]
        fileprivate let accountSession: AccountSessionToken
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
        let tracking: Double?
    }

    struct SocialFormatToken: Decodable {
        let maximumMediaItems: Int
        let allowsTextOnlyPublication: Bool
        let acceptsImages: Bool
        let acceptsVideos: Bool
        let maximumVideoDurationSeconds: Double?
        let mediaAspectRatio: Double
        let selectionThumbnailSide: Double
        let emptyPreviewHeight: Double
        let editorMaximumWidth: Double
        let usesFixedCanvasAspectRatio: Bool
        let supportedCanvasAspectRatios: [Double]
    }

    struct AccountSessionToken: Decodable {
        let interactiveSignInRetentionDays: Int
        let profileDoubleTapCyclesAccount: Bool
        let allowsAdditionalSignedInAccounts: Bool
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

struct LegendApplicationLocalizedCopy: Codable, Equatable, Sendable {
    let id: String
    let source: String
    let text: String
    let context: String
    let sourceRevision: String
    let placeholders: [String]
    let provider: String
    let provenance: String
    let validationState: String
    let createdUtc: String
    let reused: Bool
    let failureCode: String?
}

struct LegendApplicationLocalizationCatalog: Codable, Equatable, Sendable {
    let catalogVersion: String
    let sourceLanguageCode: String
    let languageCode: String
    let locale: String
    let generatedUtc: String
    let isComplete: Bool
    let entries: [LegendApplicationLocalizedCopy]
}

private struct LegendBundledApplicationCopyManifest: Decodable {
    let catalogVersion: String
    let sourceLanguageCode: String
    let entries: [LegendBundledApplicationCopy]
}

private struct LegendBundledApplicationCopy: Decodable {
    let id: String
    let source: String
    let context: String
}

private struct LegendLocalizationKey: Hashable {
    let source: String
    let context: String
}

/// Thread-safe presentation lookup installed only from the one bundled/server
/// catalog contract. It never calls a provider and never stores a competing
/// language preference.
private enum LegendLocalizationRuntime {
    static let visualContext = "visual interface copy"
    static let accessibilityContext = "accessibility copy"
    private static let lock = NSLock()
    nonisolated(unsafe) private static var translations: [LegendLocalizationKey: String] = [:]
    nonisolated(unsafe) private static var activeLocale = Locale(identifier: "en")

    static func install(
        _ values: [LegendLocalizationKey: String],
        locale: Locale
    ) {
        lock.lock()
        translations = values
        activeLocale = locale
        lock.unlock()
    }

    static func text(_ source: String, context: String) -> String {
        lock.lock()
        defer { lock.unlock() }
        return translations[LegendLocalizationKey(source: source, context: context)] ?? source
    }

    static var locale: Locale {
        lock.lock()
        defer { lock.unlock() }
        return activeLocale
    }
}

func LegendLocalized(
    _ source: String,
    context: String = "visual interface copy"
) -> String {
    LegendLocalizationRuntime.text(source, context: context)
}

/// Locale formatting uses the same runtime catalog installation as copy.
/// This exposes presentation locale only; it is not a second preference.
func LegendActiveLocale() -> Locale {
    LegendLocalizationRuntime.locale
}

func LegendLocalized(
    _ source: String,
    context: String,
    arguments: [String: CustomStringConvertible]
) -> String {
    LegendLocalized(source, arguments: arguments, context: context)
}

func LegendLocalized(
    _ source: String,
    arguments: [String: CustomStringConvertible],
    context: String = "visual interface copy"
) -> String {
    arguments.reduce(LegendLocalizationRuntime.text(source, context: context)) {
        $0.replacingOccurrences(of: "{\($1.key)}", with: $1.value.description)
    }
}

@MainActor
final class LegendApplicationLocalization: ObservableObject {
    @Published private(set) var activeActorKey: String?
    @Published private(set) var languageCode = "en"
    @Published private(set) var locale = Locale(identifier: "en")
    @Published private(set) var revision = 0

    private let sourceManifest: LegendBundledApplicationCopyManifest

    init() {
        guard let url = Bundle.main.url(
            forResource: "legend-application-copy",
            withExtension: "json"
        ), let data = try? Data(contentsOf: url),
           let manifest = try? JSONDecoder().decode(
            LegendBundledApplicationCopyManifest.self,
            from: data
           ) else {
            preconditionFailure("Missing or invalid canonical application-copy manifest.")
        }
        sourceManifest = manifest
        installSource(actorKey: nil)
    }

    func isReady(for session: MobileSession) -> Bool {
        activeActorKey == Self.actorKey(session)
    }

    func activate(
        session: MobileSession,
        coordinator: MobileSessionCoordinator,
        launchCache: any LegendLaunchCaching
    ) async {
        let actorKey = Self.actorKey(session)
        if let cachedData = launchCache.readPayload(.localization, actorKey: actorKey),
           let cached = try? JSONDecoder.mobile.decode(
            LegendApplicationLocalizationCatalog.self,
            from: cachedData
           ), isPresentable(cached), session.preferredLanguageCode == nil ||
            cached.languageCode.caseInsensitiveCompare(session.preferredLanguageCode!) == .orderedSame {
            apply(cached, actorKey: actorKey)
        }

        // First use must never hold the authenticated shell behind network or
        // provider latency. Present one internally consistent source catalog
        // until the complete preferred-language catalog is ready to swap in.
        if activeActorKey != actorKey {
            installSource(actorKey: actorKey)
        }

        do {
            let catalog = try await coordinator.applicationLocalizationCatalog(
                participantType: session.actor.identity.participantType)
            guard isPresentable(catalog),
                  session.preferredLanguageCode == nil ||
                    catalog.languageCode.caseInsensitiveCompare(
                        session.preferredLanguageCode!) == .orderedSame else {
                if activeActorKey != actorKey { installSource(actorKey: actorKey) }
                return
            }
            apply(catalog, actorKey: actorKey)
            if let data = try? JSONEncoder.mobile.encode(catalog) {
                launchCache.writePayload(data, kind: .localization, actorKey: actorKey)
            }
        } catch {
            // Cached or packaged source copy already provides the fail-safe.
        }
    }

    func clearPresentation() {
        installSource(actorKey: nil)
    }

    func refresh(
        session: MobileSession,
        coordinator: MobileSessionCoordinator,
        launchCache: any LegendLaunchCaching
    ) async {
        let actorKey = Self.actorKey(session)
        do {
            let catalog = try await coordinator.applicationLocalizationCatalog(
                participantType: session.actor.identity.participantType)
            guard isPresentable(catalog) else { return }
            apply(catalog, actorKey: actorKey)
            if let data = try? JSONEncoder.mobile.encode(catalog) {
                launchCache.writePayload(data, kind: .localization, actorKey: actorKey)
            }
        } catch {
            // Keep the last known complete catalog (or source fallback) active.
        }
    }

    private func apply(
        _ catalog: LegendApplicationLocalizationCatalog,
        actorKey: String
    ) {
        let byID = Dictionary(uniqueKeysWithValues: catalog.entries.map { ($0.id, $0) })
        let values = Dictionary(uniqueKeysWithValues: sourceManifest.entries.map { source in
            let text = byID[source.id]?.text.trimmingCharacters(in: .whitespacesAndNewlines)
            return (
                LegendLocalizationKey(source: source.source, context: source.context),
                text?.isEmpty == false ? text! : source.source
            )
        })
        install(values, languageCode: catalog.locale, actorKey: actorKey)
    }

    private func isPresentable(
        _ catalog: LegendApplicationLocalizationCatalog
    ) -> Bool {
        let expectedIDs = Set(sourceManifest.entries.map(\.id))
        return catalog.catalogVersion == sourceManifest.catalogVersion &&
            Set(catalog.entries.map(\.id)) == expectedIDs &&
            !catalog.entries.contains { entry in
                entry.failureCode != nil &&
                    entry.failureCode != "approved_translation_unavailable"
            }
    }

    private func installSource(actorKey: String?) {
        install(
            Dictionary(uniqueKeysWithValues: sourceManifest.entries.map {
                (LegendLocalizationKey(source: $0.source, context: $0.context), $0.source)
            }),
            languageCode: sourceManifest.sourceLanguageCode,
            actorKey: actorKey)
    }

    private func install(
        _ values: [LegendLocalizationKey: String],
        languageCode: String,
        actorKey: String?
    ) {
        let resolvedLocale = Locale(identifier: languageCode.replacingOccurrences(of: "-", with: "_"))
        LegendLocalizationRuntime.install(values, locale: resolvedLocale)
        self.languageCode = languageCode
        locale = resolvedLocale
        activeActorKey = actorKey
        revision += 1
    }

    private static func actorKey(_ session: MobileSession) -> String {
        legendLaunchActorKey(session.actor.identity)
    }
}

extension Notification.Name {
    static let legendPreferredLanguageDidChange = Notification.Name(
        "LegendPreferredLanguageDidChange")
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
