using System;
using System.Threading.Tasks;
using Domain.Entities;
using Infrastructure.Leads;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentPortal.Tests;

public class WebsiteLifeLeadCaptureServiceTests
{
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
}
