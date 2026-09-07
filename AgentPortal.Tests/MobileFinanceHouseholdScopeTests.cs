using System;
using System.Threading;
using System.Threading.Tasks;
using Domain.Entities;
using Infrastructure.Households;
using Infrastructure.Mobile;
using Moq;
using Xunit;

namespace AgentPortal.Tests;

public sealed class MobileFinanceHouseholdScopeTests
{
    [Fact]
    public async Task PartnerReadsAuthoritativeHouseholdInputs_NotNewerHistoricalProfileRow()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var partner = Guid.NewGuid();
        var owner = Guid.NewGuid();
        var household = Guid.NewGuid();
        db.FinanceToolStates.Add(new FinanceToolState
        {
            ClientProfileId = owner, HouseholdAccountId = household, ToolId = "ExpenseLens",
            JsonState = MobileFinancialOperatingSystemProjectionServiceTests.LiveInputs
        });
        db.FinanceToolStates.Add(new FinanceToolState
        {
            ClientProfileId = partner, ToolId = "ExpenseLens", JsonState = "{}",
            UpdatedUtc = DateTime.UtcNow.AddDays(1)
        });
        await db.SaveChangesAsync();
        var households = new Mock<IHouseholdMembershipService>();
        households.Setup(h => h.ResolveActiveAccessAsync(partner, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HouseholdAccessResolution(true, household, owner, null, null));
        var service = new MobileFinancialOperatingSystemProjectionService(db, households.Object);
        var result = await service.ProjectAsync(partner, new DateOnly(2027, 9, 4));
        Assert.Equal(500000, result.MonthAtGlance!.IncomeCents);

        households.Setup(h => h.ResolveActiveAccessAsync(partner, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HouseholdAccessResolution(false, null, null, null, "revoked"));
        var revoked = await service.ProjectAsync(partner, new DateOnly(2027, 9, 4));
        Assert.Equal("MOBILE_FINANCIAL_HOUSEHOLD_ACCESS_REQUIRED", revoked.Projection.ReasonCode);
        Assert.Null(revoked.WeekAtGlance);
        Assert.False(db.ChangeTracker.HasChanges());
    }
}
