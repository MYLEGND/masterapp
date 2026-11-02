using System;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Domain.Entities;
using Infrastructure.Commerce.Payments;
using Infrastructure.Data;
using Infrastructure.WebsiteEditing;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Moq;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ParfaitApp.Controllers;
using ParfaitApp.Services;
using Infrastructure.Businesses;
using Legend.Commerce;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace AgentPortal.Tests;

public sealed class CommerceManagementCapabilityPolicyTests
{
    [Theory]
    [InlineData("dashboard", "dashboard", "GET")]
    [InlineData("settings/business-profile", "business-profile", "GET")]
    [InlineData("settings/business-profile", "business-profile", "POST")]
    [InlineData("settings/team", "team", "GET")]
    [InlineData("commerce/products", "products", "GET")]
    [InlineData("commerce/products", "product", "POST")]
    [InlineData("commerce/products/settings", "settings/commerce", "POST")]
    [InlineData("commerce/products/delete", "product/delete", "POST")]
    [InlineData("commerce/products/images/upload", "product/images/upload", "POST")]
    [InlineData("commerce/products/images/delete", "product/images/delete", "POST")]
    [InlineData("commerce/products/images/reorder", "product/images/reorder", "POST")]
    [InlineData("commerce/products/images/display", "product/images/display", "POST")]
    [InlineData("commerce/products/reorder", "products/reorder", "POST")]
    [InlineData("commerce/orders", "orders", "GET")]
    [InlineData("commerce/orders/update", "order", "POST")]
    [InlineData("commerce/orders/receipt", "order/receipt", "POST")]
    [InlineData("automations", "automations", "GET")]
    [InlineData("automations/workflows", "automations/workflows", "POST")]
    [InlineData("automations/workflows/delete", "automations/workflows/delete", "POST")]
    [InlineData("analytics", "analytics", "GET")]
    [InlineData("analytics/health-monitor", "analytics/health-monitor", "GET")]
    [InlineData("analytics/marketing-setup", "analytics/marketing-setup", "GET")]
    [InlineData("analytics/meta-connect", "analytics/meta-connect", "GET")]
    [InlineData("analytics/meta-callback", "analytics/meta-callback", "GET")]
    [InlineData("analytics/meta-connection-status", "analytics/meta-connection-status", "GET")]
    [InlineData("analytics/meta-campaigns", "analytics/meta-campaigns", "GET")]
    [InlineData("analytics/meta-disconnect", "analytics/meta-disconnect", "POST")]
    [InlineData("analytics/openai-connect", "analytics/openai-connect", "POST")]
    [InlineData("analytics/openai-refresh", "analytics/openai-refresh", "POST")]
    [InlineData("analytics/openai-disconnect", "analytics/openai-disconnect", "POST")]
    [InlineData("settings/payments", "payments", "GET")]
    [InlineData("settings/payments/callback", "payments/callback", "GET")]
    [InlineData("settings/payments/connect", "payments/connect", "POST")]
    [InlineData("settings/payments/resume", "payments/resume", "POST")]
    [InlineData("settings/payments/confirm", "payments/confirm", "POST")]
    [InlineData("settings/payments/default", "payments/default", "POST")]
    [InlineData("settings/payments/disconnect", "payments/disconnect", "POST")]
    [InlineData("settings/payments/refresh", "payments/refresh", "POST")]
    [InlineData("settings/payments/reconcile", "payments/reconcile", "POST")]
    [InlineData("settings/payments/refund", "payments/refund", "POST")]
    public void InternalAdaptersUseExistingActionCapability(string alias, string action, string method)
    {
        var expected = CommerceManagementCapabilityPolicy.ForRequest(new PathString("/commerce/manage/" + action), method);
        Assert.NotNull(expected);
        Assert.Equal(expected, CommerceManagementCapabilityPolicy.ForRequest(new PathString("/internal/" + alias), method));
        if (method == "GET")
            Assert.Equal(expected, CommerceManagementCapabilityPolicy.ForRequest(new PathString("/internal/" + alias), "HEAD"));
        Assert.Null(CommerceManagementCapabilityPolicy.ForRequest(new PathString("/internal/" + alias), "DELETE"));
    }

    [Theory]
    [InlineData("/internal/businesses", "GET")]
    [InlineData("/internal/login", "GET")]
    [InlineData("/internal/logout", "POST")]
    [InlineData("/internal/denied", "GET")]
    [InlineData("/internal", "GET")]
    [InlineData("/internal/settings/team/add", "POST")]
    [InlineData("/internal/settings/team/00000000-0000-0000-0000-000000000001", "POST")]
    [InlineData("/internal/analytics/meta-settings", "POST")]
    [InlineData("/internal/settings/payments/refund", "GET")]
    [InlineData("/internal/settings/payments/callback", "POST")]
    [InlineData("/internal/commerce/products/images/upload", "GET")]
    [InlineData("/internal/commerce/orders", "POST")]
    [InlineData("/internal/dashboard", "POST")]
    [InlineData("/internal/commerce/../settings/team", "GET")]
    [InlineData("/internal//dashboard", "GET")]
    [InlineData("/internal/unknown", "GET")]
    public void UnmappedInternalOperationsNeverGainManagementCapability(string route, string method)
        => Assert.Null(CommerceManagementCapabilityPolicy.ForRequest(new PathString(route), method));

    [Theory]
    [InlineData("/commerce/manage/dashboard", "GET", "website")]
    [InlineData("/commerce/manage/preview/product/leggings", "GET", "website")]
    [InlineData("/commerce/manage/business-profile", "GET", "settings")]
    [InlineData("/commerce/manage/business-profile", "POST", "settings")]
    [InlineData("/commerce/manage/team", "GET", "team")]
    [InlineData("/commerce/manage/team/permissions", "POST", "team")]
    [InlineData("/commerce/manage/products", "GET", "catalog")]
    [InlineData("/commerce/manage/product/images/upload", "POST", "catalog")]
    [InlineData("/commerce/manage/settings/commerce", "POST", "catalog")]
    [InlineData("/commerce/manage/orders", "GET", "orders")]
    [InlineData("/commerce/manage/order/receipt", "POST", "orders")]
    [InlineData("/commerce/manage/automations", "GET", "automations")]
    [InlineData("/commerce/manage/automations/workflows/delete", "POST", "automations")]
    [InlineData("/commerce/manage/analytics", "GET", "analytics")]
    [InlineData("/commerce/manage/analytics/meta-campaigns", "GET", "analytics")]
    [InlineData("/commerce/manage/analytics/meta-connect", "GET", "settings")]
    [InlineData("/commerce/manage/analytics/meta-callback", "GET", "settings")]
    [InlineData("/commerce/manage/analytics/openai-connect", "POST", "settings")]
    [InlineData("/commerce/manage/analytics/meta-disconnect", "POST", "settings")]
    [InlineData("/commerce/manage/payments", "GET", "settings")]
    [InlineData("/commerce/manage/payments/callback", "GET", "settings")]
    [InlineData("/commerce/manage/payments/connect", "POST", "settings")]
    [InlineData("/commerce/manage/payments/resume", "POST", "settings")]
    [InlineData("/commerce/manage/payments/refund", "POST", "settings")]
    [InlineData("/commerce/manage/payments/reconcile", "POST", "settings")]
    [InlineData("/commerce/manage/payments/confirm", "POST", "settings")]
    [InlineData("/commerce/manage/payments/default", "POST", "settings")]
    [InlineData("/commerce/manage/payments/disconnect", "POST", "settings")]
    [InlineData("/commerce/manage/payments/refresh", "POST", "settings")]
    public void ExistingSharedRoutesRequireAnExactCapability(string route, string verb, string expected)
    {
        Assert.Equal(expected, CommerceManagementCapabilityPolicy.ForRequest(
            new PathString(route), verb));
    }

    [Theory]
    [InlineData("/commerce/manage/products", "POST")]
    [InlineData("/commerce/manage/order/receipt", "GET")]
    [InlineData("/commerce/manage/unknown", "GET")]
    [InlineData("/commerce/manage/team/permissions", "GET")]
    [InlineData("/commerce/manage/business-profile/unknown", "POST")]
    [InlineData("/commerce/manage/analytics/not-registered", "GET")]
    [InlineData("/commerce/manage/automations/secret", "POST")]
    [InlineData("/commerce/manage/order", "PUT")]
    [InlineData("/store/cart", "GET")]
    [InlineData("/commerce/manage/payments/connect", "GET")]
    [InlineData("/commerce/manage/payments/resume", "GET")]
    [InlineData("/commerce/manage/payments/refund", "GET")]
    [InlineData("/commerce/manage/payments/reconcile", "GET")]
    [InlineData("/commerce/manage/payments/default", "GET")]
    [InlineData("/commerce/manage/payments/callback", "POST")]
    [InlineData("/commerce/manage/payments/unregistered", "POST")]
    public void UnknownVerbOrOperationCannotInheritStoreOwnerPermission(string route, string verb)
    {
        Assert.Null(CommerceManagementCapabilityPolicy.ForRequest(new PathString(route), verb));
    }

    [Fact]
    public void SharedControllerUsesCanonicalMembershipNotLegacyParfaitTeamFile()
    {
        // Method readback alongside the active, tested policy prevents a later
        // addition from silently authorizing catalog/order writes as website.
        var method = typeof(ParfaitApp.Controllers.CommerceManagementController)
            .GetMethod("Products");
        Assert.NotNull(method);
        Assert.Equal("catalog", CommerceManagementCapabilityPolicy.ForRequest(
            new PathString("/commerce/manage/products"), "GET"));
        Assert.Equal("orders", CommerceManagementCapabilityPolicy.ForRequest(
            new PathString("/commerce/manage/order"), "POST"));
    }
    [Theory]
    [InlineData("account", "Active", true, true)]
    [InlineData("owner", "Active", true, true)]
    [InlineData("platform-owner", "Active", false, true)]
    [InlineData("staff", "Active", true, false)]
    [InlineData("account", "Revoked", true, false)]
    [InlineData("owner", "Inactive", true, false)]
    public async Task PaymentsNavigationMatchesCanonicalSettingsAndDirectRoute(string role, string status, bool canTeam, bool expected)
    {
        await using var f = new NavigationFixture(role, status, canTeam);
        var canonical = await BusinessWorkspaceAccess.ResolveAsync(f.Db, f.Business.Id, f.Profile.Id, "actor", null, "settings", default);
        Assert.Equal(expected, canonical is not null);
        Assert.Equal(expected, BusinessWorkspaceAccess.Allows(f.Member, "settings"));
        Assert.Equal(expected && canTeam, BusinessWorkspaceAccess.Allows(f.Member, "team"));
        if (status == "Active")
            Assert.NotNull(await WebsiteTicketAuthorization.ResolveAsync(f.Db, f.Tickets, new ConfigurationBuilder().Build(), f.Ticket));
        f.Controller.HttpContext.Request.Path = "/commerce/manage/dashboard";
        var resolver = typeof(CommerceManagementController).GetMethod("ResolveAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var store = await (Task<CommerceStoreContext?>)resolver.Invoke(f.Controller, [f.Ticket, CancellationToken.None])!;
        Assert.Equal(status == "Active", store is not null);
        Assert.Equal(expected, f.Controller.ViewData["CommerceCanSettings"] as bool? ?? false);
        if (status == "Active")
        {
            Assert.True(f.Controller.ViewData["CommerceCanCatalog"] as bool?);
            Assert.Equal(expected && canTeam, f.Controller.ViewData["CommerceCanTeam"] as bool?);
        }
        f.Controller.HttpContext.Request.Path = "/commerce/manage/payments";
        var result = await f.Controller.Payments(f.Ticket);
        Assert.Equal(expected, result is ViewResult);
        if (!expected) Assert.IsType<UnauthorizedResult>(result);
    }

    [Theory]
    [InlineData("wrong-actor")]
    [InlineData("cross-tenant")]
    [InlineData("inactive-business")]
    [InlineData("storefront-revoked")]
    public async Task PaymentsTicketCannotBypassBusinessAndActorAuthorization(string denial)
    {
        await using var f = new NavigationFixture("account", "Active", true);
        if (denial == "inactive-business") f.Business.IsActive = false;
        if (denial == "storefront-revoked") f.Member.CanManageStorefront = false;
        var ticketBusinessId = f.Business.Id;
        if (denial == "cross-tenant")
        {
            var other = new CommerceBusiness { Key = "other-business", DisplayName = "Other", IsActive = true, Status = "Active" };
            var otherProfile = new ClientProfile { ClientUserId = "other-actor", Email = "other@example.invalid" };
            f.Db.CommerceBusinesses.Add(other); f.Db.ClientProfiles.Add(otherProfile);
            f.Db.CommerceBusinessMembers.Add(new() { CommerceBusinessId = other.Id, ClientProfileId = otherProfile.Id, RoleKey = "account", Status = "Active" });
            f.Db.Set<WebsiteContentState>().Add(new() { OwnerKey = WebsiteEditorSiteKeys.BusinessOwnerKey(other.Id), SiteKey = WebsiteEditorSiteKeys.Business,
                CommerceBusinessId = other.Id, DraftJson = JsonSerializer.Serialize(WebsiteContentSanitizer.Sanitize(new WebsiteContentDocument { Store = new() { Enabled = true } }), new JsonSerializerOptions(JsonSerializerDefaults.Web)) });
            ticketBusinessId = other.Id;
        }
        await f.Db.SaveChangesAsync();
        var ticket = f.Tickets.Protect(new(WebsiteEditorSiteKeys.Business, WebsiteEditorSiteKeys.BusinessOwnerKey(ticketBusinessId), null, false, DateTime.UtcNow.AddMinutes(5),
            ticketBusinessId,
            denial == "wrong-actor" ? "outsider" : "actor", null, f.Profile.Id));
        Assert.NotNull(f.Tickets.TryUnprotect(ticket));
        if (denial == "cross-tenant")
            Assert.Null(await BusinessWorkspaceAccess.ResolveAsync(f.Db, ticketBusinessId, f.Profile.Id, "actor", null, "settings", default));
        f.Controller.HttpContext.Request.Path = "/commerce/manage/payments";
        Assert.IsType<UnauthorizedResult>(await f.Controller.Payments(ticket));
        Assert.False(f.Controller.ViewData["CommerceCanSettings"] as bool? ?? false);
    }

    private sealed class NavigationFixture : IAsyncDisposable
    {
        public MasterAppDbContext Db = ControllerTestHelpers.BuildDb();
        public CommerceBusiness Business = new() { Key = "synthetic-business", DisplayName = "Synthetic", IsActive = true, Status = "Active" };
        public ClientProfile Profile = new() { ClientUserId = "actor", Email = "fixture@example.invalid" };
        public CommerceBusinessMember Member;
        public WebsiteEditorTicketProtector Tickets;
        public CommerceManagementController Controller;
        public string Ticket;
        private readonly ServiceProvider services;
        public NavigationFixture(string role, string status, bool canTeam)
        {
            Member = new() { CommerceBusinessId = Business.Id, ClientProfileId = Profile.Id, RoleKey = role, Status = status, CanManageStorefront = true, CanManageCatalog = true, CanManageTeam = canTeam };
            Db.CommerceBusinesses.Add(Business); Db.ClientProfiles.Add(Profile); Db.CommerceBusinessMembers.Add(Member);
            Db.Set<WebsiteContentState>().Add(new() { OwnerKey = WebsiteEditorSiteKeys.BusinessOwnerKey(Business.Id), SiteKey = WebsiteEditorSiteKeys.Business, CommerceBusinessId = Business.Id, DraftJson = JsonSerializer.Serialize(WebsiteContentSanitizer.Sanitize(new WebsiteContentDocument { Store = new() { Enabled = true } }), new JsonSerializerOptions(JsonSerializerDefaults.Web)) });
            Db.SaveChanges();
            var protection = new EphemeralDataProtectionProvider();
            var configuration = new ConfigurationBuilder().Build();
            Tickets = new(protection);
            Ticket = Tickets.Protect(new(WebsiteEditorSiteKeys.Business, WebsiteEditorSiteKeys.BusinessOwnerKey(Business.Id), null, false, DateTime.UtcNow.AddMinutes(5), Business.Id, "actor", null, Profile.Id));
            services = new ServiceCollection().AddSingleton(Db)
                .AddSingleton(new MerchantConnectionService(Db, protection, Array.Empty<IMerchantPaymentGateway>(), configuration)).BuildServiceProvider();
            var stores = new CommerceStoreContextService(Db, new CommerceBusinessScopeResolver(Db), null!, null!, configuration);
            Controller = new(Tickets, configuration, stores, null!, null!, null!, null!, null!, null!, null!, null!, null!)
            { ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { RequestServices = services } } };
            Controller.HttpContext.Request.Method = "GET";
            Controller.TempData = new TempDataDictionary(Controller.HttpContext, Mock.Of<ITempDataProvider>());
        }
        public async ValueTask DisposeAsync() { Tickets.Dispose(); await services.DisposeAsync(); await Db.DisposeAsync(); }
    }

}
