using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Shared.Messaging;
using Xunit;

namespace AgentPortal.Tests;

public sealed class MessagingHubNegotiationTests
{
    [Fact]
    public async Task CookieOnlyHost_NegotiatesWithoutRegisteringTheMobileBearerScheme()
    {
        using var host = await new HostBuilder()
            .ConfigureWebHost(webBuilder =>
            {
                webBuilder
                    .UseTestServer()
                    .ConfigureServices(services =>
                    {
                        services.AddRouting();
                        services.AddSignalR();
                        services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
                            .AddScheme<AuthenticationSchemeOptions, CookieTestAuthHandler>(
                                CookieAuthenticationDefaults.AuthenticationScheme,
                                _ => { });
                        services.AddAuthorization();
                    })
                    .Configure(app =>
                    {
                        app.UseRouting();
                        app.UseAuthentication();
                        app.UseAuthorization();
                        app.UseEndpoints(endpoints => endpoints.MapLegendMessagingHub(
                            "/messaginghub",
                            CookieAuthenticationDefaults.AuthenticationScheme));
                    });
            })
            .StartAsync();

        var response = await host.GetTestClient().PostAsync(
            "/messaginghub/negotiate?negotiateVersion=1",
            content: null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private sealed class CookieTestAuthHandler : AuthenticationHandler<AuthenticationSchemeOptions>
    {
        public CookieTestAuthHandler(
            IOptionsMonitor<AuthenticationSchemeOptions> options,
            ILoggerFactory logger,
            UrlEncoder encoder)
            : base(options, logger, encoder)
        {
        }

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var identity = new ClaimsIdentity(
                [new Claim("oid", "client-test")],
                CookieAuthenticationDefaults.AuthenticationScheme);
            var ticket = new AuthenticationTicket(
                new ClaimsPrincipal(identity),
                CookieAuthenticationDefaults.AuthenticationScheme);
            return Task.FromResult(AuthenticateResult.Success(ticket));
        }
    }
}
