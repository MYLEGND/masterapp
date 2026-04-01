using System;
using System.Linq;
using System.Threading.Tasks;
using AgentPortal.Services;
using Domain.Entities;
using Domain.Enums;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AgentPortal.Tests;

public class ExecutionEngineTests
{
    private static MasterAppDbContext BuildDb()
    {
        var options = new DbContextOptionsBuilder<MasterAppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new MasterAppDbContext(options);
    }

    [Fact]
    public async Task CreateAction_SetsDefaults_AndLogsCreation()
    {
        using var db = BuildDb();
        var engine = new ExecutionEngine(db);
        var action = new ActionItem
        {
            RelatedEntityType = RelatedEntityType.Client,
            RelatedEntityId = "client-123",
            Title = "Follow up",
            OwnerId = "agent-1",
            CreatedBy = "agent-1"
        };

        var created = await engine.CreateActionAsync(action);

        Assert.Equal(ActionSurface.CrmOnly, created.ActionSurface); // defaulted
        Assert.Equal(ActionCategory.Other, created.ActionCategory); // defaulted
        Assert.NotEqual(default, created.CreatedUtc);
        Assert.Single(db.ActionLogs.Where(l => l.ActionId == created.Id && l.Verb == "created"));
    }

    [Fact]
    public async Task CompleteAction_EnforcesOwnerAndMarksCompleted()
    {
        using var db = BuildDb();
        var engine = new ExecutionEngine(db);
        var mine = new ActionItem
        {
            RelatedEntityType = RelatedEntityType.Lead,
            RelatedEntityId = "lead-1",
            Title = "Call back",
            OwnerId = "agent-1",
            CreatedBy = "agent-1"
        };
        db.ActionItems.Add(mine);
        await db.SaveChangesAsync();

        var notYours = await engine.CompleteActionAsync(mine.Id, "agent-2");
        Assert.Null(notYours); // safety guard: wrong owner cannot complete

        var completed = await engine.CompleteActionAsync(mine.Id, "agent-1");
        Assert.NotNull(completed);
        Assert.Equal(ActionStatus.Completed, completed!.Status);
        Assert.NotNull(completed.CompletedAtUtc);
    }
}
