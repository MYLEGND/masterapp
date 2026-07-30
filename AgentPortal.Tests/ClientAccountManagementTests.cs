using System.ComponentModel.DataAnnotations;
using System.Collections.Generic;
using System;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using AgentPortal.Models;
using ClientApp.Controllers;
using ClientApp.Infrastructure;
using ClientApp.Services;
using Domain.Entities;
using Domain.Enums;
using Infrastructure.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace AgentPortal.Tests;

public sealed class ClientAccountManagementTests
{
    [Fact]
    public void CreateClientViewModel_RequiresManagementMode_ForPortalClientsOnly()
    {
        var portalClient = new CreateClientViewModel
        {
            RecordType = "Client",
            FirstName = "Taylor",
            LastName = "Client",
            Email = "taylor@example.com",
            CrmStatus = "Active",
            CrmPriority = "Normal",
            PipelineStage = "Client",
            SubscriptionPriceType = "Fixed100",
            SubscriptionBillingAnchorMode = "FirstOfMonth"
        };

        var portalResults = Validate(portalClient);
        Assert.Contains(portalResults, result =>
            result.MemberNames.Contains(nameof(CreateClientViewModel.AccountManagementMode)));

        var lead = new CreateClientViewModel
        {
            RecordType = "Lead",
            CrmStatus = "Lead",
            CrmPriority = "Normal",
            PipelineStage = "NewLead"
        };

        var leadResults = Validate(lead);
        Assert.DoesNotContain(leadResults, result =>
            result.MemberNames.Contains(nameof(CreateClientViewModel.AccountManagementMode)));
    }

    [Fact]
    public async Task SharedAccessAuthority_AllowsSharedAndDeniesSelfManaged()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var shared = new ClientProfile
        {
            ClientUserId = "shared-client",
            Email = "shared@example.test",
            AccountManagementMode = ClientAccountManagementModes.SharedAccount
        };
        var privateClient = new ClientProfile
        {
            ClientUserId = "private-client",
            Email = "private@example.test",
            AccountManagementMode = ClientAccountManagementModes.SelfManaged
        };
        db.ClientProfiles.AddRange(shared, privateClient);
        db.AgentClients.AddRange(
            new AgentClient { AgentUserId = "agent-1", ClientUserId = shared.ClientUserId },
            new AgentClient { AgentUserId = "agent-1", ClientUserId = privateClient.ClientUserId });
        await db.SaveChangesAsync();

        Assert.True(await db.AgentCanAccessClientWorkspaceAsync("agent-1", shared.ClientUserId));
        Assert.False(await db.AgentCanAccessClientWorkspaceAsync("agent-1", privateClient.ClientUserId));
    }

    [Fact]
    public async Task ExistingImpersonationCookie_IsRejectedAfterClientSelectsSelfManaged()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var profile = new ClientProfile
        {
            Id = Guid.NewGuid(),
            ClientUserId = "private-client",
            Email = "private@example.test",
            AccountManagementMode = ClientAccountManagementModes.SelfManaged
        };
        db.ClientProfiles.Add(profile);
        db.AgentClients.Add(new AgentClient { AgentUserId = "agent-1", ClientUserId = profile.ClientUserId });
        await db.SaveChangesAsync();

        var context = new DefaultHttpContext
        {
            User = ControllerTestHelpers.BuildUser("agent-1", "agent-1@example.test")
        };
        context.Request.Headers.Cookie = $"impClientProfileId={profile.Id}";

        var resolved = await new EffectiveClientContextService(db)
            .ResolveAsync(context.User, context.Request.Cookies);

        Assert.Null(resolved);
    }

    [Fact]
    public async Task SupportViewAsClient_RejectsSelfManagedClientForOwningAgent()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var profile = new ClientProfile
        {
            Id = Guid.NewGuid(),
            ClientUserId = "private-client",
            Email = "private@example.test",
            AccountManagementMode = ClientAccountManagementModes.SelfManaged
        };
        db.ClientProfiles.Add(profile);
        db.AgentClients.Add(new AgentClient { AgentUserId = "agent-1", ClientUserId = profile.ClientUserId });
        await db.SaveChangesAsync();

        var controller = new SupportController(db, new ClientAppReturnUrlNormalizer())
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = ControllerTestHelpers.BuildUser("agent-1", "agent-1@example.test")
                }
            }
        };

        var result = await controller.ViewAsClient(profile.Id, "/profile");

        Assert.IsType<ForbidResult>(result);
    }

    private static IReadOnlyCollection<ValidationResult> Validate(object model)
    {
        var results = new List<ValidationResult>();
        Validator.TryValidateObject(model, new ValidationContext(model), results, validateAllProperties: true);
        return results;
    }
}
