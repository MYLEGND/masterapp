import Foundation

struct MobileHomeResponse: Codable, Equatable, Sendable {
    let identity: MobileHomeIdentity
    let messaging: MobileMessagingSummary
    let subscription: MobileSubscriptionSummary?
    let entitlement: MobileEntitlementSummary?
    let journey: MobileJourneySummary?
    let financial: MobileFinancialSnapshotResponse?
    let upcomingAppointments: [MobileUpcomingAppointment]
    let actions: [MobileActionItem]
    let notifications: [MobileBillingNotification]
    let activeClientCount: Int
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

struct MobileSubscriptionSummary: Codable, Equatable, Sendable {
    let id: UUID
    let status: String
    let paymentStanding: String
    let monthlyAmountCents: Int
    let currency: String
    let nextBillingDateUTC: Date?
    let currentPeriodStartUTC: Date?
    let currentPeriodEndUTC: Date?
    let cancelAtPeriodEnd: Bool

    private enum CodingKeys: String, CodingKey {
        case id, status, paymentStanding, monthlyAmountCents, currency, cancelAtPeriodEnd
        case nextBillingDateUTC = "nextBillingDateUtc"
        case currentPeriodStartUTC = "currentPeriodStartUtc"
        case currentPeriodEndUTC = "currentPeriodEndUtc"
    }

    var monthlyAmount: Decimal { Decimal(monthlyAmountCents) / 100 }
}

struct MobileEntitlementSummary: Codable, Equatable, Sendable {
    let status: String
    let effectiveUTC: Date?
    let expirationUTC: Date?
    let graceOrSuspensionUTC: Date?
    let reasonCode: String?
    let summary: String

    private enum CodingKeys: String, CodingKey {
        case status, reasonCode, summary
        case effectiveUTC = "effectiveUtc"
        case expirationUTC = "expirationUtc"
        case graceOrSuspensionUTC = "graceOrSuspensionUtc"
    }
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

struct MobileBillingNotification: Codable, Equatable, Identifiable, Sendable {
    let id: UUID
    let kind: String
    let subject: String
    let occurredUTC: Date

    private enum CodingKeys: String, CodingKey {
        case id, kind, subject
        case occurredUTC = "occurredUtc"
    }
}

struct MobileFinancialSnapshotResponse: Codable, Equatable, Sendable {
    let position: MobileFinancialPosition?
    let intelligence: MobileFinancialIntelligenceSummary?
    let upcomingBills: [MobileUpcomingBill]
    let operatingSystem: MobileFinancialOperatingSystemSnapshotResponse?
    let presentation: MobileFinancialPresentationResponse?
}

struct MobileFinancialPresentationResponse: Codable, Equatable, Sendable {
    let assignedAgent: MobileFinancialAssignedAgentContextResponse
    let prioritySections: [MobileFinancialPrioritySectionResponse]
}

struct MobileFinancialAssignedAgentContextResponse: Codable, Equatable, Sendable {
    let hasAssignedAgent: Bool
    let displayName: String?
    let firstName: String?
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
    let discussionPrompt: String
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
        if amount == 0 {
            return .neutral
        }

        switch kind {
        case .assets, .income, .payoffProgress:
            return amount > 0 ? .success : .neutral
        case .liabilities, .bills, .endingDebt:
            return amount > 0 ? .warning : .neutral
        case .debt:
            return amount > 0 ? .danger : .neutral
        case .netWorth, .endingCash:
            return amount > 0 ? .success : .danger
        case .openingCash:
            return amount < 0 ? .danger : .neutral
        case .historical:
            return .neutral
        }
    }

    static func tone(
        forCents amountCents: Int64,
        kind: MobileFinancialAmountKind
    ) -> LegendNextTone {
        tone(for: Decimal(amountCents) / Decimal(100), kind: kind)
    }

    static func tone(forStatus status: String) -> LegendNextTone {
        let normalized = status
            .trimmingCharacters(in: .whitespacesAndNewlines)
            .lowercased()

        if normalized.contains("critical") ||
            normalized.contains("danger") ||
            normalized.contains("severe") ||
            normalized.contains("exposed") ||
            normalized.contains("negative") ||
            normalized.contains("high") ||
            normalized.contains("shortfall") {
            return .danger
        }

        if normalized.contains("warning") ||
            normalized.contains("watch") ||
            normalized.contains("tight") ||
            normalized.contains("pressure") ||
            normalized.contains("moderate") ||
            normalized.contains("review") ||
            normalized.contains("incomplete") {
            return .warning
        }

        if normalized.contains("healthy") ||
            normalized.contains("strong") ||
            normalized.contains("excellent") ||
            normalized.contains("stable") ||
            normalized.contains("positive") ||
            normalized.contains("improving") ||
            normalized.contains("clear") ||
            normalized.contains("low") {
            return .success
        }

        if normalized.contains("progress") ||
            normalized.contains("building") ||
            normalized.contains("active") ||
            normalized.contains("scheduled") ||
            normalized.contains("current") {
            return .information
        }

        return .neutral
    }

    static func tone(
        forEventKind eventKind: String,
        amountCents: Int64
    ) -> LegendNextTone {
        let normalized = eventKind.lowercased()

        if normalized.contains("income") {
            return tone(forCents: amountCents, kind: .income)
        }

        if normalized.contains("debt") {
            return tone(forCents: amountCents, kind: .debt)
        }

        return tone(forCents: amountCents, kind: .bills)
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
