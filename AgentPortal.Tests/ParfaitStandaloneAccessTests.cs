using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Domain.Entities;
using Infrastructure.Commerce.Payments;
using Infrastructure.Data;
using Legend.Commerce;
using Legend.Commerce.Shipping;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using ParfaitApp.Controllers;
using ParfaitApp.Models;
using ParfaitApp.Services;
using Xunit;
namespace AgentPortal.Tests;

public sealed class ParfaitStandaloneAccessTests
{
    [Fact]
    public async Task NativeShippingRoutesPreserveTeamPermissionsScopeAndCsrf()
    {
        await using var f=await Fixture.Start(); using var client=f.Client;
        Assert.Equal(HttpStatusCode.Unauthorized,(await client.GetAsync("/internal/settings/shipping")).StatusCode);
        client.DefaultRequestHeaders.Add("X-Fixture-User","denied");
        Assert.Equal(HttpStatusCode.Forbidden,(await client.GetAsync("/internal/settings/shipping")).StatusCode);
        client.DefaultRequestHeaders.Remove("X-Fixture-User");client.DefaultRequestHeaders.Add("X-Fixture-User","settings");
        Assert.Equal(HttpStatusCode.OK,(await client.GetAsync("/internal/settings/shipping?businessKey=foreign")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,(await client.GetAsync("/internal/commerce/orders/shipping?orderNumber=LOCAL")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,(await client.PostAsync("/internal/settings/shipping/connect",new FormUrlEncodedContent(new Dictionary<string,string>()))).StatusCode);
        client.DefaultRequestHeaders.Remove("X-Fixture-User");client.DefaultRequestHeaders.Add("X-Fixture-User","orders");
        Assert.Equal(HttpStatusCode.OK,(await client.GetAsync("/internal/commerce/orders/shipping?orderNumber=LOCAL")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,(await client.GetAsync("/internal/commerce/orders/shipping?orderNumber=FOREIGN&businessKey=foreign")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,(await client.GetAsync("/internal/commerce/orders/reference-label?orderNumber=FOREIGN")).StatusCode);
        foreach(var suffix in new[]{"rates","buy","reconcile"})
            Assert.Equal(HttpStatusCode.BadRequest,(await client.PostAsync("/internal/commerce/orders/shipping/"+suffix,new FormUrlEncodedContent(new Dictionary<string,string>()))).StatusCode);
        var pages=f.Host.Services.GetRequiredService<IParfaitInternalPageRegistry>().GetAllPages();
        Assert.Single(pages.Where(x=>x.Route=="/internal/settings/shipping"));
        Assert.False(pages.Single(x=>x.Route=="/internal/commerce/orders/shipping").ShowInNavigation);
        Assert.Empty(await f.Db.CommerceBusinessSettings.ToListAsync());
        Assert.All(await f.Db.CommerceOrders.ToListAsync(),x=>Assert.Null(x.ShippingLabelStateJson));
        f.Team.Verify(x=>x.AuthorizePageAsync(It.IsAny<ClaimsPrincipal>(),"/internal/commerce/orders/shipping","/internal/commerce/orders",It.IsAny<CancellationToken>()),Times.AtLeastOnce);
    }

    [Fact]
    public async Task AccountChoiceUsesMicrosoftAndOnlyLocalReturnWithoutGrantingAccess()
    {
        await using var f=await Fixture.Start();using var client=f.Client;
        var login=await client.GetAsync("/internal/login?returnUrl=https://evil.invalid/");
        var challenge=await login.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("select_account",challenge.GetProperty("prompt").GetString());
        Assert.Equal("/internal",challenge.GetProperty("returnUri").GetString());
        client.DefaultRequestHeaders.Add("X-Fixture-User","denied");
        Assert.Equal(HttpStatusCode.BadRequest,(await client.PostAsync("/internal/login",new FormUrlEncodedContent(new Dictionary<string,string>()))).StatusCode);
        var tokenResponse=await client.GetAsync("/fixture-token");
        client.DefaultRequestHeaders.Add("Cookie",string.Join("; ",tokenResponse.Headers.GetValues("Set-Cookie").Select(x=>x.Split(';')[0])));
        var token=await tokenResponse.Content.ReadFromJsonAsync<JsonElement>();
        client.DefaultRequestHeaders.Add("RequestVerificationToken",token.GetProperty("token").GetString());
        var switched=await client.PostAsync("/internal/login",new FormUrlEncodedContent(new Dictionary<string,string>{{"returnUrl","/internal/settings/shipping"}}));
        Assert.Equal("Cookies",Assert.Single(switched.Headers.GetValues("X-Fixture-Signed-Out")));
        challenge=await switched.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("select_account",challenge.GetProperty("prompt").GetString());
        Assert.Equal("/internal/settings/shipping",challenge.GetProperty("returnUri").GetString());
        Assert.Equal(HttpStatusCode.Forbidden,(await client.GetAsync("/internal/settings/shipping")).StatusCode);
        f.Team.Verify(x=>x.AuthorizeSignInAsync(It.IsAny<ClaimsPrincipal>(),It.IsAny<CancellationToken>()),Times.Never);
    }

    [Fact]
    public async Task NativeSettingsRenderBoundAccessibleFieldsAndSubmitThroughExistingCsrfAction()
    {
        await using var f=await Fixture.Start(); using var client=f.Client;
        client.DefaultRequestHeaders.Add("X-Fixture-User","team");
        var response=await client.GetAsync("/internal/settings/team");
        Assert.Equal(HttpStatusCode.OK,response.StatusCode);
        var html=await response.Content.ReadAsStringAsync();
        foreach(var field in new[]{"Email","DisplayName","RoleKey"}) {
            Assert.Contains($"name=\"NewMember.{field}\"",html);
            Assert.Contains($"id=\"NewMember_{field}\"",html);
            Assert.Contains($"for=\"NewMember_{field}\"",html);
            Assert.Contains($"data-valmsg-for=\"NewMember.{field}\"",html);
        }
        Assert.Contains("data-val-required=",html);
        Assert.DoesNotContain("asp-for=",html);Assert.DoesNotContain("asp-validation-for=",html);
        Assert.Contains("action=\"/internal/settings/team/add\"",html);
        var token=System.Text.RegularExpressions.Regex.Match(html,"name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value;
        Assert.NotEmpty(token);
        client.DefaultRequestHeaders.Add("Cookie",string.Join("; ",response.Headers.GetValues("Set-Cookie").Select(x=>x.Split(';')[0])));
        var form=new Dictionary<string,string>{{"NewMember.Email","external@example.invalid"},{"NewMember.DisplayName","External Member"},{"NewMember.RoleKey","support"},{"__RequestVerificationToken",WebUtility.HtmlDecode(token)}};
        var submitted=await client.PostAsync("/internal/settings/team/add",new FormUrlEncodedContent(form));
        Assert.Equal(HttpStatusCode.Redirect,submitted.StatusCode);
        f.Team.Verify(x=>x.AddMemberAsync(It.Is<ParfaitTeamCreateMemberInput>(m=>m.Email=="external@example.invalid" && m.DisplayName=="External Member" && m.RoleKey=="support"),It.IsAny<string>(),It.IsAny<string>(),It.IsAny<CancellationToken>()),Times.Once);
        form.Remove("__RequestVerificationToken");
        Assert.Equal(HttpStatusCode.BadRequest,(await client.PostAsync("/internal/settings/team/add",new FormUrlEncodedContent(form))).StatusCode);
        var profile=await client.GetAsync("/internal/settings/business-profile");
        Assert.Equal(HttpStatusCode.OK,profile.StatusCode);
        var profileHtml=await profile.Content.ReadAsStringAsync();
        Assert.Contains("action=\"/internal/settings/business-profile\"",profileHtml);
        Assert.Contains("name=\"__RequestVerificationToken\"",profileHtml);
        Assert.Contains("name=\"StoreName\"",profileHtml);
        Assert.DoesNotContain("asp-action=",profileHtml);Assert.DoesNotContain("asp-for=",profileHtml);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public required IHost Host;public required MasterAppDbContext Db;public required Mock<IParfaitTeamAccessService> Team;
        public HttpClient Client=>Host.GetTestClient();
        public static async Task<Fixture> Start()
        {
            var db=ControllerTestHelpers.BuildDb();
            var business=new CommerceBusiness {Key="parfait",DisplayName="Parfait"};
            db.AddRange(business,new CommerceOrder {CommerceBusinessId=business.Id,OrderNumber="LOCAL"},new CommerceOrder {CommerceBusinessId=Guid.NewGuid(),OrderNumber="FOREIGN"});await db.SaveChangesAsync();
            var team=new Mock<IParfaitTeamAccessService>();
            team.Setup(x=>x.AuthorizePageAsync(It.IsAny<ClaimsPrincipal>(),It.IsAny<string>(),It.IsAny<string?>(),It.IsAny<CancellationToken>()))
                .ReturnsAsync((ClaimsPrincipal user,string path,string? key,CancellationToken ct)=>new ParfaitPageAccessResult {Allowed=(user.Identity!.Name=="team" && (key??path) is "/internal/settings/team" or "/internal/settings/business-profile") || (key??path)==(user.Identity!.Name=="settings"?"/internal/settings/shipping":user.Identity.Name=="orders"?"/internal/commerce/orders":"none")});
            team.Setup(x=>x.GetTeamManagementViewModelAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new ParfaitTeamManagementViewModel {RoleOptions=[new ParfaitTeamRoleDefinition {Key="support",Label="Support",Description="Support"}]});
            team.Setup(x=>x.GetVisiblePagesAsync(It.IsAny<ClaimsPrincipal>(),It.IsAny<CancellationToken>())).ReturnsAsync(Array.Empty<ParfaitInternalPageDefinition>());
            var profile=new Mock<IParfaitBusinessProfileService>();
            profile.Setup(x=>x.GetProfileAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new ParfaitBusinessProfileViewModel());
            var host=await new HostBuilder().ConfigureWebHost(web=>web.UseEnvironment("Testing").UseContentRoot(System.IO.Path.GetTempPath()).UseTestServer().ConfigureServices(services=>{
                services.AddSingleton(db);services.AddSingleton(team.Object);services.AddSingleton(profile.Object);services.AddSingleton<IParfaitInternalPageRegistry,ParfaitInternalPageRegistry>();
                services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());services.AddScoped<ParfaitBusinessScopeService>();services.AddScoped<ParfaitOrderService>();
                services.AddSingleton(sp=>MerchantPaymentProtection.CreateShared(sp.GetRequiredService<IConfiguration>(),sp.GetRequiredService<IHostEnvironment>()));
                services.AddScoped<CommerceShippingService>();
                services.AddAntiforgery(o=>o.HeaderName="RequestVerificationToken");
                services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
                    .AddScheme<AuthenticationSchemeOptions,FixtureAuthentication>(CookieAuthenticationDefaults.AuthenticationScheme,_=>{})
                    .AddScheme<AuthenticationSchemeOptions,FixtureAuthentication>(OpenIdConnectDefaults.AuthenticationScheme,_=>{});
                services.AddAuthorization();
                services.AddControllersWithViews(o=>o.Filters.Add<ShippingResult>()).AddApplicationPart(typeof(InternalController).Assembly).AddLegendCommerceMvc().AddControllersAsServices();
                services.AddTransient(sp=>new InternalModulesController(null!,null!,null!,null!,null!,null!,null!,null!,null!,sp.GetRequiredService<ParfaitBusinessScopeService>(),null!));
            }).Configure(app=>{
                app.UseRouting();app.UseAuthentication();app.UseMiddleware<ParfaitInternalAccessMiddleware>();app.UseAuthorization();
                app.UseEndpoints(endpoints=>{endpoints.MapControllers();endpoints.MapGet("/fixture-token",async context=>{
                    var token=context.RequestServices.GetRequiredService<IAntiforgery>().GetAndStoreTokens(context);await context.Response.WriteAsJsonAsync(new{token=token.RequestToken});});});
            })).StartAsync();
            return new(){Host=host,Db=db,Team=team};
        }
        public async ValueTask DisposeAsync(){Host.Dispose();await Db.DisposeAsync();}
    }
    private sealed class ShippingResult:IAsyncResultFilter
    {
        public async Task OnResultExecutionAsync(ResultExecutingContext context,ResultExecutionDelegate next)
        {
            if(context.Result is ViewResult {Model:ShippingSetupView or ShippingOrderView}) context.Result=new OkResult();
            await next();
        }
    }
    private sealed class FixtureAuthentication(IOptionsMonitor<AuthenticationSchemeOptions> options,ILoggerFactory logger,UrlEncoder encoder):SignInAuthenticationHandler<AuthenticationSchemeOptions>(options,logger,encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var name=Request.Headers["X-Fixture-User"].ToString();
            return Task.FromResult(string.IsNullOrEmpty(name)?AuthenticateResult.NoResult():AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity(new[]{new Claim(ClaimTypes.Name,name)},Scheme.Name)),Scheme.Name)));
        }
        protected override Task HandleChallengeAsync(AuthenticationProperties properties)
        {
            if(Scheme.Name==CookieAuthenticationDefaults.AuthenticationScheme){Response.StatusCode=401;return Task.CompletedTask;}
            return Response.WriteAsJsonAsync(new{prompt=properties.Parameters["prompt"],returnUri=properties.RedirectUri});
        }
        protected override Task HandleForbiddenAsync(AuthenticationProperties properties){Response.StatusCode=403;return Task.CompletedTask;}
        protected override Task HandleSignInAsync(ClaimsPrincipal user,AuthenticationProperties? properties)=>Task.CompletedTask;
        protected override Task HandleSignOutAsync(AuthenticationProperties? properties){Response.Headers.Append("X-Fixture-Signed-Out",Scheme.Name);return Task.CompletedTask;}
    }
}
