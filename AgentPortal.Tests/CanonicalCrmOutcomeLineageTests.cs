using System;
using System.Linq;
using System.Threading.Tasks;
using Domain.Entities;
using Domain.Enums;
using Infrastructure.Analytics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Shared.Crm;

namespace AgentPortal.Tests;

public sealed class CanonicalCrmOutcomeLineageTests
{
    [Fact]
    public async Task PolicyIssuedAndPaidResolveBackToTheOriginalWebsiteLeadAndAgentOwner()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var websiteLeadId = Guid.NewGuid();
        var trackingId = Guid.NewGuid();
        var version = Guid.NewGuid();
        var originalTime = new DateTime(2026, 8, 22, 11, 30, 0, DateTimeKind.Utc);

        db.AgentTrackingProfiles.Add(new AgentTrackingProfile
        {
            Id = trackingId,
            AgentUserId = "agent-lineage",
            AgentUpn = "agent-lineage@example.test",
            Slug = "agent-lineage",
            Status = "active",
            CreatedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow
        });
        db.WebsiteLeads.Add(new WebsiteLead
        {
            LeadId = websiteLeadId,
            AgentTrackingProfileId = trackingId,
            AgentSlug = "agent-lineage",
            WebsiteContentVersionId = version,
            WebsiteBindingId = "original-binding",
            Host = "protect.mylegnd.com",
            FirstName = "Taylor",
            LastName = "Lead",
            Email = "taylor@example.test",
            Phone = "6025550100",
            CreatedUtc = DateTime.UtcNow,
            Status = "New"
        });
        db.WebsiteLeadIntakeLinks.Add(new WebsiteLeadIntakeLink
        {
            Id = Guid.NewGuid(),
            WebsiteLeadPublicId = websiteLeadId,
            WorkstationLeadId = "lead-lineage",
            AgentUserId = "agent-lineage",
            SessionId = "original-session", VisitorId = "original-visitor",
            UtmSource = "source", UtmMedium = "medium", UtmCampaign = "campaign", UtmId = "campaign-id",
            MetaCampaignId = "meta-campaign", MetaAdSetId = "meta-adset", MetaAdId = "meta-ad",
            Fbclid = "fb-click", Fbc = "fbc-click", Fbp = "fbp-browser", Oppref = "openai-click",
            ReferrerUrl = "https://referrer.example.test", PagePath = "/original", LandingPageUrl = "https://protect.mylegnd.com/original",
            ClientIpAddress = "192.0.2.1", ClientUserAgent = "original-agent",
            SubmittedUtc = DateTime.UtcNow,
            CapturedUtc = DateTime.UtcNow
        });
        await db.SaveChangesAsync();

        var service = new MetaSignalCrmOutcomeService(
            db,
            NullLogger<MetaSignalCrmOutcomeService>.Instance);

        var issuedRecordId = Guid.NewGuid();
        db.ProductionRecords.Add(new ProductionRecord { Id = issuedRecordId, AgentUserId = "agent-lineage", UpdatedUtc = originalTime });
        await db.SaveChangesAsync();
        await service.RecordProductionOutcomeAsync(
            issuedRecordId,
            "agent-lineage",
            ProductionSide.Lead,
            ProductionStatus.Issued,
            "lead-lineage",
            null,
            250000m,
            1200m,
            "issued");

        var paidRecordId = Guid.NewGuid();
        db.ProductionRecords.Add(new ProductionRecord { Id = paidRecordId, AgentUserId = "agent-lineage", UpdatedUtc = originalTime });
        await db.SaveChangesAsync();
        await service.RecordProductionOutcomeAsync(
            paidRecordId,
            "agent-lineage",
            ProductionSide.Lead,
            ProductionStatus.Paid,
            "lead-lineage",
            null,
            250000m,
            1200m,
            "paid");

        // Replay must not create a duplicate paid outcome.
        await service.RecordProductionOutcomeAsync(
            paidRecordId,
            "agent-lineage",
            ProductionSide.Lead,
            ProductionStatus.Paid,
            "lead-lineage",
            null,
            250000m,
            1200m,
            "paid");

        Assert.Empty(db.MetaSignalEvents);
        foreach (var source in await db.AnalyticsEvents.AsNoTracking().ToListAsync())
        {
            Assert.Equal(originalTime, source.EventUtc);
            Assert.Equal(version, source.WebsiteContentVersionId);
            Assert.Equal("original-binding", source.WebsiteBindingId);
            Assert.Equal("original-session", source.SessionId);
            Assert.Equal("original-visitor", source.VisitorId);
            Assert.Equal("campaign", source.UtmCampaign);
            Assert.Equal("meta-campaign", source.MetaCampaignId);
            Assert.Equal("openai-click", source.Oppref);
            Assert.Equal("fb-click", source.Fbclid);
            Assert.Equal("/original", source.Path);
            Assert.Equal("fbc-click", CanonicalAdvertisingEventProjection.ReadString(source.MetadataJson, "fbc"));
            Assert.Equal("fbp-browser", CanonicalAdvertisingEventProjection.ReadString(source.MetadataJson, "fbp"));
            Assert.Equal(120000, CanonicalAdvertisingEventProjection.ReadInt64(source.MetadataJson, "valueCents"));
            Assert.True(await MetaSignalAnalyticsBridge.PersistAsync(db, source));
        }
        var events = await db.MetaSignalEvents
            .Where(x => x.EventName == "PolicyIssued" || x.EventName == "PolicyPaid")
            .OrderBy(x => x.FunnelStep)
            .ToListAsync();

        Assert.Equal(2, events.Count);
        Assert.Collection(events,
            issued =>
            {
                Assert.Equal("PolicyIssued", issued.EventName);
                Assert.Equal(websiteLeadId, issued.LeadId);
                Assert.Equal(trackingId, issued.AgentTrackingProfileId);
                Assert.Equal("agent-lineage", issued.AgentSlug);
                Assert.Equal("policy_issued", issued.StepName);
                Assert.False(issued.MetaBrowserSent);
                Assert.False(issued.MetaServerSent);
            },
            paid =>
            {
                Assert.Equal("PolicyPaid", paid.EventName);
                Assert.Equal(websiteLeadId, paid.LeadId);
                Assert.Equal(trackingId, paid.AgentTrackingProfileId);
                Assert.Equal("agent-lineage", paid.AgentSlug);
                Assert.Equal("policy_paid", paid.StepName);
                Assert.Equal(originalTime, paid.CreatedUtc);
                Assert.Equal("original-session", paid.SessionId);
                Assert.Equal("original-binding", paid.WebsiteBindingId);
                Assert.Equal("openai-click", CanonicalAdvertisingEventProjection.ReadString(paid.MetadataJson, "oppref"));
                Assert.Equal(120000, CanonicalAdvertisingEventProjection.ReadInt64(paid.MetadataJson, "valueCents"));
                Assert.False(paid.MetaBrowserSent);
                Assert.False(paid.MetaServerSent);
            });
    }

    [Fact]
    public async Task ConvertedClientPolicyOutcomeKeepsOriginalWebsiteLeadLineage()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var websiteLeadId = Guid.NewGuid();
        var trackingId = Guid.NewGuid();
        var clientUserId = Guid.NewGuid().ToString();

        db.AgentTrackingProfiles.Add(new AgentTrackingProfile
        {
            Id = trackingId,
            AgentUserId = "agent-client-lineage",
            AgentUpn = "agent-client@example.test",
            Slug = "agent-client",
            Status = "active",
            CreatedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow
        });
        db.WebsiteLeads.Add(new WebsiteLead
        {
            LeadId = websiteLeadId,
            AgentTrackingProfileId = trackingId,
            AgentSlug = "agent-client",
            FirstName = "Converted",
            Email = "converted@example.test",
            CreatedUtc = DateTime.UtcNow
        });
        db.WebsiteLeadIntakeLinks.Add(new WebsiteLeadIntakeLink
        {
            Id = Guid.NewGuid(),
            WebsiteLeadPublicId = websiteLeadId,
            WorkstationLeadId = "source-workstation-lead",
            AgentUserId = "agent-client-lineage",
            SubmittedUtc = DateTime.UtcNow,
            CapturedUtc = DateTime.UtcNow
        });
        db.ClientProfiles.Add(new ClientProfile
        {
            Id = Guid.NewGuid(),
            ClientUserId = clientUserId,
            FirstName = "Converted",
            LastName = "Client",
            Email = "converted@example.test",
            CrmNotes = ClientCrmMetaSerializer.Serialize(new ClientCrmMeta
            {
                RecordType = "Client",
                SourceWorkstationLeadId = "source-workstation-lead"
            })
        });
        await db.SaveChangesAsync();

        var service = new MetaSignalCrmOutcomeService(
            db,
            NullLogger<MetaSignalCrmOutcomeService>.Instance);

        await service.RecordProductionOutcomeAsync(
            Guid.NewGuid(),
            "agent-client-lineage",
            ProductionSide.Client,
            ProductionStatus.Paid,
            null,
            clientUserId,
            100000m,
            900m,
            "paid");

        foreach (var source in await db.AnalyticsEvents.AsNoTracking().ToListAsync())
            await Infrastructure.Analytics.MetaSignalAnalyticsBridge.PersistAsync(db, source);
        var paid = await db.MetaSignalEvents.SingleAsync(x => x.EventName == "PolicyPaid");
        Assert.Equal(websiteLeadId, paid.LeadId);
        Assert.Equal(trackingId, paid.AgentTrackingProfileId);
        Assert.Equal("agent-client", paid.AgentSlug);
    }

    [Fact]
    public void ServerConversionCatalogIncludesLeadBookingAndPolicyTruthEvents()
    {
        foreach (var eventName in new[]
                 {
                     "Lead",
                     "QualifiedLead",
                     "AppointmentBooked",
                     "AppointmentCompleted",
                     "ApplicationSubmitted",
                     "PolicyIssued",
                     "PolicyPaid"
                 })
        {
            Assert.True(Shared.Analytics.MetaSignalEventCatalog.TryGet(eventName, out var definition));
            Assert.False(definition.AllowBrowserPixel);
            Assert.True(definition.AllowServerForward);
            Assert.True(Shared.Analytics.MetaSignalEventCatalog.IsServerAuthorityEvent(eventName));
        }
    }
}
