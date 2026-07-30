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
using Microsoft.AspNetCore.Mvc.Authorization;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace AgentPortal.Tests;

// Step 2B (F3) regression coverage for ClientApp/Controllers/ProductionController.
//   1. BEHAVIORAL: an in-memory host reproducing ClientApp's actual policy —
//      AddControllersWithViews with a global RequireAuthenticatedUser filter,
//      AddAntiforgery(HeaderName = "RequestVerificationToken"), and PER-ACTION
//      [ValidateAntiForgeryToken] (no global auto-validate) — proves a
//      cookie-authenticated mutation is rejected without a token, reaches the
//      controller with a valid token, that an unauthenticated request is
//      rejected, and that a read-only GET is unaffected.
//   2. ATTRIBUTE INVARIANTS: reflection over the real ClientApp ProductionController
//      proves the three state-changing POSTs now require anti-forgery, the
//      read-only GET does not, and no unsafe [IgnoreAntiforgeryToken] bypass exists.
public class ClientAppProductionAntiforgeryTests
{
    private const string AuthHeader = "X-CTest-Auth";

    // ---------------------------------------------------------------------
    // 1. BEHAVIORAL (ClientApp policy reproduction)
    // ---------------------------------------------------------------------
    [Fact]
    public async Task ClientAppPolicy_RejectsMutationWithoutToken_AllowsWithToken_RejectsAnonymous_AndLeavesGetAlone()
    {
        using var host = await BuildClientPolicyHostAsync();
        var client = host.GetTestClient();

        // (1) Authenticated cookie mutation WITHOUT a token -> rejected (400).
        var missing = new HttpRequestMessage(HttpMethod.Post, "/__cprod/mutate")
        {
            Content = new StringContent(string.Empty)
        };
        missing.Headers.Add(AuthHeader, "1");
        var missingResp = await client.SendAsync(missing);
        Assert.Equal(HttpStatusCode.BadRequest, missingResp.StatusCode);

        // (2) Same authorized mutation WITH a valid token+cookie -> reaches controller.
        var tokenReq = new HttpRequestMessage(HttpMethod.Get, "/__cprod/token");
        tokenReq.Headers.Add(AuthHeader, "1");
        var tokenResp = await client.SendAsync(tokenReq);
        tokenResp.EnsureSuccessStatusCode();
        var dto = await tokenResp.Content.ReadFromJsonAsync<AntiforgeryPolicyTests.TokenDto>();
        Assert.NotNull(dto);
        Assert.False(string.IsNullOrWhiteSpace(dto!.RequestToken));

        var withToken = new HttpRequestMessage(HttpMethod.Post, "/__cprod/mutate")
        {
            Content = new StringContent(string.Empty)
        };
        withToken.Headers.Add(AuthHeader, "1");
        withToken.Headers.Add("RequestVerificationToken", dto.RequestToken);
        var cookie = ExtractAntiforgeryCookie(tokenResp);
        if (!string.IsNullOrEmpty(cookie))
            withToken.Headers.Add("Cookie", cookie);
        var accepted = await client.SendAsync(withToken);
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        Assert.Equal("mutate-reached", await accepted.Content.ReadAsStringAsync());

        // (3) UNAUTHENTICATED mutation -> rejected by the global authorize filter (401).
        var anon = new HttpRequestMessage(HttpMethod.Post, "/__cprod/mutate")
        {
            Content = new StringContent(string.Empty)
        };
        var anonResp = await client.SendAsync(anon);
        Assert.Equal(HttpStatusCode.Unauthorized, anonResp.StatusCode);

        // (4) Read-only GET is unaffected: authenticated, no token required.
        var read = new HttpRequestMessage(HttpMethod.Get, "/__cprod/read");
        read.Headers.Add(AuthHeader, "1");
        var readResp = await client.SendAsync(read);
        Assert.Equal(HttpStatusCode.OK, readResp.StatusCode);
        Assert.Equal("read-reached", await readResp.Content.ReadAsStringAsync());
    }

    private static Task<IHost> BuildClientPolicyHostAsync()
    {
        return new HostBuilder()
            .ConfigureWebHost(webBuilder =>
            {
                webBuilder
                    .UseTestServer()
                    .ConfigureServices(services =>
                    {
                        services
                            .AddControllersWithViews(options =>
                            {
                                // Mirrors ClientApp/Program.cs: global auth, but NO
                                // global auto-validate anti-forgery filter (per-action only).
                                var policy = new AuthorizationPolicyBuilder()
                                    .RequireAuthenticatedUser()
                                    .Build();
                                options.Filters.Add(new AuthorizeFilter(policy));
                            })
                            .AddApplicationPart(typeof(ClientAppProductionAntiforgeryTests).Assembly);
                        services.AddAntiforgery(o => o.HeaderName = "RequestVerificationToken");
                        services.AddAuthentication("CTest")
                            .AddScheme<AuthenticationSchemeOptions, ToggleAuthHandler>("CTest", _ => { });
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

    // Authenticates only when the request carries the test header, so the same
    // host can exercise both authenticated and anonymous paths.
    private sealed class ToggleAuthHandler : AuthenticationHandler<AuthenticationSchemeOptions>
    {
        public ToggleAuthHandler(
            IOptionsMonitor<AuthenticationSchemeOptions> options,
            ILoggerFactory logger,
            UrlEncoder encoder)
            : base(options, logger, encoder)
        {
        }

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.ContainsKey(AuthHeader))
                return Task.FromResult(AuthenticateResult.NoResult());

            var identity = new ClaimsIdentity(
                new[] { new Claim("oid", "client-test") }, "CTest");
            var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), "CTest");
            return Task.FromResult(AuthenticateResult.Success(ticket));
        }
    }

    // ---------------------------------------------------------------------
    // 2. ATTRIBUTE INVARIANTS over the real ClientApp ProductionController
    // ---------------------------------------------------------------------
    [Fact]
    public void ClientAppProduction_Mutations_RequireAntiforgery_GetIsUntouched()
    {
        var controller = typeof(ClientApp.Controllers.ProductionController);

        foreach (var name in new[] { "AddClient", "Update", "Delete" })
        {
            var method = controller.GetMethods().Single(m => m.Name == name);
            Assert.NotNull(method.GetCustomAttribute<HttpPostAttribute>());
            Assert.NotNull(method.GetCustomAttribute<ValidateAntiForgeryTokenAttribute>());
            // Requirement (6): no unsafe bypass retained on the mutations.
            Assert.Null(method.GetCustomAttribute<IgnoreAntiforgeryTokenAttribute>());
        }

        // Read-only GET must stay a GET and must NOT gain mutation protections.
        var clientHistory = controller.GetMethods().Single(m => m.Name == "ClientHistory");
        Assert.NotNull(clientHistory.GetCustomAttribute<HttpGetAttribute>());
        Assert.Null(clientHistory.GetCustomAttribute<ValidateAntiForgeryTokenAttribute>());

        // No blanket bypass at the controller level.
        Assert.Null(controller.GetCustomAttribute<IgnoreAntiforgeryTokenAttribute>());
    }
}

// Test-only probe controller, discovered solely by the in-memory host in this
// file via AddApplicationPart. Inert for every other test.
[Route("__cprod")]
public sealed class ClientProductionAfProbeController : Microsoft.AspNetCore.Mvc.ControllerBase
{
    [HttpGet("token")]
    public IActionResult Token([FromServices] IAntiforgery antiforgery)
    {
        var tokens = antiforgery.GetAndStoreTokens(HttpContext);
        return Ok(new AntiforgeryPolicyTests.TokenDto
        {
            RequestToken = tokens.RequestToken ?? string.Empty
        });
    }

    [HttpGet("read")]
    public IActionResult Read() => Ok("read-reached");

    [HttpPost("mutate")]
    [ValidateAntiForgeryToken]
    public IActionResult Mutate() => Ok("mutate-reached");
}
