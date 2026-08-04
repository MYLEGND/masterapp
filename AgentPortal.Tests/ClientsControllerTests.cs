using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using AgentPortal.Controllers;
using AgentPortal.Models;
using AgentPortal.Services;
using Domain.Entities;
using Domain.Enums;
using Infrastructure.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Moq;
using Xunit;

namespace AgentPortal.Tests;

public class ClientsControllerTests
{
    [Fact]
    public async Task Edit_RedirectsOwnedAgentToTheManagedClientProfile()
    {
        using var db = ControllerTestHelpers.BuildDb();
        const string agentId = "agent-1";
        var clientUserId = Guid.NewGuid().ToString();
        var profile = new ClientProfile
        {
            ClientUserId = clientUserId,
            FirstName = "Client",
            LastName = "One",
            Email = "client@example.com",
            NormalizedEmail = "client@example.com",
            CreatedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow
        };
        db.ClientProfiles.Add(profile);
        db.AgentClients.Add(new AgentClient
        {
            AgentUserId = agentId,
            ClientUserId = clientUserId,
            CreatedUtc = DateTime.UtcNow
        });
        await db.SaveChangesAsync();

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new[]
            {
                new KeyValuePair<string, string?>("GraphProvisioning:TenantId", "test-tenant"),
                new KeyValuePair<string, string?>("GraphProvisioning:ClientId", "test-client"),
                new KeyValuePair<string, string?>("GraphProvisioning:ClientSecret", "test-secret"),
                new KeyValuePair<string, string?>("Provisioning:ClientPortalBaseUrl", "https://client.mylegnd.com")
            })
            .Build();
        var controller = ControllerTestHelpers.BuildClientsController(
            db,
            Mock.Of<IExecutionEngine>(),
            Mock.Of<ICommitmentService>(),
            ControllerTestHelpers.BuildUser(agentId),
            configuration: config);

        var result = await controller.Edit(clientUserId);

        var redirect = Assert.IsType<RedirectResult>(result);
        Assert.Equal(
            $"https://client.mylegnd.com/support/view-as-client/{profile.Id}?returnUrl=%2Fprofile",
            redirect.Url);
    }

    [Fact]
    public async Task Edit_RejectsAnAgentWhoDoesNotOwnTheClient()
    {
        using var db = ControllerTestHelpers.BuildDb();
        const string clientUserId = "client-1";
        db.ClientProfiles.Add(new ClientProfile
        {
            ClientUserId = clientUserId,
            FirstName = "Client",
            LastName = "One",
            Email = "client@example.com",
            NormalizedEmail = "client@example.com",
            CreatedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow
        });
        db.AgentClients.Add(new AgentClient
        {
            AgentUserId = "agent-owner",
            ClientUserId = clientUserId,
            CreatedUtc = DateTime.UtcNow
        });
        await db.SaveChangesAsync();

        var controller = ControllerTestHelpers.BuildClientsController(
            db,
            Mock.Of<IExecutionEngine>(),
            Mock.Of<ICommitmentService>(),
            ControllerTestHelpers.BuildUser("agent-other"));

        var result = await controller.Edit(clientUserId);

        Assert.IsType<ForbidResult>(result);
    }

    private static async Task SeedOwnedClientAsync(
        MasterAppDbContext db,
        string agentId,
        string clientUserId,
        string email,
        string crmNotesJson)
    {
        db.ClientProfiles.Add(new ClientProfile
        {
            ClientUserId = clientUserId,
            FirstName = "Client",
            LastName = "One",
            Email = email,
            NormalizedEmail = email.ToLowerInvariant(),
            CrmStatus = "Active",
            CrmPriority = "Normal",
            CrmNotes = crmNotesJson,
            CreatedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow
        });
        db.AgentClients.Add(new AgentClient
        {
            AgentUserId = agentId,
            ClientUserId = clientUserId,
            CreatedUtc = DateTime.UtcNow
        });
        await db.SaveChangesAsync();
    }

    private static async Task SeedAgentProfileAsync(MasterAppDbContext db, string agentId, string npn = "12345678")
    {
        db.AgentProfiles.Add(new AgentProfile
        {
            AgentUserId = agentId,
            AgentUpn = $"{agentId}@example.com",
            Npn = npn,
            CreatedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow
        });
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task CreateAction_Redirects_ForClient()
    {
        using var db = ControllerTestHelpers.BuildDb();
        db.ClientProfiles.Add(new ClientProfile
        {
            ClientUserId = "C-1",
            FirstName = "Client",
            LastName = "One",
            Email = "client@example.com",
            NormalizedEmail = "client@example.com",
            CreatedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow
        });
        db.AgentClients.Add(new AgentClient
        {
            AgentUserId = "agent-1",
            ClientUserId = "C-1",
            CreatedUtc = DateTime.UtcNow
        });
        await db.SaveChangesAsync();

        var execMock = new Mock<IExecutionEngine>();
        ActionItem? captured = null;
        execMock.Setup(x => x.CreateActionAsync(It.IsAny<ActionItem>(), default))
            .Callback<ActionItem, System.Threading.CancellationToken>((a, _) => captured = a)
            .ReturnsAsync(new ActionItem());
        var commitments = Mock.Of<ICommitmentService>();
        var controller = ControllerTestHelpers.BuildClientsController(db, execMock.Object, commitments, ControllerTestHelpers.BuildUser());

        var result = await controller.CreateAction(new ClientsController.CreateClientActionRequest
        {
            ClientId = "C-1",
            Title = "Prep review",
            ShowInCommandCenter = true
        });

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal(nameof(ClientsController.Actions), redirect.ActionName);
        Assert.NotNull(captured);
        Assert.Equal(ActionSurface.CommandCenter, captured!.ActionSurface);
    }

    [Fact]
    public async Task SaveQuickView_OmittedMeetingFields_PreservesExistingMeetingValues()
    {
        using var db = ControllerTestHelpers.BuildDb();
        const string agentId = "agent-1";
        const string clientUserId = "client-keep";
        const string email = "keep@example.com";

        var meta = new ClientCrmMeta
        {
            MeetingLocation = "Main Office",
            ZoomJoinUrl = "https://zoom.us/j/123",
            UsePersonalZoomLink = true,
            MeetingTime = "14:15",
            MeetingDurationMinutes = 45
        };
        await SeedOwnedClientAsync(db, agentId, clientUserId, email, ClientCrmMetaSerializer.Serialize(meta));

        var controller = ControllerTestHelpers.BuildClientsController(
            db,
            Mock.Of<IExecutionEngine>(),
            Mock.Of<ICommitmentService>(),
            ControllerTestHelpers.BuildUser(agentId, "agent-1@example.com"));

        var result = await controller.SaveQuickView(new ClientsController.QuickViewRequest
        {
            ClientUserId = clientUserId,
            Email = email,
            CrmStatus = "Active",
            CrmPriority = "Normal"
            // Meeting fields intentionally omitted (null)
        });

        Assert.IsType<JsonResult>(result);

        var persisted = await db.ClientProfiles.SingleAsync(x => x.ClientUserId == clientUserId);
        var persistedMeta = ClientCrmMetaSerializer.Deserialize(persisted.CrmNotes);
        Assert.Equal("Main Office", persistedMeta.MeetingLocation);
        Assert.Equal("https://zoom.us/j/123", persistedMeta.ZoomJoinUrl);
        Assert.True(persistedMeta.UsePersonalZoomLink);
        Assert.Equal("14:15", persistedMeta.MeetingTime);
        Assert.Equal(45, persistedMeta.MeetingDurationMinutes);
    }

    [Fact]
    public async Task SaveQuickView_ExplicitMeetingFields_UpdateMeetingValues()
    {
        using var db = ControllerTestHelpers.BuildDb();
        const string agentId = "agent-1";
        const string clientUserId = "client-update";
        const string email = "update@example.com";

        var meta = new ClientCrmMeta
        {
            MeetingLocation = "Old Location",
            ZoomJoinUrl = "https://zoom.us/j/old",
            UsePersonalZoomLink = true,
            MeetingTime = "16:00",
            MeetingDurationMinutes = 60
        };
        await SeedOwnedClientAsync(db, agentId, clientUserId, email, ClientCrmMetaSerializer.Serialize(meta));

        var controller = ControllerTestHelpers.BuildClientsController(
            db,
            Mock.Of<IExecutionEngine>(),
            Mock.Of<ICommitmentService>(),
            ControllerTestHelpers.BuildUser(agentId));

        var result = await controller.SaveQuickView(new ClientsController.QuickViewRequest
        {
            ClientUserId = clientUserId,
            Email = email,
            CrmStatus = "Active",
            CrmPriority = "Normal",
            MeetingLocation = "",
            ZoomJoinUrl = "",
            UsePersonalZoomLink = false,
            MeetingTime = "10:30",
            MeetingDurationMinutes = 30
        });

        Assert.IsType<JsonResult>(result);

        var persisted = await db.ClientProfiles.SingleAsync(x => x.ClientUserId == clientUserId);
        var persistedMeta = ClientCrmMetaSerializer.Deserialize(persisted.CrmNotes);
        Assert.Null(persistedMeta.MeetingLocation);
        Assert.Null(persistedMeta.ZoomJoinUrl);
        Assert.False(persistedMeta.UsePersonalZoomLink);
        Assert.Equal("10:30", persistedMeta.MeetingTime);
        Assert.Equal(30, persistedMeta.MeetingDurationMinutes);
    }


    [Fact]
    public async Task Create_WhenLeadCreated_PersistsRequestedLeadBucket()
    {
        using var db = ControllerTestHelpers.BuildDb();
        const string agentId = "agent-1";

        var controller = ControllerTestHelpers.BuildClientsController(
            db,
            Mock.Of<IExecutionEngine>(),
            Mock.Of<ICommitmentService>(),
            ControllerTestHelpers.BuildUser(agentId, "agent-1@example.com"));

        var result = await controller.Create(new CreateClientViewModel
        {
            RecordType = "Lead",
            FirstName = "Fresh",
            LastName = "Lead",
            Email = string.Empty,
            Phone = "555-222-3333",
            MaritalStatus = string.Empty,
            CrmStatus = "Lead",
            CrmPriority = "Normal",
            PipelineStage = "Qualified"
        });

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal(nameof(ClientsController.Index), redirect.ActionName);

        var persisted = await db.ClientProfiles.SingleAsync();
        var persistedMeta = ClientCrmMetaSerializer.Deserialize(persisted.CrmNotes);
        Assert.Equal("Lead", persistedMeta.RecordType);
        Assert.Equal("Qualified", persistedMeta.PipelineStage);
        Assert.False(Guid.TryParse(persisted.ClientUserId, out _));
    }

    [Fact]
    public async Task Create_FromClientsCrmLead_PrefillsTheSharedClientAccountForm()
    {
        using var db = ControllerTestHelpers.BuildDb();
        const string agentId = "agent-1";
        const string leadId = "lead-convert-client-crm";
        await SeedOwnedClientAsync(
            db,
            agentId,
            leadId,
            "prospect@example.com",
            ClientCrmMetaSerializer.Serialize(new ClientCrmMeta { RecordType = "Lead", PipelineStage = "Qualified" }));

        var controller = ControllerTestHelpers.BuildClientsController(
            db,
            Mock.Of<IExecutionEngine>(),
            Mock.Of<ICommitmentService>(),
            ControllerTestHelpers.BuildUser(agentId));
        var url = new Mock<IUrlHelper>();
        url.Setup(x => x.IsLocalUrl(It.IsAny<string>())).Returns(true);
        controller.Url = url.Object;

        var result = await controller.Create(
            returnUrl: "/Clients",
            sourceLeadClientUserId: leadId,
            recordType: "BusinessClient");

        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<CreateClientViewModel>(view.Model);
        Assert.Equal(leadId, model.SourceLeadClientUserId);
        Assert.Equal("BusinessClient", model.RecordType);
        Assert.Equal("Client", model.FirstName);
        Assert.Equal("prospect@example.com", model.Email);
    }

    [Fact]
    public async Task Create_FromLeadsCrmLead_PrefillsTheSharedClientAccountForm()
    {
        using var db = ControllerTestHelpers.BuildDb();
        const string agentId = "agent-1";
        const string leadId = "workstation-convert-lead";
        db.WorkstationLeadProfiles.Add(new WorkstationLeadProfile
        {
            LeadId = leadId,
            AgentUserId = agentId,
            Bucket = "LifeInsurance",
            FirstName = "Taylor",
            LastName = "Prospect",
            Email = "taylor@example.com",
            Phone = "555-444-5555",
            DOB = new DateTime(1990, 1, 2),
            CrmStatus = "Lead",
            CrmStage = "Qualified",
            CrmNotes = ClientCrmMetaSerializer.Serialize(new ClientCrmMeta
            {
                CrmPriority = "High",
                CrmTags = "Life"
            })
        });
        await db.SaveChangesAsync();

        var controller = ControllerTestHelpers.BuildClientsController(
            db,
            Mock.Of<IExecutionEngine>(),
            Mock.Of<ICommitmentService>(),
            ControllerTestHelpers.BuildUser(agentId));
        var url = new Mock<IUrlHelper>();
        url.Setup(x => x.IsLocalUrl(It.IsAny<string>())).Returns(true);
        controller.Url = url.Object;

        var result = await controller.Create(
            returnUrl: "/Leads",
            sourceWorkstationLeadId: leadId,
            recordType: "Client");

        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<CreateClientViewModel>(view.Model);
        Assert.Equal(leadId, model.SourceWorkstationLeadId);
        Assert.Equal("Client", model.RecordType);
        Assert.Equal("Taylor", model.FirstName);
        Assert.Equal("High", model.CrmPriority);
        Assert.Equal("Life", model.CrmTags);
    }

    [Fact]
    public async Task Delete_WhenSoleClientRecord_IsBlockedFromDeletingTheAccount()
    {
        using var db = ControllerTestHelpers.BuildDb();
        const string agentId = "agent-1";
        const string clientUserId = "lead-delete";
        const string email = "";

        var meta = new ClientCrmMeta
        {
            RecordType = "Lead",
            PipelineStage = "NewLead"
        };

        await SeedOwnedClientAsync(db, agentId, clientUserId, email, ClientCrmMetaSerializer.Serialize(meta));
        var profile = await db.ClientProfiles.SingleAsync(x => x.ClientUserId == clientUserId);
        db.FinanceToolStates.Add(new FinanceToolState
        {
            ClientProfileId = profile.Id,
            ToolId = "ExpenseLens",
            JsonState = "{}"
        });
        db.ClientFinancialPlans.Add(new ClientFinancialPlan
        {
            ClientId = profile.Id,
            JsonData = "{}",
            UpdatedBy = agentId
        });
        await db.SaveChangesAsync();

        var controller = ControllerTestHelpers.BuildClientsController(
            db,
            Mock.Of<IExecutionEngine>(),
            Mock.Of<ICommitmentService>(),
            ControllerTestHelpers.BuildUser(agentId));

        var result = await controller.Delete(clientUserId);

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal(nameof(ClientsController.Index), redirect.ActionName);
        Assert.Single(await db.ClientProfiles.ToListAsync());
        Assert.Single(await db.AgentClients.ToListAsync());
        Assert.Single(await db.FinanceToolStates.ToListAsync());
        Assert.Single(await db.ClientFinancialPlans.ToListAsync());
    }

    [Fact]
    public async Task Delete_WhenClientOwnsHousehold_ReturnsConflictInsteadOfServerError()
    {
        using var db = ControllerTestHelpers.BuildDb();
        const string agentId = "agent-1";
        var clientUserId = Guid.NewGuid().ToString();

        await SeedOwnedClientAsync(
            db,
            agentId,
            clientUserId,
            "subscribed-client@example.com",
            ClientCrmMetaSerializer.Serialize(new ClientCrmMeta
            {
                RecordType = "Client",
                PipelineStage = "Client"
            }));
        var profile = await db.ClientProfiles.SingleAsync(x => x.ClientUserId == clientUserId);
        db.HouseholdAccounts.Add(new HouseholdAccount
        {
            SubscriptionOwnerClientProfileId = profile.Id,
            Status = HouseholdAccountStatus.Active,
            CreatedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow
        });
        await db.SaveChangesAsync();

        var controller = ControllerTestHelpers.BuildClientsController(
            db,
            Mock.Of<IExecutionEngine>(),
            Mock.Of<ICommitmentService>(),
            ControllerTestHelpers.BuildUser(agentId));
        controller.HttpContext.Request.Headers["X-Requested-With"] = "fetch";

        var result = await controller.Delete(clientUserId);

        Assert.IsType<ConflictObjectResult>(result);
        Assert.Single(await db.ClientProfiles.ToListAsync());
        Assert.Single(await db.HouseholdAccounts.ToListAsync());
    }

    [Fact]
    public async Task Delete_WhenSharedClient_RemovesOnlyCurrentAgentLink()
    {
        using var db = ControllerTestHelpers.BuildDb();
        const string agentId = "agent-1";
        const string otherAgentId = "agent-2";
        var clientUserId = Guid.NewGuid().ToString();
        const string email = "shared-delete@example.com";

        var meta = new ClientCrmMeta
        {
            RecordType = "BusinessClient",
            PipelineStage = "BusinessClient"
        };

        await SeedOwnedClientAsync(db, agentId, clientUserId, email, ClientCrmMetaSerializer.Serialize(meta));
        db.AgentClients.Add(new AgentClient
        {
            AgentUserId = otherAgentId,
            ClientUserId = clientUserId,
            CreatedUtc = DateTime.UtcNow
        });
        await db.SaveChangesAsync();

        var controller = ControllerTestHelpers.BuildClientsController(
            db,
            Mock.Of<IExecutionEngine>(),
            Mock.Of<ICommitmentService>(),
            ControllerTestHelpers.BuildUser(agentId));

        var result = await controller.Delete(clientUserId);

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal(nameof(ClientsController.Index), redirect.ActionName);
        Assert.Single(await db.ClientProfiles.ToListAsync());
        var remainingLink = Assert.Single(await db.AgentClients.ToListAsync());
        Assert.Equal(otherAgentId, remainingLink.AgentUserId);
        Assert.Equal(clientUserId, remainingLink.ClientUserId);
    }

    [Fact]
    public async Task Delete_FounderOverridePostedByScopedAgent_IsForbidden()
    {
        using var db = ControllerTestHelpers.BuildDb();
        const string clientUserId = "founder-override-protected-client";

        await SeedOwnedClientAsync(
            db,
            "another-agent",
            clientUserId,
            "protected-client@example.com",
            ClientCrmMetaSerializer.Serialize(new ClientCrmMeta
            {
                RecordType = "Client",
                PipelineStage = "Client"
            }));

        var controller = ControllerTestHelpers.BuildClientsController(
            db,
            Mock.Of<IExecutionEngine>(),
            Mock.Of<ICommitmentService>(),
            ControllerTestHelpers.BuildUser("scoped-agent", "scoped-agent@example.com"));

        var result = await controller.Delete(clientUserId, founderOverride: true);

        Assert.IsType<ForbidResult>(result);
        Assert.Single(await db.ClientProfiles.ToListAsync());
        Assert.Single(await db.AgentClients.ToListAsync());
    }
}
