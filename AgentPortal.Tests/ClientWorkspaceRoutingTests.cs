using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using AgentPortal.Controllers;
using Domain.Entities;
using Domain.Enums;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace AgentPortal.Tests;

public sealed class ClientWorkspaceRoutingTests
{
    [Fact]
    public async Task Index_RedirectsOwnedSharedClientToCanonicalClientAppWorkspace()
    {
        using var db = ControllerTestHelpers.BuildDb();
        var clientUserId = Guid.NewGuid().ToString();
        var profile = new ClientProfile
        {
            ClientUserId = clientUserId,
            FirstName = "Test",
            LastName = "Client",
            Email = "client@example.com",
            NormalizedEmail = "client@example.com",
            AccountManagementMode = ClientAccountManagementModes.SharedAccount,
            CreatedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow
        };
        db.ClientProfiles.Add(profile);
        db.AgentClients.Add(new AgentClient
        {
            AgentUserId = "agent-1",
            ClientUserId = clientUserId,
            CreatedUtc = DateTime.UtcNow
        });
        await db.SaveChangesAsync();

        var controller = BuildController(db, "agent-1");
        var result = await controller.Index(clientUserId);

        var redirect = Assert.IsType<RedirectResult>(result);
        Assert.Equal(
            $"https://client.mylegnd.com/support/view-as-client/{profile.Id}?returnUrl=%2F",
            redirect.Url);
    }

    [Fact]
    public async Task Index_DoesNotGrantWorkspaceAccessToAnotherAgent()
    {
        using var db = ControllerTestHelpers.BuildDb();
        var clientUserId = Guid.NewGuid().ToString();
        db.ClientProfiles.Add(new ClientProfile
        {
            ClientUserId = clientUserId,
            FirstName = "Test",
            LastName = "Client",
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

        Assert.IsType<ForbidResult>(await BuildController(db, "other-agent").Index(clientUserId));
    }

    [Fact]
    public async Task Index_DoesNotGrantAgentAccessToSelfManagedClient()
    {
        using var db = ControllerTestHelpers.BuildDb();
        var clientUserId = Guid.NewGuid().ToString();
        db.ClientProfiles.Add(new ClientProfile
        {
            ClientUserId = clientUserId,
            FirstName = "Test",
            LastName = "Client",
            Email = "client@example.com",
            NormalizedEmail = "client@example.com",
            AccountManagementMode = ClientAccountManagementModes.SelfManaged,
            CreatedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow
        });
        db.AgentClients.Add(new AgentClient
        {
            AgentUserId = "agent-1",
            ClientUserId = clientUserId,
            CreatedUtc = DateTime.UtcNow
        });
        await db.SaveChangesAsync();

        Assert.IsType<ForbidResult>(await BuildController(db, "agent-1").Index(clientUserId));
    }

    private static ClientWorkspaceController BuildController(
        Infrastructure.Data.MasterAppDbContext db, string agentId)
    {
        return new ClientWorkspaceController(
            db,
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Provisioning:ClientPortalBaseUrl"] = "https://client.mylegnd.com"
            }).Build())
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = ControllerTestHelpers.BuildUser(agentId) }
            }
        };
    }
}
