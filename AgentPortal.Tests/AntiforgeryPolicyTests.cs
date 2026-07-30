using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Reflection;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace AgentPortal.Tests;

// Step 2A (F2) regression coverage for the AgentPortal browser anti-forgery
// policy. Two complementary layers:
//   1. BEHAVIORAL: a minimal in-memory host wired with the exact same policy as
//      AgentPortal/Program.cs (AddAntiforgery header "RequestVerificationToken"
//      + a global AutoValidateAntiforgeryTokenAttribute) proves that a
//      cookie-authenticated mutation is rejected without a token, reaches the
//      controller with a valid token, and that [IgnoreAntiforgeryToken]
//      (bearer / signed-webhook trust boundaries) still bypasses as required.
//   2. ATTRIBUTE INVARIANTS: reflection over the real AgentPortal controllers
//      proves the confirmed cookie-authenticated browser mutations no longer
//      opt out of anti-forgery, while the genuine trust boundaries (mobile
//      bearer APIs, signed webhooks/ingest) and the read-only ProductionController
//      GET endpoints retain their bypass.
public class AntiforgeryPolicyTests
{
    // ---------------------------------------------------------------------
    // 1. BEHAVIORAL: policy mechanism (same wiring as AgentPortal/Program.cs)
    // ---------------------------------------------------------------------
    [Fact]
    public async Task GlobalPolicy_RejectsMutationWithoutToken_AllowsWithToken_AndHonorsBypass()
    {
        using var host = await BuildPolicyHostAsync();
        var client = host.GetTestClient();

        // (a) Cookie-authenticated mutation WITHOUT a token -> rejected (400).
        var missing = await client.PostAsync("/__test/af/guarded", new StringContent(string.Empty));
        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);

        // (b) Same authorized mutation WITH a valid token+cookie -> reaches controller.
        var tokenResp = await client.GetAsync("/__test/af/token");
        tokenResp.EnsureSuccessStatusCode();
        var dto = await tokenResp.Content.ReadFromJsonAsync<TokenDto>();
        Assert.NotNull(dto);
        Assert.False(string.IsNullOrWhiteSpace(dto!.RequestToken));

        var withToken = new HttpRequestMessage(HttpMethod.Post, "/__test/af/guarded")
        {
            Content = new StringContent(string.Empty)
        };
        withToken.Headers.Add("RequestVerificationToken", dto.RequestToken);
        var cookie = ExtractAntiforgeryCookie(tokenResp);
        if (!string.IsNullOrEmpty(cookie))
            withToken.Headers.Add("Cookie", cookie);

        var accepted = await client.SendAsync(withToken);
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        Assert.Equal("guarded-reached", await accepted.Content.ReadAsStringAsync());

        // (c) A [IgnoreAntiforgeryToken] endpoint (bearer / signed-webhook trust
        //     boundary) still succeeds WITHOUT a token — these must not break.
        var bypassed = await client.PostAsync("/__test/af/bypassed", new StringContent(string.Empty));
        Assert.Equal(HttpStatusCode.OK, bypassed.StatusCode);
        Assert.Equal("bypassed-reached", await bypassed.Content.ReadAsStringAsync());
    }

    private static Task<IHost> BuildPolicyHostAsync()
    {
        return new HostBuilder()
            .ConfigureWebHost(webBuilder =>
            {
                webBuilder
                    .UseTestServer()
                    .ConfigureServices(services =>
                    {
                        // AddControllersWithViews mirrors AgentPortal/Program.cs and
                        // registers the ViewFeatures anti-forgery authorization filter
                        // that AutoValidateAntiforgeryTokenAttribute resolves from DI.
                        services
                            .AddControllersWithViews(options =>
                                options.Filters.Add(new AutoValidateAntiforgeryTokenAttribute()))
                            .AddApplicationPart(typeof(AntiforgeryPolicyTests).Assembly);
                        services.AddAntiforgery(o => o.HeaderName = "RequestVerificationToken");
                        services.AddAuthentication("Test")
                            .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>("Test", _ => { });
                        services.AddAuthorization();
                    })
                    .Configure(app =>
                    {
                        app.UseRouting();
                        app.UseAuthentication();
                        app.UseAuthorization();
                        app.UseEndpoints(endpoints => endpoints.MapControllers());
                    });
            })
            .StartAsync();
    }

    private static string ExtractAntiforgeryCookie(HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues("Set-Cookie", out var cookies))
            return string.Empty;

        foreach (var c in cookies)
        {
            if (c.StartsWith(".AspNetCore.Antiforgery", StringComparison.OrdinalIgnoreCase))
                return c.Split(';')[0];
        }

        return string.Empty;
    }

    public sealed class TokenDto
    {
        public string RequestToken { get; set; } = string.Empty;
    }

    // Authenticates every request so [Authorize] passes and we exercise the
    // anti-forgery layer for a genuinely authenticated (cookie-style) caller.
    private sealed class TestAuthHandler : AuthenticationHandler<AuthenticationSchemeOptions>
    {
        public TestAuthHandler(
            IOptionsMonitor<AuthenticationSchemeOptions> options,
            ILoggerFactory logger,
            UrlEncoder encoder)
            : base(options, logger, encoder)
        {
        }

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var identity = new ClaimsIdentity(
                new[] { new Claim("oid", "agent-test") }, "Test");
            var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), "Test");
            return Task.FromResult(AuthenticateResult.Success(ticket));
        }
    }

    // ---------------------------------------------------------------------
    // 2. ATTRIBUTE INVARIANTS over the real AgentPortal controllers
    // ---------------------------------------------------------------------
    private static readonly Assembly AgentPortalAssembly =
        typeof(AgentPortal.Controllers.CalendarController).Assembly;

    private static Type Controller(string name) =>
        AgentPortalAssembly.GetTypes().Single(t =>
            t.Name == name &&
            typeof(Microsoft.AspNetCore.Mvc.ControllerBase).IsAssignableFrom(t));

    private static bool MethodBypassesAntiforgery(MethodInfo method) =>
        method.GetCustomAttribute<IgnoreAntiforgeryTokenAttribute>() != null ||
        method.DeclaringType!.GetCustomAttribute<IgnoreAntiforgeryTokenAttribute>() != null;

    private static bool TypeHasAnyBypass(Type type) =>
        type.GetCustomAttribute<IgnoreAntiforgeryTokenAttribute>() != null ||
        type.GetMethods().Any(m => m.GetCustomAttribute<IgnoreAntiforgeryTokenAttribute>() != null);

    [Fact]
    public void ConfirmedCookieMutations_NoLongerBypassAntiforgery()
    {
        // Class-level bypass removed (POST/PUT/DELETE now validated by the global policy).
        Assert.Null(Controller("UnderwritingController").GetCustomAttribute<IgnoreAntiforgeryTokenAttribute>());
        Assert.Null(Controller("ProposalsController").GetCustomAttribute<IgnoreAntiforgeryTokenAttribute>());
        Assert.Null(Controller("ZoomLinksController").GetCustomAttribute<IgnoreAntiforgeryTokenAttribute>());

        // Method-level bypass removed on the specific browser mutations.
        Assert.False(MethodBypassesAntiforgery(
            Controller("WorkstationController").GetMethods().Single(m => m.Name == "AdvancedMarketsCalculate")));
        Assert.False(MethodBypassesAntiforgery(
            Controller("ActionsController").GetMethods().Single(m => m.Name == "Edit" && m.GetParameters().Length == 2)));
        Assert.False(MethodBypassesAntiforgery(
            Controller("ActionsController").GetMethods().Single(m => m.Name == "Delete")));
        Assert.False(MethodBypassesAntiforgery(
            Controller("DashboardController").GetMethods().Single(m => m.Name == "CompleteAction")));
        Assert.False(MethodBypassesAntiforgery(
            Controller("LeadBridgeController").GetMethods().Single(m => m.Name == "Next")));
        Assert.False(MethodBypassesAntiforgery(
            Controller("LeadBridgeController").GetMethods().Single(m => m.Name == "SetFilters")));
    }

    [Fact]
    public void TrustBoundaries_RetainTheirAntiforgeryBypass()
    {
        // Mobile bearer APIs: browser anti-forgery is not applicable.
        Assert.NotNull(Controller("MobileHomeController").GetCustomAttribute<IgnoreAntiforgeryTokenAttribute>());
        Assert.NotNull(Controller("MobileSocialController").GetCustomAttribute<IgnoreAntiforgeryTokenAttribute>());
        Assert.NotNull(Controller("MobileMessagingController").GetCustomAttribute<IgnoreAntiforgeryTokenAttribute>());
        Assert.NotNull(Controller("MobileAccountController").GetCustomAttribute<IgnoreAntiforgeryTokenAttribute>());
        Assert.NotNull(Controller("MobileJourneyCirclesController").GetCustomAttribute<IgnoreAntiforgeryTokenAttribute>());

        // Signed provider webhooks: authenticated by signature, not browser tokens.
        Assert.NotNull(Controller("GraphCalendarWebhookController").GetCustomAttribute<IgnoreAntiforgeryTokenAttribute>());
        Assert.NotNull(Controller("BillingWebhooksController").GetCustomAttribute<IgnoreAntiforgeryTokenAttribute>());

        // Signed ingest endpoints (HMAC / shared-secret validated).
        Assert.True(TypeHasAnyBypass(Controller("LeadSubmitController")));
        Assert.True(TypeHasAnyBypass(Controller("AnalyticsIngestController")));
    }

    [Fact]
    public void AgentPortalProductionController_ReadOnlyGets_RetainRequiredBypass()
    {
        // Corrected classification: these are [HttpGet] read-only endpoints whose
        // bypass is REQUIRED to coexist with the controller's class-level
        // [ValidateAntiForgeryToken]; they are not state-changing mutations.
        var production = Controller("ProductionController"); // AgentPortal assembly only
        foreach (var name in new[] { "LeadHistory", "ClientHistory", "LeadSummary", "ClientSummary" })
        {
            var method = production.GetMethods().Single(m => m.Name == name);
            Assert.NotNull(method.GetCustomAttribute<HttpGetAttribute>());
            Assert.NotNull(method.GetCustomAttribute<IgnoreAntiforgeryTokenAttribute>());
        }
    }
}

// Test-only probe controller, discovered solely by the in-memory host in
// GlobalPolicy_* via AddApplicationPart. It is inert for every other test (no
// host maps it) and never ships in AgentPortal.
[Authorize]
public sealed class TestAntiforgeryProbeController : Microsoft.AspNetCore.Mvc.ControllerBase
{
    [HttpGet("/__test/af/token")]
    public IActionResult GetToken([FromServices] IAntiforgery antiforgery)
    {
        var tokens = antiforgery.GetAndStoreTokens(HttpContext);
        return Ok(new AntiforgeryPolicyTests.TokenDto
        {
            RequestToken = tokens.RequestToken ?? string.Empty
        });
    }

    [HttpPost("/__test/af/guarded")]
    public IActionResult Guarded() => Ok("guarded-reached");

    [HttpPost("/__test/af/bypassed")]
    [IgnoreAntiforgeryToken]
    public IActionResult Bypassed() => Ok("bypassed-reached");
}
