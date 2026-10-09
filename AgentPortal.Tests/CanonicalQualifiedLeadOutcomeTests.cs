using System;
using System.Linq;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Domain.Entities;
using Infrastructure.Analytics;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AgentPortal.Tests;

public sealed class CanonicalQualifiedLeadOutcomeTests
{
    [Fact]
    public async Task QualificationTransitionWritesOneAuthorityAndReconcilesCurrentTruthWithoutDuplicateDelivery()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var trackingId = Guid.NewGuid();
        var websiteLeadId = Guid.NewGuid();

        db.AgentTrackingProfiles.Add(new AgentTrackingProfile
        {
            Id = trackingId,
            AgentUserId = "agent-qualified",
            AgentUpn = "agent-qualified@example.test",
            Slug = "agent-qualified",
            Status = "active",
            CreatedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow
        });
        db.WebsiteLeads.Add(new WebsiteLead
        {
            LeadId = websiteLeadId,
            AgentTrackingProfileId = trackingId,
            AgentSlug = "agent-qualified",
            FirstName = "Qualified",
            Email = "qualified@example.test",
            SessionId = "qualified-session",
            VisitorId = "qualified-visitor",
            UtmSource = "meta",
            UtmMedium = "paid",
            UtmCampaign = "qualified-campaign",
            CreatedUtc = DateTime.UtcNow,
            Status = "New"
        });
        db.WebsiteLeadIntakeLinks.Add(new WebsiteLeadIntakeLink
        {
            Id = Guid.NewGuid(),
            WebsiteLeadPublicId = websiteLeadId,
            WorkstationLeadId = "workstation-qualified",
            AgentUserId = "agent-qualified",
            SessionId = "qualified-session",
            VisitorId = "qualified-visitor",
            UtmSource = "meta",
            UtmMedium = "paid",
            UtmCampaign = "qualified-campaign",
            Oppref = "qualified-openai-click",
            SubmittedUtc = DateTime.UtcNow,
            CapturedUtc = DateTime.UtcNow
        });
        var lead = new WorkstationLeadProfile
        {
            LeadId = "workstation-qualified",
            AgentUserId = "agent-qualified",
            CrmStatus = "Lead",
            CrmStage = "Contacted",
            Bucket = "Contacted",
            UpdatedUtc = DateTime.UtcNow
        };
        db.WorkstationLeadProfiles.Add(lead);
        await db.SaveChangesAsync();

        var paidLanding = UnifiedEventMapper.ToAnalytics(new UnifiedEventContext
        {
            EventName = "page_view",
            EventUtc = DateTime.UtcNow.AddMinutes(-5),
            AgentTrackingProfileId = trackingId,
            SessionId = "qualified-session",
            VisitorId = "qualified-visitor",
            Gclid = "google-click-qualified",
            Ttclid = "tiktok-click-qualified",
            IsBrowserSignal = true,
            IsServerAuthority = false
        });
        paidLanding.ClientEventId = Guid.NewGuid();
        UnifiedAnalyticsWriter.Write(db, paidLanding);
        await db.SaveChangesAsync();

        lead.CrmStage = "Qualified";
        lead.Bucket = "Qualified";
        lead.UpdatedUtc = DateTime.UtcNow.AddMinutes(1);
        await CanonicalCrmOutcomeService.SaveLeadChangesAsync(db);

        var events = await db.AnalyticsEvents.AsNoTracking().ToListAsync();
        var authority = Assert.Single(events.Where(x =>
            x.TrackingVersion == "crm-qualification-authority-v1"));
        Assert.Equal("QualifiedLead", authority.EventType);
        Assert.Equal(trackingId, authority.AgentTrackingProfileId);
        Assert.Equal("qualified-session", authority.SessionId);
        Assert.Equal("qualified-visitor", authority.VisitorId);
        Assert.Equal("qualified-campaign", authority.UtmCampaign);
        Assert.Equal("qualified-openai-click", authority.Oppref);
        Assert.Equal("google-click-qualified",
            CanonicalAdvertisingEventProjection.ReadString(authority.MetadataJson, "gclid"));
        Assert.Equal("tiktok-click-qualified",
            CanonicalAdvertisingEventProjection.ReadString(authority.MetadataJson, "ttclid"));
        Assert.True(CanonicalAdvertisingEventProjection.ReadBoolean(
            authority.MetadataJson, "qualificationActive"));

        var current = CanonicalMarketingOutcomeProjection.ConfirmedOutcomes(events);
        Assert.Single(current.Where(x => x.EventType == "QualifiedLead"));

        lead.CrmStage = "Booked";
        lead.Bucket = "Booked";
        lead.UpdatedUtc = DateTime.UtcNow.AddMinutes(2);
        await CanonicalCrmOutcomeService.SaveLeadChangesAsync(db);

        events = await db.AnalyticsEvents.AsNoTracking().ToListAsync();
        Assert.Empty(events.Where(x => x.TrackingVersion == "crm-qualification-state-v1"));
        Assert.Single(CanonicalMarketingOutcomeProjection.ConfirmedOutcomes(events)
            .Where(x => x.EventType == "QualifiedLead"));

        lead.CrmStage = "Contacted";
        lead.Bucket = "Contacted";
        lead.UpdatedUtc = DateTime.UtcNow.AddMinutes(3);
        await CanonicalCrmOutcomeService.SaveLeadChangesAsync(db);

        events = await db.AnalyticsEvents.AsNoTracking().ToListAsync();
        Assert.Single(events.Where(x => x.TrackingVersion == "crm-qualification-authority-v1"));
        var reversal = Assert.Single(events.Where(x =>
            x.TrackingVersion == "crm-qualification-state-v1"));
        Assert.False(CanonicalAdvertisingEventProjection.ReadBoolean(
            reversal.MetadataJson, "qualificationActive"));
        Assert.False(CanonicalAdvertisingEventProjection.ReadBoolean(
            reversal.MetadataJson, "measurementServerAuthorityEligible"));
        Assert.False(CanonicalAdvertisingEventProjection.ReadBoolean(
            reversal.MetadataJson, "metaServerAuthorityEligible"));
        Assert.Empty(CanonicalMarketingOutcomeProjection.ConfirmedOutcomes(events)
            .Where(x => x.EventType == "QualifiedLead"));

        lead.CrmStage = "Qualified";
        lead.Bucket = "Qualified";
        lead.UpdatedUtc = DateTime.UtcNow.AddMinutes(4);
        await CanonicalCrmOutcomeService.SaveLeadChangesAsync(db);

        events = await db.AnalyticsEvents.AsNoTracking().ToListAsync();
        Assert.Single(events.Where(x => x.TrackingVersion == "crm-qualification-authority-v1"));
        Assert.Equal(2, events.Count(x => x.TrackingVersion == "crm-qualification-state-v1"));
        Assert.Single(CanonicalMarketingOutcomeProjection.ConfirmedOutcomes(events)
            .Where(x => x.EventType == "QualifiedLead"));
    }

    [Fact]
    public void CanonicalSourceContractsKeepGrowthLearningAndFounderToolsOnExistingAuthorities()
    {
        var root = GetRepoRoot();
        var manager = System.IO.File.ReadAllText(System.IO.Path.Combine(
            root, "Infrastructure", "Analytics", "MarketingManagerService.cs"));
        var builder = System.IO.File.ReadAllText(System.IO.Path.Combine(
            root, "Infrastructure", "Analytics", "WebsiteAnalyticsAiDataBuilder.cs"));
        var tools = System.IO.File.ReadAllText(System.IO.Path.Combine(
            root, "AgentPortal", "Services", "LegendFounderToolAuthority.cs"));
        var calendar = System.IO.File.ReadAllText(System.IO.Path.Combine(
            root, "AgentPortal", "Controllers", "API", "GraphCalendarWebhookController.cs"));

        Assert.Contains("PrioritizeLeadsAsync", manager, StringComparison.Ordinal);
        Assert.Contains("OutcomeCalibration", builder, StringComparison.Ordinal);
        Assert.Contains("HighIntentLeadSignal", builder, StringComparison.Ordinal);
        Assert.Contains("LeadReadySignal", builder, StringComparison.Ordinal);
        Assert.Contains("legend_growth_operator", tools, StringComparison.Ordinal);
        Assert.Contains("IMarketingManagerService", tools, StringComparison.Ordinal);
        Assert.Contains("legend_propose_ad_change", tools, StringComparison.Ordinal);
        Assert.Contains("IAdvertisingCommandCenterService", tools, StringComparison.Ordinal);
        Assert.Contains("lead.CrmStage = leadTargetStage", calendar, StringComparison.Ordinal);
        Assert.DoesNotContain("qualified: \"Contacted\"", System.IO.File.ReadAllText(System.IO.Path.Combine(
            root, "AgentPortal", "wwwroot", "js", "leads-index.js")), StringComparison.Ordinal);
    }

    private static string GetRepoRoot([CallerFilePath] string currentFile = "")
    {
        var directory = Path.GetDirectoryName(currentFile)
            ?? throw new DirectoryNotFoundException("Could not resolve test file path.");
        return Path.GetFullPath(Path.Combine(directory, ".."));
    }
}
