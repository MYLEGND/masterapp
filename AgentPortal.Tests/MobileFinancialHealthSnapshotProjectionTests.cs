using System;
using System.Linq;
using Infrastructure.Mobile;
using Shared.Finance;
using Xunit;

namespace AgentPortal.Tests;

public sealed class MobileFinancialHealthSnapshotProjectionTests
{
    [Fact]
    public void Create_TransportsCalculatedBalanceSheetComponentsAndTotalsWithoutRecalculation()
    {
        var state = LegendLivingBalanceSheetCalculator.Calculate(
            new LegendLivingBalanceSheetState
            {
                Assets = new LegendBalanceSheetAssets
                {
                    PersonalProperty = 1_250.50m,
                    Savings = 9_900.25m,
                    Investments = 44_000m,
                    Retirement = 88_500m,
                    RealEstate = 310_000m,
                    Business = 15_000m
                },
                Liabilities = new LegendBalanceSheetLiabilities
                {
                    ShortTerm = 4_100m,
                    Mortgages = 200_000m,
                    BusinessDebt = 20_000m
                },
                CashFlow = new LegendBalanceSheetCashFlow
                {
                    Earnings = 150_000m,
                    InsuranceCosts = 6_000m,
                    AnnualSavings = 18_000m,
                    DebtObligations = 12_000m
                },
                TaxProfile = new LegendBalanceSheetTaxProfile
                {
                    FilingStatus = "Married Filing Jointly",
                    FederalTaxRate = 0.20m,
                    StateTaxRate = 0.05m,
                    FicaRate = 0.0765m
                },
                Protection = new LegendBalanceSheetProtection
                {
                    IfSick = new LegendDualProtectionItem
                    {
                        Primary = new LegendProtectionItem
                        {
                            Status = LegendProtectionStatuses.Partial,
                            CoverageAmount = 240_000m,
                            GapAmount = 60_000m
                        },
                        Spouse = new LegendProtectionItem
                        {
                            Status = LegendProtectionStatuses.Protected,
                            CoverageAmount = 180_000m,
                            GapAmount = 0m
                        }
                    },
                    WillsTrusts = new LegendEstatePlanningItem
                    {
                        Status = LegendEstatePlanStatuses.BasicWill
                    }
                }
            });
        var updatedUtc = new DateTime(2026, 8, 8, 15, 0, 0, DateTimeKind.Utc);

        var snapshot = MobileFinancialHealthSnapshotProjection.Create(state, updatedUtc);

        Assert.Equal(updatedUtc, snapshot.UpdatedUtc);
        Assert.Equal(
            ["assets", "liabilities", "cash-flow", "protection", "tax-profile"],
            snapshot.Sections.Select(section => section.Key));

        var assets = Section(snapshot, "assets");
        Assert.Equal(1_250_50, Metric(assets, "personal-property").AmountCents);
        Assert.Equal(
            ToCents(state.Assets.Total),
            Assert.IsType<MobileFinancialHealthMetric>(assets.Total).AmountCents);

        var liabilities = Section(snapshot, "liabilities");
        Assert.Equal(
            ToCents(state.Liabilities.Taxes),
            Metric(liabilities, "taxes").AmountCents);
        Assert.Equal(
            ToCents(state.Liabilities.Total),
            Assert.IsType<MobileFinancialHealthMetric>(liabilities.Total).AmountCents);

        var cashFlow = Section(snapshot, "cash-flow");
        Assert.Equal("Annual", cashFlow.Period);
        Assert.Equal(
            ToCents(state.CashFlow.DebtsAndTaxCosts),
            Metric(cashFlow, "debts-and-tax-costs").AmountCents);
        Assert.Equal(
            ToCents(state.CashFlow.LifestyleRemaining),
            Assert.IsType<MobileFinancialHealthMetric>(cashFlow.Total).AmountCents);

        var protection = Section(snapshot, "protection");
        var sickProtection = Assert.Single(
            protection.Groups,
            group => group.Key == "if-sick");
        var estatePlan = Assert.Single(
            protection.Groups,
            group => group.Key == "wills-trusts");
        Assert.Equal(
            LegendProtectionStatuses.Partial,
            Assert.Single(sickProtection.Metrics,
                metric => metric.Key == "primary-status").TextValue);
        Assert.Equal(
            "primary",
            Assert.Single(sickProtection.Metrics,
                metric => metric.Key == "active-person").TextValue);
        Assert.Equal(
            ToCents(state.Protection.IfSick.Primary.CoverageAmount),
            Assert.Single(sickProtection.Metrics,
                metric => metric.Key == "primary-coverage").AmountCents);
        Assert.Equal(
            LegendEstatePlanStatuses.BasicWill,
            Assert.Single(estatePlan.Metrics,
                metric => metric.Key == "estate-plan-status").TextValue);
        Assert.Equal(
            ToCents(state.Summary.ProtectionCoverageTotal),
            Assert.IsType<MobileFinancialHealthMetric>(protection.Total).AmountCents);
        Assert.Equal(
            ToCents(state.Summary.ProtectionGapTotal),
            Assert.Single(
                Assert.Single(protection.Groups,
                    group => group.Key == "protection-summary").Metrics,
                metric => metric.Key == "total-protection-gap").AmountCents);

        var taxProfile = Section(snapshot, "tax-profile");
        Assert.Equal(
            state.TaxProfile.EffectiveTaxRate,
            Metric(taxProfile, "effective-tax-rate").NumericValue);
        Assert.Equal(
            ToCents(state.TaxProfile.CalculatedTaxAmount),
            Assert.IsType<MobileFinancialHealthMetric>(taxProfile.Total).AmountCents);
    }

    private static MobileFinancialHealthSection Section(
        MobileFinancialHealthSnapshot snapshot,
        string key) => Assert.Single(snapshot.Sections, section => section.Key == key);

    private static MobileFinancialHealthMetric Metric(
        MobileFinancialHealthSection section,
        string key) => Assert.Single(
            section.Groups.SelectMany(group => group.Metrics),
            metric => metric.Key == key);

    private static long ToCents(decimal amount) => checked((long)decimal.Round(
        amount * 100m,
        0,
        MidpointRounding.AwayFromZero));
}
