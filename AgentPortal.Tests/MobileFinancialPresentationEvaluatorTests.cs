using System;
using System.Collections.Generic;
using System.Linq;
using Infrastructure.Mobile;
using Xunit;

namespace AgentPortal.Tests;

public sealed class MobileFinancialPresentationEvaluatorTests
{
    [Fact]
    public void Evaluate_NegativeCurrentWeekEndingCash_OutranksStableMonthlyHistory()
    {
        var presentation = MobileFinancialPresentationEvaluator.Evaluate(
            position: Position(),
            intelligence: null,
            upcomingBills: [],
            operatingSystem: OperatingSystem(
                week: Week(endingCashCents: -25_00, pressureStatus: "Critical"),
                month: Month()));

        var currentOutlook = Section(presentation, "current-outlook");
        var monthlyOutlook = Section(presentation, "monthly-outlook");

        Assert.Equal(1, currentOutlook.Priority);
        Assert.Equal("Projected shortfall", currentOutlook.Status);
        Assert.True(
            presentation.PrioritySections.ToList().IndexOf(currentOutlook) <
            presentation.PrioritySections.ToList().IndexOf(monthlyOutlook));
    }

    [Fact]
    public void Evaluate_SevereHealthState_OutranksInformationalFinancialPosition()
    {
        var presentation = MobileFinancialPresentationEvaluator.Evaluate(
            position: Position(healthScore: 22, positionStatus: "Severe"),
            intelligence: null,
            upcomingBills: [],
            operatingSystem: OperatingSystem(
                week: Week(),
                month: Month()));

        var position = Section(presentation, "financial-position");
        var weeklyOutlook = Section(presentation, "current-outlook");

        Assert.Equal(1, position.Priority);
        Assert.Equal("Needs attention", position.Status);
        Assert.True(
            presentation.PrioritySections.ToList().IndexOf(position) <
            presentation.PrioritySections.ToList().IndexOf(weeklyOutlook));
    }

    [Fact]
    public void Evaluate_LargestMonthlyObligation_OutranksRoutineBalanceDetails_WhenOutflowExceedsIncome()
    {
        var presentation = MobileFinancialPresentationEvaluator.Evaluate(
            position: Position(),
            intelligence: null,
            upcomingBills: [],
            operatingSystem: OperatingSystem(
                week: Week(),
                month: Month(
                    incomeCents: 100_00,
                    debitExpenseCents: 200_00,
                    obligation: new MobileFinancialLargestObligation(
                        "Northstar Mortgage",
                        new DateOnly(2026, 8, 1),
                        150_00,
                        "DebitExpense"))));

        var obligation = Section(presentation, "debt-obligations");
        var position = Section(presentation, "financial-position");

        Assert.Equal(2, obligation.Priority);
        Assert.Equal("Review", obligation.Status);
        Assert.True(
            presentation.PrioritySections.ToList().IndexOf(obligation) <
            presentation.PrioritySections.ToList().IndexOf(position));
    }

    [Fact]
    public void Evaluate_UnavailableExpenseLensProjection_OutranksHealthyInformationalSections()
    {
        var presentation = MobileFinancialPresentationEvaluator.Evaluate(
            position: Position(),
            intelligence: null,
            upcomingBills: [],
            operatingSystem: null);

        var dataAttention = Section(presentation, "data-attention");
        var position = Section(presentation, "financial-position");

        Assert.Equal(1, dataAttention.Priority);
        Assert.Equal(4, position.Priority);
        Assert.Equal(dataAttention, presentation.PrioritySections.First());
    }

    [Fact]
    public void Evaluate_UsesStableTieBreaking_AndDoesNotInventUrgencyForStableData()
    {
        var operatingSystem = OperatingSystem(
            week: Week(),
            month: Month());
        IReadOnlyList<MobileUpcomingBill> upcomingBills =
        [
            new MobileUpcomingBill(
                Guid.Parse("00000000-0000-0000-0000-000000000001"),
                "Water service",
                45_00,
                "Monthly",
                new DateTime(2026, 8, 2, 0, 0, 0, DateTimeKind.Utc),
                "Current")
        ];

        var first = MobileFinancialPresentationEvaluator.Evaluate(
            Position(), null, upcomingBills, operatingSystem);
        var second = MobileFinancialPresentationEvaluator.Evaluate(
            Position(), null, upcomingBills, operatingSystem);

        Assert.Equal(
            first.PrioritySections.Select(section => (section.Key, section.Priority)),
            second.PrioritySections.Select(section => (section.Key, section.Priority)));
        Assert.DoesNotContain(
            first.PrioritySections,
            section => section.Status.Contains("urgent", StringComparison.OrdinalIgnoreCase) ||
                       section.Status.Contains("critical", StringComparison.OrdinalIgnoreCase));
    }

    private static MobileFinancialPrioritySection Section(
        MobileFinancialPresentation presentation,
        string key) =>
        Assert.Single(presentation.PrioritySections.Where(section => section.Key == key));

    private static MobileFinancialPosition Position(
        int healthScore = 72,
        decimal netWorth = 50_000m,
        string positionStatus = "Stable") =>
        new(
            healthScore,
            AssetsTotal: 150_000m,
            LiabilitiesTotal: 100_000m,
            NetWorth: netWorth,
            AnnualEarnings: 100_000m,
            AnnualLifestyleRemaining: 20_000m,
            AnnualTaxes: 15_000m,
            ProtectionGapTotal: 0m,
            PositionStatus: positionStatus,
            PositionSummary: "Saved financial information is current.",
            EstatePlanningStatus: "Current",
            EstatePlanningRiskLevel: "Low",
            UpdatedUtc: new DateTime(2026, 7, 27, 0, 0, 0, DateTimeKind.Utc));

    private static MobileFinancialOperatingSystemSnapshot OperatingSystem(
        MobileFinancialWeekAtGlance week,
        MobileFinancialMonthAtGlance month) =>
        new(
            new MobileFinancialProjectionStatus(
                "Available",
                null,
                "The saved Expense Lens projection is current."),
            new MobileFinancialDataFreshness(
                null,
                null,
                new DateTime(2026, 7, 27, 0, 0, 0, DateTimeKind.Utc)),
            week,
            month,
            []);

    private static MobileFinancialWeekAtGlance Week(
        long endingCashCents = 500_00,
        string pressureStatus = "Healthy") =>
        new(
            "2026-07-27",
            new DateOnly(2026, 7, 27),
            new DateOnly(2026, 8, 2),
            OpeningCashCents: 250_00,
            IncomeCents: 700_00,
            DebitExpenseCents: 150_00,
            CreditExpenseCents: 50_00,
            RequiredDebtPaymentCents: 0,
            ExtraDebtPaymentCents: 0,
            EndingCashCents: endingCashCents,
            OpeningDebtCents: 500_00,
            EndingDebtCents: 500_00,
            PressureStatus: pressureStatus,
            PressureSummary: null,
            Events: []);

    private static MobileFinancialMonthAtGlance Month(
        long incomeCents = 3_000_00,
        long debitExpenseCents = 900_00,
        MobileFinancialLargestObligation? obligation = null) =>
        new(
            "2026-08",
            new DateOnly(2026, 8, 1),
            new DateOnly(2026, 8, 31),
            OpeningCashCents: 500_00,
            IncomeCents: incomeCents,
            DebitExpenseCents: debitExpenseCents,
            CreditExpenseCents: 100_00,
            RequiredDebtPaymentCents: 200_00,
            ExtraDebtPaymentCents: 0,
            EndingCashCents: 1_000_00,
            OpeningDebtCents: 500_00,
            EndingDebtCents: 300_00,
            SavingsContributionCents: 0,
            PressureStatus: "Healthy",
            PressureSummary: null,
            LargestObligation: obligation,
            Weeks: []);
}
