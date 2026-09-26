using System;
using System.Linq;
using System.Threading.Tasks;
using Domain.Entities;
using Domain.Enums;
using Infrastructure.Analytics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentPortal.Tests;

public sealed class CanonicalCrmOutcomeLineageTests
{
    [Fact]
    public async Task PolicyIssuedAndPaidResolveBackToTheOriginalWebsiteLeadAndAgentOwner()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var websiteLeadId = Guid.NewGuid();
        var trackingId = Guid.NewGuid();

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
            SubmittedUtc = DateTime.UtcNow,
            CapturedUtc = DateTime.UtcNow
        });
        await db.SaveChangesAsync();

        var service = new MetaSignalCrmOutcomeService(
            db,
            NullLogger<MetaSignalCrmOutcomeService>.Instance);

        var issuedRecordId = Guid.NewGuid();
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
                Assert.False(paid.MetaBrowserSent);
                Assert.False(paid.MetaServerSent);
            });
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
