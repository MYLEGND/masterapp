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
            lead.LeadId == mobileLead.ClientUserId && lead.CrmStage == "Qualified");
        Assert.Contains(result.Leads, lead =>
            lead.LeadId == "workstation-lead" && lead.CrmStage == "Contacted");
        Assert.DoesNotContain(result.Leads, lead => lead.LeadId == legacyPortalClient.ClientUserId);
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
