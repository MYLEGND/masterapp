using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using AgentPortal.Controllers;
using AgentPortal.Models;
using AgentPortal.Services;
using Domain.Entities;
using Domain.Enums;
using Infrastructure.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;
using AgentPortal.Services.Tracking;
using AgentPortal.Hubs;

namespace AgentPortal.Tests;

public class LeadsControllerTests
{
    [Fact]
    public async Task CreateAction_Redirects_AndPassesActionSurface()
    {
        var db = ControllerTestHelpers.BuildDb();
        var execMock = new Mock<IExecutionEngine>();
        ActionItem? captured = null;
        execMock.Setup(x => x.CreateActionAsync(It.IsAny<ActionItem>(), default))
            .Callback<ActionItem, System.Threading.CancellationToken>((a, _) => captured = a)
            .ReturnsAsync(new ActionItem());
        var commitments = Mock.Of<ICommitmentService>();
        var controller = ControllerTestHelpers.BuildLeadsController(db, execMock.Object, commitments, ControllerTestHelpers.BuildUser());

        var result = await controller.CreateAction(new LeadsController.CreateLeadActionRequest
        {
            LeadId = "L-1",
            Title = "Call lead",
            ShowInCommandCenter = true
        });

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal(nameof(LeadsController.Actions), redirect.ActionName);
        Assert.NotNull(captured);
        Assert.Equal(ActionSurface.CommandCenter, captured!.ActionSurface);
    }

    [Fact]
    public async Task Leads_List_Uses_Canonical_Row()
    {
        var db = ControllerTestHelpers.BuildDb();
        var now = DateTime.UtcNow;
        db.WorkstationLeadProfiles.AddRange(
            new WorkstationLeadProfile
            {
                LeadId = "L-1",
                AgentUserId = "agent-1",
                Bucket = "MortgageProtection",
                CrmStage = "New",
                CallCount = 1,
                UpdatedUtc = now.AddMinutes(-5),
                CreatedUtc = now.AddHours(-1)
            },
            new WorkstationLeadProfile
            {
                LeadId = "L-1",
                AgentUserId = "agent-1",
                Bucket = "MortgageProtection",
                CrmStage = "New",
                CallCount = 4,
                UpdatedUtc = now,
                CreatedUtc = now.AddMinutes(-30)
            });
        await db.SaveChangesAsync();

        var controller = ControllerTestHelpers.BuildLeadsController(db, Mock.Of<IExecutionEngine>(), Mock.Of<ICommitmentService>(), ControllerTestHelpers.BuildUser());

        var result = await controller.Leads(null);
        var json = Assert.IsType<JsonResult>(result);
        var list = Assert.IsAssignableFrom<IEnumerable<dynamic>>(json.Value);
        var single = Assert.Single(list);
        Assert.Equal(4, (int)single.CallCount);
    }

    [Fact]
    public async Task IncrementCall_Targets_Canonical_Row()
    {
        var db = ControllerTestHelpers.BuildDb();
        var now = DateTime.UtcNow;
        db.WorkstationLeadProfiles.AddRange(
            new WorkstationLeadProfile
            {
                LeadId = "L-2",
                AgentUserId = "agent-1",
                Bucket = "MortgageProtection",
                CrmStage = "New",
                CallCount = 0,
                UpdatedUtc = now.AddMinutes(-10),
                CreatedUtc = now.AddHours(-2)
            },
            new WorkstationLeadProfile
            {
                LeadId = "L-2",
                AgentUserId = "agent-1",
                Bucket = "MortgageProtection",
                CrmStage = "New",
                CallCount = 2,
                UpdatedUtc = now,
                CreatedUtc = now.AddHours(-1)
            });
        await db.SaveChangesAsync();

        var controller = ControllerTestHelpers.BuildLeadsController(db, Mock.Of<IExecutionEngine>(), Mock.Of<ICommitmentService>(), ControllerTestHelpers.BuildUser());

        var ok = await controller.IncrementCall("L-2");
        Assert.IsType<OkObjectResult>(ok);

        var rows = await db.WorkstationLeadProfiles.ToListAsync();
        var canonical = Assert.Single(rows.OrderByDescending(r => r.UpdatedUtc).Take(1));
        Assert.Equal(3, canonical.CallCount); // incremented
        var older = rows.OrderBy(r => r.UpdatedUtc).First();
        Assert.Equal(0, older.CallCount); // untouched
    }

    [Fact]
    public async Task Lead_Returns_Latest_Intake_Snapshot_For_Shared_Quick_View()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        db.WebsiteLeads.AddRange(
            new WebsiteLead
            {
                Id = 101,
                LeadId = Guid.NewGuid(),
                FirstName = "Skyler",
                Email = "skyler@example.com",
                CreatedUtc = new DateTime(2026, 5, 20, 10, 0, 0, DateTimeKind.Utc)
            },
            new WebsiteLead
            {
                Id = 102,
                LeadId = Guid.NewGuid(),
                FirstName = "Skyler",
                Email = "skyler@example.com",
                CreatedUtc = new DateTime(2026, 5, 21, 11, 0, 0, DateTimeKind.Utc)
            });
        db.WorkstationLeadProfiles.Add(new WorkstationLeadProfile
        {
            LeadId = "L-INTAKE-1",
            AgentUserId = "agent-1",
            Bucket = "TermLife",
            OriginalLeadType = "TermLife",
            FirstName = "Skyler",
            LastName = "Intake",
            Email = "skyler@example.com",
            Phone = "6025550123",
            CreatedUtc = new DateTime(2026, 5, 20, 9, 0, 0, DateTimeKind.Utc),
            UpdatedUtc = new DateTime(2026, 5, 21, 9, 0, 0, DateTimeKind.Utc)
        });
        db.WebsiteLeadIntakeLinks.AddRange(
            new WebsiteLeadIntakeLink
            {
                Id = Guid.NewGuid(),
                WebsiteLeadRowId = 101,
                WebsiteLeadPublicId = Guid.NewGuid(),
                WorkstationLeadId = "L-INTAKE-1",
                AgentUserId = "agent-1",
                Bucket = "TermLife",
                SubmittedUtc = new DateTime(2026, 5, 20, 10, 0, 0, DateTimeKind.Utc),
                CapturedUtc = new DateTime(2026, 5, 20, 10, 1, 0, DateTimeKind.Utc),
                SourcePageKey = "quote_term_life"
            },
            new WebsiteLeadIntakeLink
            {
                Id = Guid.NewGuid(),
                WebsiteLeadRowId = 102,
                WebsiteLeadPublicId = Guid.NewGuid(),
                WorkstationLeadId = "L-INTAKE-1",
                AgentUserId = "agent-1",
                Bucket = "TermLife",
                SubmittedUtc = new DateTime(2026, 5, 21, 11, 0, 0, DateTimeKind.Utc),
                CapturedUtc = new DateTime(2026, 5, 21, 11, 1, 0, DateTimeKind.Utc),
                SourcePageKey = "quote_term_life_landing",
                PageVariant = "low_friction_options",
                PageMode = "paid_landing",
                InterestType = "life_term",
                OfferKey = "term",
                ProductType = "life_term",
                UtmSource = "facebook",
                UtmMedium = "paid_social",
                UtmCampaign = "term_retarget",
                UtmId = "utm-222",
                Fbclid = "fbclid-222",
                EstimateSummary = "Best fit: Term Life · Coverage target: $250,000",
                RecommendationPrimaryTitle = "Term Life",
                RecommendationSecondaryTitle = "Whole Life",
                DiscoverySummaryJson = JsonSerializer.Serialize(new[]
                {
                    new { Label = "Protecting", Value = "Family" },
                    new { Label = "Goal", Value = "Replace Income" }
                }),
                SnapshotJson = "{\"OfferKey\":\"term\"}"
            });
        await db.SaveChangesAsync();

        var controller = ControllerTestHelpers.BuildLeadsController(db, Mock.Of<IExecutionEngine>(), Mock.Of<ICommitmentService>(), ControllerTestHelpers.BuildUser());

        var result = await controller.Lead("L-INTAKE-1");
        var json = Assert.IsType<JsonResult>(result);
        var serialized = JsonSerializer.Serialize(json.Value);
        using var doc = JsonDocument.Parse(serialized);
        var root = doc.RootElement;
        var intake = root.GetProperty("intakeSnapshot");

        Assert.Equal("quote_term_life_landing", intake.GetProperty("sourcePageKey").GetString());
        Assert.Equal("low_friction_options", intake.GetProperty("pageVariant").GetString());
        Assert.Equal("facebook", intake.GetProperty("utmSource").GetString());
        Assert.Equal("term_retarget", intake.GetProperty("utmCampaign").GetString());
        Assert.Equal("Term Life", intake.GetProperty("interestLabel").GetString());
        Assert.Equal("Best fit: Term Life · Coverage target: $250,000", intake.GetProperty("estimateSummary").GetString());
        Assert.Equal("Family", intake.GetProperty("discoveryItems")[0].GetProperty("value").GetString());
    }

    [Fact]
    public async Task Leads_Default_Excludes_NotInterested_Even_With_Duplicate()
    {
        var db = ControllerTestHelpers.BuildDb();
        var now = DateTime.UtcNow;
        db.WorkstationLeadProfiles.AddRange(
            new WorkstationLeadProfile
            {
                LeadId = "L-3",
                AgentUserId = "agent-1",
                Bucket = "NotInterested",
                CrmStage = "NotInterested",
                CallCount = 5,
                UpdatedUtc = now
            },
            new WorkstationLeadProfile
            {
                LeadId = "L-3",
                AgentUserId = "agent-1",
                Bucket = "MortgageProtection",
                CrmStage = "New",
                CallCount = 2,
                UpdatedUtc = now.AddMinutes(-5)
            });
        await db.SaveChangesAsync();

        var controller = ControllerTestHelpers.BuildLeadsController(db, Mock.Of<IExecutionEngine>(), Mock.Of<ICommitmentService>(), ControllerTestHelpers.BuildUser());

        var result = await controller.Leads(null);
        var json = Assert.IsType<JsonResult>(result);
        var list = Assert.IsAssignableFrom<IEnumerable<dynamic>>(json.Value);
        Assert.Empty(list); // canonical row is NotInterested; excluded from default
    }

    [Fact]
    public async Task LeadBridge_Queue_Uses_Canonical_Row()
    {
        var db = ControllerTestHelpers.BuildDb();
        var now = DateTime.UtcNow;
        db.WorkstationLeadProfiles.AddRange(
            new WorkstationLeadProfile
            {
                LeadId = "LQ-1",
                AgentUserId = "agent-1",
                Bucket = "MortgageProtection",
                CrmStage = "New",
                CallCount = 0,
                UpdatedUtc = now.AddMinutes(-5),
                CreatedUtc = now.AddHours(-1)
            },
            new WorkstationLeadProfile
            {
                LeadId = "LQ-1",
                AgentUserId = "agent-1",
                Bucket = "MortgageProtection",
                CrmStage = "New",
                CallCount = 5,
                UpdatedUtc = now,
                CreatedUtc = now.AddMinutes(-30)
            });
        await db.SaveChangesAsync();

        var stateService = new LeadBridgeStateService();
        var controller = ControllerTestHelpers.BuildLeadBridgeController(db, stateService, ControllerTestHelpers.BuildUser());

        var result = await controller.Active(null);
        var ok = Assert.IsType<OkObjectResult>(result);
        dynamic payload = ok.Value!;
        Assert.Equal("LQ-1", (string)payload.ActiveLeadId);
        Assert.Equal(1, (int)payload.Total); // duplicate collapsed by canonicalization
        Assert.Equal(1, (int)payload.Position);
    }

    [Fact]
    public async Task LeadBridge_LifeInsurance_Queue_Includes_Term_Whole_And_Iul_Leads()
    {
        var db = ControllerTestHelpers.BuildDb();
        var now = DateTime.UtcNow;
        db.WorkstationLeadProfiles.AddRange(
            new WorkstationLeadProfile
            {
                LeadId = "LT-1",
                AgentUserId = "agent-1",
                Bucket = "Contacted",
                OriginalLeadType = "TermLife",
                UpdatedUtc = now,
                CreatedUtc = now.AddHours(-3)
            },
            new WorkstationLeadProfile
            {
                LeadId = "LW-1",
                AgentUserId = "agent-1",
                Bucket = "FollowUp",
                OriginalLeadType = "WholeLife",
                UpdatedUtc = now.AddMinutes(-2),
                CreatedUtc = now.AddHours(-2)
            },
            new WorkstationLeadProfile
            {
                LeadId = "LI-1",
                AgentUserId = "agent-1",
                Bucket = "Booked",
                OriginalLeadType = "IUL",
                UpdatedUtc = now.AddMinutes(-1),
                CreatedUtc = now.AddHours(-1)
            },
            new WorkstationLeadProfile
            {
                LeadId = "LF-1",
                AgentUserId = "agent-1",
                Bucket = "Contacted",
                OriginalLeadType = "FinalExpense",
                UpdatedUtc = now.AddMinutes(-4),
                CreatedUtc = now.AddHours(-4)
            });
        await db.SaveChangesAsync();

        var stateService = new LeadBridgeStateService();
        var controller = ControllerTestHelpers.BuildLeadBridgeController(db, stateService, ControllerTestHelpers.BuildUser());

        var result = await controller.Active("LifeInsurance");
        var ok = Assert.IsType<OkObjectResult>(result);
        dynamic payload = ok.Value!;
        Assert.Equal(3, (int)payload.Total);
        Assert.Contains((string)payload.ActiveLeadId, new[] { "LT-1", "LW-1", "LI-1" });
    }

    [Fact]
    public async Task ApplyOutcome_Uses_Canonical_Row()
    {
        var db = ControllerTestHelpers.BuildDb();
        var now = DateTime.UtcNow;
        db.WorkstationLeadProfiles.AddRange(
            new WorkstationLeadProfile
            {
                LeadId = "L-4",
                AgentUserId = "agent-1",
                Bucket = "MortgageProtection",
                CrmStage = "New",
                CallCount = 1,
                UpdatedUtc = now.AddMinutes(-5),
                CreatedUtc = now.AddHours(-1)
            },
            new WorkstationLeadProfile
            {
                LeadId = "L-4",
                AgentUserId = "agent-1",
                Bucket = "MortgageProtection",
                CrmStage = "New",
                CallCount = 3,
                UpdatedUtc = now,
                CreatedUtc = now.AddMinutes(-30)
            });
        await db.SaveChangesAsync();

        var controller = ControllerTestHelpers.BuildLeadsController(db, Mock.Of<IExecutionEngine>(), Mock.Of<ICommitmentService>(), ControllerTestHelpers.BuildUser());
        var result = await controller.ApplyOutcome(new LeadsController.LeadOutcomeRequest("L-4", "NotInterested", "note"));
        var json = Assert.IsType<JsonResult>(result);
        dynamic payload = json.Value!;
        Assert.Equal("NotInterested", (string)payload.payload.bucket);

        var rows = await db.WorkstationLeadProfiles.Where(l => l.LeadId == "L-4").ToListAsync();
        var canonical = rows.OrderByDescending(r => r.UpdatedUtc).First();
        Assert.Equal("NotInterested", canonical.Bucket);
        var stale = rows.OrderBy(r => r.UpdatedUtc).First();
        Assert.Equal("New", stale.CrmStage); // untouched
    }

    [Fact]
    public async Task SaveQuickView_Uses_Canonical_Row()
    {
        var db = ControllerTestHelpers.BuildDb();
        var now = DateTime.UtcNow;
        db.WorkstationLeadProfiles.AddRange(
            new WorkstationLeadProfile
            {
                LeadId = "L-5",
                AgentUserId = "agent-1",
                Bucket = "MortgageProtection",
                CrmStage = "New",
                FirstName = "Old",
                UpdatedUtc = now.AddMinutes(-5),
                CreatedUtc = now.AddHours(-1)
            },
            new WorkstationLeadProfile
            {
                LeadId = "L-5",
                AgentUserId = "agent-1",
                Bucket = "MortgageProtection",
                CrmStage = "New",
                FirstName = "Newer",
                UpdatedUtc = now,
                CreatedUtc = now.AddMinutes(-30)
            });
        await db.SaveChangesAsync();

        var controller = ControllerTestHelpers.BuildLeadsController(db, Mock.Of<IExecutionEngine>(), Mock.Of<ICommitmentService>(), ControllerTestHelpers.BuildUser());
        var req = new LeadsController.LeadQuickViewRequest(
            "L-5",            // clientUserId
            "Updated",        // firstName
            null, null, null, null, null, null, null, null, null, null, null, null, null, null, // lastName..loanAmount
            null, null, null, null, null, null, null, // crmStatus..agentNotes
            "MortgageProtection", // pipelineStage
            null, null, null, null, null, null, null, // meetingLocation..pinnedBrief
            null, null, null, null, null,             // doc flags
            null, null, null                          // watchers, mentionNote, btc
        );
        var result = await controller.SaveQuickView(req);
        var json = Assert.IsType<JsonResult>(result);
        dynamic payload = ((dynamic)json.Value!).payload;
        Assert.Equal("Updated", (string)payload.firstName);

        var rows = await db.WorkstationLeadProfiles.Where(l => l.LeadId == "L-5").ToListAsync();
        var canonical = rows.OrderByDescending(r => r.UpdatedUtc).First();
        Assert.Equal("Updated", canonical.FirstName);
        var stale = rows.OrderBy(r => r.UpdatedUtc).First();
        Assert.Equal("Old", stale.FirstName); // untouched
    }

    [Fact]
    public async Task SaveQuickView_PersistsLeadCrmMetadataInCrmNotesJson()
    {
        var db = ControllerTestHelpers.BuildDb();
        var now = DateTime.UtcNow;
        db.WorkstationLeadProfiles.Add(new WorkstationLeadProfile
        {
            LeadId = "L-meta",
            AgentUserId = "agent-1",
            Bucket = "MortgageProtection",
            CrmStage = "New",
            FirstName = "Meta",
            LastName = "Lead",
            Email = "meta@example.com",
            Phone = "6025550101",
            UpdatedUtc = now,
            CreatedUtc = now.AddHours(-1),
            CrmNotes = "legacy note"
        });
        await db.SaveChangesAsync();

        var controller = ControllerTestHelpers.BuildLeadsController(db, Mock.Of<IExecutionEngine>(), Mock.Of<ICommitmentService>(), ControllerTestHelpers.BuildUser());
        var req = new LeadsController.LeadQuickViewRequest(
            "L-meta",
            "Meta",
            "Lead",
            "meta@example.com",
            "6025550101",
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            "Lead",
            "High",
            now.ToString("yyyy-MM-dd"),
            now.AddDays(1).ToString("yyyy-MM-dd"),
            "Follow up on docs",
            "Mortgage, Priority",
            "Agent note",
            "Booked",
            "Zoom Call",
            "https://zoom.us/j/meta",
            true,
            "10:30",
            45,
            "WaitingOnClient",
            "Pinned summary",
            true,
            true,
            false,
            false,
            false,
            "teammate@contoso.com",
            "Please jump in",
            null
        );

        var result = await controller.SaveQuickView(req);
        var json = Assert.IsType<JsonResult>(result);
        dynamic payload = ((dynamic)json.Value!).payload;
        Assert.Equal("High", (string)payload.crmPriority);
        Assert.Equal("Follow up on docs", (string)payload.crmNextText);
        Assert.Equal("Agent note", (string)payload.agentNotes);
        Assert.Equal("Zoom Call", (string)payload.meetingLocation);

        var persisted = await db.WorkstationLeadProfiles.SingleAsync(x => x.LeadId == "L-meta");
        var meta = ClientCrmMetaSerializer.Deserialize(persisted.CrmNotes);
        Assert.Equal("High", meta.CrmPriority);
        Assert.Equal("Follow up on docs", meta.CrmNextText);
        Assert.Equal("Agent note", meta.AgentNotes);
        Assert.Equal("Mortgage, Priority", meta.CrmTags);
        Assert.Equal("Zoom Call", meta.MeetingLocation);
        Assert.Equal("https://zoom.us/j/meta", meta.ZoomJoinUrl);
        Assert.True(meta.UsePersonalZoomLink);
        Assert.Equal("10:30", meta.MeetingTime);
        Assert.Equal(45, meta.MeetingDurationMinutes);
        Assert.Equal("WaitingOnClient", meta.WaitingOn);
        Assert.Equal("Pinned summary", meta.PinnedBrief);
        Assert.True(meta.DocChecklist.IdReceived);
        Assert.True(meta.DocChecklist.AppSent);
        Assert.Single(meta.Collaboration.Watchers);
        Assert.Single(meta.Collaboration.MentionNotes);
    }

    [Fact]
    public async Task CreateCommitment_MissingDue_ReturnsBadRequest()
    {
        var db = ControllerTestHelpers.BuildDb();
        var exec = Mock.Of<IExecutionEngine>();
        var commitments = new Mock<ICommitmentService>();
        var controller = ControllerTestHelpers.BuildLeadsController(db, exec, commitments.Object, ControllerTestHelpers.BuildUser());

        var result = await controller.CreateCommitment(new LeadsController.CreateCommitmentRequest
        {
            LeadId = "L-1",
            PromiseText = "Send proposal",
            DueDateUtc = null
        });

        Assert.IsType<BadRequestObjectResult>(result);
    }
}
