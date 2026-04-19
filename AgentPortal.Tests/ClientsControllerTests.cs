using System;
using System.Threading.Tasks;
using AgentPortal.Controllers;
using AgentPortal.Models;
using AgentPortal.Services;
using Domain.Entities;
using Domain.Enums;
using Infrastructure.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Moq;
using Xunit;

namespace AgentPortal.Tests;

public class ClientsControllerTests
{
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
            ControllerTestHelpers.BuildUser(agentId));

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
    public async Task Edit_WhenPortalClientChangesToBusinessClient_MovesToBusinessClientBucket()
    {
        using var db = ControllerTestHelpers.BuildDb();
        const string agentId = "agent-1";
        var clientUserId = Guid.NewGuid().ToString();
        const string email = "business@example.com";

        var meta = new ClientCrmMeta
        {
            RecordType = "Client",
            PipelineStage = "NewLead"
        };

        await SeedOwnedClientAsync(db, agentId, clientUserId, email, ClientCrmMetaSerializer.Serialize(meta));
        await SeedAgentProfileAsync(db, agentId);

        var controller = ControllerTestHelpers.BuildClientsController(
            db,
            Mock.Of<IExecutionEngine>(),
            Mock.Of<ICommitmentService>(),
            ControllerTestHelpers.BuildUser(agentId));

        var result = await controller.Edit(new EditClientViewModel
        {
            ClientUserId = clientUserId,
            RecordType = "BusinessClient",
            HasPortalAccess = true,
            FirstName = "Client",
            LastName = "One",
            Email = email,
            Phone = "555-111-2222",
            MaritalStatus = "Single",
            CrmStatus = "Active",
            CrmPriority = "Normal"
        });

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal(nameof(ClientsController.Index), redirect.ActionName);

        var persisted = await db.ClientProfiles.SingleAsync(x => x.ClientUserId == clientUserId);
        var persistedMeta = ClientCrmMetaSerializer.Deserialize(persisted.CrmNotes);
        Assert.Equal("BusinessClient", persistedMeta.RecordType);
        Assert.Equal("BusinessClient", persistedMeta.PipelineStage);
    }
}
