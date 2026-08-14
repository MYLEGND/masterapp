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
                "prioritySections": [
                  {
                    "key": "current-outlook",
                    "eyebrow": "Current outlook",
                    "title": "Week at a Glance",
                    "systemImage": "calendar.day.timeline.leading",
                    "priority": 1,
                    "status": "Projected shortfall",
                    "reason": "Projected ending cash for the current week is below zero.",
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
            .danger)
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
        XCTAssertEqual(
            MobileFinancialAmountSemantic.tone(for: -2_500, kind: .historical),
            .danger)
    }

    func testSharedHealthSnapshotPresentationNeverUsesSuccessForNegativeOrOutflowAmounts() {
        let negativeLifestyle = MobileFinancialHealthMetricResponse(
            key: "annual-lifestyle-remaining",
            label: "What's Left for Lifestyle",
            valueType: "Currency",
            amountCents: -1,
            numericValue: nil,
            textValue: nil,
            status: nil)
        let insuranceCost = MobileFinancialHealthMetricResponse(
            key: "annual-insurance-costs",
            label: "Insurance Costs",
            valueType: "Currency",
            amountCents: 1,
            numericValue: nil,
            textValue: nil,
            status: nil)
        let earnings = MobileFinancialHealthMetricResponse(
            key: "annual-earnings",
            label: "Earnings",
            valueType: "Currency",
            amountCents: 1,
            numericValue: nil,
            textValue: nil,
            status: nil)

        XCTAssertEqual(
            LegendFinancialPresentation.metricTone(
                negativeLifestyle,
                sectionSemantic: "cash-flow"),
            .danger)
        XCTAssertEqual(
            LegendFinancialPresentation.metricTone(
                insuranceCost,
                sectionSemantic: "cash-flow"),
            .danger)
        XCTAssertEqual(
            LegendFinancialPresentation.metricTone(
                earnings,
                sectionSemantic: "cash-flow"),
            .success)
    }

    func testNavigationDestinationMappingHasOneSupportedDetailForEachServerSection() {
        XCTAssertEqual(
            MobileFinancialDetailDestination(rawValue: "assets"),
            .assets)
        XCTAssertEqual(
            MobileFinancialDetailDestination(rawValue: "liabilities"),
            .liabilities)
        XCTAssertEqual(
            MobileFinancialDetailDestination(rawValue: "cash-flow"),
            .cashFlow)
        XCTAssertEqual(
            MobileFinancialDetailDestination(rawValue: "protection"),
            .protection)
        XCTAssertEqual(
            MobileFinancialDetailDestination(rawValue: "tax-profile"),
            .taxProfile)
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

    func testHealthSnapshotDecodesServerRowsAndTotalsForNativeSectionDetail() throws {
        let financial = try JSONDecoder.mobile.decode(
            MobileFinancialSnapshotResponse.self,
            from: Data("""
            {
              "position": null,
              "intelligence": null,
              "upcomingBills": [],
              "operatingSystem": null,
              "presentation": null,
              "healthSnapshot": {
                "updatedUtc": "2026-08-08T15:00:00Z",
                "sections": [
                  {
                    "key": "assets",
                    "title": "Assets",
                    "semantic": "assets",
                    "period": null,
                    "groups": [
                      {
                        "key": "asset-components",
                        "title": null,
                        "metrics": [
                          {
                            "key": "personal-property",
                            "label": "Personal Property",
                            "valueType": "Currency",
                            "amountCents": 125050,
                            "numericValue": null,
                            "textValue": null,
                            "status": null
                          },
                          {
                            "key": "savings",
                            "label": "Savings",
                            "valueType": "Currency",
                            "amountCents": 990025,
                            "numericValue": null,
                            "textValue": null,
                            "status": null
                          }
                        ]
                      }
                    ],
                    "total": {
                      "key": "total-assets",
                      "label": "Total Assets",
                      "valueType": "Currency",
                      "amountCents": 1115075,
                      "numericValue": null,
                      "textValue": null,
                      "status": null
                    }
                  },
                  {
                    "key": "protection",
                    "title": "Protection",
                    "semantic": "protection",
                    "period": null,
                    "groups": [
                      {
                        "key": "if-sick",
                        "title": "If You Get Sick",
                        "metrics": [
                          {
                            "key": "primary-status",
                            "label": "Jordan Status",
                            "valueType": "Text",
                            "amountCents": null,
                            "numericValue": null,
                            "textValue": "Partial",
                            "status": "Partial"
                          },
                          {
                            "key": "primary-gap",
                            "label": "Jordan Gap",
                            "valueType": "Currency",
                            "amountCents": 6000000,
                            "numericValue": null,
                            "textValue": null,
                            "status": null
                          }
                        ]
                      }
                    ],
                    "total": null
                  }
                ]
              }
            }
            """.utf8))

        let snapshot = try XCTUnwrap(financial.healthSnapshot)
        let assets = try XCTUnwrap(snapshot.section(for: .assets))
        XCTAssertEqual(assets.groups.first?.metrics.map(\.amountCents), [125050, 990025])
        XCTAssertEqual(assets.total?.amountCents, 1115075)

        let protection = try XCTUnwrap(snapshot.section(for: .protection))
        XCTAssertEqual(protection.groups.first?.title, "If You Get Sick")
        XCTAssertEqual(protection.groups.first?.metrics.first?.label, "Jordan Status")
        XCTAssertEqual(protection.groups.first?.metrics.first?.textValue, "Partial")
        XCTAssertEqual(protection.groups.first?.metrics.last?.label, "Jordan Gap")
        XCTAssertEqual(protection.groups.first?.metrics.last?.amountCents, 6000000)
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

    func testSyncedWeekAndMonthPayloadKeepsEveryModalBreakdownItem() throws {
        let financial = try JSONDecoder.mobile.decode(
            MobileFinancialSnapshotResponse.self,
            from: Data("""
            {
              "position": null,
              "intelligence": null,
              "upcomingBills": [],
              "operatingSystem": {
                "projection": {
                  "status": "Ready",
                  "reasonCode": null,
                  "summary": "Synced from the saved financial projection."
                },
                "freshness": {
                  "financeStateUpdatedUtc": "2026-07-28T23:48:00Z",
                  "intelligenceEvaluatedUtc": "2026-07-28T23:48:00Z",
                  "generatedUtc": "2026-07-28T23:48:00Z"
                },
                "weekAtGlance": {
                  "weekKey": "2026-W31",
                  "startDate": "2026-07-27",
                  "endDate": "2026-08-02",
                  "openingCashCents": 307800,
                  "incomeCents": 380000,
                  "debitExpenseCents": 215000,
                  "creditExpenseCents": 107736,
                  "requiredDebtPaymentCents": 90000,
                  "extraDebtPaymentCents": 15000,
                  "endingCashCents": 365064,
                  "openingDebtCents": 1200000,
                  "endingDebtCents": 1095000,
                  "pressureStatus": "Current",
                  "pressureSummary": "Cash remains positive after scheduled activity.",
                  "events": [
                    {
                      "eventKey": "income:salary:2026-07-29",
                      "occursOn": "2026-07-29",
                      "kind": "Income",
                      "title": "Salary",
                      "amountCents": 380000,
                      "sourceToolId": "IncomeTracker",
                      "sourceItemId": "salary",
                      "status": "Scheduled"
                    },
                    {
                      "eventKey": "bill:mortgage:2026-08-01",
                      "occursOn": "2026-08-01",
                      "kind": "Debit expense",
                      "title": "Mortgage",
                      "amountCents": 215000,
                      "sourceToolId": "ExpenseLens",
                      "sourceItemId": "mortgage",
                      "status": "Scheduled"
                    }
                  ]
                },
                "monthAtGlance": {
                  "monthKey": "2026-07",
                  "startDate": "2026-07-01",
                  "endDate": "2026-07-31",
                  "openingCashCents": 0,
                  "incomeCents": 910000,
                  "debitExpenseCents": 385000,
                  "creditExpenseCents": 100036,
                  "requiredDebtPaymentCents": 90000,
                  "extraDebtPaymentCents": 15000,
                  "endingCashCents": 409964,
                  "openingDebtCents": 1200000,
                  "endingDebtCents": 1095000,
                  "savingsContributionCents": 75000,
                  "pressureStatus": "Current",
                  "pressureSummary": "The monthly projection is on track.",
                  "largestObligation": {
                    "title": "Mortgage",
                    "occursOn": "2026-07-31",
                    "amountCents": 215000,
                    "kind": "Debit expense"
                  },
                  "weeks": [
                    {
                      "weekKey": "2026-W30",
                      "startDate": "2026-07-20",
                      "endDate": "2026-07-26",
                      "incomeCents": 530000,
                      "outflowCents": 225000,
                      "endingCashCents": 305000,
                      "endingDebtCents": 1200000,
                      "pressureStatus": "Current"
                    },
                    {
                      "weekKey": "2026-W31",
                      "startDate": "2026-07-27",
                      "endDate": "2026-08-02",
                      "incomeCents": 380000,
                      "outflowCents": 260036,
                      "endingCashCents": 409964,
                      "endingDebtCents": 1095000,
                      "pressureStatus": "Current"
                    }
                  ]
                },
                "tools": []
              },
              "presentation": null
            }
            """.utf8))

        let operatingSystem = try XCTUnwrap(financial.operatingSystem)
        let week = try XCTUnwrap(operatingSystem.weekAtGlance)
        let month = try XCTUnwrap(operatingSystem.monthAtGlance)

        XCTAssertEqual(week.events.map(\.title), ["Salary", "Mortgage"])
        XCTAssertEqual(month.weeks.map(\.weekKey), ["2026-W30", "2026-W31"])
        XCTAssertEqual(month.largestObligation?.title, "Mortgage")
    }
}
