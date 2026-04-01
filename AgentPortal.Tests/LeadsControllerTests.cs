using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AgentPortal.Controllers;
using AgentPortal.Services;
using Domain.Entities;
using Domain.Enums;
using Infrastructure.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;
using AgentPortal.Services;
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
