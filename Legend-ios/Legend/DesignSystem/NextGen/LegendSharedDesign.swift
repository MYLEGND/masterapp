import Foundation
import SwiftUI
import UIKit
import Observation

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

    static var messageBubble: MessageBubbleToken { specification.messaging.messageBubble }
    static var contactCard: ContactCardToken { specification.messaging.contactCard }
    struct MessageBubbleToken: Decodable {
        let horizontalPadding, verticalPadding, cornerRadius, metadataGap, bodySize, timestampSize: CGFloat
        let timestampWeight, timestampColor: String
        func timestampFont(size: CGFloat? = nil) -> Font { .system(size: size ?? timestampSize, weight: LegendSharedDesign.fontWeight(timestampWeight)) }
    }
    struct ContactCardToken: Decodable {
        let cornerRadius, minimumHeight, horizontalPadding, verticalPadding, borderWidth, shadowRadius, shadowOffsetY: CGFloat
        let borderOpacity, shadowOpacity: Double
    }
    static var reactionBubble: ReactionBubbleToken { specification.messaging.reactionBubble }
    struct ReactionBubbleToken: Decodable {
        let height: CGFloat
        let touchTarget: CGFloat
        let emojiSize: CGFloat
        let ownFillColor: String
        let ownFillOpacity: Double
        let otherFillColor: String
        let borderColor: String
        let borderOpacity: Double
        let horizontalPadding: CGFloat
        let itemSpacing: CGFloat
        let borderWidth: CGFloat
        let outsideFraction: CGFloat
        let trailingInset: CGFloat
        var overflow: CGFloat { overflow(for: touchTarget) }
        func overflow(for measuredHeight: CGFloat) -> CGFloat {
            max(0, measuredHeight - (touchTarget - height)) * outsideFraction + (touchTarget - height) / 2
        }
    }
    fileprivate struct MessagingToken: Decodable {
        let reactionBubble: ReactionBubbleToken
        let messageBubble: MessageBubbleToken
        let contactCard: ContactCardToken
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
        fileprivate let messaging: MessagingToken
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

    func validatedText(source: String, context: String, revision: String, placeholders: [String]) -> String? {
        guard failureCode == nil, self.source == source, self.context == context,
              sourceRevision == revision, self.placeholders.sorted() == placeholders.sorted(),
              !text.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else { return nil }
        return text
    }
}

struct LegendApplicationLocalizationCatalog: Codable, Equatable, Sendable {
    let catalogVersion: String
    let sourceLanguageCode: String
    let languageCode: String
    let locale: String
    let generatedUtc: String
    let isComplete: Bool
    var entries: [LegendApplicationLocalizedCopy]
    var continuation: LegendApplicationLocalizationContinuation? = nil
}

struct LegendApplicationLocalizationContinuation: Codable, Equatable, Sendable {
    let disposition: String
    let remainingEntries: Int
    let retryAfterSeconds: Int?
    let maximumConsecutiveNoProgress: Int
    let maximumDurationSeconds: Int
    let maximumRequestsPerPass: Int
    let cooldownSeconds: Int

    var isResumable: Bool {
        ["Pending", "RetryableFailure"].contains(disposition) && remainingEntries > 0 &&
        (1...2_678_400).contains(retryAfterSeconds ?? 0) &&
        (1...10).contains(maximumConsecutiveNoProgress) && (1...600).contains(maximumDurationSeconds) &&
        (1...256).contains(maximumRequestsPerPass) && (15...900).contains(cooldownSeconds)
    }
}

// Stored inside the existing actor-scoped localization payload, never a separate translation store.
struct LegendApplicationLocalizationCache: Codable {
    var catalogs: [LegendApplicationLocalizationCatalog] = []

    mutating func remember(_ value: LegendApplicationLocalizationCatalog) -> LegendApplicationLocalizationCatalog {
        catalogs.removeAll { $0.catalogVersion == value.catalogVersion && $0.languageCode == value.languageCode }
        catalogs.append(value)
        catalogs = Array(catalogs.suffix(8))
        return value
    }

    func matching(_ language: String?) -> LegendApplicationLocalizationCatalog? {
        catalogs.last { language == nil || $0.languageCode.caseInsensitiveCompare(language!) == .orderedSame }
    }
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
    let sourceRevision: String
    let placeholders: [String]
}

private struct LegendLocalizationKey: Hashable {
    let source: String
    let context: String
}

/// Thread-safe presentation lookup installed only from the one bundled/server
/// catalog contract. It never calls a provider and never stores a competing
/// language preference.
@Observable
private final class LegendLocalizationPresentation: @unchecked Sendable {
    var translations: [LegendLocalizationKey: String] = [:]
    var locale = Locale(identifier: "en")
}

private enum LegendLocalizationRuntime {
    static let visualContext = "visual interface copy"
    static let accessibilityContext = "accessibility copy"
    private static let lock = NSRecursiveLock()
    private static let presentation = LegendLocalizationPresentation()

    static func install(
        _ values: [LegendLocalizationKey: String],
        locale: Locale
    ) -> Bool {
        lock.lock()
        defer { lock.unlock() }
        guard presentation.translations != values || presentation.locale != locale else { return false }
        presentation.translations = values
        presentation.locale = locale
        return true
    }

    static func text(_ source: String, context: String) -> String {
        lock.lock()
        defer { lock.unlock() }
        return presentation.translations[LegendLocalizationKey(source: source, context: context)] ?? source
    }

    static var locale: Locale {
        lock.lock()
        defer { lock.unlock() }
        return presentation.locale
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
    @Published private(set) var status: String?

    private var requestGeneration = 0
    private var fetchTask: Task<Void, Never>?
    private var foreground = true
    private var resumeRequest: (() -> Void)?
    private var warmCache = LegendApplicationLocalizationCache()
    private var continuation: LegendApplicationLocalizationContinuation?
    private var notBefore = Date.distantPast
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

    func activate(session: MobileSession, coordinator: MobileSessionCoordinator,
                  launchCache: any LegendLaunchCaching) async {
        guard !Task.isCancelled, case .authenticated(let current) = coordinator.state,
              current.actor.identity == session.actor.identity else { return }
        stopFetch()
        let actorKey = Self.actorKey(session)
        if activeActorKey != actorKey {
            warmCache = LegendApplicationLocalizationCache()
            continuation = nil; notBefore = .distantPast
            installSource(actorKey: actorKey)
        }
        if let data = launchCache.readPayload(.localization, actorKey: actorKey) {
            if let saved = try? JSONDecoder.mobile.decode(LegendApplicationLocalizationCache.self, from: data) {
                warmCache.catalogs = saved.catalogs.filter(isPresentable)
            } else if let legacy = try? JSONDecoder.mobile.decode(LegendApplicationLocalizationCatalog.self, from: data), isPresentable(legacy) {
                _ = warmCache.remember(legacy)
            }
        }
        applyWarm(session.preferredLanguageCode, actorKey: actorKey)
        configureRequest(session: session, coordinator: coordinator, launchCache: launchCache)
    }

    func clearPresentation() {
        stopFetch()
        resumeRequest = nil; warmCache = LegendApplicationLocalizationCache()
        continuation = nil; notBefore = .distantPast; status = nil
        installSource(actorKey: nil)
    }

    func refresh(session: MobileSession, coordinator: MobileSessionCoordinator,
                 launchCache: any LegendLaunchCaching, preferredLanguageCode: String? = nil) async {
        guard !Task.isCancelled, case .authenticated(let current) = coordinator.state,
              current.actor.identity == session.actor.identity else { return }
        guard activeActorKey == Self.actorKey(session) else {
            await activate(session: session, coordinator: coordinator, launchCache: launchCache)
            return
        }
        stopFetch()
        if preferredLanguageCode != nil { continuation = nil; notBefore = .distantPast }
        applyWarm(preferredLanguageCode, actorKey: Self.actorKey(session))
        // The caller supplies the actual successful account-save response. Effective catalog locale is not a preference.
        if let preferredLanguageCode, !preferredLanguageCode.isEmpty,
           let prior = launchCache.readSession(), prior.actor.identity == session.actor.identity,
           prior.credentialFingerprint != nil {
            launchCache.writeSession(MobileSessionCacheEntry(actor: prior.actor, capabilities: prior.capabilities,
                permittedParticipantTypes: prior.permittedParticipantTypes, cachedUtc: prior.cachedUtc,
                credentialFingerprint: prior.credentialFingerprint, preferredLanguageCode: preferredLanguageCode))
        }
        configureRequest(session: session, coordinator: coordinator, launchCache: launchCache)
    }

    func setForeground(_ value: Bool) {
        guard foreground != value else { return }
        foreground = value
        if value { resumeRequest?() } else { stopFetch() }
    }

    private func stopFetch() { requestGeneration += 1; fetchTask?.cancel(); fetchTask = nil }

    private func applyWarm(_ language: String?, actorKey: String) {
        if let cached = warmCache.matching(language) { apply(cached, actorKey: actorKey) }
        else if let language, languageCode.caseInsensitiveCompare(language) != .orderedSame { installSource(actorKey: actorKey) }
    }

    private func configureRequest(session: MobileSession, coordinator: MobileSessionCoordinator, launchCache: any LegendLaunchCaching) {
        resumeRequest = { [weak self, weak coordinator] in
            guard let self, let coordinator, self.foreground, self.fetchTask == nil,
                  case .authenticated(let current) = coordinator.state,
                  current.actor.identity == session.actor.identity else { return }
            let generation = self.requestGeneration
            self.fetchTask = Task { [weak self] in
                guard let self else { return }
                await self.fetchCatalog(session: session, coordinator: coordinator, launchCache: launchCache, generation: generation)
                if self.requestGeneration == generation { self.fetchTask = nil }
            }
        }
        resumeRequest?()
    }

    private func fetchCatalog(session: MobileSession, coordinator: MobileSessionCoordinator,
                              launchCache: any LegendLaunchCaching, generation: Int) async {
        let actorKey = Self.actorKey(session)
        var started = ContinuousClock.now
        var requests = 0
        var unchanged = 0
        var identity: String?
        var remaining = Int.max
        var transportFailures = 0
        while foreground && generation == requestGeneration && !Task.isCancelled {
            do {
                let delay = notBefore.timeIntervalSinceNow
                if delay > 0 { try await Task.sleep(for: .seconds(delay)) }
                guard generation == requestGeneration, !Task.isCancelled,
                      case .authenticated(let current) = coordinator.state,
                      current.actor.identity == session.actor.identity else { return }
                let catalog = try await coordinator.applicationLocalizationCatalog(participantType: session.actor.identity.participantType)
                guard generation == requestGeneration, !Task.isCancelled,
                      case .authenticated(let current) = coordinator.state,
                      current.actor.identity == session.actor.identity else { return }
                guard isPresentable(catalog) else { status = LegendSharedDesign.copy("localization.unavailable"); return }
                apply(warmCache.remember(catalog), actorKey: actorKey)
                if let data = try? JSONEncoder.mobile.encode(warmCache) { launchCache.writePayload(data, kind: .localization, actorKey: actorKey) }
                transportFailures = 0
                continuation = catalog.continuation
                guard let next = catalog.continuation, next.isResumable else {
                    status = catalog.isComplete ? nil : LegendSharedDesign.copy("localization.unavailable")
                    return
                }
                let key = catalog.catalogVersion + "\n" + catalog.languageCode
                unchanged = identity == key && next.remainingEntries >= remaining ? unchanged + 1 : 0
                identity = key; remaining = next.remainingEntries; requests += 1
                let exhausted = unchanged >= next.maximumConsecutiveNoProgress || requests >= next.maximumRequestsPerPass ||
                    started.duration(to: .now) >= .seconds(next.maximumDurationSeconds)
                notBefore = Date().addingTimeInterval(Double(max(next.retryAfterSeconds!, exhausted ? next.cooldownSeconds : 0)))
                status = LegendSharedDesign.copy("localization.updating")
                if exhausted { started = .now; requests = 0; unchanged = 0; remaining = .max }
            } catch {
                guard generation == requestGeneration, !Task.isCancelled else { return }
                if let failure = error as? MobileAPIError {
                    switch failure {
                    case .unauthorized, .forbidden, .apiUnauthorized, .apiForbidden:
                        clearPresentation(); return
                    case .apiServer(let code, _, _, _), .server(let code, _):
                        if (400..<500).contains(code) && code != 408 && code != 429 { continuation = nil; status = LegendSharedDesign.copy("localization.unavailable"); return }
                    case .invalidBaseURL, .invalidPath, .invalidServerResponse, .decodingFailed:
                        continuation = nil; status = LegendSharedDesign.copy("localization.unavailable"); return
                    default: break
                    }
                }
                status = LegendSharedDesign.copy("localization.unavailable")
                transportFailures += 1
                let delay = min(60, pow(2.0, Double(min(transportFailures, 6))))
                notBefore = Date().addingTimeInterval(max(delay, Double(max(continuation?.retryAfterSeconds ?? 0, continuation?.cooldownSeconds ?? 0))))
                started = .now; requests = 0; unchanged = 0; remaining = .max
            }
        }
    }

    private func apply(
        _ catalog: LegendApplicationLocalizationCatalog,
        actorKey: String
    ) {
        let byID = Dictionary(uniqueKeysWithValues: catalog.entries.map { ($0.id, $0) })
        let values = Dictionary(uniqueKeysWithValues: sourceManifest.entries.map { source in
            let text = byID[source.id]?.validatedText(source: source.source, context: source.context, revision: source.sourceRevision, placeholders: source.placeholders)
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
        // Catalog releases may differ across installed apps. Each entry is
        // checked against its immutable source contract before presentation.
        return catalog.sourceLanguageCode == sourceManifest.sourceLanguageCode &&
            !catalog.languageCode.isEmpty && !catalog.entries.isEmpty &&
            Set(catalog.entries.map(\.id)).count == catalog.entries.count
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
        let presentationChanged = LegendLocalizationRuntime.install(values, locale: resolvedLocale)
        // Cache hydration and the server response commonly contain identical
        // copy. Republishing that catalog tears down the authenticated shell
        // and restarts all of its account-scoped requests.
        guard presentationChanged || activeActorKey != actorKey ||
                self.languageCode != languageCode else { return }
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
