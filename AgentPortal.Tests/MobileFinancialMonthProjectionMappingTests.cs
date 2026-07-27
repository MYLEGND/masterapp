using System;
using System.Reflection;
using System.Text.Json;
using Infrastructure.Mobile;
using Xunit;

namespace AgentPortal.Tests;

public sealed class MobileFinancialMonthProjectionMappingTests
{
    private static readonly MethodInfo MapMethod =
        typeof(MobileFinancialOperatingSystemProjectionService)
            .GetMethod(
                "MapMobileMonthProjection",
                BindingFlags.NonPublic |
                BindingFlags.Static)
        ?? throw new InvalidOperationException(
            "Phase 3B month projection mapper was not found.");

    [Fact]
    public void MapMobileMonthProjection_MapsAuthoritativeSnapshotDirectly()
    {
        using var document = JsonDocument.Parse(
            """
            {
              "mobileMonthProjection": {
                "schemaVersion": 1,
                "monthKey": "2026-07",
                "monthLabel": "July 2026",
                "startDate": "2026-07-01",
                "endDate": "2026-07-31",
                "status": "current",
                "pressureSummary": null,
                "openingCashCents": 125000,
                "incomeCents": 450000,
                "debitBillsCents": 130000,
                "creditBillsCents": 35000,
                "requiredExpensesCents": 165000,
                "requiredDebtMinimumCents": 35000,
                "extraDebtPaymentCents": 25000,
                "endingCashCents": 220000,
                "openingDebtCents": 500000,
                "endingDebtCents": 440000,
                "savingsContributionCents": null,
                "savingsProjectionStatus":
                  "not-projected-by-expense-lens",
                "largestObligation": {
                  "key": "expense:rent:2026-07-01",
                  "title": "Rent",
                  "dateKey": "2026-07-01",
                  "amountCents": 75000,
                  "kind": "expense"
                },
                "weeks": [
                  {
                    "weekId": "2026-07-week-1",
                    "weekLabel": "Week 1",
                    "startDate": "2026-06-29",
                    "endDate": "2026-07-05",
                    "status": "historical-reconciled",
                    "incomeCents": 200000,
                    "debitBillsCents": 80000,
                    "creditBillsCents": 20000,
                    "requiredDebtMinimumCents": 20000,
                    "extraDebtPaymentCents": 10000,
                    "outflowCents": 110000,
                    "closingCashCents": 210000,
                    "closingDebtCents": 470000
                  },
                  {
                    "weekId": "2026-07-week-2",
                    "weekLabel": "Week 2",
                    "startDate": "2026-07-06",
                    "endDate": "2026-07-12",
                    "status": "current",
                    "incomeCents": 250000,
                    "debitBillsCents": 50000,
                    "creditBillsCents": 15000,
                    "requiredDebtMinimumCents": 15000,
                    "extraDebtPaymentCents": 15000,
                    "outflowCents": 80000,
                    "closingCashCents": 220000,
                    "closingDebtCents": 440000
                  }
                ]
              }
            }
            """);

        var result = InvokeMapper(document.RootElement);

        Assert.NotNull(result);
        Assert.Equal("2026-07", result.MonthKey);
        Assert.Equal(
            new DateOnly(2026, 7, 1),
            result.StartDate);
        Assert.Equal(
            new DateOnly(2026, 7, 31),
            result.EndDate);
        Assert.Equal(125000, result.OpeningCashCents);
        Assert.Equal(450000, result.IncomeCents);
        Assert.Equal(130000, result.DebitExpenseCents);
        Assert.Equal(35000, result.CreditExpenseCents);
        Assert.Equal(
            35000,
            result.RequiredDebtPaymentCents);
        Assert.Equal(
            25000,
            result.ExtraDebtPaymentCents);
        Assert.Equal(220000, result.EndingCashCents);
        Assert.Equal(500000, result.OpeningDebtCents);
        Assert.Equal(440000, result.EndingDebtCents);

        Assert.Equal(
            0,
            result.SavingsContributionCents);

        Assert.Equal("current", result.PressureStatus);
        Assert.Null(result.PressureSummary);

        Assert.NotNull(result.LargestObligation);
        Assert.Equal(
            "Rent",
            result.LargestObligation.Title);
        Assert.Equal(
            new DateOnly(2026, 7, 1),
            result.LargestObligation.OccursOn);
        Assert.Equal(
            75000,
            result.LargestObligation.AmountCents);

        Assert.Equal(2, result.Weeks.Count);
        Assert.Equal(
            "2026-07-week-1",
            result.Weeks[0].WeekKey);
        Assert.Equal(
            110000,
            result.Weeks[0].OutflowCents);
        Assert.Equal(
            440000,
            result.Weeks[1].EndingDebtCents);
    }

    [Fact]
    public void MapMobileMonthProjection_ReturnsNullWhenSnapshotIsMissing()
    {
        using var document =
            JsonDocument.Parse("""{ "stateVersion": 2 }""");

        Assert.Null(
            InvokeMapper(document.RootElement));
    }

    [Fact]
    public void MapMobileMonthProjection_ReturnsNullForUnsupportedSchema()
    {
        using var document = JsonDocument.Parse(
            """
            {
              "mobileMonthProjection": {
                "schemaVersion": 99
              }
            }
            """);

        Assert.Null(
            InvokeMapper(document.RootElement));
    }

    [Fact]
    public void MapMobileMonthProjection_ReturnsNullForInvalidWeekData()
    {
        using var document = JsonDocument.Parse(
            """
            {
              "mobileMonthProjection": {
                "schemaVersion": 1,
                "monthKey": "2026-07",
                "startDate": "2026-07-01",
                "endDate": "2026-07-31",
                "status": "current",
                "openingCashCents": 0,
                "incomeCents": 0,
                "debitBillsCents": 0,
                "creditBillsCents": 0,
                "requiredDebtMinimumCents": 0,
                "extraDebtPaymentCents": 0,
                "endingCashCents": 0,
                "openingDebtCents": 0,
                "endingDebtCents": 0,
                "weeks": [
                  {
                    "weekId": "invalid-week"
                  }
                ]
              }
            }
            """);

        Assert.Null(
            InvokeMapper(document.RootElement));
    }

    private static MobileFinancialMonthAtGlance?
        InvokeMapper(JsonElement root)
    {
        return (MobileFinancialMonthAtGlance?)
            MapMethod.Invoke(
                null,
                new object[] { root });
    }
}
