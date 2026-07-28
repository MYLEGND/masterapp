import XCTest
@testable import Legend

final class MobileFinancialPresentationTests: XCTestCase {
    func testServerPriorityOrderAndPersonalizedLabelsDecodeWithoutLocalReordering() throws {
        let financial = try JSONDecoder.mobile.decode(
            MobileFinancialSnapshotResponse.self,
            from: Data("""
            {
              "position": null,
              "intelligence": null,
              "upcomingBills": [],
              "operatingSystem": null,
              "presentation": {
                "assignedAgent": {
                  "hasAssignedAgent": true,
                  "displayName": "Morgan Riley",
                  "firstName": "Morgan"
                },
                "prioritySections": [
                  {
                    "key": "current-outlook",
                    "eyebrow": "Current outlook",
                    "title": "Week at a Glance",
                    "systemImage": "calendar.day.timeline.leading",
                    "priority": 1,
                    "status": "Projected shortfall",
                    "reason": "Projected ending cash for the current week is below zero.",
                    "discussionPrompt": "Consider reviewing this with Morgan.",
                    "primaryMetric": {
                      "label": "Ending cash",
                      "amountCents": -2500,
                      "date": null,
                      "textValue": null,
                      "semantic": "negative"
                    },
                    "secondaryMetric": null
                  },
                  {
                    "key": "debt-obligations",
                    "eyebrow": "Largest upcoming obligation",
                    "title": "Northstar Mortgage",
                    "systemImage": "calendar.badge.exclamationmark",
                    "priority": 2,
                    "status": "Review",
                    "reason": "This is the largest scheduled outflow in the current monthly view.",
                    "discussionPrompt": "Consider reviewing this with Morgan.",
                    "primaryMetric": {
                      "label": "Amount",
                      "amountCents": 150000,
                      "date": null,
                      "textValue": null,
                      "semantic": "caution"
                    },
                    "secondaryMetric": null
                  }
                ]
              }
            }
            """.utf8))

        let presentation = try XCTUnwrap(financial.presentation)
        XCTAssertEqual(presentation.prioritySections.map(\.key), ["current-outlook", "debt-obligations"])
        XCTAssertEqual(presentation.prioritySections.first?.primaryMetric.semantic, .negative)
        XCTAssertEqual(presentation.assignedAgent.firstName, "Morgan")
        XCTAssertFalse(presentation.prioritySections.contains {
            $0.title.localizedCaseInsensitiveContains("Partner Income Stream")
        })
    }

    func testSemanticAmountMappingUsesMeaningRatherThanPositiveOrNegativeAlone() {
        XCTAssertEqual(
            MobileFinancialAmountSemantic.tone(for: 12_000, kind: .assets),
            .success)
        XCTAssertEqual(
            MobileFinancialAmountSemantic.tone(for: 12_000, kind: .liabilities),
            .warning)
        XCTAssertEqual(
            MobileFinancialAmountSemantic.tone(for: 12_000, kind: .debt),
            .danger)
        XCTAssertEqual(
            MobileFinancialAmountSemantic.tone(for: -1, kind: .netWorth),
            .danger)
        XCTAssertEqual(
            MobileFinancialAmountSemantic.tone(for: 0, kind: .openingCash),
            .neutral)
        XCTAssertEqual(
            MobileFinancialAmountSemantic.tone(for: -1, kind: .openingCash),
            .danger)
        XCTAssertEqual(
            MobileFinancialAmountSemantic.tone(for: 2_500, kind: .historical),
            .neutral)
    }

    func testNavigationDestinationMappingHasOneSupportedDetailForEachServerSection() {
        XCTAssertEqual(
            MobileFinancialDetailDestination(rawValue: "current-outlook"),
            .currentOutlook)
        XCTAssertEqual(
            MobileFinancialDetailDestination(rawValue: "monthly-outlook"),
            .monthlyOutlook)
        XCTAssertEqual(
            MobileFinancialDetailDestination(rawValue: "debt-obligations"),
            .debtObligations)
        XCTAssertEqual(
            MobileFinancialDetailDestination(rawValue: "financial-position"),
            .financialPosition)
        XCTAssertEqual(
            MobileFinancialDetailDestination(rawValue: "upcoming-activity"),
            .upcomingActivity)
        XCTAssertEqual(
            MobileFinancialDetailDestination(rawValue: "protection-discussion"),
            .protectionDiscussion)
        XCTAssertEqual(
            MobileFinancialDetailDestination(rawValue: "data-attention"),
            .dataAttention)
        XCTAssertNil(MobileFinancialDetailDestination(rawValue: "unverified-section"))
    }

    func testDiscussionCopyUsesAssignedAgentOrNonAdvisoryReflection() {
        let agentPrompt = "Consider reviewing this with Morgan."
        let noAgentPrompt = "This view can help you consider the timing and amount before making a related financial decision."

        XCTAssertTrue(agentPrompt.contains("Morgan"))
        XCTAssertFalse(agentPrompt.localizedCaseInsensitiveContains("you should"))
        XCTAssertFalse(agentPrompt.localizedCaseInsensitiveContains("we recommend"))
        XCTAssertTrue(noAgentPrompt.localizedCaseInsensitiveContains("consider"))
        XCTAssertFalse(noAgentPrompt.localizedCaseInsensitiveContains("you should"))
        XCTAssertFalse(noAgentPrompt.localizedCaseInsensitiveContains("we recommend"))
        XCTAssertFalse(noAgentPrompt.localizedCaseInsensitiveContains("must"))
    }

    func testSavedIncomeLabelAndNeutralFallbackDecodeExactlyAsServerSuppliesThem() throws {
        let data = Data("""
        {
          "eventKey": "income:secondary-salary:2026-07-27",
          "occursOn": "2026-07-27",
          "kind": "Income",
          "title": "Daphne's Salary",
          "amountCents": 100000,
          "sourceToolId": "ExpenseLens",
          "sourceItemId": "secondary-salary",
          "status": "Scheduled"
        }
        """.utf8)
        let savedLabel = try JSONDecoder.mobile.decode(
            MobileFinancialCashFlowEventResponse.self,
            from: data)

        XCTAssertEqual(savedLabel.title, "Daphne's Salary")
        XCTAssertFalse(savedLabel.title.localizedCaseInsensitiveContains("Partner Income Stream"))
    }
}
