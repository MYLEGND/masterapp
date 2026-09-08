using System;
using System.Linq;
using System.Threading.Tasks;
using AgentPortal.Models;
using Domain.Entities;
using Domain.JourneyCircles;
using Domain.Messaging;
using Infrastructure.Mobile;
using Moq;
using Xunit;

namespace AgentPortal.Tests;

public sealed class MobileAgentCrmProjectionTests
{
    [Fact]
    public async Task GetAgentLeadsAsync_IncludesHistoricalMobileCrmLeadsWithoutLeakingThemIntoMemberClients()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        const string agentUserId = "agent-mobile-crm";
        var mobileLead = new ClientProfile
        {
            Id = Guid.NewGuid(),
            ClientUserId = "lead-mobile-historical",
            FirstName = "Mobile",
            Email = "search@example.com", Phone = "6025550123",
            LastName = "Lead",
            CrmStatus = "Lead",
            CrmNotes = ClientCrmMetaSerializer.Serialize(new ClientCrmMeta
            {
                RecordType = "Lead",
                PipelineStage = "Qualified"
            }),
            UpdatedUtc = new DateTime(2026, 8, 14, 15, 0, 0, DateTimeKind.Utc)
        };
        var legacyPortalClient = new ClientProfile
        {
            Id = Guid.NewGuid(),
            ClientUserId = Guid.NewGuid().ToString("D"),
            FirstName = "Legacy",
            LastName = "Client",
            CrmStatus = "Active",
            CrmNotes = null,
            UpdatedUtc = new DateTime(2026, 8, 14, 14, 0, 0, DateTimeKind.Utc)
        };
        db.ClientProfiles.AddRange(mobileLead, legacyPortalClient);
        db.AgentClients.AddRange(
            new AgentClient { AgentUserId = agentUserId, ClientUserId = mobileLead.ClientUserId },
            new AgentClient { AgentUserId = agentUserId, ClientUserId = legacyPortalClient.ClientUserId });
        db.WorkstationLeadProfiles.Add(new WorkstationLeadProfile
        {
            LeadId = "workstation-lead",
            AgentUserId = agentUserId,
            FirstName = "Workstation",
            LastName = "Lead",
            CrmStage = "Contacted",
            UpdatedUtc = new DateTime(2026, 8, 14, 13, 0, 0, DateTimeKind.Utc)
        });
        await db.SaveChangesAsync();

        var service = CreateService(db);
        var actor = new MobileResolvedActor(
            new MessagingActor(agentUserId, MessagingParticipantTypes.Agent),
            Guid.NewGuid(),
            "Agent CRM");

        var result = await service.GetAgentLeadsAsync(actor);

        Assert.True(result.Succeeded);
        Assert.Contains(result.Leads, lead =>
            lead.LeadId == mobileLead.ClientUserId && lead.CrmStage == "Qualified" && lead.Email == "search@example.com" && lead.Phone == "6025550123");
        Assert.Contains(result.Leads, lead =>
            lead.LeadId == "workstation-lead" && lead.CrmStage == "Contacted");
        Assert.DoesNotContain(result.Leads, lead => lead.LeadId == legacyPortalClient.ClientUserId);
    }

    [Fact]
    public async Task ClosedProfiles_AreClassifiedIntoArchive_WithoutDependingOnRedactedNames()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var lead = new ClientProfile { Id = Guid.NewGuid(), ClientUserId = "lead-closed", FirstName = "Original", CrmStatus = "Lead" };
        var client = new ClientProfile { Id = Guid.NewGuid(), ClientUserId = Guid.NewGuid().ToString(), FirstName = "Original", CrmStatus = "Active" };
        db.ClientProfiles.AddRange(lead, client);
        foreach (var profile in new[] { lead, client })
        {
            db.AgentClients.Add(new AgentClient { AgentUserId = "agent-a", ClientUserId = profile.ClientUserId });
            db.AccountLifecycleRecords.Add(new AccountLifecycleRecord { UserId = profile.ClientUserId, ProfileId = profile.Id,
                ParticipantType = MessagingParticipantTypes.Client, State = Domain.Accounts.AccountLifecycleStates.Closed });
        }
        await db.SaveChangesAsync();
        var service = CreateService(db);
        var actor = new MobileResolvedActor(new MessagingActor("agent-a", MessagingParticipantTypes.Agent), Guid.NewGuid(), "Agent");
        var clients = (await service.GetAgentClientsAsync(actor)).Clients;
        var leads = (await service.GetAgentLeadsAsync(actor)).Leads;
        Assert.Equal(client.Id, Assert.Single(clients).ProfileId); Assert.True(clients[0].Archived);
        Assert.Equal(lead.ClientUserId, Assert.Single(leads).LeadId); Assert.True(leads[0].Archived);
        var other = new MobileResolvedActor(new MessagingActor("agent-b", MessagingParticipantTypes.Agent), Guid.NewGuid(), "Agent");
        Assert.Empty((await service.GetAgentClientsAsync(other)).Clients);
        Assert.Empty((await service.GetAgentLeadsAsync(other)).Leads);
    }

    private static MobileHomeService CreateService(Infrastructure.Data.MasterAppDbContext db) =>
        new(
            db,
            Mock.Of<IMessagingService>(),
            Mock.Of<IJourneyCirclesService>(),
            Mock.Of<Domain.FinancialIntelligence.IFinancialIntelligenceEvaluationService>(),
            Mock.Of<IMobileFinancialOperatingSystemProjectionService>(),
            Mock.Of<Infrastructure.Households.IHouseholdMembershipService>(),
            Mock.Of<Infrastructure.DailyScripture.IDailyScriptureService>());
}
