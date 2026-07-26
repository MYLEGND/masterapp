import Foundation

enum LegendHomeFeedFilter: String, CaseIterable, Identifiable {
    case forYou = "For You"
    case circles = "Circles"
    case guidance = "Guidance"

    var id: String {
        rawValue
    }
}

enum LegendJourneyMomentKind: String, CaseIterable, Identifiable {
    case milestone
    case protection
    case financial
    case family
    case community
    case guidance

    var id: String {
        rawValue
    }

    var systemImageName: String {
        switch self {
        case .milestone:
            return "flag.checkered"
        case .protection:
            return "shield.checkered"
        case .financial:
            return "chart.line.uptrend.xyaxis"
        case .family:
            return "figure.2.and.child.holdinghands"
        case .community:
            return "person.3.fill"
        case .guidance:
            return "lightbulb.fill"
        }
    }
}

struct LegendJourneyMoment: Identifiable, Hashable {
    let id: UUID
    let title: String
    let subtitle: String
    let kind: LegendJourneyMomentKind
    let isCurrentUser: Bool

    init(
        id: UUID = UUID(),
        title: String,
        subtitle: String,
        kind: LegendJourneyMomentKind,
        isCurrentUser: Bool = false
    ) {
        self.id = id
        self.title = title
        self.subtitle = subtitle
        self.kind = kind
        self.isCurrentUser = isCurrentUser
    }
}

enum LegendJourneyPostKind: String, CaseIterable, Identifiable {
    case milestone
    case guidance
    case circle
    case financial
    case protection

    var id: String {
        rawValue
    }

    var systemImageName: String {
        switch self {
        case .milestone:
            return "trophy.fill"
        case .guidance:
            return "sparkles"
        case .circle:
            return "person.3.fill"
        case .financial:
            return "dollarsign.circle.fill"
        case .protection:
            return "shield.fill"
        }
    }
}

struct LegendJourneyPost: Identifiable, Hashable {
    let id: UUID
    let authorName: String
    let authorContext: String
    let title: String
    let body: String
    let kind: LegendJourneyPostKind
    let filter: LegendHomeFeedFilter
    let timestampText: String
    let celebrateCount: Int
    let discussionCount: Int
    let isSaved: Bool
    let detailPoints: [String]

    init(
        id: UUID = UUID(),
        authorName: String,
        authorContext: String,
        title: String,
        body: String,
        kind: LegendJourneyPostKind,
        filter: LegendHomeFeedFilter,
        timestampText: String,
        celebrateCount: Int = 0,
        discussionCount: Int = 0,
        isSaved: Bool = false,
        detailPoints: [String] = []
    ) {
        self.id = id
        self.authorName = authorName
        self.authorContext = authorContext
        self.title = title
        self.body = body
        self.kind = kind
        self.filter = filter
        self.timestampText = timestampText
        self.celebrateCount = celebrateCount
        self.discussionCount = discussionCount
        self.isSaved = isSaved
        self.detailPoints = detailPoints
    }
}

struct LegendFinancialPulse: Equatable {
    let monthlyIncome: Decimal?
    let monthlyAvailableCash: Decimal?
    let totalDebt: Decimal?
    let emergencyFundProgress: Double?
    let protectionStatus: String

    init(
        monthlyIncome: Decimal? = nil,
        monthlyAvailableCash: Decimal? = nil,
        totalDebt: Decimal? = nil,
        emergencyFundProgress: Double? = nil,
        protectionStatus: String
    ) {
        self.monthlyIncome = monthlyIncome
        self.monthlyAvailableCash = monthlyAvailableCash
        self.totalDebt = totalDebt
        self.emergencyFundProgress = emergencyFundProgress
        self.protectionStatus = protectionStatus
    }
}

enum LegendHomeDestination: Hashable {
    case create
    case circles
    case messages
    case profile
}
