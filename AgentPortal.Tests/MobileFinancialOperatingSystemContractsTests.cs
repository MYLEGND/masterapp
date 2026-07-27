using System;
using System.Text.Json;
using Infrastructure.Mobile;
using Xunit;

namespace AgentPortal.Tests;

public sealed class MobileFinancialOperatingSystemContractsTests
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    [Fact]
    public void ExistingMobileFinancialSnapshotConstruction_RemainsCompatible()
    {
        var snapshot = new MobileFinancialSnapshot(
            Position: null,
            Intelligence: null,
            UpcomingBills: Array.Empty<MobileUpcomingBill>());

        Assert.Null(snapshot.OperatingSystem);
    }

    [Fact]
    public void OperatingSystemContract_SerializesStableCamelCaseShape()
    {
        var generatedUtc = new DateTime(
            2026,
            7,
            26,
            23,
            0,
            0,
            DateTimeKind.Utc);

        var snapshot = new MobileFinancialOperatingSystemSnapshot(
            Projection: new MobileFinancialProjectionStatus(
                Status: "Available",
                ReasonCode: null,
                Summary: "Projection is current."),
            Freshness: new MobileFinancialDataFreshness(
                FinanceStateUpdatedUtc: generatedUtc.AddMinutes(-10),
                IntelligenceEvaluatedUtc: generatedUtc.AddMinutes(-5),
                GeneratedUtc: generatedUtc),
            WeekAtGlance: new MobileFinancialWeekAtGlance(
                WeekKey: "2026-07-27",
                StartDate: new DateOnly(2026, 7, 27),
                EndDate: new DateOnly(2026, 8, 2),
                OpeningCashCents: 125_000,
                IncomeCents: 200_000,
                DebitExpenseCents: 80_000,
                CreditExpenseCents: 20_000,
                RequiredDebtPaymentCents: 15_000,
                ExtraDebtPaymentCents: 10_000,
                EndingCashCents: 200_000,
                OpeningDebtCents: 500_000,
                EndingDebtCents: 475_000,
                PressureStatus: "Healthy",
                PressureSummary: null,
                Events:
                [
                    new MobileFinancialCashFlowEvent(
                        EventKey: "income-paycheck-2026-07-31",
                        OccursOn: new DateOnly(2026, 7, 31),
                        Kind: "Income",
                        Title: "Paycheck",
                        AmountCents: 200_000,
                        SourceToolId: "ExpenseLens",
                        SourceItemId: "paycheck",
                        Status: "Scheduled")
                ]),
            MonthAtGlance: new MobileFinancialMonthAtGlance(
                MonthKey: "2026-07",
                StartDate: new DateOnly(2026, 7, 1),
                EndDate: new DateOnly(2026, 7, 31),
                OpeningCashCents: 100_000,
                IncomeCents: 800_000,
                DebitExpenseCents: 300_000,
                CreditExpenseCents: 75_000,
                RequiredDebtPaymentCents: 60_000,
                ExtraDebtPaymentCents: 50_000,
                EndingCashCents: 415_000,
                OpeningDebtCents: 600_000,
                EndingDebtCents: 490_000,
                SavingsContributionCents: 40_000,
                PressureStatus: "Healthy",
                PressureSummary: null,
                LargestObligation: new MobileFinancialLargestObligation(
                    Title: "Housing",
                    OccursOn: new DateOnly(2026, 7, 1),
                    AmountCents: 180_000,
                    Kind: "DebitExpense"),
                Weeks:
                [
                    new MobileFinancialWeekSummary(
                        WeekKey: "2026-07-27",
                        StartDate: new DateOnly(2026, 7, 27),
                        EndDate: new DateOnly(2026, 8, 2),
                        IncomeCents: 200_000,
                        OutflowCents: 125_000,
                        EndingCashCents: 415_000,
                        EndingDebtCents: 490_000,
                        PressureStatus: "Healthy")
                ]),
            Tools:
            [
                new MobileFinancialToolSummary(
                    ToolId: "ExpenseLens",
                    Title: "Expense Lens",
                    Category: "CashFlow",
                    Priority: 1,
                    AvailabilityStatus: "Available",
                    UpdatedUtc: generatedUtc.AddMinutes(-10),
                    Summary: "Weekly cash-flow plan",
                    Metrics:
                    [
                        new MobileFinancialMetric(
                            Key: "endingCash",
                            Label: "Projected ending cash",
                            ValueType: "Currency",
                            AmountCents: 415_000,
                            NumericValue: null,
                            TextValue: null,
                            Status: "Healthy")
                    ])
            ]);

        var json = JsonSerializer.Serialize(snapshot, JsonOptions);

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        Assert.Equal(
            "Available",
            root.GetProperty("projection")
                .GetProperty("status")
                .GetString());

        Assert.Equal(
            200_000,
            root.GetProperty("weekAtGlance")
                .GetProperty("endingCashCents")
                .GetInt64());

        Assert.Equal(
            "2026-07",
            root.GetProperty("monthAtGlance")
                .GetProperty("monthKey")
                .GetString());

        Assert.Equal(
            "ExpenseLens",
            root.GetProperty("tools")[0]
                .GetProperty("toolId")
                .GetString());

        Assert.Equal(
            415_000,
            root.GetProperty("tools")[0]
                .GetProperty("metrics")[0]
                .GetProperty("amountCents")
                .GetInt64());
    }

    [Fact]
    public void CashFlowContracts_PreserveNegativeAndLargeCentValues()
    {
        var week = new MobileFinancialWeekAtGlance(
            WeekKey: "2026-08-03",
            StartDate: new DateOnly(2026, 8, 3),
            EndDate: new DateOnly(2026, 8, 9),
            OpeningCashCents: 0,
            IncomeCents: 0,
            DebitExpenseCents: 250_000,
            CreditExpenseCents: 0,
            RequiredDebtPaymentCents: 0,
            ExtraDebtPaymentCents: 0,
            EndingCashCents: -250_000,
            OpeningDebtCents: 9_000_000_000,
            EndingDebtCents: 9_000_000_000,
            PressureStatus: "Critical",
            PressureSummary: "Projected cash is below zero.",
            Events: Array.Empty<MobileFinancialCashFlowEvent>());

        Assert.Equal(-250_000, week.EndingCashCents);
        Assert.Equal(9_000_000_000, week.EndingDebtCents);
    }
}
