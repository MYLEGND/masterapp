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

// These HTTP/Razor integration cases run in the full application test project.
// The payment-only diagnostic project links the dependency-light policy cases.
public sealed partial class CommerceManagementCapabilityPolicyTests
{
    [Theory]
    [InlineData("calendar-callback")]
    [InlineData("external-ads/callback")]
    public async Task Analytics_callbacks_use_registered_private_adapter_on_verified_host(string callback)
    {
        await using var f = new NavigationFixture("owner", "Active", true);
        f.Controller.Request.Scheme = "https";
        f.Controller.Request.Host = new HostString("one.example.invalid");
        var method = typeof(CommerceManagementController).GetMethod("AnalyticsCallbackUri",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        Assert.Equal("https://one.example.invalid/internal/analytics/" + callback,
            method.Invoke(f.Controller, new object[] { callback }));
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
        builder.Services.AddSingleton(f.Controller.HttpContext.RequestServices.GetRequiredService<MerchantConnectionService>());
        builder.Services.AddSingleton(f.Stores); builder.Services.AddSingleton(f.Tickets);
        builder.Services.AddSingleton(new WebsiteDomainService(f.Db, Mock.Of<System.Net.Http.IHttpClientFactory>(), new ConfigurationBuilder().Build()));
        builder.Services.AddSingleton(new BusinessWorkspaceService(f.Db, Mock.Of<Infrastructure.Analytics.IAnalyticsQueryService>()));
        builder.Services.AddSingleton(Mock.Of<Infrastructure.Identity.IClientEntraLifecycleService>());
        builder.Services.AddDataProtection().UseEphemeralDataProtectionProvider();
        builder.Services.AddLegendCommerceSessions();
        builder.Services.AddControllersWithViews().AddApplicationPart(Assembly.Load("ProtectWebsite")).AddLegendCommerceManagementMvc().AddControllersAsServices();
        builder.Services.Replace(ServiceDescriptor.Transient<CommerceManagementController>(_ => f.Controller));
        await using var app = builder.Build();
        app.UseMiddleware<Infrastructure.WebsiteRuntime.BusinessWebsiteMiddleware>();
        app.UseAuthentication(); app.UseAuthorization(); app.MapControllers();
        app.MapGet("/internal/test-token", (Microsoft.AspNetCore.Antiforgery.IAntiforgery af, HttpContext c) => af.GetAndStoreTokens(c).RequestToken!);
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

        var authenticatedToken = await client.GetAsync("/internal/test-token");
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
        // The production request receives a fresh scoped DbContext after atomic acceptance.
        foreach (var entry in f.Db.ChangeTracker.Entries<CommerceBusinessMember>().Where(e => e.Entity.Id == staffMember.Id).ToList())
            entry.State = EntityState.Detached;
        var teamPage = await client.GetAsync("/internal/settings/team");
        Assert.Equal(System.Net.HttpStatusCode.OK, teamPage.StatusCode);
        var renderedTeam = await teamPage.Content.ReadAsStringAsync();
        Assert.Contains("http-staff@example.invalid", renderedTeam);
        Assert.Contains("pf-team-card", renderedTeam);
        Assert.Contains("data-pf-open=", renderedTeam);
        Assert.DoesNotContain("pf-command-panel", renderedTeam);
        Assert.DoesNotContain("--pf-cream:", renderedTeam);
        var paymentsPage = await client.GetAsync("/internal/settings/payments");
        Assert.Equal(System.Net.HttpStatusCode.OK, paymentsPage.StatusCode);
        var paymentsHtml = await paymentsPage.Content.ReadAsStringAsync();
        Assert.Contains("/internal/settings/payments?environment=Sandbox", paymentsHtml);
        Assert.Contains("/internal/settings/payments?environment=Production", paymentsHtml);
        Assert.DoesNotContain("/internal/settings/payments&amp;environment=", paymentsHtml);

        var renderedTicks = System.Text.RegularExpressions.Regex.Match(renderedTeam,
            "name=\"ExpectedUpdatedTicks\" value=\"([0-9]+)\"").Groups[1].Value;
        Assert.Equal(staffMember.UpdatedUtc.Ticks.ToString(), renderedTicks);
        var savedPermissions = await client.PostAsync("/internal/settings/team/permissions", new System.Net.Http.FormUrlEncodedContent(new System.Collections.Generic.Dictionary<string,string> {
            ["__RequestVerificationToken"] = token, ["MemberId"] = staffMember.Id.ToString(),
            ["ExpectedUpdatedTicks"] = renderedTicks, ["CanManageStorefront"] = "true", ["CanManageCatalog"] = "true" }));
        Assert.Equal(System.Net.HttpStatusCode.Redirect, savedPermissions.StatusCode);
        staffMember = await f.Db.CommerceBusinessMembers.AsNoTracking().SingleAsync(m => m.Id == staffMember.Id);
        Assert.True(staffMember.CanManageCatalog);
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
        builder.Services.AddSingleton(new BusinessWorkspaceService(f.Db, Mock.Of<Infrastructure.Analytics.IAnalyticsQueryService>(), new ConfigurationBuilder().Build()));
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

}
