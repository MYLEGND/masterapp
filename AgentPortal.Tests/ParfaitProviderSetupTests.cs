using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Domain.Entities;
using Infrastructure.Analytics;
using Infrastructure.Businesses;
using Infrastructure.WebsiteEditing;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using ParfaitApp.Controllers;
using ParfaitApp.Security;
using ParfaitApp.Services;
using Shared.Analytics;
using Xunit;

namespace AgentPortal.Tests;

public sealed class ParfaitProviderSetupTests
{
    [Fact]
    public void InternalProviderRoutesRequirePageAccessAndWritesRequireAntiforgery()
    {
        Assert.NotNull(typeof(InternalModulesController).GetCustomAttribute<AuthorizeAttribute>());
        foreach (var action in new[] { "MarketingSetup", "ConnectOpenAi", "RefreshOpenAi", "DisconnectOpenAi" })
        {
            var method = typeof(InternalModulesController).GetMethod(action)!;
            Assert.Null(method.GetCustomAttribute<AllowAnonymousAttribute>());
            Assert.Equal("/internal/analytics", method.GetCustomAttribute<ParfaitInternalPageAccessAttribute>()?.PageKey);
            if (action != "MarketingSetup")
            {
                Assert.NotNull(method.GetCustomAttribute<ValidateAntiForgeryTokenAttribute>());
                Assert.NotNull(typeof(CommerceManagementController).GetMethod(action)!.GetCustomAttribute<ValidateAntiForgeryTokenAttribute>());
            }
        }
    }

    [Fact]
    public async Task TicketProviderRoutesRejectInvalidTicketBeforeResolvingProviders()
    {
        using var db = ControllerTestHelpers.BuildDb();
        var config = new ConfigurationBuilder().Build();
        using var tickets = new WebsiteEditorTicketProtector(DataProtectionProvider.Create("ParfaitProviderSetupTests"));
        var stores = new CommerceStoreContextService(db, new CommerceBusinessScopeResolver(db), new ParfaitBusinessScopeService(db),
            new WebsiteDomainService(db, Mock.Of<IHttpClientFactory>(), config), config);
        var controller = Commerce(tickets, config, stores);
        controller.ControllerContext = new() { HttpContext = new DefaultHttpContext() };
        Assert.IsType<UnauthorizedResult>(await controller.MarketingSetup("invalid"));
        Assert.IsType<UnauthorizedResult>(await controller.ConnectOpenAi("invalid", new("unused", null)));
        Assert.IsType<UnauthorizedResult>(await controller.RefreshOpenAi("invalid", new(Guid.NewGuid())));
        Assert.IsType<UnauthorizedResult>(await controller.DisconnectOpenAi("invalid", new(Guid.NewGuid())));
        Assert.Empty(db.MarketingConnections);
    }

    [Fact]
    public async Task InternalConnectionUsesOnlyPermanentParfaitBusiness()
    {
        using var db = ControllerTestHelpers.BuildDb();
        var business = new CommerceBusiness { Key = "parfait", IsActive = true, Status = "Active" };
        var other = new CommerceBusiness { Key = "other", IsActive = true, Status = "Active" };
        db.AddRange(business, other);
        await db.SaveChangesAsync();
        var owner = MarketingOwnerScope.Business(business.Id);
        var direct = new Mock<IOpenAiAdsDirectConnectionService>(MockBehavior.Strict);
        direct.Setup(service => service.ConnectAsync(owner, "key", null, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ArgumentException("No provider call in test."));
        var controller = new InternalModulesController(null!, null!, null!, null!, null!, null!, null!, null!, null!, new ParfaitBusinessScopeService(db), null!);
        controller.ControllerContext = new() { HttpContext = new DefaultHttpContext {
            RequestServices = new ServiceCollection().AddSingleton(direct.Object).BuildServiceProvider() } };
        Assert.IsType<BadRequestObjectResult>(await controller.ConnectOpenAi(new("key", null)));
        direct.Verify(service => service.ConnectAsync(owner, "key", null, It.IsAny<CancellationToken>()), Times.Once);
        Assert.Empty(db.MarketingConnections);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProtectCommerceConnectUsesCanonicalAgentOrFounderOwner(bool founder)
    {
        using var db = ControllerTestHelpers.BuildDb();
        var profile = new AgentTrackingProfile { AgentUserId = "actor", AgentUpn = founder ? "founder@example.test" : "agent@example.test", Slug = "actor", Status = "Active" };
        var business = new CommerceBusiness { Key = "actor-store", IsActive = true, Status = "Active" };
        db.AddRange(profile, business, new WebsiteContentState { OwnerKey = "actor", SiteKey = WebsiteEditorSiteKeys.Protect,
            CommerceBusinessId = business.Id, DraftJson = "{\"store\":{\"enabled\":true}}" });
        await db.SaveChangesAsync();
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Founder:Upn"] = "founder@example.test" }).Build();
        using var tickets = new WebsiteEditorTicketProtector(DataProtectionProvider.Create("ParfaitProviderSetupTests"));
        var stores = new CommerceStoreContextService(db, new CommerceBusinessScopeResolver(db), new ParfaitBusinessScopeService(db),
            new WebsiteDomainService(db, Mock.Of<IHttpClientFactory>(), config), config);
        var owner = founder ? MarketingOwnerScope.Founder : MarketingOwnerScope.Agent(profile.Id);
        var direct = new Mock<IOpenAiAdsDirectConnectionService>(MockBehavior.Strict);
        direct.Setup(service => service.ConnectAsync(owner, "key", null, It.IsAny<CancellationToken>())).ThrowsAsync(new ArgumentException("No live provider."));
        var controller = Commerce(tickets, config, stores);
        controller.ControllerContext = new() { HttpContext = new DefaultHttpContext {
            RequestServices = new ServiceCollection().AddSingleton(db).AddSingleton(direct.Object).BuildServiceProvider() } };
        var token = tickets.Protect(new WebsiteEditorTicket(WebsiteEditorSiteKeys.Protect, "actor", "actor", founder, DateTime.UtcNow.AddMinutes(5), ActorUserId: "actor"));
        Assert.IsType<BadRequestObjectResult>(await controller.ConnectOpenAi(token, new("key", null)));
        direct.Verify(service => service.ConnectAsync(owner, "key", null, It.IsAny<CancellationToken>()), Times.Once);
        Assert.Empty(db.MarketingConnections);
    }

    private static CommerceManagementController Commerce(WebsiteEditorTicketProtector tickets, IConfiguration configuration, CommerceStoreContextService stores) =>
        new(tickets, configuration, stores, null!, null!, null!, null!, null!, null!, null!, null!, null!);
}
