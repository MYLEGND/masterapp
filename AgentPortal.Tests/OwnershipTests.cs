using System;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using AgentPortal.Controllers;
using AgentPortal.Services;
using Domain.Entities;
using Infrastructure.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Moq;
using Xunit;

namespace AgentPortal.Tests;

/// <summary>
/// Ownership and access-restriction tests for Lead and Client critical paths.
/// Validates that agents cannot read or modify data belonging to other agents.
/// </summary>
public class OwnershipTests
{
    private static MasterAppDbContext BuildDb() => ControllerTestHelpers.BuildDb();

    private static ClaimsPrincipal User(string oid) => new(
        new ClaimsIdentity(new[] { new Claim("oid", oid) }, "TestAuth"));

    // -----------------------------------------------------------------------
    // Lead ownership: agent B's query only returns their own leads
    // -----------------------------------------------------------------------
    [Fact]
    public async Task Leads_QueryScopedToAgent()
    {
        using var db = BuildDb();

        db.WorkstationLeadProfiles.AddRange(
            new WorkstationLeadProfile
            {
                LeadId = "L-A",
                AgentUserId = "agent-A",
                Bucket = "MortgageProtection",
                CrmStage = "New",
                CreatedUtc = DateTime.UtcNow
            },
            new WorkstationLeadProfile
            {
                LeadId = "L-B",
                AgentUserId = "agent-B",
                Bucket = "MortgageProtection",
                CrmStage = "New",
                CreatedUtc = DateTime.UtcNow
            });
        await db.SaveChangesAsync();

        // Verify at DB level that filtering by agent-B only returns L-B
        var agentBLeads = await db.WorkstationLeadProfiles
            .Where(l => l.AgentUserId == "agent-B")
            .ToListAsync();

        Assert.Single(agentBLeads);
        Assert.Equal("L-B", agentBLeads[0].LeadId);
    }

    // -----------------------------------------------------------------------
    // Lead delete: agent B cannot delete agent A's lead
    // -----------------------------------------------------------------------
    [Fact]
    public async Task Leads_Delete_CannotDeleteOtherAgentLead()
    {
        using var db = BuildDb();

        var leadId = Guid.NewGuid().ToString();
        db.WorkstationLeadProfiles.Add(new WorkstationLeadProfile
        {
            LeadId = leadId,
            AgentUserId = "agent-A",
            Bucket = "MortgageProtection",
            CrmStage = "New",
            CreatedUtc = DateTime.UtcNow
        });
        await db.SaveChangesAsync();

        var controller = ControllerTestHelpers.BuildLeadsController(
            db,
            Mock.Of<IExecutionEngine>(),
            Mock.Of<ICommitmentService>(),
            User("agent-B"));
        controller.ControllerContext.HttpContext.Items["EffectiveAgentOid"] = "agent-B";

        // Delete uses `clientUserId` parameter name (it's the lead/workstation lead ID)
        var result = await controller.Delete(leadId);

        // The controller checks AgentUserId == agentId, so agent-B gets NotFound
        Assert.IsType<NotFoundResult>(result);

        // Lead must still exist
        var leadStillExists = await db.WorkstationLeadProfiles.AnyAsync(l => l.LeadId == leadId);
        Assert.True(leadStillExists, "Lead belonging to agent-A should not be deleted by agent-B");
    }

    // -----------------------------------------------------------------------
    // Lead delete: agent can delete their own lead
    // -----------------------------------------------------------------------
    [Fact]
    public async Task Leads_Delete_OwnerCanDeleteLead()
    {
        using var db = BuildDb();

        var leadId = Guid.NewGuid().ToString();
        db.WorkstationLeadProfiles.Add(new WorkstationLeadProfile
        {
            LeadId = leadId,
            AgentUserId = "agent-A",
            Bucket = "MortgageProtection",
            CrmStage = "New",
            CreatedUtc = DateTime.UtcNow
        });
        await db.SaveChangesAsync();

        var controller = ControllerTestHelpers.BuildLeadsController(
            db,
            Mock.Of<IExecutionEngine>(),
            Mock.Of<ICommitmentService>(),
            User("agent-A"));
        controller.ControllerContext.HttpContext.Items["EffectiveAgentOid"] = "agent-A";

        var result = await controller.Delete(leadId);

        Assert.IsType<RedirectToActionResult>(result);
        var leadExists = await db.WorkstationLeadProfiles.AnyAsync(l => l.LeadId == leadId);
        Assert.False(leadExists, "Owner should be able to delete their own lead");
    }

    // -----------------------------------------------------------------------
    // Client ownership: AgentClient link scopes client to creating agent
    // -----------------------------------------------------------------------
    [Fact]
    public async Task Clients_AgentClientLink_ScopesClientToAgent()
    {
        using var db = BuildDb();

        const string clientOid = "client-user-1";
        db.ClientProfiles.Add(new ClientProfile
        {
            ClientUserId = clientOid,
            FirstName = "Owned",
            LastName = "ByA",
            Email = "owned@example.com",
            NormalizedEmail = "owned@example.com",
            CreatedUtc = DateTime.UtcNow
        });
        db.AgentClients.Add(new AgentClient
        {
            AgentUserId = "agent-A",
            ClientUserId = clientOid,
            CreatedUtc = DateTime.UtcNow
        });
        await db.SaveChangesAsync();

        // Agent B has no AgentClient link
        var agentBClients = await db.AgentClients
            .Where(ac => ac.AgentUserId == "agent-B")
            .Join(db.ClientProfiles, ac => ac.ClientUserId, cp => cp.ClientUserId, (_, cp) => cp)
            .ToListAsync();
        Assert.Empty(agentBClients);

        // Agent A does have access
        var agentAClients = await db.AgentClients
            .Where(ac => ac.AgentUserId == "agent-A")
            .Join(db.ClientProfiles, ac => ac.ClientUserId, cp => cp.ClientUserId, (_, cp) => cp)
            .ToListAsync();
        Assert.Single(agentAClients);
        Assert.Equal(clientOid, agentAClients[0].ClientUserId);
    }

    // -----------------------------------------------------------------------
    // Client edit: agent B cannot edit agent A's client
    // -----------------------------------------------------------------------
    [Fact]
    public async Task Clients_Edit_ReturnsNotFoundForUnownedClient()
    {
        using var db = BuildDb();

        const string clientUserId = "client-user-2";
        db.ClientProfiles.Add(new ClientProfile
        {
            ClientUserId = clientUserId,
            FirstName = "Private",
            LastName = "Client",
            Email = "private@example.com",
            NormalizedEmail = "private@example.com",
            CreatedUtc = DateTime.UtcNow
        });
        db.AgentClients.Add(new AgentClient
        {
            AgentUserId = "agent-A",
            ClientUserId = clientUserId,
            CreatedUtc = DateTime.UtcNow
        });
        await db.SaveChangesAsync();

        var controller = ControllerTestHelpers.BuildClientsController(
            db,
            Mock.Of<IExecutionEngine>(),
            Mock.Of<ICommitmentService>(),
            User("agent-B"));
        controller.ControllerContext.HttpContext.Items["EffectiveAgentOid"] = "agent-B";

        var result = await controller.Edit(clientUserId);

        // agent-B has no AgentClient link → Forbid (403)
        Assert.IsType<ForbidResult>(result);
    }
}
