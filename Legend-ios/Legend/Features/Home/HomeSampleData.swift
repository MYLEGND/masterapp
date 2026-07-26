import Foundation

enum LegendHomeSampleData {
    static let moments: [LegendJourneyMoment] = [
        LegendJourneyMoment(
            title: "Your Journey",
            subtitle: "Share an update",
            kind: .milestone,
            isCurrentUser: true
        ),
        LegendJourneyMoment(
            title: "Debt Freedom",
            subtitle: "A major balance fell",
            kind: .financial
        ),
        LegendJourneyMoment(
            title: "Family Protected",
            subtitle: "Coverage completed",
            kind: .protection
        ),
        LegendJourneyMoment(
            title: "New Home",
            subtitle: "Preparing to purchase",
            kind: .family
        ),
        LegendJourneyMoment(
            title: "Building Together",
            subtitle: "Circle momentum",
            kind: .community
        ),
        LegendJourneyMoment(
            title: "Next Right Step",
            subtitle: "Guidance for today",
            kind: .guidance
        )
    ]

    static let financialPulse = LegendFinancialPulse(
        monthlyIncome: 4_333.33,
        monthlyAvailableCash: 3_711.33,
        totalDebt: 10_000,
        emergencyFundProgress: 0.42,
        protectionStatus: "Protection plan in progress"
    )

    static let posts: [LegendJourneyPost] = [
        LegendJourneyPost(
            authorName: "Marcus T.",
            authorContext: "Debt Freedom Journey",
            title: "One card officially paid off",
            body: "I made the final payment today. It took consistency, accountability, and a plan I could actually follow.",
            kind: .milestone,
            filter: .forYou,
            timestampText: "18 min",
            celebrateCount: 42,
            discussionCount: 8,
            detailPoints: [
                "Remaining debt is now concentrated into two balances.",
                "The previous payment will roll into the next payoff target.",
                "The updated plan shortens the projected payoff timeline."
            ]
        ),
        LegendJourneyPost(
            authorName: "Legend Guidance",
            authorContext: "Financial Stewardship",
            title: "Your emergency fund does not have to begin perfectly",
            body: "Start by protecting one month of essential expenses. Build consistency first, then expand toward the full six-month target.",
            kind: .guidance,
            filter: .guidance,
            timestampText: "1 hr",
            celebrateCount: 19,
            discussionCount: 5,
            isSaved: true,
            detailPoints: [
                "Separate emergency savings from normal spending.",
                "Automate a realistic contribution after each payday.",
                "Increase the contribution when debt payments disappear."
            ]
        ),
        LegendJourneyPost(
            authorName: "First-Time Homebuyers",
            authorContext: "Journey Circle",
            title: "What changed when we reviewed the full monthly picture",
            body: "Several members found that their home target became clearer after combining debt, savings, insurance, and future housing costs in one place.",
            kind: .circle,
            filter: .circles,
            timestampText: "2 hr",
            celebrateCount: 31,
            discussionCount: 14,
            detailPoints: [
                "Members compared estimated housing payments with current cash flow.",
                "Debt reduction targets were aligned with purchase timelines.",
                "Protection gaps were identified before taking on a mortgage."
            ]
        ),
        LegendJourneyPost(
            authorName: "Your Financial Pulse",
            authorContext: "Living Balance Sheet",
            title: "Available monthly cash is the fuel for your next move",
            body: "The goal is not only to know what remains. The goal is to intentionally direct it toward debt freedom, reserves, protection, and long-term growth.",
            kind: .financial,
            filter: .forYou,
            timestampText: "Today",
            celebrateCount: 12,
            discussionCount: 3,
            detailPoints: [
                "$3,711.33 is currently available before planned allocations.",
                "Debt reduction remains the highest-priority active objective.",
                "Emergency savings progress is moving toward the first milestone."
            ]
        ),
        LegendJourneyPost(
            authorName: "Legend Protection",
            authorContext: "Family Continuity",
            title: "Protection planning is more than buying a policy",
            body: "A complete plan connects income replacement, beneficiaries, legal documents, emergency contacts, and the people responsible for carrying out your wishes.",
            kind: .protection,
            filter: .guidance,
            timestampText: "Yesterday",
            celebrateCount: 24,
            discussionCount: 7,
            detailPoints: [
                "Confirm beneficiary information remains accurate.",
                "Store essential documents where trusted family can access them.",
                "Review coverage after major family, income, or debt changes."
            ]
        ),
        LegendJourneyPost(
            authorName: "Building Phase Circle",
            authorContext: "Journey Circle",
            title: "This week’s commitment: one intentional financial action",
            body: "Choose one move that makes your position stronger by next week—pay down a balance, fund reserves, update protection, or organize one important document.",
            kind: .circle,
            filter: .circles,
            timestampText: "Yesterday",
            celebrateCount: 37,
            discussionCount: 21,
            detailPoints: [
                "Post the action you selected.",
                "Share what could prevent you from completing it.",
                "Return with the result before the next circle check-in."
            ]
        )
    ]

    static func posts(for filter: LegendHomeFeedFilter) -> [LegendJourneyPost] {
        switch filter {
        case .forYou:
            return posts.filter { post in
                post.filter == .forYou
            }

        case .circles:
            return posts.filter { post in
                post.filter == .circles
            }

        case .guidance:
            return posts.filter { post in
                post.filter == .guidance
            }
        }
    }
}
