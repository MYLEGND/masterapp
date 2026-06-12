using AgentPortal.Services;
using Domain.Entities;
using Microsoft.Extensions.Logging.Abstractions;
using System;
using System.Threading.Tasks;
using Xunit;

namespace AgentPortal.Tests;

public class ProductionServiceTests
{
    [Fact]
    public async Task GetAgentTotalsAsync_UsesLatestRowPerContactForActiveTotals()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var now = new DateTime(2026, 6, 12, 0, 0, 0, DateTimeKind.Utc);

        db.ProductionRecords.AddRange(
            new ProductionRecord
            {
                AgentUserId = "agent-1",
                Side = ProductionSide.Lead,
                LeadId = "lead-1",
                Status = ProductionStatus.Submitted,
                Amount = 100m,
                PersonalAmount = 10m,
                CreatedUtc = now.AddMinutes(-20),
                UpdatedUtc = now.AddMinutes(-20)
            },
            new ProductionRecord
            {
                AgentUserId = "agent-1",
                Side = ProductionSide.Lead,
                LeadId = "lead-1",
                Status = ProductionStatus.Paid,
                Amount = 300m,
                PersonalAmount = 40m,
                CreatedUtc = now.AddMinutes(-10),
                UpdatedUtc = now.AddMinutes(-10)
            },
            new ProductionRecord
            {
                AgentUserId = "agent-1",
                Side = ProductionSide.Lead,
                LeadId = "lead-2",
                Status = ProductionStatus.Submitted,
                Amount = 150m,
                PersonalAmount = 0m,
                CreatedUtc = now.AddMinutes(-30),
                UpdatedUtc = now.AddMinutes(-30)
            },
            new ProductionRecord
            {
                AgentUserId = "agent-1",
                Side = ProductionSide.Lead,
                LeadId = "lead-2",
                Status = ProductionStatus.Issued,
                Amount = 200m,
                PersonalAmount = 0m,
                CreatedUtc = now.AddMinutes(-5),
                UpdatedUtc = now.AddMinutes(-5)
            },
            new ProductionRecord
            {
                AgentUserId = "agent-1",
                Side = ProductionSide.Lead,
                LeadId = "lead-3",
                Status = ProductionStatus.Submitted,
                Amount = 90m,
                PersonalAmount = 15m,
                CreatedUtc = now.AddMinutes(-3),
                UpdatedUtc = now.AddMinutes(-3)
            },
            new ProductionRecord
            {
                AgentUserId = "agent-1",
                Side = ProductionSide.Client,
                ClientUserId = "client-1",
                Status = ProductionStatus.Paid,
                Amount = 777m,
                PersonalAmount = 70m,
                CreatedUtc = now.AddMinutes(-2),
                UpdatedUtc = now.AddMinutes(-2)
            },
            new ProductionRecord
            {
                AgentUserId = "agent-2",
                Side = ProductionSide.Lead,
                LeadId = "lead-x",
                Status = ProductionStatus.Paid,
                Amount = 999m,
                PersonalAmount = 999m,
                CreatedUtc = now.AddMinutes(-1),
                UpdatedUtc = now.AddMinutes(-1)
            });

        await db.SaveChangesAsync();

        var service = new ProductionService(db, NullLogger<ProductionService>.Instance);
        var totals = await service.GetAgentTotalsAsync("agent-1", ProductionSide.Lead);

        Assert.Equal(90m, totals.Submitted);
        Assert.Equal(1, totals.CountSubmitted);
        Assert.Equal(200m, totals.Issued);
        Assert.Equal(1, totals.CountIssued);
        Assert.Equal(300m, totals.Paid);
        Assert.Equal(1, totals.CountPaid);
        Assert.Equal(55m, totals.Personal);
        Assert.Equal(2, totals.CountPersonal);
    }
}
