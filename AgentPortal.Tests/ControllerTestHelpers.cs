using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Net.Http;
using System.Threading;
using AgentPortal.Controllers;
using AgentPortal.Services;
using Domain.Billing;
using AgentPortal.Services.Tracking;
using AgentPortal.Hubs;
using Infrastructure.Data;
using Infrastructure.Identity;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Identity.Web;
using Moq;

namespace AgentPortal.Tests;

internal static class ControllerTestHelpers
{
    public static ClaimsPrincipal BuildUser(string oid = "agent-1", string? upn = null)
    {
        var claims = new List<Claim> { new("oid", oid) };
        if (!string.IsNullOrWhiteSpace(upn))
            claims.Add(new Claim("preferred_username", upn));

        var identity = new ClaimsIdentity(claims, "TestAuth");
        return new ClaimsPrincipal(identity);
    }

    public static MasterAppDbContext BuildDb()
    {
        var options = new DbContextOptionsBuilder<MasterAppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(warnings => warnings.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new MasterAppDbContext(options);
    }

    public static LeadsController BuildLeadsController(
        MasterAppDbContext db,
        IExecutionEngine execution,
        ICommitmentService commitments,
        ClaimsPrincipal user)
    {
        var timeResolver = Mock.Of<IAgentTimeZoneResolver>();
        var prod = new ProductionService(db, NullLogger<ProductionService>.Instance);
        var http = new DefaultHttpContext { User = user };
        var accessor = new HttpContextAccessor { HttpContext = http };
        var tracking = Mock.Of<IAgentTrackingService>();
        var effCtx = new EffectiveAgentContext(accessor, tracking, NullLogger<EffectiveAgentContext>.Instance);
        var featureFlags = Options.Create(new AgentPortal.Models.AppFeatureFlags());
        var importValidator = new AgentPortal.Services.ImportValidation.LeadImportValidator();
        var metaSignalOutcomes = new MetaSignalCrmOutcomeService(db, NullLogger<MetaSignalCrmOutcomeService>.Instance);
        var clientBillingWorkspaceService = new ClientBillingWorkspaceService(db);
        var controller = new LeadsController(db, timeResolver, prod, effCtx, execution, commitments, NullLogger<LeadsController>.Instance, featureFlags, importValidator, metaSignalOutcomes, clientBillingWorkspaceService)
        {
            ControllerContext = new ControllerContext { HttpContext = accessor.HttpContext! }
        };
        return controller;
    }

    public static DashboardController BuildDashboardController(IExecutionEngine execution, ClaimsPrincipal user)
    {
        var blockers = Mock.Of<IBlockerService>();
        var http = new DefaultHttpContext { User = user };
        var accessor = new HttpContextAccessor { HttpContext = http };
        var tracking = Mock.Of<IAgentTrackingService>();
        var effCtx = new EffectiveAgentContext(accessor, tracking, NullLogger<EffectiveAgentContext>.Instance);
        var db = BuildDb();
        var derivedAnalytics = new AgentPortal.Services.Analytics.DerivedAnalyticsService(db);
        var featureFlags = Options.Create(new AgentPortal.Models.AppFeatureFlags());
        var controller = new DashboardController(execution, blockers, db, effCtx, derivedAnalytics, featureFlags)
        {
            ControllerContext = new ControllerContext { HttpContext = http }
        };
        return controller;
    }

    public static ProposalsController BuildProposalsController(
        MasterAppDbContext db,
        IDecisionService decisions,
        IPlaybookEngine playbook,
        ClaimsPrincipal user)
    {
        var http = new DefaultHttpContext { User = user };
        var controller = new ProposalsController(db, decisions, playbook)
        {
            ControllerContext = new ControllerContext { HttpContext = http }
        };
        return controller;
    }

    public static ClientsController BuildClientsController(
        MasterAppDbContext db,
        IExecutionEngine execution,
        ICommitmentService commitments,
        ClaimsPrincipal user,
        IBillingOrchestrator? billingOrchestrator = null,
        IEmailSender? emailSender = null,
        IConfiguration? configuration = null,
        IAgentTimeZoneResolver? timeResolver = null)
    {
        var config = configuration ?? new ConfigurationBuilder()
            .AddInMemoryCollection(new[]
            {
                new KeyValuePair<string,string?>("GraphProvisioning:TenantId","test-tenant"),
                new KeyValuePair<string,string?>("GraphProvisioning:ClientId","test-client"),
                new KeyValuePair<string,string?>("GraphProvisioning:ClientSecret","secret")
            })
            .Build();
        var provisioning = new ClientProvisioningService(config, NullLogger<ClientProvisioningService>.Instance, db);
        timeResolver ??= Mock.Of<IAgentTimeZoneResolver>();
        var azureClientEmailSync = new Mock<IAzureClientEmailSyncService>();
        azureClientEmailSync
            .Setup(service => service.UpdateEmailAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AzureClientEmailSyncResult(true, false));
        var subscriptionIdentitySync = new Mock<IClientSubscriptionIdentitySyncService>();
        subscriptionIdentitySync
            .Setup(service => service.SynchronizeAfterEmailChangeAsync(
                It.IsAny<Guid>(),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ClientSubscriptionIdentitySyncResult(false, 0, 0));
        billingOrchestrator ??= Mock.Of<IBillingOrchestrator>();
        emailSender ??= Mock.Of<IEmailSender>();
        var clientBillingWorkspaceService = new ClientBillingWorkspaceService(db);
        var subscriptionInvitationEmailService = new ClientSubscriptionInvitationEmailService(db, config, emailSender);
        var prod = new ProductionService(db, NullLogger<ProductionService>.Instance);
        var http = new DefaultHttpContext { User = user };
        var accessor = new HttpContextAccessor { HttpContext = http };
        var tracking = Mock.Of<IAgentTrackingService>();
        var effCtx = new EffectiveAgentContext(accessor, tracking, NullLogger<EffectiveAgentContext>.Instance);
        var controller = new ClientsController(db, provisioning, config, NullLogger<ClientsController>.Instance, timeResolver, azureClientEmailSync.Object, subscriptionIdentitySync.Object, prod, effCtx, execution, commitments, billingOrchestrator, clientBillingWorkspaceService, subscriptionInvitationEmailService)
        {
            ControllerContext = new ControllerContext { HttpContext = http },
            TempData = new TempDataDictionary(http, Mock.Of<ITempDataProvider>())
        };
        return controller;
    }

    public static LeadBridgeController BuildLeadBridgeController(
        MasterAppDbContext db,
        ILeadBridgeStateService stateService,
        ClaimsPrincipal user)
    {
        var accessor = new HttpContextAccessor { HttpContext = new DefaultHttpContext { User = user } };
        var tracking = Mock.Of<IAgentTrackingService>();
        var effCtx = new EffectiveAgentContext(accessor, tracking, NullLogger<EffectiveAgentContext>.Instance);

        var hubClients = new Mock<IHubClients>();
        var hubContext = new Mock<IHubContext<LeadBridgeHub>>();
        hubContext.Setup(h => h.Clients).Returns(hubClients.Object);

        return new LeadBridgeController(db, stateService, hubContext.Object, effCtx)
        {
            ControllerContext = new ControllerContext { HttpContext = accessor.HttpContext! }
        };
    }

    public static CalendarController BuildCalendarController(
        MasterAppDbContext db,
        ClaimsPrincipal user,
        HttpMessageHandler handler,
        string accessToken = "test-access-token")
    {
        var accessor = new HttpContextAccessor { HttpContext = new DefaultHttpContext { User = user } };
        var tokenAcquisition = new Mock<ITokenAcquisition>();
        tokenAcquisition
            .Setup(x => x.GetAccessTokenForUserAsync(
                It.IsAny<IEnumerable<string>>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<ClaimsPrincipal>(),
                It.IsAny<TokenAcquisitionOptions>()))
            .ReturnsAsync(accessToken);

        var client = new HttpClient(handler, disposeHandler: false);
        var httpClientFactory = new Mock<IHttpClientFactory>();
        httpClientFactory
            .Setup(x => x.CreateClient(It.IsAny<string>()))
            .Returns(client);

        return new CalendarController(
            tokenAcquisition.Object,
            NullLogger<CalendarController>.Instance,
            db,
            httpClientFactory.Object,
            Mock.Of<IAgentTimeZoneResolver>(),
            null!)
        {
            ControllerContext = new ControllerContext { HttpContext = accessor.HttpContext! }
        };
    }
}
