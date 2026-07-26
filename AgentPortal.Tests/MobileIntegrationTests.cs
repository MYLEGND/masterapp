using System;
using System.Collections.Generic;
using System.IdentityModel.Tokens.Jwt;
using System.IO;
using System.Linq;
using System.Security.Claims;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AgentPortal.Mobile;
using AgentPortal.Services.Tracking;
using Domain.Entities;
using Domain.JourneyCircles;
using Domain.Messaging;
using Infrastructure.Messaging;
using Infrastructure.Mobile;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.IdentityModel.Tokens;
using Moq;
using Xunit;

namespace AgentPortal.Tests;

public sealed class MobileIntegrationTests
{
    [Fact]
    public async Task MobileActorResolver_UsesOnlyCanonicalOid_AndPreservesDualTypedRoles()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var sharedUserId = "same-entra-oid";
        var agent = new AgentProfile
        {
            Id = Guid.NewGuid(),
            AgentUserId = sharedUserId,
            AgentUpn = "agent@example.test",
            FullName = "Agent Identity",
            IsActive = true
        };
        var client = new ClientProfile
        {
            Id = Guid.NewGuid(),
            ClientUserId = sharedUserId,
            FirstName = "Client",
            LastName = "Identity",
            Email = "client@example.test"
        };
        db.AddRange(agent, client);
        await db.SaveChangesAsync();

        var resolver = CreateResolver(db);
        var principal = Principal(sharedUserId, email: "not-an-authority@example.test");

        var choice = await resolver.ResolveAsync(principal);
        Assert.True(choice.Succeeded);
        Assert.True(choice.RequiresParticipantSelection);
        Assert.Null(choice.SelectedActor);
        Assert.Equal(
            [MessagingParticipantTypes.Agent, MessagingParticipantTypes.Client],
            choice.PermittedActors.Select(x => x.Actor.ParticipantType));

        var resolvedAgent = await resolver.ResolveAsync(principal, MessagingParticipantTypes.Agent);
        var resolvedClient = await resolver.ResolveAsync(principal, MessagingParticipantTypes.Client);
        Assert.Equal(agent.Id, resolvedAgent.SelectedActor!.ProfileId);
        Assert.Equal(MessagingParticipantTypes.Agent, resolvedAgent.SelectedActor.Actor.ParticipantType);
        Assert.Equal(client.Id, resolvedClient.SelectedActor!.ProfileId);
        Assert.Equal(MessagingParticipantTypes.Client, resolvedClient.SelectedActor.Actor.ParticipantType);
        Assert.Empty(db.AgentTrackingProfiles);
    }

    [Fact]
    public async Task MobileActorResolver_DoesNotUseEmailOrUpnFallback_AndRejectsUnavailableRole()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        db.AgentProfiles.Add(new AgentProfile
        {
            AgentUserId = "agent-oid",
            AgentUpn = "shared@example.test",
            FullName = "Agent",
            IsActive = true
        });
        await db.SaveChangesAsync();
        var resolver = CreateResolver(db);

        var emailOnly = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim("preferred_username", "shared@example.test")], "Bearer"));
        var unresolved = await resolver.ResolveAsync(emailOnly);
        Assert.False(unresolved.Succeeded);
        Assert.Equal("MOBILE_ACTOR_UNRESOLVED", unresolved.ErrorCode);

        var forgedClientRole = await resolver.ResolveAsync(Principal("agent-oid"), MessagingParticipantTypes.Client);
        Assert.False(forgedClientRole.Succeeded);
        Assert.Equal("MOBILE_ROLE_FORBIDDEN", forgedClientRole.ErrorCode);
    }

    [Fact]
    public async Task MobileBearerPolicy_RequiresTheDedicatedBearerSchemeTenantAndDelegatedScope()
    {
        var configuration = ConfiguredMobileAuth();
        Assert.True(configuration.IsConfigured);

        var policyBuilder = new AuthorizationPolicyBuilder();
        MobileApiAuthorization.ConfigurePolicy(policyBuilder);
        var policy = policyBuilder.Build();
        Assert.Equal([MobileApiAuthorization.BearerScheme], policy.AuthenticationSchemes);

        var requirement = Assert.IsType<MobileApiScopeRequirement>(policy.Requirements.Single(x => x is MobileApiScopeRequirement));
        var valid = Principal("valid-oid", tenantId: "test-tenant", scope: "mobile_access");
        var context = new AuthorizationHandlerContext([requirement], valid, null);
        await new MobileApiScopeAuthorizationHandler(configuration).HandleAsync(context);
        Assert.True(context.HasSucceeded);

        var missingScope = new AuthorizationHandlerContext(
            [requirement],
            Principal("valid-oid", tenantId: "test-tenant", scope: "other_scope"),
            null);
        await new MobileApiScopeAuthorizationHandler(configuration).HandleAsync(missingScope);
        Assert.False(missingScope.HasSucceeded);

        var wrongTenant = new AuthorizationHandlerContext(
            [requirement],
            Principal("valid-oid", tenantId: "other-tenant", scope: "mobile_access"),
            null);
        await new MobileApiScopeAuthorizationHandler(configuration).HandleAsync(wrongTenant);
        Assert.False(wrongTenant.HasSucceeded);
    }

    [Fact]
    public void MobileBearerOptions_ValidateConfiguredIssuerAudienceLifetimeAndSignature()
    {
        var configuration = ConfiguredMobileAuth();
        var options = new Microsoft.AspNetCore.Authentication.JwtBearer.JwtBearerOptions();
        MobileBearerOptions.Configure(options, configuration);
        var signingKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes("test-signing-key-must-be-at-least-thirty-two-bytes"));
        options.TokenValidationParameters.IssuerSigningKey = signingKey;

        var handler = new JwtSecurityTokenHandler();
        var valid = CreateToken(configuration.Authority!, configuration.TokenAudience!, signingKey, DateTime.UtcNow.AddMinutes(10));
        Assert.NotNull(handler.ValidateToken(valid, options.TokenValidationParameters, out _));

        var wrongIssuer = CreateToken("https://issuer.example.test/other", configuration.TokenAudience!, signingKey, DateTime.UtcNow.AddMinutes(10));
        Assert.Throws<SecurityTokenInvalidIssuerException>(() => handler.ValidateToken(wrongIssuer, options.TokenValidationParameters, out _));

        var wrongAudience = CreateToken(configuration.Authority!, "api://other-api", signingKey, DateTime.UtcNow.AddMinutes(10));
        Assert.Throws<SecurityTokenInvalidAudienceException>(() => handler.ValidateToken(wrongAudience, options.TokenValidationParameters, out _));

        var applicationIdUriAudience = CreateToken(configuration.Authority!, configuration.Audience!, signingKey, DateTime.UtcNow.AddMinutes(10));
        Assert.Throws<SecurityTokenInvalidAudienceException>(() => handler.ValidateToken(applicationIdUriAudience, options.TokenValidationParameters, out _));

        var expired = CreateToken(configuration.Authority!, configuration.TokenAudience!, signingKey, DateTime.UtcNow.AddMinutes(-5));
        Assert.Throws<SecurityTokenExpiredException>(() => handler.ValidateToken(expired, options.TokenValidationParameters, out _));
    }

    [Fact]
    public void MobileAuthConfiguration_NormalizesAnApplicationIdUriToTheEntraV2TokenAudience()
    {
        var configuration = ConfiguredMobileAuth();

        Assert.True(configuration.IsConfigured);
        Assert.Equal("00000000-0000-0000-0000-000000000001", configuration.TokenAudience);
    }

    [Fact]
    public async Task MobileErrorContract_IsJsonAndContainsCorrelationId()
    {
        var context = new DefaultHttpContext();
        context.TraceIdentifier = "mobile-correlation";
        context.Response.Body = new MemoryStream();

        await MobileApiErrorWriter.WriteAsync(context, StatusCodes.Status401Unauthorized, "mobile_authentication_required", "A valid mobile session is required.");

        context.Response.Body.Position = 0;
        using var response = await System.Text.Json.JsonDocument.ParseAsync(context.Response.Body);
        Assert.Equal(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
        Assert.StartsWith("application/json", context.Response.ContentType, StringComparison.Ordinal);
        Assert.Equal("mobile-correlation", context.Response.Headers["X-Correlation-ID"]);
        Assert.Equal("mobile-correlation", response.RootElement.GetProperty("correlationId").GetString());
    }

    [Fact]
    public async Task MobileStatusCodeErrorsStayInTheJsonContract()
    {
        var context = new DefaultHttpContext();
        context.TraceIdentifier = "mobile-not-found";
        context.Response.StatusCode = StatusCodes.Status404NotFound;
        context.Response.Body = new MemoryStream();

        await MobileApiErrorWriter.WriteStatusCodeAsync(context);

        context.Response.Body.Position = 0;
        using var response = await System.Text.Json.JsonDocument.ParseAsync(context.Response.Body);
        Assert.Equal(StatusCodes.Status404NotFound, context.Response.StatusCode);
        Assert.Equal("mobile_route_not_found", response.RootElement.GetProperty("code").GetString());
        Assert.Equal("mobile-not-found", response.RootElement.GetProperty("correlationId").GetString());
    }

    [Fact]
    public async Task MobileUnhandledExceptionsStayInTheJsonContract()
    {
        var context = new DefaultHttpContext();
        context.TraceIdentifier = "mobile-failure";
        context.Response.Body = new MemoryStream();

        await MobileApiErrorWriter.WriteUnhandledExceptionAsync(context);

        context.Response.Body.Position = 0;
        using var response = await System.Text.Json.JsonDocument.ParseAsync(context.Response.Body);
        Assert.Equal(StatusCodes.Status500InternalServerError, context.Response.StatusCode);
        Assert.Equal("mobile_request_failed", response.RootElement.GetProperty("code").GetString());
        Assert.Equal("mobile-failure", response.RootElement.GetProperty("correlationId").GetString());
    }

    [Fact]
    public async Task MobileController_ResolvesTheServerActorAndRejectsForgedParticipantType()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        db.AgentProfiles.Add(new AgentProfile { AgentUserId = "agent-oid", AgentUpn = "agent@example.test", FullName = "Agent", IsActive = true });
        await db.SaveChangesAsync();
        var messaging = new Mock<IMessagingService>(MockBehavior.Strict);
        messaging.Setup(x => x.ListConversationsAsync(
                It.Is<MessagingActor>(actor => actor.UserId == "agent-oid" && actor.ParticipantType == MessagingParticipantTypes.Agent),
                It.IsAny<MessagingConversationListQuery>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MessagingConversationListResult(true, null, null, Array.Empty<MessagingConversationSummary>()));
        var controller = CreateController(db, messaging.Object, Principal("agent-oid"));

        var allowed = await controller.ListConversations(CancellationToken.None);
        Assert.IsType<OkObjectResult>(allowed);

        controller.HttpContext.Request.Headers[MobileApiAuthorization.ParticipantTypeHeader] = MessagingParticipantTypes.Client;
        var forbidden = await controller.ListConversations(CancellationToken.None);
        var response = Assert.IsType<ObjectResult>(forbidden);
        Assert.Equal(StatusCodes.Status403Forbidden, response.StatusCode);
        messaging.VerifyAll();
    }

    [Fact]
    public async Task MobileSession_UsesTheExistingTypedProfileImageAuthority()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var agentId = Guid.NewGuid();
        db.AgentProfiles.Add(new AgentProfile
        {
            Id = agentId,
            AgentUserId = "agent-oid",
            AgentUpn = "agent@example.test",
            FullName = "Agent",
            IsActive = true
        });
        await db.SaveChangesAsync();

        var messaging = new Mock<IMessagingService>(MockBehavior.Strict);
        var profiles = new Mock<IMessagingProfileImageResolver>(MockBehavior.Strict);
        profiles.Setup(x => x.ResolveAsync(
                It.Is<MessagingParticipantIdentity>(identity =>
                    identity.ProfileId == agentId &&
                    identity.ParticipantType == MessagingParticipantTypes.Agent),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MessagingProfileImage([1, 2, 3], "image/png"));
        var controller = CreateController(db, messaging.Object, Principal("agent-oid"), profiles.Object);

        var result = await controller.Session(CancellationToken.None);

        var response = Assert.IsType<OkObjectResult>(result).Value as MobileSessionResponse;
        Assert.NotNull(response?.Actor?.Avatar);
        Assert.Equal("image/png", response!.Actor!.Avatar!.ContentType);
        Assert.Equal(Convert.ToBase64String(new byte[] { 1, 2, 3 }), response.Actor.Avatar.Base64Content);
        profiles.VerifyAll();
    }

    [Fact]
    public async Task MobileController_SendUsesTheResolvedActorAndRejectsUnauthorizedConversations()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        db.AgentProfiles.Add(new AgentProfile { AgentUserId = "agent-oid", AgentUpn = "agent@example.test", FullName = "Agent", IsActive = true });
        await db.SaveChangesAsync();
        var conversationId = Guid.NewGuid();
        SendMessagingMessageCommand? sent = null;
        var messaging = new Mock<IMessagingService>(MockBehavior.Strict);
        messaging.Setup(x => x.SendMessageAsync(It.IsAny<SendMessagingMessageCommand>(), It.IsAny<CancellationToken>()))
            .Callback<SendMessagingMessageCommand, CancellationToken>((command, _) => sent = command)
            .ReturnsAsync(MessagingMessageResult.Failure("MESSAGING_CONVERSATION_NOT_FOUND", "Not available."));
        var controller = CreateController(db, messaging.Object, Principal("agent-oid"));

        var result = await controller.SendMessage(conversationId, new MobileSendMessageRequest("server-owned actor only"), CancellationToken.None);
        var response = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status404NotFound, response.StatusCode);
        Assert.NotNull(sent);
        Assert.Equal(new MessagingActor("agent-oid", MessagingParticipantTypes.Agent), sent!.Actor);
        Assert.Equal("server-owned actor only", sent.Body);
    }

    [Fact]
    public async Task MobileController_ConversationMessagesAndReadUseTheResolvedTypedActor()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        db.AgentProfiles.Add(new AgentProfile { AgentUserId = "agent-oid", AgentUpn = "agent@example.test", FullName = "Agent", IsActive = true });
        await db.SaveChangesAsync();

        var conversationId = Guid.NewGuid();
        var actor = new MessagingActor("agent-oid", MessagingParticipantTypes.Agent);
        var detail = new MessagingConversationDetail(
            conversationId,
            MessagingConversationTypes.ClientAgent,
            null,
            DateTime.UtcNow.AddMinutes(-1),
            DateTime.UtcNow,
            false,
            false,
            false,
            [
                new MessagingParticipantSummary("agent-oid", MessagingParticipantTypes.Agent, "Agent"),
                new MessagingParticipantSummary("client-oid", MessagingParticipantTypes.Client, "Client")
            ],
            [
                new MessagingMessageSummary(
                    Guid.NewGuid(),
                    conversationId,
                    "client-oid",
                    MessagingParticipantTypes.Client,
                    "Server-authorized message",
                    DateTime.UtcNow,
                    null,
                    false,
                    Array.Empty<MessagingAttachmentSummary>())
            ]);
        var messaging = new Mock<IMessagingService>(MockBehavior.Strict);
        messaging.Setup(x => x.GetConversationAsync(actor, conversationId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MessagingConversationResult(true, null, null, detail));
        messaging.Setup(x => x.MarkConversationReadAsync(
                It.Is<MessagingConversationActionCommand>(command => command.Actor == actor && command.ConversationId == conversationId),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(MessagingOperationResult.Success());
        var controller = CreateController(db, messaging.Object, Principal("agent-oid"));

        var conversationResult = await controller.Conversation(conversationId, CancellationToken.None);
        var conversation = Assert.IsType<OkObjectResult>(conversationResult).Value as MobileConversationDetailDto;
        Assert.NotNull(conversation);
        Assert.Single(conversation!.Messages);
        Assert.False(conversation.Messages[0].IsMine);

        var messagesResult = await controller.Messages(conversationId, CancellationToken.None);
        var messages = Assert.IsAssignableFrom<IReadOnlyList<MobileMessageDto>>(Assert.IsType<OkObjectResult>(messagesResult).Value);
        Assert.Single(messages);
        Assert.Equal("Server-authorized message", messages[0].Body);

        var readResult = await controller.MarkRead(conversationId, CancellationToken.None);
        Assert.IsType<NoContentResult>(readResult);
        messaging.VerifyAll();
    }

    [Fact]
    public async Task MobileJourneyCircles_RejectsAnAgentIdentityBeforeCallingTheClientService()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        db.AgentProfiles.Add(new AgentProfile
        {
            AgentUserId = "agent-oid",
            AgentUpn = "agent@example.test",
            FullName = "Agent",
            IsActive = true
        });
        await db.SaveChangesAsync();

        var journeyCircles = new Mock<IJourneyCirclesService>(MockBehavior.Strict);
        var controller = CreateJourneyController(db, journeyCircles.Object, Principal("agent-oid"));

        var result = await controller.Dashboard(CancellationToken.None);

        var response = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status403Forbidden, response.StatusCode);
        journeyCircles.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task MobileAccount_UpdatesOnlyTheSelectedTypedProfileForOnePhysicalUser()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        const string sharedUserId = "dual-mobile-oid";
        var agent = new AgentProfile
        {
            Id = Guid.NewGuid(),
            AgentUserId = sharedUserId,
            AgentUpn = "agent@example.test",
            FullName = "Agent Original",
            Phone = "111-111-1111",
            IsActive = true
        };
        var client = new ClientProfile
        {
            Id = Guid.NewGuid(),
            ClientUserId = sharedUserId,
            FirstName = "Client",
            LastName = "Original",
            Email = "client@example.test",
            Phone = "222-222-2222"
        };
        db.AddRange(agent, client);
        await db.SaveChangesAsync();

        var controller = CreateAccountController(db, Principal(sharedUserId));

        controller.HttpContext.Request.Headers[MobileApiAuthorization.ParticipantTypeHeader] = MessagingParticipantTypes.Agent;
        var agentResult = await controller.Update(
            new MobileAccountUpdateRequest("Agent Updated", "333-333-3333", "Advisor", "Profile-owned bio"),
            CancellationToken.None);
        var agentProfile = Assert.IsType<OkObjectResult>(agentResult).Value as MobileAccountProfile;
        Assert.NotNull(agentProfile);
        Assert.Equal(MessagingParticipantTypes.Agent, agentProfile!.ParticipantType);
        Assert.Equal(agent.Id, agentProfile.ProfileId);

        await db.Entry(agent).ReloadAsync();
        await db.Entry(client).ReloadAsync();
        Assert.Equal("Agent Updated", agent.FullName);
        Assert.Equal("333-333-3333", agent.Phone);
        Assert.Equal("Client", client.FirstName);
        Assert.Equal("Original", client.LastName);
        Assert.Equal("222-222-2222", client.Phone);

        controller.HttpContext.Request.Headers[MobileApiAuthorization.ParticipantTypeHeader] = MessagingParticipantTypes.Client;
        var clientResult = await controller.Update(
            new MobileAccountUpdateRequest("Client Updated", "444-444-4444", "Forged title", "Forged bio"),
            CancellationToken.None);
        var clientProfile = Assert.IsType<OkObjectResult>(clientResult).Value as MobileAccountProfile;
        Assert.NotNull(clientProfile);
        Assert.Equal(MessagingParticipantTypes.Client, clientProfile!.ParticipantType);
        Assert.Equal(client.Id, clientProfile.ProfileId);

        await db.Entry(agent).ReloadAsync();
        await db.Entry(client).ReloadAsync();
        Assert.Equal("Agent Updated", agent.FullName);
        Assert.Equal("Advisor", agent.Title);
        Assert.Equal("Profile-owned bio", agent.ShortBio);
        Assert.Equal("Client", client.FirstName);
        Assert.Equal("Updated", client.LastName);
        Assert.Equal("444-444-4444", client.Phone);
    }

    [Fact]
    public async Task MobileJourneyCircles_ProjectsAClientAvatarOnlyFromTheTypedClientProfile()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var profileId = Guid.NewGuid();
        db.ClientProfiles.Add(new ClientProfile
        {
            Id = profileId,
            ClientUserId = "client-oid",
            FirstName = "Client",
            LastName = "Identity",
            Email = "client@example.test"
        });
        await db.SaveChangesAsync();

        var profile = new JourneyCirclePublicProfile(
            profileId,
            "Client Identity",
            null,
            Array.Empty<string>(),
            Array.Empty<string>(),
            Array.Empty<string>(),
            Array.Empty<string>(),
            Array.Empty<string>(),
            Array.Empty<string>(),
            Array.Empty<string>(),
            Array.Empty<string>(),
            "not-an-avatar-authority");
        var dashboard = new JourneyCircleDashboard(
            profile,
            null,
            Array.Empty<JourneyCircleRecommendation>(),
            Array.Empty<JourneyCircleConnectionSummary>(),
            Array.Empty<JourneyCircleConnectionSummary>(),
            Array.Empty<string>(),
            Array.Empty<string>(),
            Array.Empty<string>(),
            Array.Empty<string>(),
            Array.Empty<string>(),
            Array.Empty<string>(),
            Array.Empty<string>(),
            Array.Empty<string>());
        var journeyCircles = new Mock<IJourneyCirclesService>(MockBehavior.Strict);
        journeyCircles
            .Setup(service => service.GetDashboardAsync("client-oid", It.IsAny<CancellationToken>()))
            .ReturnsAsync(dashboard);
        var identities = new Dictionary<Guid, MessagingParticipantIdentity>
        {
            [profileId] = new MessagingParticipantIdentity(
                "client-oid",
                MessagingParticipantTypes.Client,
                profileId,
                "Client Identity",
                "client@example.test",
                "CI")
        };
        var images = new Mock<IMessagingProfileImageResolver>(MockBehavior.Strict);
        images
            .Setup(service => service.ResolveClientIdentitiesByProfileIdAsync(
                It.Is<IEnumerable<Guid>>(ids =>
                    ids.Single() == profileId),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(identities);
        images
            .Setup(service => service.ResolveAsync(
                It.Is<MessagingParticipantIdentity>(identity =>
                    identity.UserId == "client-oid" &&
                    identity.ParticipantType == MessagingParticipantTypes.Client &&
                    identity.ProfileId == profileId),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MessagingProfileImage([1, 2, 3], "image/png"));
        var controller = CreateJourneyController(db, journeyCircles.Object, Principal("client-oid"), images.Object);

        var result = await controller.Dashboard(CancellationToken.None);

        var response = Assert.IsType<OkObjectResult>(result).Value as MobileJourneyDashboard;
        Assert.NotNull(response?.Profile?.Avatar);
        Assert.Equal("image/png", response!.Profile!.Avatar!.ContentType);
        Assert.Equal(Convert.ToBase64String(new byte[] { 1, 2, 3 }), response.Profile.Avatar.Base64Content);
        journeyCircles.VerifyAll();
        images.VerifyAll();
    }

    [Fact]
    public async Task MobileAgentClients_UsesTheServerResolvedAgentAndProjectsOnlyClientOwnedImageData()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        db.AgentProfiles.Add(new AgentProfile
        {
            Id = Guid.NewGuid(),
            AgentUserId = "agent-oid",
            AgentUpn = "agent@example.test",
            FullName = "Agent Identity",
            IsActive = true
        });
        await db.SaveChangesAsync();

        var clientProfileId = Guid.NewGuid();
        var home = new Mock<IMobileHomeService>(MockBehavior.Strict);
        home.Setup(service => service.GetAgentClientsAsync(
                It.Is<MobileResolvedActor>(actor =>
                    actor.Actor.UserId == "agent-oid" &&
                    actor.Actor.ParticipantType == MessagingParticipantTypes.Agent),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(MobileAgentClientsResult.Success(
            [
                new MobileAgentClient(
                    clientProfileId,
                    "Client Identity",
                    "client@example.test",
                    "Active",
                    [1, 2, 3],
                    "image/png")
            ]));
        var controller = CreateHomeController(db, home.Object, Principal("agent-oid"));

        var result = await controller.AgentClients(CancellationToken.None);

        var response = Assert.IsType<OkObjectResult>(result);
        var client = Assert.Single(Assert.IsAssignableFrom<IEnumerable<MobileAgentClientDto>>(response.Value));
        Assert.Equal(clientProfileId, client.ProfileId);
        Assert.Equal("Client Identity", client.DisplayName);
        Assert.NotNull(client.Avatar);
        Assert.Equal("image/png", client.Avatar!.ContentType);
        Assert.Equal(Convert.ToBase64String(new byte[] { 1, 2, 3 }), client.Avatar.Base64Content);
        home.VerifyAll();
    }

    [Fact]
    public async Task MobileAgentWorkspace_RejectsAClientIdentityBeforeCallingAgentEndpoints()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        db.ClientProfiles.Add(new ClientProfile
        {
            Id = Guid.NewGuid(),
            ClientUserId = "client-oid",
            FirstName = "Client",
            LastName = "Identity",
            Email = "client@example.test"
        });
        await db.SaveChangesAsync();

        var home = new Mock<IMobileHomeService>(MockBehavior.Strict);
        var controller = CreateHomeController(db, home.Object, Principal("client-oid"));

        var clients = await controller.AgentClients(CancellationToken.None);
        var leads = await controller.AgentLeads(CancellationToken.None);

        Assert.Equal(StatusCodes.Status403Forbidden, Assert.IsType<ObjectResult>(clients).StatusCode);
        Assert.Equal(StatusCodes.Status403Forbidden, Assert.IsType<ObjectResult>(leads).StatusCode);
        home.VerifyNoOtherCalls();
    }

    [Fact]
    public void MobileApiRoute_IsNarrowAndDoesNotMatchNormalPortalRoutes()
    {
        var mobile = new DefaultHttpContext();
        mobile.Request.Path = "/api/v1/mobile/messaging/conversations";
        Assert.True(MobileApiRoute.IsMobileApi(mobile.Request));

        var portal = new DefaultHttpContext();
        portal.Request.Path = "/Clients";
        Assert.False(MobileApiRoute.IsMobileApi(portal.Request));

        var lookalike = new DefaultHttpContext();
        lookalike.Request.Path = "/api/v1/mobile-not-a-route";
        Assert.False(MobileApiRoute.IsMobileApi(lookalike.Request));
    }

    [Fact]
    public async Task MobileRoute_DoesNotTriggerAgentTrackingProvisioning()
    {
        var tracking = new Mock<IAgentTrackingService>(MockBehavior.Strict);
        var filter = new AgentTrackingProvisioningFilter(tracking.Object, NullLogger<AgentTrackingProvisioningFilter>.Instance);
        var context = new DefaultHttpContext { User = Principal("client-oid") };
        context.Request.Path = "/api/v1/mobile/messaging/conversations";
        var actionContext = new ActionContext(context, new RouteData(), new ActionDescriptor());
        var executing = new ActionExecutingContext(actionContext, [], new Dictionary<string, object?>(), new object());
        var nextCalled = false;

        await filter.OnActionExecutionAsync(executing, () =>
        {
            nextCalled = true;
            return Task.FromResult(new ActionExecutedContext(actionContext, [], new object()));
        });

        Assert.True(nextCalled);
        tracking.VerifyNoOtherCalls();
    }

    private static MobileActorResolver CreateResolver(Infrastructure.Data.MasterAppDbContext db) =>
        new(db, NullLogger<MobileActorResolver>.Instance);

    private static MobileMessagingController CreateController(
        Infrastructure.Data.MasterAppDbContext db,
        IMessagingService messaging,
        ClaimsPrincipal principal,
        IMessagingProfileImageResolver? providedProfiles = null)
    {
        var profiles = providedProfiles ?? CreateEmptyProfileResolver();
        var controller = new MobileMessagingController(CreateResolver(db), messaging, profiles)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = principal }
            }
        };
        return controller;
    }

    private static MobileHomeController CreateHomeController(
        Infrastructure.Data.MasterAppDbContext db,
        IMobileHomeService home,
        ClaimsPrincipal principal)
    {
        return new MobileHomeController(CreateResolver(db), home)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = principal }
            }
        };
    }

    private static MobileJourneyCirclesController CreateJourneyController(
        Infrastructure.Data.MasterAppDbContext db,
        IJourneyCirclesService journeyCircles,
        ClaimsPrincipal principal,
        IMessagingProfileImageResolver? providedProfiles = null)
    {
        var controller = new MobileJourneyCirclesController(
            CreateResolver(db),
            journeyCircles,
            providedProfiles ?? CreateEmptyProfileResolver())
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = principal }
            }
        };
        return controller;
    }

    private static MobileAccountController CreateAccountController(
        Infrastructure.Data.MasterAppDbContext db,
        ClaimsPrincipal principal)
    {
        var accounts = new MobileAccountService(db);

        return new MobileAccountController(
            CreateResolver(db),
            accounts,
            CreateEmptyProfileResolver())
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = principal
                }
            }
        };
    }

    private static IMessagingProfileImageResolver CreateEmptyProfileResolver()
    {
        var profiles = new Mock<IMessagingProfileImageResolver>();
        profiles.Setup(x => x.ResolveIdentitiesAsync(
                It.IsAny<IEnumerable<MessagingParticipantReference>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<(string UserId, string ParticipantType), MessagingParticipantIdentity>());

        profiles.Setup(x => x.ResolveClientIdentitiesByProfileIdAsync(
                It.IsAny<IEnumerable<Guid>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<Guid, MessagingParticipantIdentity>());
        return profiles.Object;
    }

    private static MobileAuthConfiguration ConfiguredMobileAuth() =>
        MobileAuthConfiguration.FromConfiguration(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["MobileAuth:TenantId"] = "test-tenant",
                ["MobileAuth:Authority"] = "https://issuer.example.test/test-tenant/v2.0",
                ["MobileAuth:Audience"] = "api://00000000-0000-0000-0000-000000000001",
                ["MobileAuth:RequiredScope"] = "api://00000000-0000-0000-0000-000000000001/mobile_access"
            })
            .Build());

    private static ClaimsPrincipal Principal(
        string oid,
        string? email = null,
        string? tenantId = null,
        string? scope = null)
    {
        var claims = new List<Claim> { new("oid", oid) };
        if (email is not null) claims.Add(new Claim("preferred_username", email));
        if (tenantId is not null) claims.Add(new Claim("tid", tenantId));
        if (scope is not null) claims.Add(new Claim("scp", scope));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, MobileApiAuthorization.BearerScheme));
    }

    private static string CreateToken(string issuer, string audience, SecurityKey signingKey, DateTime expiresUtc)
    {
        var notBeforeUtc = expiresUtc > DateTime.UtcNow
            ? DateTime.UtcNow.AddMinutes(-1)
            : expiresUtc.AddMinutes(-1);
        var token = new JwtSecurityToken(
            issuer,
            audience,
            [new Claim("oid", "test-oid"), new Claim("tid", "test-tenant"), new Claim("scp", "mobile_access")],
            notBefore: notBeforeUtc,
            expires: expiresUtc,
            signingCredentials: new SigningCredentials(signingKey, SecurityAlgorithms.HmacSha256));
        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}
