using System;
using System.Linq;
using System.Threading.Tasks;
using AgentPortal.Services;
using Domain.Entities;
using Domain.Enums;
using Xunit;

namespace AgentPortal.Tests;

public sealed class BusinessActionScopeTests
{
    [Fact]
    public async Task CanonicalEngineKeepsBusinessTasksOutOfAgentAndOtherBusinessScopes()
    {
        using var db = ControllerTestHelpers.BuildDb();
        var business = Guid.NewGuid();
        var otherBusiness = Guid.NewGuid();
        db.Add(new WorkstationLeadProfile { LeadId = "business-contact", CommerceBusinessId = business, AgentUserId = "" });
        await db.SaveChangesAsync();
        var engine = new ExecutionEngine(db, business);
        var action = await engine.CreateActionAsync(new ActionItem
        {
            Title = "Confirm estimate", RelatedEntityId = "business-contact", RelatedEntityType = RelatedEntityType.BusinessContact,
            OwnerType = ActionOwnerType.Business, OwnerId = business.ToString(), CreatedBy = "member-a"
        });
        Assert.Single(await engine.GetByRelatedAsync(RelatedEntityType.BusinessContact, "business-contact", "member-b"));
        var agent = new ExecutionEngine(db);
        Assert.Null(await agent.GetByIdAsync(action.Id, "member-a"));
        Assert.Null(await agent.GetByIdAsync(action.Id, business.ToString()));
        Assert.Null(await agent.CompleteActionAsync(action.Id, "member-a"));
        var other = new ExecutionEngine(db, otherBusiness);
        Assert.Null(await other.GetByIdAsync(action.Id, "member-a"));
        Assert.False(await other.DeleteActionAsync(action.Id, "member-a"));
        Assert.Null(await other.CompleteActionAsync(action.Id, "member-a"));
        var updated = await engine.UpdateActionAsync(action.Id, "member-b", "Revised estimate", "Call customer", null, ActionPriority.P1);
        Assert.Equal("Revised estimate", updated!.Title);
        Assert.Equal(ActionStatus.Completed, (await engine.CompleteActionAsync(action.Id, "member-b"))!.Status);
        Assert.Contains(db.ActionLogs, x => x.ActionId == action.Id && x.ActorId == "member-b" && x.Verb == "completed");
        await Assert.ThrowsAsync<ArgumentException>(() => engine.ReassignAsync(action.Id, ActionOwnerType.Agent, "member-b"));
    }

    [Fact]
    public async Task CanonicalEngineRejectsForeignAndMixedOwnershipOnCreate()
    {
        using var db = ControllerTestHelpers.BuildDb();
        var business = Guid.NewGuid();
        db.Add(new WorkstationLeadProfile { LeadId = "foreign", CommerceBusinessId = Guid.NewGuid(), AgentUserId = "" });
        db.Add(new WorkstationLeadProfile { LeadId = "mixed", CommerceBusinessId = business, AgentUserId = "agent" });
        await db.SaveChangesAsync();
        var engine = new ExecutionEngine(db, business);
        foreach (var id in new[] { "foreign", "mixed", "missing" })
        {
            await Assert.ThrowsAsync<ArgumentException>(() => engine.CreateActionAsync(new ActionItem
            {
                Title = "Invalid", RelatedEntityId = id, RelatedEntityType = RelatedEntityType.BusinessContact,
                OwnerType = ActionOwnerType.Business, OwnerId = business.ToString(), CreatedBy = "member"
            }));
        }
        Assert.Empty(db.ActionItems);
    }
}
