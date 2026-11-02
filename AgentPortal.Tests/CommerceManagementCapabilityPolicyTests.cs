using Microsoft.EntityFrameworkCore;
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
using ParfaitApp.Models;
using ParfaitApp.Services;
using Infrastructure.Businesses;
using Legend.Commerce;
using Microsoft.AspNetCore.Http;
using Xunit;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AgentPortal.Tests;

public sealed class CommerceManagementCapabilityPolicyTests
{
    [Fact]
    public async Task Global_store_entry_requires_exact_current_published_store_and_verified_domain()
    {
        await using var f = new NavigationFixture("owner", "Active", true);
        var state = f.Db.Set<WebsiteContentState>().Single();
        state.ProvisionedStoreBusinessId = f.Business.Id;
        var domain = new WebsiteDomainBinding { CommerceBusinessId = f.Business.Id, Hostname = "one.example.invalid",
            Status = "active", CertificateStatus = "active", LastCheckedUtc = DateTime.UtcNow };
        f.Db.Add(domain); await f.Db.SaveChangesAsync();
        Task<string?> Entry() => CommerceStoreContextService.PublishedManagementEntryAsync(f.Db, null, state.SiteKey, state.OwnerKey);
        Assert.Null(await Entry());
        var published = new WebsiteContentVersion { StateId = state.Id, DocumentJson = state.DraftJson };
        f.Db.Add(published); state.PublishedVersionId = published.Id; await f.Db.SaveChangesAsync();
        Assert.Equal("https://one.example.invalid/internal/login", await Entry());
        published.DocumentJson = "{}"; await f.Db.SaveChangesAsync(); Assert.Null(await Entry());
        published.DocumentJson = state.DraftJson; published.StateId = Guid.NewGuid(); await f.Db.SaveChangesAsync(); Assert.Null(await Entry());
        published.StateId = state.Id; published.SnapshotRetiredUtc = DateTime.UtcNow; await f.Db.SaveChangesAsync(); Assert.Null(await Entry());
        published.SnapshotRetiredUtc = null; domain.Status = "pending"; await f.Db.SaveChangesAsync(); Assert.Null(await Entry());
    }

    [Fact]
    public void Provider_continuations_reject_malformed_actor_expiry_and_purpose_confusion()
    {
        var provider = new EphemeralDataProtectionProvider(); using var tickets = new WebsiteEditorTicketProtector(provider);
        var id = Guid.NewGuid();
        var actor = new WebsiteEditorTicket(WebsiteEditorSiteKeys.Business, WebsiteEditorSiteKeys.BusinessOwnerKey(id), null,
            false, DateTime.UtcNow.AddMinutes(10), id, "actor", ActorClientProfileId: Guid.NewGuid());
        var context = new StoreProviderContinuation(actor, "https://one.example.invalid", "settings");
        var token = tickets.ProtectStoreProviderContinuation(context);
        Assert.NotNull(tickets.TryUnprotectStoreProviderContinuation(token));
        Assert.Null(tickets.TryUnprotectStoreProviderContinuation(tickets.Protect(actor)));
        foreach (var invalid in new[] { context with { Actor = null! }, context with { DestinationOrigin = "https://one.example.invalid/escape" },
            context with { Capability = "analytics" }, context with { Actor = actor with { ExpiresUtc = DateTime.UtcNow.AddMinutes(-1) } },
            context with { Actor = actor with { ExpiresUtc = DateTime.UtcNow.AddHours(1) } }, context with { Actor = actor with { CommerceBusinessId = Guid.Empty } } })
        {
            Assert.Throws<ArgumentException>(() => tickets.ProtectStoreProviderContinuation(invalid));
            var malformed = provider.CreateProtector("LEGEND.StoreManagement.ProviderContinuation.v1").Protect(JsonSerializer.Serialize(invalid, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
            Assert.Null(tickets.TryUnprotectStoreProviderContinuation(malformed));
        }
    }

    [Fact]
    public async Task Session_meta_callback_preserves_scope_without_returning_editor_tickets()
    {
        await using var f = new NavigationFixture("owner", "Active", true);
        f.Db.Set<WebsiteContentState>().Single().ProvisionedStoreBusinessId = f.Business.Id;
        f.Db.Add(new WebsiteDomainBinding { CommerceBusinessId = f.Business.Id, Hostname = "one.example.invalid",
            Status = "active", CertificateStatus = "active", LastCheckedUtc = DateTime.UtcNow });
        await f.Db.SaveChangesAsync();
        f.Controller.Request.Scheme = "https"; f.Controller.Request.Host = new HostString("one.example.invalid");
        f.Controller.Request.Path = "/internal/analytics/meta-connect";
        f.Controller.HttpContext.Items["LegendStoreSession"] = true;
        var connect = Assert.IsType<RedirectResult>(await f.Controller.MetaConnect(f.Ticket));
        var state = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(new Uri(connect.Url).Query)["state"].ToString();
        var inspected = f.Meta.InspectState(state);
        var context = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(new Uri("https://local" + inspected.ReturnUrl).Query)["storeContinuation"].ToString();
        Assert.NotEmpty(context); Assert.Null(f.Tickets.TryUnprotect(context)); Assert.Null(f.Tickets.TryUnprotectStoreLoginRequest(context));
        Assert.NotNull(f.Tickets.TryUnprotectStoreProviderContinuation(context));
        f.Controller.Request.Path = "/commerce/manage/analytics/meta-callback";
        f.Controller.HttpContext.Items.Clear();
        var returned = Assert.IsType<RedirectResult>(await f.Controller.MetaCallback(state: state, error: "access_denied"));
        Assert.StartsWith("https://one.example.invalid/internal/analytics?", returned.Url);
        Assert.DoesNotContain("ticket", returned.Url); Assert.DoesNotContain("storeContinuation", returned.Url);
        f.Member.RoleKey = "staff"; f.Member.CanManageAnalytics = true; await f.Db.SaveChangesAsync();
        Assert.True(BusinessWorkspaceAccess.Allows(f.Member, "analytics"));
        Assert.False(BusinessWorkspaceAccess.Allows(f.Member, "settings"));
        Assert.IsType<UnauthorizedResult>(await f.Controller.MetaCallback(state: state, error: "access_denied"));
    }

    [Fact]
    public async Task Normal_login_redeems_one_use_handoff_into_host_cookie_and_rechecks_revocation()
    {
        await using var f = new NavigationFixture("owner", "Active", true, relational: true);
        f.Db.Set<WebsiteContentState>().Single().ProvisionedStoreBusinessId = f.Business.Id;
        f.Db.Add(new WebsiteDomainBinding { CommerceBusinessId = f.Business.Id, Hostname = "one.example.invalid",
            Status = "active", CertificateStatus = "active", LastCheckedUtc = DateTime.UtcNow });
        await f.Db.SaveChangesAsync();
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton(f.Db);
        builder.Services.AddSingleton(f.Stores); builder.Services.AddSingleton(f.Tickets);
        builder.Services.AddSingleton(new BusinessWorkspaceService(f.Db, Mock.Of<Infrastructure.Analytics.IAnalyticsQueryService>()));
        builder.Services.AddSingleton(Mock.Of<Infrastructure.Identity.IClientEntraLifecycleService>());
        builder.Services.AddDataProtection().UseEphemeralDataProtectionProvider();
        builder.Services.AddLegendCommerceSessions();
        builder.Services.AddControllersWithViews().AddApplicationPart(Assembly.Load("ProtectWebsite")).AddLegendCommerceManagementMvc().AddControllersAsServices();
        builder.Services.Replace(ServiceDescriptor.Transient<CommerceManagementController>(_ => f.Controller));
        await using var app = builder.Build();
        app.UseAuthentication(); app.UseAuthorization(); app.MapControllers();
        app.MapGet("/test-token", (Microsoft.AspNetCore.Antiforgery.IAntiforgery af, HttpContext c) => af.GetAndStoreTokens(c).RequestToken!);
        await app.StartAsync();
        using var client = app.GetTestClient(); client.BaseAddress = new Uri("https://one.example.invalid");
        var unauthenticated = await client.GetAsync("/internal");
        Assert.Equal(System.Net.HttpStatusCode.Redirect, unauthenticated.StatusCode);
        Assert.StartsWith("/internal/login?", unauthenticated.Headers.Location!.OriginalString);
        var tokenResponse = await client.GetAsync("/internal/login");
        Assert.Equal(System.Net.HttpStatusCode.OK, tokenResponse.StatusCode);
        var loginHtml = await tokenResponse.Content.ReadAsStringAsync();
        var token = System.Text.RegularExpressions.Regex.Match(loginHtml, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value;
        Assert.NotEmpty(token);
        var afCookie = string.Join("; ", tokenResponse.Headers.GetValues("Set-Cookie").Select(x => x.Split(';')[0]));
        client.DefaultRequestHeaders.Add("Cookie", afCookie);
        System.Net.Http.FormUrlEncodedContent LoginBody(string? state = null) => new(new System.Collections.Generic.Dictionary<string,string> {
            ["__RequestVerificationToken"] = token, ["state"] = state ?? "", ["returnPath"] = "/internal" });
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, (await client.PostAsync("/internal/login", new System.Net.Http.StringContent(""))).StatusCode);
        var login = await client.PostAsync("/internal/login", LoginBody());
        Assert.Equal(System.Net.HttpStatusCode.Redirect, login.StatusCode);
        Assert.Equal("portal.mylegnd.com", login.Headers.Location!.Host);
        var query = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(login.Headers.Location.Query);
        var callbackHttp = new DefaultHttpContext { RequestServices = app.Services };
        var properties = new Microsoft.AspNetCore.Authentication.AuthenticationProperties();
        properties.Items["legend.store.request"] = query["request"].ToString();
        var principal = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity(new[] {
            new System.Security.Claims.Claim("oid", "actor") }, "synthetic-validated-provider"));
        var callback = new Microsoft.AspNetCore.Authentication.TicketReceivedContext(callbackHttp,
            new Microsoft.AspNetCore.Authentication.AuthenticationScheme("OpenIdConnect", null, typeof(Microsoft.AspNetCore.Authentication.OpenIdConnect.OpenIdConnectHandler)),
            new Microsoft.AspNetCore.Authentication.OpenIdConnect.OpenIdConnectOptions(),
            new Microsoft.AspNetCore.Authentication.AuthenticationTicket(principal, properties, "OpenIdConnect"));
        var portalSignInCalled = false;
        await AccountController.StoreTicketHandler(_ => { portalSignInCalled = true; return Task.CompletedTask; })(callback);
        Assert.True(callback.Result!.Handled); Assert.False(portalSignInCalled);
        Assert.Equal(302, callbackHttp.Response.StatusCode);
        Assert.False(callbackHttp.Response.Headers.ContainsKey("Set-Cookie"));
        var callbackTarget = new Uri(callbackHttp.Response.Headers.Location.ToString());
        Assert.Equal("one.example.invalid", callbackTarget.Host);
        var opaqueState = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(callbackTarget.Query)["state"].ToString();
        client.DefaultRequestHeaders.Remove("Cookie");
        client.DefaultRequestHeaders.Add("Cookie", afCookie + "; " + string.Join("; ", login.Headers.GetValues("Set-Cookie").Select(x => x.Split(';')[0])));
        var prompt = await client.GetAsync(callbackTarget.PathAndQuery);
        Assert.Equal(System.Net.HttpStatusCode.OK, prompt.StatusCode);
        Assert.Contains("__RequestVerificationToken", await prompt.Content.ReadAsStringAsync());
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, (await client.PostAsync("/internal/login/complete", new System.Net.Http.FormUrlEncodedContent(new System.Collections.Generic.Dictionary<string,string> { ["state"] = opaqueState }))).StatusCode);
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, (await client.PostAsync("/internal/login/complete", new System.Net.Http.FormUrlEncodedContent(new System.Collections.Generic.Dictionary<string,string> { ["state"] = opaqueState, ["__RequestVerificationToken"] = "forged" }))).StatusCode);
        var completion = await client.PostAsync("/internal/login/complete", LoginBody(opaqueState));
        Assert.Equal(System.Net.HttpStatusCode.Redirect, completion.StatusCode);
        Assert.Equal("/internal/dashboard", completion.Headers.Location!.OriginalString);
        Assert.DoesNotContain("ticket", completion.Headers.Location.OriginalString);
        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, (await client.PostAsync("/internal/login/complete", LoginBody(opaqueState))).StatusCode);
        client.DefaultRequestHeaders.Remove("Cookie");
        var session = Assert.Single(completion.Headers.GetValues("Set-Cookie").Where(x => x.StartsWith("__Host-LEGEND.StoreSession=")));
        Assert.Contains("secure", session, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("httponly", session, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("domain=", session, StringComparison.OrdinalIgnoreCase);
        client.DefaultRequestHeaders.Add("Cookie", session.Split(';')[0]);
        var workspace = await client.GetAsync("/internal");
        Assert.Equal(System.Net.HttpStatusCode.Redirect, workspace.StatusCode);
        Assert.Equal("/internal/dashboard", workspace.Headers.Location!.OriginalString);
        var otherBusiness = new CommerceBusiness { Key = "disjoint", DisplayName = "Disjoint" };
        f.Db.Add(otherBusiness);
        f.Db.AddRange(new CommerceProduct { CommerceBusinessId = f.Business.Id, ExternalProductKey = "own", Name = "Own live catalog item", Slug = "own-item", PriceCents = 8900 },
            new CommerceProduct { CommerceBusinessId = otherBusiness.Id, ExternalProductKey = "other", Name = "Other tenant private item", Slug = "other-item", PriceCents = 100 });
        await f.Db.SaveChangesAsync();
        var catalogResponse = await client.GetAsync("/internal/preview?catalogOnly=true");
        Assert.Equal(System.Net.HttpStatusCode.OK, catalogResponse.StatusCode);
        var catalogHtml = await catalogResponse.Content.ReadAsStringAsync();
        Assert.Contains("Own live catalog item", catalogHtml);
        Assert.DoesNotContain("Other tenant private item", catalogHtml);
        Assert.Contains("/internal/preview/product/own-item", catalogHtml);
        Assert.Contains("data-cms-store-style=\"title\"", catalogHtml);
        Assert.Empty(await f.Db.AnalyticsEvents.ToListAsync());

        var authenticatedToken = await client.GetAsync("/test-token");
        token = await authenticatedToken.Content.ReadAsStringAsync();
        client.DefaultRequestHeaders.Remove("Cookie");
        client.DefaultRequestHeaders.Add("Cookie", session.Split(';')[0] + "; " + string.Join("; ", authenticatedToken.Headers.GetValues("Set-Cookie").Select(x => x.Split(';')[0])));
        System.Net.Http.FormUrlEncodedContent Profile(string name) => new(new System.Collections.Generic.Dictionary<string,string> {
            ["__RequestVerificationToken"] = token, ["Name"] = name, ["BusinessType"] = "Retail" });
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, (await client.PostAsync("/internal/settings/business-profile", Profile(""))).StatusCode);
        var saved = await client.PostAsync("/internal/settings/business-profile", Profile("Synthetic store"));
        Assert.Equal(System.Net.HttpStatusCode.Redirect, saved.StatusCode);
        Assert.Equal("/internal/settings/business-profile", saved.Headers.Location!.OriginalString);
        Assert.Equal("Synthetic store", f.Business.DisplayName);
        var staffId = Guid.NewGuid().ToString("D");
        var staff = new ClientProfile { ClientUserId = staffId, ExternalIdentityObjectId = staffId, Email = "http-staff@example.invalid", NormalizedEmail = "http-staff@example.invalid" };
        f.Db.Add(staff); await f.Db.SaveChangesAsync();
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, (await client.PostAsync("/internal/settings/team/add", new System.Net.Http.StringContent(""))).StatusCode);
        var invited = await client.PostAsync("/internal/settings/team/add", new System.Net.Http.FormUrlEncodedContent(new System.Collections.Generic.Dictionary<string,string> {
            ["__RequestVerificationToken"] = token, ["firstName"] = "Synthetic", ["lastName"] = "Staff", ["email"] = staff.Email }));
        Assert.Equal(System.Net.HttpStatusCode.OK, invited.StatusCode);
        var receiptHtml = await invited.Content.ReadAsStringAsync();
        Assert.Contains("Invitation created", receiptHtml);
        var invitationUrl = System.Net.WebUtility.HtmlDecode(System.Text.RegularExpressions.Regex.Match(receiptHtml, "value=\"(https://[^\"]+/Account/BusinessInvitation[^\"]+)\"").Groups[1].Value);
        Assert.NotEmpty(invitationUrl);
        var invitationProperties = new Microsoft.AspNetCore.Authentication.AuthenticationProperties();
        invitationProperties.Items["legend.business.invitation"] = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(new Uri(invitationUrl).Query)["token"].ToString();
        var invitationHttp = new DefaultHttpContext { RequestServices = app.Services };
        var invitationCallback = new Microsoft.AspNetCore.Authentication.TicketReceivedContext(invitationHttp, callback.Scheme,
            new Microsoft.AspNetCore.Authentication.OpenIdConnect.OpenIdConnectOptions(),
            new Microsoft.AspNetCore.Authentication.AuthenticationTicket(new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity(new[] {
                new System.Security.Claims.Claim("oid", staffId) }, "synthetic-validated-provider")), invitationProperties, "OpenIdConnect"));
        await AccountController.StoreTicketHandler(_ => { portalSignInCalled = true; return Task.CompletedTask; })(invitationCallback);
        Assert.True(invitationCallback.Result!.Handled); Assert.False(portalSignInCalled);
        Assert.False(invitationHttp.Response.Headers.ContainsKey("Set-Cookie"));
        Assert.Equal("https://one.example.invalid/internal/login", invitationHttp.Response.Headers.Location.ToString());
        var staffMember = await f.Db.CommerceBusinessMembers.AsNoTracking().SingleAsync(m => m.ClientProfileId == staff.Id);
        Assert.Equal("Active", staffMember.Status);
        var teamPage = await client.GetAsync("/internal/settings/team");
        Assert.Equal(System.Net.HttpStatusCode.OK, teamPage.StatusCode);
        Assert.Contains("http-staff@example.invalid", await teamPage.Content.ReadAsStringAsync());
        var removed = await client.PostAsync("/internal/settings/team/remove", new System.Net.Http.FormUrlEncodedContent(new System.Collections.Generic.Dictionary<string,string> {
            ["__RequestVerificationToken"] = token, ["memberId"] = staffMember.Id.ToString(), ["expectedUpdatedTicks"] = staffMember.UpdatedUtc.Ticks.ToString() }));
        Assert.Equal(System.Net.HttpStatusCode.Redirect, removed.StatusCode);
        Assert.Equal("Revoked", await f.Db.CommerceBusinessMembers.Where(m => m.Id == staffMember.Id).Select(m => m.Status).SingleAsync());

        f.Member.Status = "Revoked"; await f.Db.SaveChangesAsync();
        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, (await client.GetAsync("/internal")).StatusCode);
        var deniedHttp = new DefaultHttpContext { RequestServices = app.Services };
        var deniedCallback = new Microsoft.AspNetCore.Authentication.TicketReceivedContext(deniedHttp, callback.Scheme,
            new Microsoft.AspNetCore.Authentication.OpenIdConnect.OpenIdConnectOptions(),
            new Microsoft.AspNetCore.Authentication.AuthenticationTicket(principal, properties, "OpenIdConnect"));
        await AccountController.StoreTicketHandler(_ => { portalSignInCalled = true; return Task.CompletedTask; })(deniedCallback);
        Assert.True(deniedCallback.Result!.Handled); Assert.False(portalSignInCalled);
        Assert.Equal(403, deniedHttp.Response.StatusCode); Assert.False(deniedHttp.Response.Headers.ContainsKey("Set-Cookie"));
    }

    [Fact]
    public async Task Internal_write_executes_Mvc_antiforgery_and_persists_only_valid_scoped_request()
    {
        await using var f = new NavigationFixture("owner", "Active", true);
        f.Db.Set<WebsiteContentState>().Single().ProvisionedStoreBusinessId = f.Business.Id;
        f.Db.Add(new WebsiteDomainBinding { CommerceBusinessId = f.Business.Id, Hostname = "one.example.invalid",
            Status = "active", CertificateStatus = "active", LastCheckedUtc = DateTime.UtcNow });
        var target = new CommerceBusinessMember { CommerceBusinessId = f.Business.Id, RoleKey = "staff", CanManageAnalytics = false };
        f.Db.Add(target);
        await f.Db.SaveChangesAsync();
        var builder = Microsoft.AspNetCore.Builder.WebApplication.CreateBuilder(new Microsoft.AspNetCore.Builder.WebApplicationOptions { EnvironmentName = "Testing" });
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton(f.Db);
        builder.Services.AddControllersWithViews().AddLegendCommerceManagementMvc().AddControllersAsServices();
        builder.Services.Replace(ServiceDescriptor.Transient<CommerceManagementController>(_ => f.Controller));
        await using var app = builder.Build();
        app.MapGet("/test-token", (Microsoft.AspNetCore.Antiforgery.IAntiforgery antiforgery, HttpContext context) => antiforgery.GetAndStoreTokens(context).RequestToken!);
        app.MapControllers();
        await app.StartAsync();
        using var client = app.GetTestClient();
        client.BaseAddress = new Uri("https://one.example.invalid");
        var url = "/internal/settings/team/permissions?ticket=" + Uri.EscapeDataString(f.Ticket);
        System.Net.Http.FormUrlEncodedContent Body(string? token = null)
        {
            var values = new System.Collections.Generic.Dictionary<string, string> {
                ["MemberId"] = target.Id.ToString(), ["ExpectedUpdatedTicks"] = target.UpdatedUtc.Ticks.ToString(), ["CanManageAnalytics"] = "true" };
            if (token is not null) values["__RequestVerificationToken"] = token;
            return new(values);
        }
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, (await client.PostAsync(url, Body())).StatusCode);
        Assert.False(target.CanManageAnalytics);
        foreach (var route in CommerceManagementCapabilityPolicy.InternalRoutes.Where(r => r.Method == "POST"))
            Assert.Equal(System.Net.HttpStatusCode.BadRequest, (await client.PostAsync(
                route.InternalPath + "?ticket=" + Uri.EscapeDataString(f.Ticket), Body())).StatusCode);
        Assert.False(target.CanManageAnalytics);
        var tokenResponse = await client.GetAsync("/test-token");
        var token = await tokenResponse.Content.ReadAsStringAsync();
        client.DefaultRequestHeaders.Add("Cookie", string.Join("; ", tokenResponse.Headers.GetValues("Set-Cookie").Select(x => x.Split(';')[0])));
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, (await client.PostAsync(url, Body("forged"))).StatusCode);
        Assert.False(target.CanManageAnalytics);
        var result = await client.PostAsync(url, Body(token));
        Assert.Equal(System.Net.HttpStatusCode.Redirect, result.StatusCode);
        Assert.StartsWith("/internal/settings/team?", result.Headers.Location!.OriginalString);
        Assert.True(target.CanManageAnalytics);
    }

    [Fact]
    public async Task Internal_team_reads_only_bound_business_and_rechecks_host_and_revocation()
    {
        await using var f = new NavigationFixture("owner", "Active", true);
        var state = f.Db.Set<WebsiteContentState>().Single();
        state.ProvisionedStoreBusinessId = f.Business.Id;
        var other = new CommerceBusiness { Key = "other-store", IsActive = true, Status = "Active" };
        f.Db.Add(other);
        f.Db.Add(new WebsiteDomainBinding { CommerceBusinessId = f.Business.Id, Hostname = "one.example.invalid",
            Status = "active", CertificateStatus = "active", LastCheckedUtc = DateTime.UtcNow });
        f.Db.Add(new WebsiteDomainBinding { CommerceBusinessId = other.Id, Hostname = "two.example.invalid",
            Status = "active", CertificateStatus = "active", LastCheckedUtc = DateTime.UtcNow });
        f.Db.Add(new WebsiteContentState { CommerceBusinessId = other.Id, ProvisionedStoreBusinessId = other.Id,
            SiteKey = WebsiteEditorSiteKeys.Business, OwnerKey = WebsiteEditorSiteKeys.BusinessOwnerKey(other.Id) });
        await f.Db.SaveChangesAsync();
        f.Controller.Request.Scheme = "https";
        f.Controller.Request.Host = new HostString("one.example.invalid");
        f.Controller.Request.Path = "/internal/settings/team";
        var view = Assert.IsType<ViewResult>(await f.Controller.Team(f.Ticket));
        var model = Assert.IsType<CommerceBusinessTeamViewModel>(view.Model);
        Assert.Equal(f.Business.Id, model.BusinessId);
        state.DraftJson = JsonSerializer.Serialize(WebsiteContentSanitizer.Sanitize(new WebsiteContentDocument
            { Store = new() { Enabled = false } }), new JsonSerializerOptions(JsonSerializerDefaults.Web));
        await f.Db.SaveChangesAsync();
        Assert.IsType<ViewResult>(await f.Controller.Team(f.Ticket));
        f.Controller.Request.Host = new HostString("two.example.invalid");
        Assert.IsType<UnauthorizedResult>(await f.Controller.Team(f.Ticket));
        f.Controller.Request.Host = new HostString("one.example.invalid");
        f.Member.Status = "Revoked";
        await f.Db.SaveChangesAsync();
        Assert.IsType<UnauthorizedResult>(await f.Controller.Team(f.Ticket));
    }

    [Theory]
    [InlineData("dashboard", "dashboard", "GET")]
    [InlineData("settings/business-profile", "business-profile", "GET")]
    [InlineData("settings/business-profile", "business-profile", "POST")]
    [InlineData("settings/team", "team", "GET")]
    [InlineData("settings/team/add", "team/add", "POST")]
    [InlineData("settings/team/remove", "team/remove", "POST")]
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
    [InlineData("/internal/settings/team/add", "GET")]
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

    [Theory]
    [InlineData("staff", "Active", true, false, false)]
    [InlineData("staff", "Active", false, true, true)]
    [InlineData("owner", "Active", true, false, false)]
    [InlineData("account", "Active", false, true, true)]
    [InlineData("staff", "Inactive", true, true, false)]
    [InlineData("staff", "Revoked", true, true, false)]
    public async Task Crm_authority_is_independent_of_orders_and_role(string role, string status,
        bool orders, bool crm, bool allowed)
    {
        await using var f = new NavigationFixture(role, status, true);
        f.Member.CanManageOrders = orders;
        f.Member.CanManageCrm = crm;
        await f.Db.SaveChangesAsync();
        Assert.Equal(allowed, await BusinessWorkspaceAccess.ResolveAsync(f.Db, f.Business.Id,
            f.Profile.Id, "actor", null, "crm", default) is not null);
        var navigation = await new BusinessWorkspaceService(f.Db, null!).NavigationAsync(f.Profile.Id, "actor", null, default);
        Assert.Equal(allowed, navigation.Any(x => x.CanCrm));
        Assert.Null(await BusinessWorkspaceAccess.ResolveAsync(f.Db, Guid.NewGuid(), f.Profile.Id, "actor", null, "crm", default));
        Assert.Null(await BusinessWorkspaceAccess.ResolveAsync(f.Db, f.Business.Id, f.Profile.Id, "outsider", null, "crm", default));
        f.Member.CanManageOrders = !orders;
        await f.Db.SaveChangesAsync();
        Assert.Equal(allowed, BusinessWorkspaceAccess.Allows(f.Member, "crm"));
    }

    [Theory]
    [InlineData("valid")]
    [InlineData("stale")]
    [InlineData("foreign")]
    public async Task Team_update_preserves_independent_crm_permission_and_scope(string scenario)
    {
        await using var f = new NavigationFixture("owner", "Active", true);
        var member = new CommerceBusinessMember { CommerceBusinessId = scenario == "foreign" ? Guid.NewGuid() : f.Business.Id,
            RoleKey = "staff", CanManageCrm = false, UpdatedUtc = DateTime.UtcNow };
        f.Db.CommerceBusinessMembers.Add(member);
        await f.Db.SaveChangesAsync();
        f.Controller.Request.Method = "POST";
        f.Controller.Request.Path = "/commerce/manage/team/permissions";
        var result = await f.Controller.SaveTeamPermissions(f.Ticket, new CommerceBusinessTeamPermissionsInput
        {
            MemberId = member.Id, ExpectedUpdatedTicks = scenario == "stale" ? member.UpdatedUtc.Ticks - 1 : member.UpdatedUtc.Ticks,
            CanManageCrm = true, CanManageOrders = false, CanManageStorefront = false
        });
        if (scenario == "valid")
        {
            Assert.IsType<RedirectToActionResult>(result);
            Assert.True(member.CanManageCrm);
            Assert.False(member.CanManageOrders);
            Assert.False(member.CanManageStorefront);
        }
        else
        {
            if (scenario == "stale") Assert.IsType<ConflictObjectResult>(result);
            else Assert.IsType<NotFoundResult>(result);
            Assert.False(member.CanManageCrm);
        }
    }

    [Theory]
    [InlineData(true, null, true)]
    [InlineData(false, null, false)]
    [InlineData(true, false, false)]
    public async Task Existing_team_clients_preserve_crm_unless_explicitly_changed(bool existing, bool? submitted, bool expected)
    {
        await using var f = new NavigationFixture("owner", "Active", true);
        var member = new CommerceBusinessMember { CommerceBusinessId = f.Business.Id,
            RoleKey = "staff", CanManageCrm = existing, UpdatedUtc = DateTime.UtcNow };
        f.Db.Add(member);
        await f.Db.SaveChangesAsync();
        f.Controller.Request.Method = "POST";
        f.Controller.Request.Path = "/commerce/manage/team/permissions";
        Assert.IsType<RedirectToActionResult>(await f.Controller.SaveTeamPermissions(f.Ticket,
            new CommerceBusinessTeamPermissionsInput { MemberId = member.Id,
                ExpectedUpdatedTicks = member.UpdatedUtc.Ticks, CanManageCrm = submitted,
                CanManageStorefront = true, CanManageOrders = true }));
        Assert.Equal(expected, member.CanManageCrm);
        Assert.True(member.CanManageOrders);
    }

    [Fact]
    public async Task Account_owner_cannot_regrant_its_protected_membership_crm()
    {
        await using var f = new NavigationFixture("account", "Active", true);
        f.Member.CanManageCrm = false;
        await f.Db.SaveChangesAsync();
        f.Controller.Request.Method = "POST";
        f.Controller.Request.Path = "/commerce/manage/team/permissions";
        Assert.IsType<ForbidResult>(await f.Controller.SaveTeamPermissions(f.Ticket,
            new CommerceBusinessTeamPermissionsInput { MemberId = f.Member.Id,
                ExpectedUpdatedTicks = f.Member.UpdatedUtc.Ticks, CanManageCrm = true,
                CanManageStorefront = true, CanManageOrders = true }));
        Assert.False(f.Member.CanManageCrm);
    }

    [Fact]
    public async Task MembershipInvitationRequiresExactIdentityAndRevocationInvalidatesAcceptance()
    {
        await using var f = new NavigationFixture("owner", "Active", true, relational: true);
        var state = await f.Db.Set<WebsiteContentState>().SingleAsync();
        state.ProvisionedStoreBusinessId = f.Business.Id;
        f.Db.Add(new WebsiteDomainBinding { CommerceBusinessId = f.Business.Id, Hostname = "store.example.invalid",
            Status = "active", CertificateStatus = "active", LastCheckedUtc = DateTime.UtcNow });
        var invitee = new ClientProfile { ClientUserId = "f1cb63b0-2ddd-48f7-8e35-253a624eab19", ExternalIdentityObjectId = "f1cb63b0-2ddd-48f7-8e35-253a624eab19",
            Email = "staff@example.invalid", NormalizedEmail = "staff@example.invalid" };
        f.Db.Add(invitee); await f.Db.SaveChangesAsync();
        var scope = new CommerceManagementScope(f.Business.Id, f.Business.Key, state.Id, state.SiteKey, state.OwnerKey, "https://store.example.invalid");
        var workspace = new BusinessWorkspaceService(f.Db, Mock.Of<Infrastructure.Analytics.IAnalyticsQueryService>());
        var identities = new Mock<Infrastructure.Identity.IClientEntraLifecycleService>(MockBehavior.Strict);
        var invitation = await workspace.InviteMemberAsync(f.Stores, scope, "actor", "Test", "Staff", invitee.Email, identities.Object, default);
        var member = await f.Db.CommerceBusinessMembers.SingleAsync(m => m.ClientProfileId == invitee.Id);
        Assert.Equal("Pending", member.Status);
        Assert.False(member.CanManageCrm); Assert.False(member.CanManageOrders); Assert.False(member.CanManageTeam);
        Assert.Null(await f.Stores.ResolveManagementActorAsync(scope, "f1cb63b0-2ddd-48f7-8e35-253a624eab19", "orders"));
        Assert.Null(await workspace.AcceptMemberInvitationAsync(invitation.Token, "outsider", f.Stores, default));
        var replacement = await workspace.InviteMemberAsync(f.Stores, scope, "actor", "Test", "Staff", invitee.Email, identities.Object, default);
        Assert.Null(await workspace.AcceptMemberInvitationAsync(invitation.Token, "f1cb63b0-2ddd-48f7-8e35-253a624eab19", f.Stores, default));
        Assert.Equal("https://store.example.invalid/internal/login", await workspace.AcceptMemberInvitationAsync(replacement.Token, "f1cb63b0-2ddd-48f7-8e35-253a624eab19", f.Stores, default));
        Assert.Null(await workspace.AcceptMemberInvitationAsync(replacement.Token, "f1cb63b0-2ddd-48f7-8e35-253a624eab19", f.Stores, default));
        await f.Db.Entry(member).ReloadAsync();
        Assert.Equal("Active", member.Status);
        member.CanManageStorefront = true; member.CanManageOrders = true; await f.Db.SaveChangesAsync();
        Assert.NotNull(await f.Stores.ResolveManagementActorAsync(scope, "f1cb63b0-2ddd-48f7-8e35-253a624eab19", "orders"));
        Assert.Null(await f.Stores.ResolveManagementActorAsync(scope, "f1cb63b0-2ddd-48f7-8e35-253a624eab19", "crm"));
        Assert.True(await workspace.RemoveMemberAsync(f.Stores, scope, "actor", member.Id, member.UpdatedUtc.Ticks, default));
        Assert.Null(await f.Stores.ResolveManagementActorAsync(scope, "f1cb63b0-2ddd-48f7-8e35-253a624eab19", "orders"));
        await f.Db.Entry(member).ReloadAsync();
        var revoked = await workspace.InviteMemberAsync(f.Stores, scope, "actor", "Test", "Staff", invitee.Email, identities.Object, default);
        Assert.True(await workspace.RemoveMemberAsync(f.Stores, scope, "actor", member.Id, member.UpdatedUtc.Ticks, default));
        Assert.Null(await workspace.AcceptMemberInvitationAsync(revoked.Token, "f1cb63b0-2ddd-48f7-8e35-253a624eab19", f.Stores, default));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => workspace.RemoveMemberAsync(f.Stores, scope, "outsider", f.Member.Id, f.Member.UpdatedUtc.Ticks, default));
        identities.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("expired")]
    [InlineData("inviter-revoked")]
    [InlineData("domain-revoked")]
    [InlineData("identity-conflict")]
    [InlineData("wrong-purpose")]
    public async Task MembershipAcceptanceRechecksCurrentAuthority(string change)
    {
        await using var f = new NavigationFixture("owner", "Active", true, relational: true);
        var state = await f.Db.Set<WebsiteContentState>().SingleAsync();
        state.ProvisionedStoreBusinessId = f.Business.Id;
        var domain = new WebsiteDomainBinding { CommerceBusinessId = f.Business.Id, Hostname = "store.example.invalid",
            Status = "active", CertificateStatus = "active", LastCheckedUtc = DateTime.UtcNow };
        var invitee = new ClientProfile { ClientUserId = "e26d10d7-1e4d-46cf-a536-967c281bdd2e", Email = "invited@example.invalid" };
        f.Db.Add(domain); f.Db.Add(invitee); await f.Db.SaveChangesAsync();
        var scope = new CommerceManagementScope(f.Business.Id, f.Business.Key, state.Id, state.SiteKey, state.OwnerKey, "https://store.example.invalid");
        var workspace = new BusinessWorkspaceService(f.Db, Mock.Of<Infrastructure.Analytics.IAnalyticsQueryService>());
        var invitation = await workspace.InviteMemberAsync(f.Stores, scope, "actor", "Test", "Invitee", invitee.Email,
            Mock.Of<Infrastructure.Identity.IClientEntraLifecycleService>(), default);
        var receipt = await f.Db.ClientIdentityContinuations.SingleAsync();
        switch (change)
        {
            case "expired": receipt.ExpiresUtc = DateTime.UtcNow.AddSeconds(-1); break;
            case "inviter-revoked": f.Member.Status = "Revoked"; break;
            case "domain-revoked": domain.CertificateStatus = "pending"; break;
            case "identity-conflict": f.Db.Add(new ClientProfile { ClientUserId = "another-stable-subject", ExternalIdentityObjectId = invitee.ClientUserId, Email = "conflict@example.invalid" }); break;
            case "wrong-purpose": receipt.Purpose = Domain.Billing.ClientIdentityContinuationPurpose.SignIn; break;
        }
        await f.Db.SaveChangesAsync();
        Assert.Null(await workspace.AcceptMemberInvitationAsync(invitation.Token, invitee.ClientUserId, f.Stores, default));
        Assert.Equal("Pending", await f.Db.CommerceBusinessMembers.Where(m => m.ClientProfileId == invitee.Id).Select(m => m.Status).SingleAsync());
    }

    private sealed class NavigationFixture : IAsyncDisposable
    {
        public MasterAppDbContext Db = ControllerTestHelpers.BuildDb();
        public CommerceBusiness Business = new() { Key = "synthetic-business", DisplayName = "Synthetic", IsActive = true, Status = "Active" };
        public ClientProfile Profile = new() { ClientUserId = "actor", Email = "fixture@example.invalid" };
        public CommerceBusinessMember Member;
        public WebsiteEditorTicketProtector Tickets;
        public CommerceManagementController Controller;
        public CommerceStoreContextService Stores;
        public Infrastructure.Analytics.MarketingMetaAdsOAuthService Meta;
        public string Ticket;
        private readonly ServiceProvider services;
        private Microsoft.Data.Sqlite.SqliteConnection? connection;
        public NavigationFixture(string role, string status, bool canTeam, bool relational = false)
        {
            if (relational)
            {
                Db.Dispose();
                connection = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=:memory:"); connection.Open();
                Db = new MasterAppDbContext(Microsoft.EntityFrameworkCore.SqliteDbContextOptionsBuilderExtensions.UseSqlite(new Microsoft.EntityFrameworkCore.DbContextOptionsBuilder<MasterAppDbContext>(), connection).Options);
                Db.Database.EnsureCreated();
            }
            Member = new() { CommerceBusinessId = Business.Id, ClientProfileId = Profile.Id, RoleKey = role, Status = status, CanManageStorefront = true, CanManageCatalog = true, CanManageTeam = canTeam };
            Db.CommerceBusinesses.Add(Business); Db.ClientProfiles.Add(Profile); Db.CommerceBusinessMembers.Add(Member);
            Db.CommerceBusinessStorefrontSettings.Add(new() { CommerceBusinessId = Business.Id });
            Db.Set<WebsiteContentState>().Add(new() { OwnerKey = WebsiteEditorSiteKeys.BusinessOwnerKey(Business.Id), SiteKey = WebsiteEditorSiteKeys.Business, CommerceBusinessId = Business.Id, DraftJson = JsonSerializer.Serialize(WebsiteContentSanitizer.Sanitize(new WebsiteContentDocument { Store = new() { Enabled = true } }), new JsonSerializerOptions(JsonSerializerDefaults.Web)) });
            Db.SaveChanges();
            var protection = new EphemeralDataProtectionProvider();
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new System.Collections.Generic.Dictionary<string,string?> { ["MetaAds:AppId"] = "synthetic-app" }).Build();
            Tickets = new(protection);
            Ticket = Tickets.Protect(new(WebsiteEditorSiteKeys.Business, WebsiteEditorSiteKeys.BusinessOwnerKey(Business.Id), null, false, DateTime.UtcNow.AddMinutes(5), Business.Id, "actor", null, Profile.Id));
            services = new ServiceCollection().AddSingleton(Db)
                .AddSingleton(new MerchantConnectionService(Db, protection, Array.Empty<IMerchantPaymentGateway>(), configuration)).BuildServiceProvider();
            var stores = Stores = new CommerceStoreContextService(Db, new CommerceBusinessScopeResolver(Db), null!, new WebsiteDomainService(Db, Mock.Of<System.Net.Http.IHttpClientFactory>(), configuration), configuration);
            Meta = new(configuration, Mock.Of<System.Net.Http.IHttpClientFactory>(), protection, Microsoft.Extensions.Logging.Abstractions.NullLogger<Infrastructure.Analytics.MarketingMetaAdsOAuthService>.Instance);
            Controller = new(Tickets, configuration, stores, new ParfaitProductService(null!, Db), null!, null!, null!, null!, null!, Meta, null!, null!)
            { ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { RequestServices = services } } };
            Controller.Url = Mock.Of<IUrlHelper>();
            Controller.HttpContext.Request.Method = "GET";
            Controller.TempData = new TempDataDictionary(Controller.HttpContext, Mock.Of<ITempDataProvider>());
        }
        public async ValueTask DisposeAsync() { Tickets.Dispose(); await services.DisposeAsync(); await Db.DisposeAsync(); if (connection is not null) await connection.DisposeAsync(); }
    }

}
