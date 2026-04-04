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

    [Fact]
    public async Task UpdateAction_DeniesNonOwner()
    {
        using var db = BuildDb();
        var engine = new ExecutionEngine(db);
        var action = new ActionItem
        {
            RelatedEntityType = RelatedEntityType.Client,
            RelatedEntityId = "client-1",
            Title = "Original",
            OwnerId = "agent-1",
            EffectiveAgentOid = "agent-1",
            CreatedBy = "agent-1"
        };
        db.ActionItems.Add(action);
        await db.SaveChangesAsync();

        var denied = await engine.UpdateActionAsync(action.Id, "agent-2", "Updated", null, null, ActionPriority.P1);
        Assert.Null(denied);

        var persisted = await db.ActionItems.SingleAsync(x => x.Id == action.Id);
        Assert.Equal("Original", persisted.Title);
    }

    [Fact]
    public async Task DeleteAction_DeniesNonOwner()
    {
        using var db = BuildDb();
        var engine = new ExecutionEngine(db);
        var action = new ActionItem
        {
            RelatedEntityType = RelatedEntityType.Client,
            RelatedEntityId = "client-1",
            Title = "Delete test",
            OwnerId = "agent-1",
            EffectiveAgentOid = "agent-1",
            CreatedBy = "agent-1"
        };
        db.ActionItems.Add(action);
        await db.SaveChangesAsync();

        var deleted = await engine.DeleteActionAsync(action.Id, "agent-2");
        Assert.False(deleted);
        Assert.True(await db.ActionItems.AnyAsync(x => x.Id == action.Id));
    }

    [Fact]
    public async Task GetByRelated_ScopesToActor()
    {
        using var db = BuildDb();
        var engine = new ExecutionEngine(db);
        var relatedId = "client-42";

        db.ActionItems.AddRange(
            new ActionItem
            {
                RelatedEntityType = RelatedEntityType.Client,
                RelatedEntityId = relatedId,
                Title = "Mine",
                OwnerId = "agent-1",
                EffectiveAgentOid = "agent-1",
                CreatedBy = "agent-1"
            },
            new ActionItem
            {
                RelatedEntityType = RelatedEntityType.Client,
                RelatedEntityId = relatedId,
                Title = "Not mine",
                OwnerId = "agent-2",
                EffectiveAgentOid = "agent-2",
                CreatedBy = "agent-2"
            });
        await db.SaveChangesAsync();

        var mine = await engine.GetByRelatedAsync(RelatedEntityType.Client, relatedId, "agent-1");
        Assert.Single(mine);
        Assert.Equal("Mine", mine[0].Title);
    }
}
