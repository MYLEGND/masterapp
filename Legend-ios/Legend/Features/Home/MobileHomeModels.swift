import Foundation

struct MobileHomeResponse: Codable, Equatable, Sendable {
    let identity: MobileHomeIdentity
    let messaging: MobileMessagingSummary
    let journey: MobileJourneySummary?
    let upcomingAppointments: [MobileUpcomingAppointment]
    let actions: [MobileActionItem]
    let dailyScripture: MobileDailyScripture
    let activeClientCount: Int
}

struct MobileDailyScripture: Codable, Equatable, Sendable {
    let date: String
    let reference: String
    let translation: String
    let verses: [String]
    let text: String
    let source: String
    let passageText: String

    init(
        date: String,
        reference: String,
        translation: String,
        verses: [String],
        text: String,
        source: String = "DailyCatalog",
        passageText: String? = nil
    ) {
        self.date = date
        self.reference = reference
        self.translation = translation
        self.verses = verses
        self.text = text
        self.source = source
        self.passageText = passageText ?? text
    }

    private enum CodingKeys: String, CodingKey {
        case date, reference, translation, verses, text, source, passageText
    }

    init(from decoder: Decoder) throws {
        let container = try decoder.container(keyedBy: CodingKeys.self)
        self.init(
            date: try container.decode(String.self, forKey: .date),
            reference: try container.decode(String.self, forKey: .reference),
            translation: try container.decode(String.self, forKey: .translation),
            verses: try container.decodeIfPresent([String].self, forKey: .verses) ?? [],
            text: try container.decodeIfPresent(String.self, forKey: .text) ?? "",
            source: try container.decodeIfPresent(String.self, forKey: .source) ?? "DailyCatalog",
            passageText: try container.decodeIfPresent(String.self, forKey: .passageText))
    }
}

struct MobileHomeIdentity: Codable, Equatable, Sendable {
    let userID: String
    let participantType: ParticipantType
    let profileID: UUID
    let displayName: String

    private enum CodingKeys: String, CodingKey {
        case userID = "userId"
        case participantType
        case profileID = "profileId"
        case displayName
    }
}

struct MobileMessagingSummary: Codable, Equatable, Sendable {
    let unreadCount: Int
    let conversationCount: Int
}

struct MobileJourneySummary: Codable, Equatable, Sendable {
    let hasProfile: Bool
    let recommendationCount: Int
    let connectedPeerCount: Int
    let pendingRequestCount: Int
}

struct MobileUpcomingAppointment: Codable, Equatable, Identifiable, Sendable {
    let id: UUID
    let startUTC: Date
    let endUTC: Date?
    let status: String

    private enum CodingKeys: String, CodingKey {
        case id, status
        case startUTC = "startUtc"
        case endUTC = "endUtc"
    }
}

struct MobileActionItem: Codable, Equatable, Identifiable, Sendable {
    let id: UUID
    let title: String
    let status: String
    let priority: String
    let dueDateUTC: Date?

    private enum CodingKeys: String, CodingKey {
        case id, title, status, priority
        case dueDateUTC = "dueDateUtc"
    }
}

struct MobileFinancialSnapshotResponse: Codable, Equatable, Sendable {
    let position: MobileFinancialPosition?
    let intelligence: MobileFinancialIntelligenceSummary?
    let upcomingBills: [MobileUpcomingBill]
    let operatingSystem: MobileFinancialOperatingSystemSnapshotResponse?
    var presentation: MobileFinancialPresentationResponse? = nil
    var healthSnapshot: MobileFinancialHealthSnapshotResponse? = nil
}

/// Read-only representation of the server-authoritative Financial Health
/// Snapshot. It carries calculated section rows and totals for native display;
/// the app never derives balances from these values.
struct MobileFinancialHealthSnapshotResponse: Codable, Equatable, Sendable {
    let updatedUTC: Date
    let sections: [MobileFinancialHealthSectionResponse]

    private enum CodingKeys: String, CodingKey {
        case sections
        case updatedUTC = "updatedUtc"
    }

    func section(
        for destination: MobileFinancialDetailDestination
    ) -> MobileFinancialHealthSectionResponse? {
        guard let key = destination.healthSectionKey else { return nil }
        return sections.first { $0.key == key }
    }
}

struct MobileFinancialHealthSectionResponse:
    Codable,
    Equatable,
    Identifiable,
    Sendable
{
    let key: String
    let title: String
    let semantic: String
    let period: String?
    let groups: [MobileFinancialHealthGroupResponse]
    let total: MobileFinancialHealthMetricResponse?

    var id: String { key }
}

struct MobileFinancialHealthGroupResponse:
    Codable,
    Equatable,
    Identifiable,
    Sendable
{
    let key: String
    let title: String?
    let metrics: [MobileFinancialHealthMetricResponse]

    var id: String { key }
}

struct MobileFinancialHealthMetricResponse:
    Codable,
    Equatable,
    Identifiable,
    Sendable
{
    let key: String
    let label: String
    let valueType: String
    let amountCents: Int64?
    let numericValue: Decimal?
    let textValue: String?
    let status: String?

    var id: String { key }

    var displayValue: String {
        switch valueType {
        case "Currency":
            guard let amountCents else { return "Not available" }
            return MobileFinancialDisplay.currency(cents: amountCents)

        case "Percentage":
            guard let numericValue else { return "Not available" }
            return numericValue.formatted(
                .percent.precision(.fractionLength(0...2)))

        default:
            return textValue?.trimmingCharacters(
                in: .whitespacesAndNewlines).isEmpty == false
                ? textValue!
                : "Not available"
        }
    }
}

struct MobileFinancialPresentationResponse: Codable, Equatable, Sendable {
    let prioritySections: [MobileFinancialPrioritySectionResponse]
}

struct MobileFinancialPrioritySectionResponse:
    Codable,
    Equatable,
    Identifiable,
    Sendable
{
    let key: String
    let eyebrow: String
    let title: String
    let systemImage: String
    let priority: Int
    let status: String
    let reason: String
    let primaryMetric: MobileFinancialSummaryMetricResponse
    let secondaryMetric: MobileFinancialSummaryMetricResponse?

    var id: String { key }
}

struct MobileFinancialSummaryMetricResponse: Codable, Equatable, Sendable {
    let label: String
    let amountCents: Int64?
    let date: String?
    let textValue: String?
    let semantic: MobileFinancialSemantic

    var displayValue: String {
        if let amountCents {
            return (Decimal(amountCents) / Decimal(100))
                .formatted(.currency(code: "USD"))
        }

        if let date {
            return MobileFinancialDisplay.date(date)
        }

        return textValue ?? "Not available"
    }
}

enum MobileFinancialSemantic: String, Codable, Equatable, Sendable {
    case positive
    case caution
    case negative
    case neutral
    case informational

    var tone: LegendNextTone {
        switch self {
        case .positive:
            return .success
        case .caution:
            return .warning
        case .negative:
            return .danger
        case .neutral:
            return .neutral
        case .informational:
            return .information
        }
    }
}

/// Maps the server-authoritative financial section key to the one native
/// detail destination that renders its complete read-only breakdown.
enum MobileFinancialDetailDestination: String, Hashable, Identifiable, Sendable {
    case assets
    case liabilities
    case cashFlow = "cash-flow"
    case protection
    case taxProfile = "tax-profile"
    case currentOutlook = "current-outlook"
    case monthlyOutlook = "monthly-outlook"
    case debtObligations = "debt-obligations"
    case financialPosition = "financial-position"
    case upcomingActivity = "upcoming-activity"
    case protectionDiscussion = "protection-discussion"
    case dataAttention = "data-attention"

    var id: String { rawValue }

    var title: String {
        switch self {
        case .assets:
            return "Assets"
        case .liabilities:
            return "Liabilities"
        case .cashFlow:
            return "Cash Flow"
        case .protection:
            return "Protection"
        case .taxProfile:
            return "Tax Profile"
        case .currentOutlook:
            return "Current Outlook"
        case .monthlyOutlook:
            return "Month at a Glance"
        case .debtObligations:
            return "Debt & Obligations"
        case .financialPosition:
            return "Balance Sheet"
        case .upcomingActivity:
            return "Upcoming Activity"
        case .protectionDiscussion:
            return "Protection Discussion"
        case .dataAttention:
            return "Data Needing Attention"
        }
    }

    var healthSectionKey: String? {
        switch self {
        case .assets, .liabilities, .cashFlow, .protection, .taxProfile:
            return rawValue
        case .currentOutlook,
                .monthlyOutlook,
                .debtObligations,
                .financialPosition,
                .upcomingActivity,
                .protectionDiscussion,
                .dataAttention:
            return nil
        }
    }
}

enum MobileFinancialAmountKind: Sendable {
    case assets
    case liabilities
    case netWorth
    case income
    case bills
    case debt
    case endingCash
    case endingDebt
    case openingCash
    case payoffProgress
    case historical
}


enum MobileFinancialAmountSemantic {
    static func tone(
        for amount: Decimal,
        kind: MobileFinancialAmountKind
    ) -> LegendNextTone {
        // Sign always wins. A negative financial value is never presented as
        // a positive/success state, regardless of its category.
        if amount < 0 {
            return .danger
        }

        guard amount > 0 else {
            return .neutral
        }

        switch kind {
        case .assets,
             .income,
             .netWorth,
             .endingCash,
             .openingCash,
             .payoffProgress:
            return .success

        case .liabilities:
            return .danger

        case .bills,
             .debt,
             .endingDebt:
            return .danger

        case .historical:
            return .neutral
        }
    }

    static func tone(
        forCents amountCents: Int64,
        kind: MobileFinancialAmountKind
    ) -> LegendNextTone {
        tone(
            for:
                Decimal(amountCents)
                / Decimal(100),
            kind: kind
        )
    }

    static func tone(
        forStatus status: String
    ) -> LegendNextTone {
        let normalized = status
            .trimmingCharacters(
                in: .whitespacesAndNewlines
            )
            .lowercased()

        if containsAny(
            normalized,
            [
                "critical",
                "danger",
                "severe",
                "exposed",
                "negative",
                "high risk",
                "shortfall",
                "deficit",
                "overdue",
                "past due",
                "delinquent"
            ]
        ) {
            return .danger
        }

        if containsAny(
            normalized,
            [
                "warning",
                "watch",
                "tight",
                "pressure",
                "moderate",
                "review",
                "incomplete",
                "attention",
                "upcoming",
                "due soon",
                "scheduled obligation"
            ]
        ) {
            return .warning
        }

        if containsAny(
            normalized,
            [
                "healthy",
                "strong",
                "excellent",
                "stable",
                "positive",
                "improving",
                "clear",
                "low risk",
                "on track",
                "complete"
            ]
        ) {
            return .success
        }

        if containsAny(
            normalized,
            [
                "progress",
                "building",
                "active",
                "scheduled",
                "current",
                "informational",
                "available",
                "updated",
                "projected"
            ]
        ) {
            return .information
        }

        return .neutral
    }

    static func tone(
        forEventKind eventKind: String,
        amountCents: Int64
    ) -> LegendNextTone {
        let normalized = eventKind
            .trimmingCharacters(
                in: .whitespacesAndNewlines
            )
            .lowercased()

        if containsAny(
            normalized,
            [
                "income",
                "paycheck",
                "payroll",
                "deposit",
                "revenue",
                "refund",
                "reimbursement",
                "cash inflow",
                "cashflow in",
                "cash flow in",
                "contribution",
                "savings",
                "interest",
                "dividend"
            ]
        ) {
            return tone(
                forCents: amountCents,
                kind: .income
            )
        }

        if containsAny(
            normalized,
            [
                "bill",
                "expense",
                "spending",
                "purchase",
                "payment",
                "withdrawal",
                "cash outflow",
                "cashflow out",
                "cash flow out",
                "fee",
                "charge",
                "premium",
                "rent",
                "mortgage",
                "utility"
            ]
        ) {
            return amountCents == 0
                ? .neutral
                : .danger
        }

        if containsAny(
            normalized,
            [
                "debt",
                "loan",
                "credit card",
                "liability",
                "principal",
                "payoff"
            ]
        ) {
            return amountCents == 0
                ? .neutral
                : .danger
        }

        if containsAny(
            normalized,
            [
                "warning",
                "attention",
                "upcoming",
                "due",
                "scheduled"
            ]
        ) {
            return .warning
        }

        if containsAny(
            normalized,
            [
                "current",
                "projection",
                "forecast",
                "informational",
                "historical"
            ]
        ) {
            return amountCents < 0
                ? .danger
                : .information
        }

        return amountCents > 0
            ? .information
            : amountCents < 0
                ? .danger
                : .neutral
    }

    private static func containsAny(
        _ value: String,
        _ candidates: [String]
    ) -> Bool {
        candidates.contains {
            value.contains($0)
        }
    }
}

/// The shared visual semantics for every native Financial Health Snapshot
/// section. It only selects presentation tone from server-supplied facts; it
/// never alters, calculates, or reclassifies financial values.
enum LegendFinancialPresentation {
    static func sectionTone(for semantic: String) -> LegendNextTone {
        switch semantic.trimmingCharacters(in: .whitespacesAndNewlines)
            .lowercased() {
        case "assets":
            return .information
        case "liabilities":
            return .danger
        case "cash-flow":
            return .success
        case "protection", "tax-profile":
            return .gold
        default:
            return .neutral
        }
    }

    static func metricTone(
        _ metric: MobileFinancialHealthMetricResponse,
        sectionSemantic: String
    ) -> LegendNextTone {
        if let amountCents = metric.amountCents {
            if amountCents < 0 {
                return .danger
            }

            if amountCents == 0 {
                return .neutral
            }
        }

        if let status = metric.status,
           !status.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
            return statusTone(status, fallback: sectionTone(for: sectionSemantic))
        }

        let descriptor = "\(metric.key) \(metric.label)"
            .lowercased()

        if containsAny(
            descriptor,
            ["cost", "debt", "liabilit", "obligation", "outflow"]
        ) {
            return .danger
        }

        if descriptor.contains("tax") {
            return .warning
        }

        if containsAny(
            descriptor,
            ["earnings", "income", "savings", "lifestyle remaining"]
        ) {
            return .success
        }

        return sectionTone(for: sectionSemantic)
    }

    static func statusTone(
        _ status: String?,
        fallback: LegendNextTone
    ) -> LegendNextTone {
        switch status?.trimmingCharacters(in: .whitespacesAndNewlines)
            .lowercased() {
        case "protected", "covered", "low", "current", "complete":
            return .success
        case "partial", "moderate", "review", "pending":
            return .warning
        case "exposed", "high", "not covered", "critical", "negative":
            return .danger
        default:
            return fallback
        }
    }

    private static func containsAny(
        _ value: String,
        _ terms: [String]
    ) -> Bool {
        terms.contains { value.contains($0) }
    }
}

enum MobileFinancialDisplay {
    static func date(_ value: String) -> String {
        let parts = value.split(separator: "-")

        guard parts.count == 3,
              let month = Int(parts[1]),
              let day = Int(parts[2]),
              (1...Calendar.current.shortMonthSymbols.count).contains(month) else {
            return value
        }

        return "\(Calendar.current.shortMonthSymbols[month - 1]) \(day)"
    }

    static func month(_ value: String) -> String {
        let parts = value.split(separator: "-")

        guard parts.count >= 2,
              let year = Int(parts[0]),
              let month = Int(parts[1]),
              (1...Calendar.current.monthSymbols.count).contains(month) else {
            return value
        }

        return "\(Calendar.current.monthSymbols[month - 1]) \(year)"
    }

    static func currency(cents: Int64) -> String {
        (Decimal(cents) / Decimal(100))
            .formatted(.currency(code: "USD"))
    }
}

struct MobileFinancialOperatingSystemSnapshotResponse: Codable, Equatable, Sendable {
    let projection: MobileFinancialProjectionStatusResponse
    let freshness: MobileFinancialDataFreshnessResponse
    let weekAtGlance: MobileFinancialWeekAtGlanceResponse?
    let monthAtGlance: MobileFinancialMonthAtGlanceResponse?
    let tools: [MobileFinancialToolSummaryResponse]
}

struct MobileFinancialProjectionStatusResponse: Codable, Equatable, Sendable {
    let status: String
    let reasonCode: String?
    let summary: String?
}

struct MobileFinancialDataFreshnessResponse: Codable, Equatable, Sendable {
    let financeStateUpdatedUTC: Date?
    let intelligenceEvaluatedUTC: Date?
    let generatedUTC: Date

    private enum CodingKeys: String, CodingKey {
        case financeStateUpdatedUTC = "financeStateUpdatedUtc"
        case intelligenceEvaluatedUTC = "intelligenceEvaluatedUtc"
        case generatedUTC = "generatedUtc"
    }
}

struct MobileFinancialWeekAtGlanceResponse: Codable, Equatable, Sendable {
    let weekKey: String
    let startDate: String
    let endDate: String
    let openingCashCents: Int64
    let incomeCents: Int64
    let debitExpenseCents: Int64
    let creditExpenseCents: Int64
    let requiredDebtPaymentCents: Int64
    let extraDebtPaymentCents: Int64
    let endingCashCents: Int64
    let openingDebtCents: Int64
    let endingDebtCents: Int64
    let pressureStatus: String
    let pressureSummary: String?
    let events: [MobileFinancialCashFlowEventResponse]
}

struct MobileFinancialMonthAtGlanceResponse: Codable, Equatable, Sendable {
    let monthKey: String
    let startDate: String
    let endDate: String
    let openingCashCents: Int64
    let incomeCents: Int64
    let debitExpenseCents: Int64
    let creditExpenseCents: Int64
    let requiredDebtPaymentCents: Int64
    let extraDebtPaymentCents: Int64
    let endingCashCents: Int64
    let openingDebtCents: Int64
    let endingDebtCents: Int64
    let savingsContributionCents: Int64
    let pressureStatus: String
    let pressureSummary: String?
    let largestObligation: MobileFinancialLargestObligationResponse?
    let weeks: [MobileFinancialWeekSummaryResponse]
}

struct MobileFinancialCashFlowEventResponse:
    Codable,
    Equatable,
    Identifiable,
    Sendable
{
    let eventKey: String
    let occursOn: String
    let kind: String
    let title: String
    let amountCents: Int64
    let sourceToolId: String?
    let sourceItemId: String?
    let status: String

    var id: String { eventKey }
}

struct MobileFinancialWeekSummaryResponse:
    Codable,
    Equatable,
    Identifiable,
    Sendable
{
    let weekKey: String
    let startDate: String
    let endDate: String
    let incomeCents: Int64
    let outflowCents: Int64
    let endingCashCents: Int64
    let endingDebtCents: Int64
    let pressureStatus: String

    var id: String { weekKey }
}

struct MobileFinancialLargestObligationResponse:
    Codable,
    Equatable,
    Sendable
{
    let title: String
    let occursOn: String
    let amountCents: Int64
    let kind: String
}

struct MobileFinancialToolSummaryResponse:
    Codable,
    Equatable,
    Identifiable,
    Sendable
{
    let toolId: String
    let title: String
    let category: String
    let priority: Int
    let availabilityStatus: String
    let updatedUTC: Date?
    let summary: String?
    let metrics: [MobileFinancialMetricResponse]

    var id: String { toolId }

    private enum CodingKeys: String, CodingKey {
        case toolId, title, category, priority, availabilityStatus, summary, metrics
        case updatedUTC = "updatedUtc"
    }
}

struct MobileFinancialMetricResponse: Codable, Equatable, Sendable {
    let key: String
    let label: String
    let valueType: String
    let amountCents: Int64?
    let numericValue: Decimal?
    let textValue: String?
    let status: String?
}

struct MobileFinancialPosition: Codable, Equatable, Sendable {
    let healthScore: Int
    let assetsTotal: Decimal
    let liabilitiesTotal: Decimal
    let netWorth: Decimal
    let annualEarnings: Decimal
    let annualLifestyleRemaining: Decimal
    let annualTaxes: Decimal
    let protectionGapTotal: Decimal
    let positionStatus: String
    let positionSummary: String
    let estatePlanningStatus: String
    let estatePlanningRiskLevel: String
    let updatedUTC: Date

    private enum CodingKeys: String, CodingKey {
        case healthScore, assetsTotal, liabilitiesTotal, netWorth, annualEarnings, annualLifestyleRemaining
        case annualTaxes, protectionGapTotal, positionStatus, positionSummary, estatePlanningStatus, estatePlanningRiskLevel
        case updatedUTC = "updatedUtc"
    }
}

struct MobileFinancialIntelligenceSummary: Codable, Equatable, Sendable {
    let status: String
    let dataCompletenessScore: Decimal
    let currentRiskSummary: String
    let currentOpportunitySummary: String
    let currentLeakageSummary: String
    let lastEvaluatedUTC: Date?
    let findings: [MobileFinancialFinding]

    private enum CodingKeys: String, CodingKey {
        case status, dataCompletenessScore, currentRiskSummary, currentOpportunitySummary, currentLeakageSummary, findings
        case lastEvaluatedUTC = "lastEvaluatedUtc"
    }
}

struct MobileFinancialFinding: Codable, Equatable, Identifiable, Sendable {
    let id: UUID
    let category: String
    let title: String
    let explanation: String
    let estimatedImpact: Decimal?
    let impactUnit: String?
    let urgency: String
    let status: String
    let lastDetectedUTC: Date

    private enum CodingKeys: String, CodingKey {
        case id, category, title, explanation, estimatedImpact, impactUnit, urgency, status
        case lastDetectedUTC = "lastDetectedUtc"
    }
}

struct MobileUpcomingBill: Codable, Equatable, Identifiable, Sendable {
    let id: UUID
    let displayName: String
    let averageAmountCents: Int64
    let cadence: String
    let nextExpectedDateUTC: Date
    let status: String

    private enum CodingKeys: String, CodingKey {
        case id, displayName, averageAmountCents, cadence, status
        case nextExpectedDateUTC = "nextExpectedDateUtc"
    }

    var amount: Decimal { Decimal(averageAmountCents) / 100 }
}

struct MobileAgentClientSummary: Codable, Equatable, Identifiable, Sendable {
    let profileID: UUID
    let displayName: String
    let email: String
    let crmStatus: String
    let avatar: ProfileAvatar?

    var id: UUID { profileID }

    private enum CodingKeys: String, CodingKey {
        case profileID = "profileId"
        case displayName, email, crmStatus, avatar
    }
}

struct MobileAgentLeadSummary: Codable, Equatable, Identifiable, Sendable {
    let leadID: String
    let displayName: String
    let crmStage: String
    let updatedUTC: Date

    var id: String { leadID }

    private enum CodingKeys: String, CodingKey {
        case leadID = "leadId"
        case displayName, crmStage
        case updatedUTC = "updatedUtc"
    }
}

/// A same-origin, short-lived launch location for the existing AgentPortal
/// Create view. The portal itself owns every field, label, validation rule,
/// visual treatment, and CRM write.
struct MobileClientCreationPortalLaunch: Decodable, Equatable, Sendable {
    let launchPath: String
}

struct MobileJourneyDashboardResponse: Codable, Equatable, Sendable {
    let profile: MobileJourneyProfile?
    let preferences: MobileJourneyPreferences?
    let recommendations: [MobileJourneyRecommendation]
    let connections: [MobileJourneyConnection]
    let requests: [MobileJourneyConnection]
    let taxonomy: MobileJourneyTaxonomy
}

struct MobileJourneyProfile: Codable, Equatable, Identifiable, Sendable {
    let clientProfileID: UUID
    let displayName: String
    let introduction: String?
    let lifeStages: [String]
    let locations: [String]
    let goals: [String]
    let interests: [String]
    let circleCodes: [String]
    let connectionTypes: [String]
    let communicationStyles: [String]
    let accountabilityFrequencies: [String]
    let avatar: ProfileAvatar?

    var id: UUID { clientProfileID }

    private enum CodingKeys: String, CodingKey {
        case displayName, introduction, lifeStages, locations, goals, interests, circleCodes, connectionTypes, communicationStyles, accountabilityFrequencies, avatar
        case clientProfileID = "clientProfileId"
    }
}

struct MobileJourneyPreferences: Codable, Equatable, Sendable {
    let consentAffirmed: Bool
    let isOptedIn: Bool
    let isDiscoverable: Bool
    let allowSuggestions: Bool
    let allowConnectionRequests: Bool
}

struct MobileJourneyConnectionRequestBody: Encodable, Sendable {
    let targetClientProfileID: UUID
    let connectionReason: String?
    let introduction: String?

    private enum CodingKeys: String, CodingKey {
        case targetClientProfileID = "targetClientProfileId"
        case connectionReason, introduction
    }
}

struct MobileJourneyProfileInput: Encodable, Sendable {
    let consentAffirmed: Bool
    let isOptedIn: Bool
    let isDiscoverable: Bool
    let allowSuggestions: Bool
    let allowConnectionRequests: Bool
    let introduction: String?
    let lifeStages: [String]
    let locations: [String]
    let goals: [String]
    let interests: [String]
    let circleCodes: [String]
    let connectionTypes: [String]
    let communicationStyles: [String]
    let accountabilityFrequencies: [String]
}

struct MobileJourneyConnectionResponseBody: Encodable, Sendable {
    let accept: Bool
}

struct MobileJourneyReportRequestBody: Encodable, Sendable {
    let category: String
    let detail: String?
}

struct MobileJourneyProfileActionRequest: Encodable, Sendable {}

struct MobileJourneyRecommendation: Codable, Equatable, Identifiable, Sendable {
    let profile: MobileJourneyProfile
    let explanation: String
    var id: UUID { profile.id }
}

struct MobileJourneyConnection: Codable, Equatable, Identifiable, Sendable {
    let id: UUID
    let profile: MobileJourneyProfile
    let status: String
    let connectionReason: String?
    let introduction: String?
    let createdUTC: Date

    private enum CodingKeys: String, CodingKey {
        case id, profile, status, connectionReason, introduction
        case createdUTC = "createdUtc"
    }
}

struct MobileJourneyTaxonomy: Codable, Equatable, Sendable {
    let goals: [String]
    let circles: [String]
    let lifeStages: [String]
    let locations: [String]
    let interests: [String]
    let connectionTypes: [String]
    let communicationStyles: [String]
    let accountabilityFrequencies: [String]
}
