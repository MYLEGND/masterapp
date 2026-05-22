using System;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Domain.Entities;
using Infrastructure.Leads;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentPortal.Tests;

public class WebsiteLifeLeadCaptureServiceTests
{
    [Fact]
    public async Task UpsertAsync_Sqlite_Persists_Workstation_Lead_And_Intake_Link()
    {
        await using var conn = new SqliteConnection("Data Source=:memory:");
        await conn.OpenAsync();

        var options = new DbContextOptionsBuilder<Infrastructure.Data.MasterAppDbContext>()
            .UseSqlite(conn)
            .Options;

        await using var db = new Infrastructure.Data.MasterAppDbContext(options);
        await db.Database.EnsureCreatedAsync();

        var trackingProfileId = Guid.NewGuid();
        var websiteLeadId = Guid.NewGuid();

        db.AgentTrackingProfiles.Add(new AgentTrackingProfile
        {
            Id = trackingProfileId,
            AgentUserId = "agent-sqlite",
            AgentUpn = "sqlite@example.com",
            Slug = "sqlite-agent",
            CreatedUtc = new DateTime(2026, 5, 21, 18, 0, 0, DateTimeKind.Utc),
            UpdatedUtc = new DateTime(2026, 5, 21, 18, 0, 0, DateTimeKind.Utc)
        });
        db.WebsiteLeads.Add(new WebsiteLead
        {
            LeadId = websiteLeadId,
            FirstName = "Riley",
            LastName = "Sqlite",
            Email = "riley@example.com",
            Phone = "(602) 555-0188",
            InterestType = "life_general",
            SourcePageKey = "quote_life",
            AgentTrackingProfileId = trackingProfileId,
            AgentSlug = "sqlite-agent",
            CreatedUtc = new DateTime(2026, 5, 21, 18, 5, 0, DateTimeKind.Utc),
            MetadataJson = JsonSerializer.Serialize(new
            {
                OfferKey = "life",
                ProductType = "life_general",
                CoverageGoal = "replace_income",
                ProtectingWho = "family",
                CoverageAmount = 250000,
                RecommendationPrimaryTitle = "Term Life"
            })
        });
        await db.SaveChangesAsync();

        var service = new WebsiteLifeLeadCaptureService(db, NullLogger<WebsiteLifeLeadCaptureService>.Instance);

        var result = await service.UpsertAsync(new WebsiteLifeLeadCaptureRequest
        {
            WebsiteLeadId = websiteLeadId,
            SubmittedUtc = new DateTime(2026, 5, 21, 18, 5, 0, DateTimeKind.Utc),
            ProductType = "life_general",
            OfferKey = "life",
            FirstName = "Riley",
            LastName = "Sqlite",
            Email = "riley@example.com",
            Phone = "(602) 555-0188",
            State = "az",
            Age = 36,
            CoverageAmount = 250000,
            AgentTrackingProfileId = trackingProfileId,
            AgentSlug = "sqlite-agent",
            RecipientEmail = "sqlite@example.com"
        });
        await db.SaveChangesAsync();

        Assert.True(result.Captured);
        Assert.Equal(WorkstationLeadBuckets.LifeInsurance, result.Bucket);

        var lead = await db.WorkstationLeadProfiles.SingleAsync();
        Assert.Equal(websiteLeadId.ToString("N"), lead.LeadId);
        Assert.Equal("agent-sqlite", lead.AgentUserId);
        Assert.Equal(WorkstationLeadBuckets.LifeInsurance, lead.Bucket);
        Assert.Equal("6025550188", lead.Phone);

        var intake = await db.WebsiteLeadIntakeLinks.SingleAsync();
        Assert.Equal(lead.LeadId, intake.WorkstationLeadId);
        Assert.Equal("agent-sqlite", intake.AgentUserId);
        Assert.Equal(websiteLeadId, intake.WebsiteLeadPublicId);
    }

    [Fact]
    public async Task UpsertAsync_Creates_TermLife_Workstation_Lead_From_Website_Submission()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var trackingProfileId = Guid.NewGuid();
        db.AgentTrackingProfiles.Add(new AgentTrackingProfile
        {
            Id = trackingProfileId,
            AgentUserId = "agent-1",
            AgentUpn = "agent1@example.com",
            Slug = "agent-one"
        });
        await db.SaveChangesAsync();

        var service = new WebsiteLifeLeadCaptureService(db, NullLogger<WebsiteLifeLeadCaptureService>.Instance);
        var websiteLeadId = Guid.NewGuid();

        var result = await service.UpsertAsync(new WebsiteLifeLeadCaptureRequest
        {
            WebsiteLeadId = websiteLeadId,
            SubmittedUtc = new DateTime(2026, 5, 19, 20, 30, 0, DateTimeKind.Utc),
            ProductType = "life_term",
            OfferKey = "term",
            FirstName = "Taylor",
            LastName = "Agent",
            Email = "taylor@example.com",
            Phone = "(602) 555-0101",
            State = "az",
            Age = 37,
            CoverageAmount = 250000,
            AgentTrackingProfileId = trackingProfileId,
            AgentSlug = "agent-one",
            RecipientEmail = "agent1@example.com"
        });
        await db.SaveChangesAsync();

        Assert.True(result.Captured);
        Assert.True(result.Created);
        Assert.Equal(WorkstationLeadBuckets.TermLife, result.Bucket);
        Assert.Equal("agent-1", result.AgentUserId);

        var lead = await db.WorkstationLeadProfiles.SingleAsync();
        Assert.Equal(websiteLeadId.ToString("N"), lead.LeadId);
        Assert.Equal(WorkstationLeadBuckets.TermLife, lead.Bucket);
        Assert.Equal(WorkstationLeadBuckets.TermLife, lead.OriginalLeadType);
        Assert.Equal("6025550101", lead.Phone);
        Assert.Equal("AZ", lead.State);
        Assert.Equal("37", lead.Age);
        Assert.Equal("250,000", lead.LoanAmount);
    }

    [Fact]
    public async Task UpsertAsync_Updates_Existing_Lead_Without_Resetting_Current_Stage()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        db.AgentProfiles.Add(new AgentProfile
        {
            AgentUserId = "agent-2",
            AgentUpn = "founder@example.com",
            NormalizedEmail = "founder@example.com"
        });
        db.WorkstationLeadProfiles.Add(new WorkstationLeadProfile
        {
            LeadId = "existing-lead",
            AgentUserId = "agent-2",
            Bucket = "FollowUp",
            OriginalLeadType = WorkstationLeadBuckets.WholeLife,
            FirstName = "Existing",
            LastName = "Lead",
            Email = "existing@example.com",
            Phone = "6025550101",
            LoanAmount = "100,000",
            CrmStage = "FollowUp",
            CrmStatus = "Lead",
            CreatedUtc = new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc),
            UpdatedUtc = new DateTime(2026, 5, 10, 0, 0, 0, DateTimeKind.Utc)
        });
        await db.SaveChangesAsync();

        var service = new WebsiteLifeLeadCaptureService(db, NullLogger<WebsiteLifeLeadCaptureService>.Instance);

        var result = await service.UpsertAsync(new WebsiteLifeLeadCaptureRequest
        {
            WebsiteLeadId = Guid.NewGuid(),
            SubmittedUtc = new DateTime(2026, 5, 19, 21, 0, 0, DateTimeKind.Utc),
            ProductType = "life_whole",
            OfferKey = "wholelife",
            FirstName = "Existing",
            LastName = "Lead",
            Email = "existing@example.com",
            Phone = "602-555-0101",
            State = "tx",
            Age = 45,
            CoverageAmount = 200000,
            RecipientEmail = "founder@example.com"
        });
        await db.SaveChangesAsync();

        Assert.True(result.Captured);
        Assert.False(result.Created);
        Assert.Equal(WorkstationLeadBuckets.WholeLife, result.Bucket);
        Assert.Equal("agent-2", result.AgentUserId);

        var lead = await db.WorkstationLeadProfiles.SingleAsync();
        Assert.Equal("existing-lead", lead.LeadId);
        Assert.Equal("FollowUp", lead.Bucket);
        Assert.Equal(WorkstationLeadBuckets.WholeLife, lead.OriginalLeadType);
        Assert.Equal("TX", lead.State);
        Assert.Equal("45", lead.Age);
        Assert.Equal("200,000", lead.LoanAmount);
    }

    [Fact]
    public async Task UpsertAsync_Creates_AutoInsurance_Workstation_Lead_From_Website_Submission()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var trackingProfileId = Guid.NewGuid();
        db.AgentTrackingProfiles.Add(new AgentTrackingProfile
        {
            Id = trackingProfileId,
            AgentUserId = "agent-auto",
            AgentUpn = "auto@example.com",
            Slug = "auto-agent"
        });
        await db.SaveChangesAsync();

        var service = new WebsiteLifeLeadCaptureService(db, NullLogger<WebsiteLifeLeadCaptureService>.Instance);
        var websiteLeadId = Guid.NewGuid();

        var result = await service.UpsertAsync(new WebsiteLifeLeadCaptureRequest
        {
            WebsiteLeadId = websiteLeadId,
            SubmittedUtc = new DateTime(2026, 5, 20, 15, 0, 0, DateTimeKind.Utc),
            ProductType = "auto",
            OfferKey = "auto",
            FirstName = "Avery",
            LastName = "Driver",
            Email = "avery@example.com",
            Phone = "(480) 555-0101",
            State = "az",
            AgentTrackingProfileId = trackingProfileId,
            AgentSlug = "auto-agent",
            RecipientEmail = "auto@example.com"
        });
        await db.SaveChangesAsync();

        Assert.True(result.Captured);
        Assert.True(result.Created);
        Assert.Equal(WorkstationLeadBuckets.AutoInsurance, result.Bucket);
        Assert.Equal("agent-auto", result.AgentUserId);

        var lead = await db.WorkstationLeadProfiles.SingleAsync(x => x.LeadId == websiteLeadId.ToString("N"));
        Assert.Equal(WorkstationLeadBuckets.AutoInsurance, lead.Bucket);
        Assert.Equal(WorkstationLeadBuckets.AutoInsurance, lead.OriginalLeadType);
        Assert.Equal("AZ", lead.State);
        Assert.Equal("4805550101", lead.Phone);
    }

    [Fact]
    public async Task UpsertAsync_Creates_Intake_Link_With_Attribution_And_Recommendation_Snapshot()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var trackingProfileId = Guid.NewGuid();
        var websiteLeadId = Guid.NewGuid();
        db.AgentTrackingProfiles.Add(new AgentTrackingProfile
        {
            Id = trackingProfileId,
            AgentUserId = "agent-life",
            AgentUpn = "life@example.com",
            Slug = "life-agent"
        });
        db.WebsiteLeads.Add(new WebsiteLead
        {
            LeadId = websiteLeadId,
            FirstName = "Jordan",
            LastName = "Tester",
            Email = "jordan@example.com",
            Phone = "(480) 555-0111",
            InterestType = "life_general",
            SourcePageKey = "quote_life_landing",
            UtmSource = "facebook",
            UtmMedium = "paid_social",
            UtmCampaign = "life_scale_v3",
            UtmId = "utm-555",
            MetaCampaignId = "meta-c-1",
            MetaAdSetId = "meta-as-2",
            MetaAdId = "meta-ad-3",
            Fbclid = "fbclid-xyz",
            SessionId = "session-123",
            VisitorId = "visitor-456",
            AgentTrackingProfileId = trackingProfileId,
            AgentSlug = "life-agent",
            CreatedUtc = new DateTime(2026, 5, 21, 15, 0, 0, DateTimeKind.Utc),
            MetadataJson = JsonSerializer.Serialize(new
            {
                OfferKey = "life",
                ProductType = "life_general",
                ProtectingWho = "family",
                CoverageGoal = "replace_income",
                CoverageAmount = 250000,
                TobaccoUse = "non_smoker",
                Age = 34,
                State = "az",
                PageVariant = "low_friction_options",
                PageMode = "paid_landing",
                PagePath = "/Quote/Life/landing",
                LandingPageUrl = "https://protect.example.com/Quote/Life/landing?variant=low_friction_options",
                ReferrerUrl = "https://m.facebook.com/",
                UtmTerm = "life+quote",
                UtmContent = "creative_a",
                RecommendationPrimaryKey = "term",
                RecommendationPrimaryTitle = "Term Life",
                RecommendationSecondaryKey = "wholelife",
                RecommendationSecondaryTitle = "Whole Life"
            })
        });
        await db.SaveChangesAsync();

        var service = new WebsiteLifeLeadCaptureService(db, NullLogger<WebsiteLifeLeadCaptureService>.Instance);

        var result = await service.UpsertAsync(new WebsiteLifeLeadCaptureRequest
        {
            WebsiteLeadId = websiteLeadId,
            SubmittedUtc = new DateTime(2026, 5, 21, 15, 0, 0, DateTimeKind.Utc),
            ProductType = "life_general",
            OfferKey = "life",
            FirstName = "Jordan",
            LastName = "Tester",
            Email = "jordan@example.com",
            Phone = "(480) 555-0111",
            State = "az",
            Age = 34,
            CoverageAmount = 250000,
            AgentTrackingProfileId = trackingProfileId,
            AgentSlug = "life-agent",
            RecipientEmail = "life@example.com"
        });
        await db.SaveChangesAsync();

        Assert.True(result.Captured);

        var intake = await db.WebsiteLeadIntakeLinks.SingleAsync();
        Assert.Equal(result.WorkstationLeadId, intake.WorkstationLeadId);
        Assert.Equal("facebook", intake.UtmSource);
        Assert.Equal("paid_social", intake.UtmMedium);
        Assert.Equal("life_scale_v3", intake.UtmCampaign);
        Assert.Equal("fbclid-xyz", intake.Fbclid);
        Assert.Equal("quote_life_landing", intake.SourcePageKey);
        Assert.Equal("low_friction_options", intake.PageVariant);
        Assert.Equal("paid_landing", intake.PageMode);
        Assert.Equal("life_general", intake.InterestType);
        Assert.Equal("life", intake.OfferKey);
        Assert.Equal("Term Life", intake.RecommendationPrimaryTitle);
        Assert.Equal("Whole Life", intake.RecommendationSecondaryTitle);
        Assert.Contains("Best fit: Term Life", intake.EstimateSummary);
        Assert.Contains("Coverage target:", intake.EstimateSummary);

        Assert.False(string.IsNullOrWhiteSpace(intake.DiscoverySummaryJson));
        using var summaryDoc = JsonDocument.Parse(intake.DiscoverySummaryJson!);
        Assert.Contains(summaryDoc.RootElement.EnumerateArray(), item =>
            item.GetProperty("Label").GetString() == "Protecting" &&
            item.GetProperty("Value").GetString() == "Family");
    }

    [Fact]
    public async Task UpsertAsync_Preserves_Repeated_Website_Submissions_As_Intake_History()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var trackingProfileId = Guid.NewGuid();
        db.AgentTrackingProfiles.Add(new AgentTrackingProfile
        {
            Id = trackingProfileId,
            AgentUserId = "agent-repeat",
            AgentUpn = "repeat@example.com",
            Slug = "repeat-agent"
        });

        var firstLeadId = Guid.NewGuid();
        var secondLeadId = Guid.NewGuid();
        db.WebsiteLeads.AddRange(
            new WebsiteLead
            {
                LeadId = firstLeadId,
                FirstName = "Avery",
                LastName = "Repeat",
                Email = "avery@example.com",
                Phone = "(602) 555-0109",
                InterestType = "life_term",
                SourcePageKey = "quote_term_life",
                UtmSource = "google",
                UtmMedium = "cpc",
                UtmCampaign = "term_launch",
                AgentTrackingProfileId = trackingProfileId,
                AgentSlug = "repeat-agent",
                CreatedUtc = new DateTime(2026, 5, 20, 16, 0, 0, DateTimeKind.Utc),
                MetadataJson = JsonSerializer.Serialize(new
                {
                    OfferKey = "term",
                    ProductType = "life_term",
                    PageMode = "site_mode",
                    RecommendationPrimaryTitle = "Term Life"
                })
            },
            new WebsiteLead
            {
                LeadId = secondLeadId,
                FirstName = "Avery",
                LastName = "Repeat",
                Email = "avery@example.com",
                Phone = "(602) 555-0109",
                InterestType = "life_term",
                SourcePageKey = "quote_term_life_landing",
                UtmSource = "facebook",
                UtmMedium = "paid_social",
                UtmCampaign = "term_retarget",
                AgentTrackingProfileId = trackingProfileId,
                AgentSlug = "repeat-agent",
                CreatedUtc = new DateTime(2026, 5, 21, 18, 0, 0, DateTimeKind.Utc),
                MetadataJson = JsonSerializer.Serialize(new
                {
                    OfferKey = "term",
                    ProductType = "life_term",
                    PageVariant = "low_friction_options",
                    PageMode = "paid_landing",
                    RecommendationPrimaryTitle = "Term Life",
                    RecommendationSecondaryTitle = "Whole Life"
                })
            });
        await db.SaveChangesAsync();

        var service = new WebsiteLifeLeadCaptureService(db, NullLogger<WebsiteLifeLeadCaptureService>.Instance);

        await service.UpsertAsync(new WebsiteLifeLeadCaptureRequest
        {
            WebsiteLeadId = firstLeadId,
            SubmittedUtc = new DateTime(2026, 5, 20, 16, 0, 0, DateTimeKind.Utc),
            ProductType = "life_term",
            OfferKey = "term",
            FirstName = "Avery",
            LastName = "Repeat",
            Email = "avery@example.com",
            Phone = "(602) 555-0109",
            State = "az",
            Age = 39,
            AgentTrackingProfileId = trackingProfileId,
            AgentSlug = "repeat-agent",
            RecipientEmail = "repeat@example.com"
        });

        await service.UpsertAsync(new WebsiteLifeLeadCaptureRequest
        {
            WebsiteLeadId = secondLeadId,
            SubmittedUtc = new DateTime(2026, 5, 21, 18, 0, 0, DateTimeKind.Utc),
            ProductType = "life_term",
            OfferKey = "term",
            FirstName = "Avery",
            LastName = "Repeat",
            Email = "avery@example.com",
            Phone = "(602) 555-0109",
            State = "az",
            Age = 39,
            AgentTrackingProfileId = trackingProfileId,
            AgentSlug = "repeat-agent",
            RecipientEmail = "repeat@example.com"
        });
        await db.SaveChangesAsync();

        var lead = await db.WorkstationLeadProfiles.SingleAsync();
        var history = await db.WebsiteLeadIntakeLinks
            .OrderBy(x => x.SubmittedUtc)
            .ToListAsync();

        Assert.Equal(2, history.Count);
        Assert.All(history, item => Assert.Equal(lead.LeadId, item.WorkstationLeadId));
        Assert.Equal("google", history[0].UtmSource);
        Assert.Equal("term_launch", history[0].UtmCampaign);
        Assert.Equal("facebook", history[1].UtmSource);
        Assert.Equal("term_retarget", history[1].UtmCampaign);
        Assert.Equal("quote_term_life_landing", history[1].SourcePageKey);
        Assert.Equal("low_friction_options", history[1].PageVariant);
    }
}
