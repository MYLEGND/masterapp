using System;
using System.Collections.Generic;
using System.Security.Claims;
using AgentPortal.Controllers;
using AgentPortal.Services;
using AgentPortal.Services.Tracking;
using AgentPortal.Hubs;
using Infrastructure.Data;
using Infrastructure.Identity;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace AgentPortal.Tests;

internal static class ControllerTestHelpers
{
    public static ClaimsPrincipal BuildUser(string oid = "agent-1")
    {
        var identity = new ClaimsIdentity(new[] { new Claim("oid", oid) }, "TestAuth");
        return new ClaimsPrincipal(identity);
    }

    public static MasterAppDbContext BuildDb()
    {
        var options = new DbContextOptionsBuilder<MasterAppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
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
        var accessor = new HttpContextAccessor { HttpContext = new DefaultHttpContext { User = user } };
        var tracking = Mock.Of<IAgentTrackingService>();
        var effCtx = new EffectiveAgentContext(accessor, tracking, NullLogger<EffectiveAgentContext>.Instance);
        var featureFlags = Options.Create(new AgentPortal.Models.AppFeatureFlags());
        var importValidator = new AgentPortal.Services.ImportValidation.LeadImportValidator();
        var controller = new LeadsController(db, timeResolver, prod, effCtx, execution, commitments, NullLogger<LeadsController>.Instance, featureFlags, importValidator)
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
        ClaimsPrincipal user)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new[]
            {
                new KeyValuePair<string,string?>("GraphProvisioning:TenantId","test-tenant"),
                new KeyValuePair<string,string?>("GraphProvisioning:ClientId","test-client"),
                new KeyValuePair<string,string?>("GraphProvisioning:ClientSecret","secret")
            })
            .Build();
        var provisioning = new ClientProvisioningService(config, NullLogger<ClientProvisioningService>.Instance, db);
        var timeResolver = Mock.Of<IAgentTimeZoneResolver>();
        var azureClientEmailSync = Mock.Of<IAzureClientEmailSyncService>();
        var prod = new ProductionService(db, NullLogger<ProductionService>.Instance);
        var accessor = new HttpContextAccessor { HttpContext = new DefaultHttpContext { User = user } };
        var tracking = Mock.Of<IAgentTrackingService>();
        var effCtx = new EffectiveAgentContext(accessor, tracking, NullLogger<EffectiveAgentContext>.Instance);
        var controller = new ClientsController(db, provisioning, config, NullLogger<ClientsController>.Instance, timeResolver, azureClientEmailSync, prod, effCtx, execution, commitments)
        {
            ControllerContext = new ControllerContext { HttpContext = accessor.HttpContext! }
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
}
