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
