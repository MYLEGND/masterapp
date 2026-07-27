using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Domain.Entities;
using Infrastructure.Data;
using Infrastructure.Mobile;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AgentPortal.Tests;

public sealed class MobileFinancialOperatingSystemProjectionServiceTests
{
    [Fact]
    public async Task ProjectAsync_MapsPersistedAuthoritativeWeekSnapshot()
    {
        await using var db = CreateDbContext();

        var clientProfileId = Guid.NewGuid();
        var updatedUtc = new DateTime(
            2026,
            7,
            26,
            18,
            30,
            0,
            DateTimeKind.Utc);

        db.FinanceToolStates.Add(new FinanceToolState
        {
            ClientProfileId = clientProfileId,
            ToolId = "ExpenseLens",
            UpdatedUtc = updatedUtc,
            JsonState =
                """
                {
                  "stateVersion": 7,
                  "mobileWeekProjection": {
                    "schemaVersion": 1,
                    "generatedUtc": "2026-07-26T18:29:00.000Z",
                    "sourceStateVersion": 7,
                    "monthKey": "2026-07",
                    "monthLabel": "July 2026",
                    "weekId": "2026-07-24_2026-07-30",
                    "weekLabel": "Jul 24 – Jul 30",
                    "startDate": "2026-07-24",
                    "endDate": "2026-07-30",
                    "status": "current",
                    "openingCashCents": 125000,
                    "incomeCents": 126667,
                    "debitBillsCents": 123867,
                    "creditBillsCents": 25000,
                    "requiredExpensesCents": 148867,
                    "requiredDebtMinimumCents": 25000,
                    "extraDebtPaymentCents": 20000,
                    "closingCashCents": -70800,
                    "openingDebtCents": 445000,
                    "closingDebtCents": 400000,
                    "events": [
                      {
                        "key": "expense:auto-insurance:2026-07-25",
                        "kind": "expense",
                        "label": "Auto Insurance",
                        "dateKey": "2026-07-25",
                        "status": "current",
                        "amountCents": 19200,
                        "impactCashCents": -19200,
                        "cashAfterCents": 105800,
                        "debtAfterCents": 445000,
                        "paymentMethod": "Debit",
                        "debtCategory": ""
                      }
                    ]
                  }
                }
                """
        });

        await db.SaveChangesAsync();

        var service =
            new MobileFinancialOperatingSystemProjectionService(db);

        var snapshot =
            await service.ProjectAsync(clientProfileId);

        Assert.Equal("Available", snapshot.Projection.Status);
        Assert.Null(snapshot.Projection.ReasonCode);
        Assert.NotNull(snapshot.WeekAtGlance);
        Assert.Null(snapshot.MonthAtGlance);

        Assert.Equal(
            updatedUtc,
            snapshot.Freshness.FinanceStateUpdatedUtc);

        var week = snapshot.WeekAtGlance!;

        Assert.Equal(
            "2026-07-24_2026-07-30",
            week.WeekKey);

        Assert.Equal(
            new DateOnly(2026, 7, 24),
            week.StartDate);

        Assert.Equal(
            new DateOnly(2026, 7, 30),
            week.EndDate);

        Assert.Equal(125000, week.OpeningCashCents);
        Assert.Equal(126667, week.IncomeCents);
        Assert.Equal(123867, week.DebitExpenseCents);
        Assert.Equal(25000, week.CreditExpenseCents);
        Assert.Equal(25000, week.RequiredDebtPaymentCents);
        Assert.Equal(20000, week.ExtraDebtPaymentCents);
        Assert.Equal(-70800, week.EndingCashCents);
        Assert.Equal(445000, week.OpeningDebtCents);
        Assert.Equal(400000, week.EndingDebtCents);
        Assert.Equal("current", week.PressureStatus);
        Assert.Equal("Jul 24 – Jul 30", week.PressureSummary);

        var eventItem = Assert.Single(week.Events);

        Assert.Equal(
            "expense:auto-insurance:2026-07-25",
            eventItem.EventKey);

        Assert.Equal(
            new DateOnly(2026, 7, 25),
            eventItem.OccursOn);

        Assert.Equal("expense", eventItem.Kind);
        Assert.Equal("Auto Insurance", eventItem.Title);
        Assert.Equal(19200, eventItem.AmountCents);
        Assert.Equal("ExpenseLens", eventItem.SourceToolId);
        Assert.Equal(eventItem.EventKey, eventItem.SourceItemId);
        Assert.Equal("current", eventItem.Status);

        var tool = Assert.Single(snapshot.Tools);

        Assert.Equal("ExpenseLens", tool.ToolId);
        Assert.Equal("Available", tool.AvailabilityStatus);
        Assert.Equal(updatedUtc, tool.UpdatedUtc);
        Assert.Empty(tool.Metrics);
    }

    [Fact]
    public async Task ProjectAsync_ReturnsUnavailableWhenExpenseLensStateIsMissing()
    {
        await using var db = CreateDbContext();

        var service =
            new MobileFinancialOperatingSystemProjectionService(db);

        var snapshot =
            await service.ProjectAsync(Guid.NewGuid());

        Assert.Equal("Unavailable", snapshot.Projection.Status);
        Assert.Equal(
            "EXPENSE_LENS_STATE_NOT_FOUND",
            snapshot.Projection.ReasonCode);

        Assert.Null(snapshot.WeekAtGlance);
        Assert.Null(snapshot.MonthAtGlance);
        Assert.Empty(snapshot.Tools);
        Assert.Null(snapshot.Freshness.FinanceStateUpdatedUtc);
    }

    [Fact]
    public async Task ProjectAsync_ReturnsUnavailableWhenMobileWeekProjectionIsMissing()
    {
        await using var db = CreateDbContext();

        var clientProfileId = Guid.NewGuid();

        db.FinanceToolStates.Add(new FinanceToolState
        {
            ClientProfileId = clientProfileId,
            ToolId = "ExpenseLens",
            JsonState =
                """
                {
                  "stateVersion": 7,
                  "categories": []
                }
                """
        });

        await db.SaveChangesAsync();

        var service =
            new MobileFinancialOperatingSystemProjectionService(db);

        var snapshot =
            await service.ProjectAsync(clientProfileId);

        Assert.Equal("Unavailable", snapshot.Projection.Status);
        Assert.Equal(
            "MOBILE_WEEK_PROJECTION_NOT_FOUND",
            snapshot.Projection.ReasonCode);

        Assert.Null(snapshot.WeekAtGlance);
    }

    [Fact]
    public async Task ProjectAsync_ReturnsUnavailableForInvalidJson()
    {
        await using var db = CreateDbContext();

        var clientProfileId = Guid.NewGuid();

        db.FinanceToolStates.Add(new FinanceToolState
        {
            ClientProfileId = clientProfileId,
            ToolId = "ExpenseLens",
            JsonState = "{invalid-json"
        });

        await db.SaveChangesAsync();

        var service =
            new MobileFinancialOperatingSystemProjectionService(db);

        var snapshot =
            await service.ProjectAsync(clientProfileId);

        Assert.Equal("Unavailable", snapshot.Projection.Status);
        Assert.Equal(
            "EXPENSE_LENS_STATE_INVALID_JSON",
            snapshot.Projection.ReasonCode);

        Assert.Null(snapshot.WeekAtGlance);
    }

    [Fact]
    public async Task ProjectAsync_ReturnsUnavailableForUnsupportedSchema()
    {
        await using var db = CreateDbContext();

        var clientProfileId = Guid.NewGuid();

        db.FinanceToolStates.Add(new FinanceToolState
        {
            ClientProfileId = clientProfileId,
            ToolId = "ExpenseLens",
            JsonState =
                """
                {
                  "mobileWeekProjection": {
                    "schemaVersion": 99
                  }
                }
                """
        });

        await db.SaveChangesAsync();

        var service =
            new MobileFinancialOperatingSystemProjectionService(db);

        var snapshot =
            await service.ProjectAsync(clientProfileId);

        Assert.Equal("Unavailable", snapshot.Projection.Status);
        Assert.Equal(
            "MOBILE_WEEK_SCHEMA_UNSUPPORTED",
            snapshot.Projection.ReasonCode);

        Assert.Null(snapshot.WeekAtGlance);
    }

    [Fact]
    public async Task ProjectAsync_RejectsEmptyClientProfileIdentifier()
    {
        await using var db = CreateDbContext();

        var service =
            new MobileFinancialOperatingSystemProjectionService(db);

        var exception = await Assert.ThrowsAsync<ArgumentException>(
            () => service.ProjectAsync(Guid.Empty));

        Assert.Equal("clientProfileId", exception.ParamName);
    }

    [Fact]
    public async Task ProjectAsync_ObservesCancellation()
    {
        await using var db = CreateDbContext();

        var service =
            new MobileFinancialOperatingSystemProjectionService(db);

        using var cancellationSource =
            new CancellationTokenSource();

        cancellationSource.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => service.ProjectAsync(
                Guid.NewGuid(),
                cancellationSource.Token));
    }

    [Fact]
    public void ProjectionService_ImplementsExpectedBoundary()
    {
        Assert.True(
            typeof(IMobileFinancialOperatingSystemProjectionService)
                .IsAssignableFrom(
                    typeof(
                        MobileFinancialOperatingSystemProjectionService)));

        var methods =
            typeof(IMobileFinancialOperatingSystemProjectionService)
                .GetMethods()
                .Select(method => method.Name)
                .ToArray();

        Assert.Single(methods);
        Assert.Equal("ProjectAsync", methods[0]);
    }

    private static MasterAppDbContext CreateDbContext()
    {
        var options =
            new DbContextOptionsBuilder<MasterAppDbContext>()
                .UseInMemoryDatabase(
                    $"mobile-financial-os-{Guid.NewGuid():N}")
                .Options;

        return new MasterAppDbContext(options);
    }
}
