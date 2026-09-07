using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Domain.Entities;
using Infrastructure.Data;
using Infrastructure.Mobile;
using Microsoft.EntityFrameworkCore;
using Xunit;
using Moq;
using Infrastructure.Households;

namespace AgentPortal.Tests;

public sealed class MobileFinancialOperatingSystemProjectionServiceTests
{
    [Fact]
    public async Task ProjectAsync_CalculatesCurrentReportFromPersistedInputs()
    {
        await using var db = CreateDbContext();
        var client = Guid.NewGuid();
        var row = new FinanceToolState { ClientProfileId = client, HouseholdAccountId = client, ToolId = "ExpenseLens", JsonState = LiveInputs };
        db.FinanceToolStates.Add(row);
        await db.SaveChangesAsync();
        var snapshot = await CreateService(db).ProjectAsync(client, new DateOnly(2027, 9, 4));
        Assert.Equal("Available", snapshot.Projection.Status);
        Assert.Equal(500000, snapshot.MonthAtGlance!.IncomeCents);
        Assert.Equal(150000, snapshot.MonthAtGlance.RequiredDebtPaymentCents);
        Assert.Equal(30000, snapshot.MonthAtGlance.DebitExpenseCents);
        Assert.Equal(180000, snapshot.MonthAtGlance.Weeks.Sum(w => w.OutflowCents));
        Assert.Equal(row.UpdatedUtc, snapshot.Freshness.FinanceStateUpdatedUtc);
        Assert.False(db.ChangeTracker.HasChanges());
    }

    [Fact]
    public async Task ProjectAsync_RecalculatesAfterMonthRolloverWithoutAWebSave()
    {
        await using var db = CreateDbContext();
        var client = Guid.NewGuid();
        var row = new FinanceToolState { ClientProfileId = client, HouseholdAccountId = client, ToolId = "ExpenseLens", JsonState = LiveInputs };
        db.FinanceToolStates.Add(row);
        await db.SaveChangesAsync();
        var service = CreateService(db);
        var first = await service.ProjectAsync(client, new DateOnly(2027, 9, 4));
        row.JsonState = LiveInputs.Replace("5000", "6000");
        await db.SaveChangesAsync();
        var next = await service.ProjectAsync(client, new DateOnly(2027, 10, 4));
        Assert.Equal(500000, first.MonthAtGlance!.IncomeCents);
        Assert.Equal("2027-10", next.MonthAtGlance!.MonthKey);
        Assert.Equal(600000, next.MonthAtGlance.IncomeCents);
        Assert.Equal(150000, next.MonthAtGlance.RequiredDebtPaymentCents);
        Assert.False(db.ChangeTracker.HasChanges());
    }

    [Fact]
    public async Task ProjectAsync_IgnoresStaleOrInvalidCachedSnapshots()
    {
        await using var db = CreateDbContext();
        var client = Guid.NewGuid();
        db.FinanceToolStates.Add(new FinanceToolState { ClientProfileId = client, HouseholdAccountId = client, ToolId = "ExpenseLens", JsonState = LiveInputs });
        await db.SaveChangesAsync();
        var snapshot = await CreateService(db).ProjectAsync(client, new DateOnly(2028, 11, 4));
        Assert.Equal("Available", snapshot.Projection.Status);
        Assert.Equal("2028-11", snapshot.MonthAtGlance!.MonthKey);
        Assert.Equal(500000, snapshot.MonthAtGlance.IncomeCents);
    }

    [Fact]
    public async Task ProjectAgentAsync_MapsOnlyTheAuthenticatedAgentsExpenseLensState()
    {
        await using var db = CreateDbContext();
        db.AgentFinanceToolStates.Add(new AgentFinanceToolState { AgentUserId = " AGENT-OID ", ToolId = "ExpenseLens", JsonState = LiveInputs });
        db.AgentFinanceToolStates.Add(new AgentFinanceToolState { AgentUserId = "other", ToolId = "ExpenseLens", JsonState = LiveInputs.Replace("5000", "9000") });
        await db.SaveChangesAsync();
        var service = CreateService(db);
        var snapshot = await service.ProjectAgentAsync("agent-oid", new DateOnly(2027, 9, 4));
        Assert.Equal(500000, snapshot.MonthAtGlance!.IncomeCents);
        var missing = await service.ProjectAgentAsync("not-an-owner", new DateOnly(2027, 9, 4));
        Assert.Equal("EXPENSE_LENS_STATE_NOT_FOUND", missing.Projection.ReasonCode);
    }

    [Fact]
    public async Task ProjectAsync_ReturnsUnavailableWhenExpenseLensStateIsMissing()
    {
        await using var db = CreateDbContext();

        var service =
            CreateService(db);

        var snapshot =
            await service.ProjectAsync(
                Guid.NewGuid(),
                new DateOnly(2026, 7, 26));

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
    public async Task ProjectAsync_UsesNewestSavedStateWhenLegacyDuplicatesExist()
    {
        await using var db = CreateDbContext();

        var clientProfileId = Guid.NewGuid();
        var olderUtc = new DateTime(
            2026,
            8,
            1,
            12,
            0,
            0,
            DateTimeKind.Utc);
        var newestUtc = olderUtc.AddMinutes(1);

        db.FinanceToolStates.AddRange(
            new FinanceToolState
            {
                ClientProfileId = clientProfileId,
            HouseholdAccountId = clientProfileId,
                ToolId = "ExpenseLens",
                CreatedUtc = olderUtc,
                UpdatedUtc = olderUtc,
                JsonState = "{invalid-json"
            },
            new FinanceToolState
            {
                ClientProfileId = clientProfileId,
            HouseholdAccountId = clientProfileId,
                ToolId = "ExpenseLens",
                CreatedUtc = newestUtc,
                UpdatedUtc = newestUtc,
                JsonState = LiveInputs
            });

        await db.SaveChangesAsync();

        var snapshot = await CreateService(db)
            .ProjectAsync(
                clientProfileId,
                new DateOnly(2026, 8, 3));

        var week = Assert.IsType<MobileFinancialWeekAtGlance>(
            snapshot.WeekAtGlance);
        Assert.Equal("Available", snapshot.Projection.Status);
        Assert.Equal(500000, snapshot.MonthAtGlance!.IncomeCents);
        Assert.Equal(newestUtc, snapshot.Freshness.FinanceStateUpdatedUtc);
    }

    [Fact]
    public async Task ProjectAsync_CalculatesWhenMobileWeekProjectionIsMissing()
    {
        await using var db = CreateDbContext();

        var clientProfileId = Guid.NewGuid();

        db.FinanceToolStates.Add(new FinanceToolState
        {
            ClientProfileId = clientProfileId,
            HouseholdAccountId = clientProfileId,
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
            CreateService(db);

        var snapshot =
            await service.ProjectAsync(
                clientProfileId,
                new DateOnly(2026, 7, 26));

        Assert.Equal("Available", snapshot.Projection.Status);
        Assert.NotNull(snapshot.WeekAtGlance);
        Assert.NotNull(snapshot.MonthAtGlance);
    }

    [Fact]
    public async Task ProjectAsync_ReturnsUnavailableForInvalidJson()
    {
        await using var db = CreateDbContext();

        var clientProfileId = Guid.NewGuid();

        db.FinanceToolStates.Add(new FinanceToolState
        {
            ClientProfileId = clientProfileId,
            HouseholdAccountId = clientProfileId,
            ToolId = "ExpenseLens",
            JsonState = "{invalid-json"
        });

        await db.SaveChangesAsync();

        var service =
            CreateService(db);

        var snapshot =
            await service.ProjectAsync(
                clientProfileId,
                new DateOnly(2026, 7, 26));

        Assert.Equal("Unavailable", snapshot.Projection.Status);
        Assert.Equal(
            "EXPENSE_LENS_STATE_INVALID_JSON",
            snapshot.Projection.ReasonCode);

        Assert.Null(snapshot.WeekAtGlance);
    }

    [Fact]
    public async Task ProjectAsync_ReturnsUnavailableWhenOnlyCachedOutputExists()
    {
        await using var db = CreateDbContext();

        var clientProfileId = Guid.NewGuid();

        db.FinanceToolStates.Add(new FinanceToolState
        {
            ClientProfileId = clientProfileId,
            HouseholdAccountId = clientProfileId,
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
            CreateService(db);

        var snapshot =
            await service.ProjectAsync(
                clientProfileId,
                new DateOnly(2026, 7, 26));

        Assert.Equal("Unavailable", snapshot.Projection.Status);
        Assert.Equal(
            "EXPENSE_LENS_INPUTS_MISSING",
            snapshot.Projection.ReasonCode);

        Assert.Null(snapshot.WeekAtGlance);
    }

    [Fact]
    public async Task ProjectAsync_UsesSavedIncomeLabelsAndCurrentHouseholdNames()
    {
        await using var db = CreateDbContext();

        var clientProfileId = Guid.NewGuid();
        db.ClientProfiles.Add(new ClientProfile
        {
            Id = clientProfileId,
            ClientUserId = "client-finance-labels",
            FirstName = "Avery",
            LastName = "Client",
            SignificantOtherFirstName = "Stale"
        });
        db.HouseholdMembers.Add(new HouseholdMember
        {
            ClientUserId = "client-finance-labels",
            RelationshipType = "SignificantOther",
            FirstName = "Daphne",
            UpdatedUtc = DateTime.UtcNow
        });
        db.FinanceToolStates.Add(new FinanceToolState
        {
            ClientProfileId = clientProfileId,
            HouseholdAccountId = clientProfileId,
            ToolId = "ExpenseLens",
            JsonState =
                """
                {
                  "incomeStreams": {
                    "primary": [
                      { "id": "consulting", "label": "Avery Consulting", "amount": "1000", "anchorDate": "2026-07-25" }
                    ],
                    "secondary": [
                      { "id": "pay", "label": "", "amount": "1000", "anchorDate": "2026-07-26" },
                      { "id": "salary", "label": "Daphne's Salary", "amount": "1000", "anchorDate": "2026-07-27" }
                    ]
                  },
                  "mobileWeekProjection": {
                    "schemaVersion": 1,
                    "weekId": "2026-07-24_2026-07-30",
                    "weekLabel": "Jul 24 – Jul 30",
                    "startDate": "2026-07-24",
                    "endDate": "2026-07-30",
                    "status": "current",
                    "openingCashCents": 0,
                    "incomeCents": 300000,
                    "debitBillsCents": 0,
                    "creditBillsCents": 0,
                    "requiredDebtMinimumCents": 0,
                    "extraDebtPaymentCents": 0,
                    "closingCashCents": 300000,
                    "openingDebtCents": 0,
                    "closingDebtCents": 0,
                    "events": [
                      {
                        "key": "income:primary-consulting:2026-07-25",
                        "kind": "income",
                        "label": "Income",
                        "dateKey": "2026-07-25",
                        "status": "current",
                        "amountCents": 100000
                      },
                      {
                        "key": "income:secondary-pay:2026-07-26",
                        "kind": "income",
                        "label": "Partner Income Stream 1",
                        "dateKey": "2026-07-26",
                        "status": "current",
                        "amountCents": 100000
                      },
                      {
                        "key": "income:secondary-salary:2026-07-27",
                        "kind": "income",
                        "label": "Partner Income Stream 2",
                        "dateKey": "2026-07-27",
                        "status": "current",
                        "amountCents": 100000
                      }
                    ]
                  }
                }
                """
        });
        await db.SaveChangesAsync();

        var snapshot = await CreateService(db)
            .ProjectAsync(
                clientProfileId,
                new DateOnly(2026, 7, 26));

        var events = snapshot.WeekAtGlance!.Events;
        Assert.Equal("Avery Consulting", events[0].Title);
        Assert.Equal("Daphne's Income", events[1].Title);
        Assert.Equal("Daphne's Salary", events[2].Title);
        Assert.DoesNotContain(
            events,
            item => item.Title.Contains(
                "Partner Income Stream",
                StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ProjectAsync_UsesNeutralIncomeLabelWhenNoSavedPersonNameExists()
    {
        await using var db = CreateDbContext();

        var clientProfileId = Guid.NewGuid();
        db.ClientProfiles.Add(new ClientProfile
        {
            Id = clientProfileId,
            ClientUserId = "client-no-personal-label"
        });
        db.FinanceToolStates.Add(new FinanceToolState
        {
            ClientProfileId = clientProfileId,
            HouseholdAccountId = clientProfileId,
            ToolId = "ExpenseLens",
            JsonState =
                """
                {
                  "incomeStreams": { "secondary": [{ "id": "pay", "amount": "1000", "anchorDate": "2026-07-26" }] },
                  "mobileWeekProjection": {
                    "schemaVersion": 1,
                    "weekId": "2026-07-24_2026-07-30",
                    "weekLabel": "Jul 24 – Jul 30",
                    "startDate": "2026-07-24",
                    "endDate": "2026-07-30",
                    "status": "current",
                    "openingCashCents": 0,
                    "incomeCents": 100000,
                    "debitBillsCents": 0,
                    "creditBillsCents": 0,
                    "requiredDebtMinimumCents": 0,
                    "extraDebtPaymentCents": 0,
                    "closingCashCents": 100000,
                    "openingDebtCents": 0,
                    "closingDebtCents": 0,
                    "events": [
                      {
                        "key": "income:secondary-pay:2026-07-26",
                        "kind": "income",
                        "label": "Partner Income Stream 1",
                        "dateKey": "2026-07-26",
                        "status": "current",
                        "amountCents": 100000
                      }
                    ]
                  }
                }
                """
        });
        await db.SaveChangesAsync();

        var snapshot = await CreateService(db)
            .ProjectAsync(
                clientProfileId,
                new DateOnly(2026, 7, 26));

        Assert.Equal("Income", Assert.Single(snapshot.WeekAtGlance!.Events).Title);
    }

    [Fact]
    public async Task ProjectAsync_RejectsEmptyClientProfileIdentifier()
    {
        await using var db = CreateDbContext();

        var service =
            CreateService(db);

        var exception = await Assert.ThrowsAsync<ArgumentException>(
            () => service.ProjectAsync(
                Guid.Empty,
                new DateOnly(2026, 7, 26)));

        Assert.Equal("clientProfileId", exception.ParamName);
    }

    [Fact]
    public async Task ProjectAsync_ObservesCancellation()
    {
        await using var db = CreateDbContext();

        var service =
            CreateService(db);

        using var cancellationSource =
            new CancellationTokenSource();

        cancellationSource.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => service.ProjectAsync(
                Guid.NewGuid(),
                new DateOnly(2026, 7, 26),
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

        Assert.Equal(2, methods.Length);
        Assert.Contains("ProjectAsync", methods);
        Assert.Contains("ProjectAgentAsync", methods);
    }

    internal const string LiveInputs = """
        {
          "incomeStreams": { "primary": [{ "id": "salary", "label": "Salary", "amount": "5000", "frequency": "monthly", "anchorDate": "2026-01-02" }] },
          "categories": [
            { "id": "card", "name": "Credit card payment", "amount": "200", "due": "2026-01-03", "frequency": "monthly", "paymentMethod": "Debit" },
            { "id": "mortgage", "name": "Mortgage", "amount": "1200", "due": "2026-01-04", "frequency": "monthly", "paymentMethod": "Debit" },
            { "id": "student", "name": "Student loan", "amount": "100", "due": "2026-01-05", "frequency": "monthly", "paymentMethod": "Debit" },
            { "id": "insurance", "name": "Insurance", "amount": "300", "due": "2026-01-06", "frequency": "monthly", "paymentMethod": "Debit" }
          ],
          "mobileWeekProjection": { "schemaVersion": 999, "incomeCents": 1 }
        }
        """;

    private static MobileFinancialOperatingSystemProjectionService CreateService(MasterAppDbContext db)
    {
        var households = new Mock<IHouseholdMembershipService>();
        households.Setup(h => h.ResolveActiveAccessAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid id, CancellationToken _) => new HouseholdAccessResolution(true, id, id, null, null));
        return new MobileFinancialOperatingSystemProjectionService(db, households.Object);
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
