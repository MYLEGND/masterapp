using System;
using System.Security.Claims;
using System.Threading.Tasks;
using ClientApp.Controllers;
using ClientApp.Services;
using Domain.Entities;
using Infrastructure.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AgentPortal.Tests;

public class ClientAppProductionControllerTests
{
    private static MasterAppDbContext BuildDb() => ControllerTestHelpers.BuildDb();

    private static ClaimsPrincipal BuildClientUser(string clientUserId, string email = "client@example.com") => new(
        new ClaimsIdentity(new[]
        {
            new Claim("oid", clientUserId),
            new Claim("preferred_username", email)
        }, "TestAuth"));

    private static ProductionController BuildController(MasterAppDbContext db, ClaimsPrincipal user, Guid selfClientProfileId)
    {
        var contextService = new EffectiveClientContextService(db);
        var http = new DefaultHttpContext { User = user };
        http.Request.Headers.Cookie = $"selfClientProfileId={selfClientProfileId}";

        return new ProductionController(db, contextService)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = http
            }
        };
    }

    [Fact]
    public async Task ClientHistory_MismatchedClient_ReturnsForbid()
    {
        using var db = BuildDb();
        var self = new ClientProfile { Id = Guid.NewGuid(), ClientUserId = "client-a", Email = "a@example.com", NormalizedEmail = "a@example.com", CreatedUtc = DateTime.UtcNow };
        db.ClientProfiles.Add(self);
        await db.SaveChangesAsync();

        var controller = BuildController(db, BuildClientUser("client-a", "a@example.com"), self.Id);
        var result = await controller.ClientHistory("client-b");

        Assert.IsType<ForbidResult>(result);
    }

    [Fact]
    public async Task AddClient_MismatchedClient_ReturnsForbid()
    {
        using var db = BuildDb();
        var self = new ClientProfile { Id = Guid.NewGuid(), ClientUserId = "client-a", Email = "a@example.com", NormalizedEmail = "a@example.com", CreatedUtc = DateTime.UtcNow };
        db.ClientProfiles.Add(self);
        await db.SaveChangesAsync();

        var controller = BuildController(db, BuildClientUser("client-a", "a@example.com"), self.Id);
        var result = await controller.AddClient("client-b", 100m, 25m, (int)ProductionStatus.Submitted, "x");

        Assert.IsType<ForbidResult>(result);
        Assert.False(await db.ProductionRecords.AnyAsync());
    }

    [Fact]
    public async Task Update_OtherClientRecord_ReturnsNotFound()
    {
        using var db = BuildDb();
        var self = new ClientProfile { Id = Guid.NewGuid(), ClientUserId = "client-a", Email = "a@example.com", NormalizedEmail = "a@example.com", CreatedUtc = DateTime.UtcNow };
        db.ClientProfiles.Add(self);

        var otherRecord = new ProductionRecord
        {
            Id = Guid.NewGuid(),
            ClientUserId = "client-b",
            AgentUserId = "agent-b",
            Side = ProductionSide.Client,
            Status = ProductionStatus.Submitted,
            Amount = 10m,
            PersonalAmount = 1m,
            CreatedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow
        };
        db.ProductionRecords.Add(otherRecord);
        await db.SaveChangesAsync();

        var controller = BuildController(db, BuildClientUser("client-a", "a@example.com"), self.Id);
        var result = await controller.Update(otherRecord.Id, 99m, 9m, (int)ProductionStatus.Issued, "updated");

        Assert.IsType<NotFoundResult>(result);
        var persisted = await db.ProductionRecords.SingleAsync(x => x.Id == otherRecord.Id);
        Assert.Equal(10m, persisted.Amount);
        Assert.Equal(ProductionStatus.Submitted, persisted.Status);
    }

    [Fact]
    public async Task Delete_OtherClientRecord_ReturnsNotFound()
    {
        using var db = BuildDb();
        var self = new ClientProfile { Id = Guid.NewGuid(), ClientUserId = "client-a", Email = "a@example.com", NormalizedEmail = "a@example.com", CreatedUtc = DateTime.UtcNow };
        db.ClientProfiles.Add(self);

        var otherRecord = new ProductionRecord
        {
            Id = Guid.NewGuid(),
            ClientUserId = "client-b",
            AgentUserId = "agent-b",
            Side = ProductionSide.Client,
            Status = ProductionStatus.Submitted,
            Amount = 10m,
            PersonalAmount = 1m,
            CreatedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow
        };
        db.ProductionRecords.Add(otherRecord);
        await db.SaveChangesAsync();

        var controller = BuildController(db, BuildClientUser("client-a", "a@example.com"), self.Id);
        var result = await controller.Delete(otherRecord.Id);

        Assert.IsType<NotFoundResult>(result);
        Assert.True(await db.ProductionRecords.AnyAsync(x => x.Id == otherRecord.Id));
    }
}
