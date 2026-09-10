using System;
using System.Net;
using System.Net.Http.Json;
using System.Text.Encodings.Web;
using System.Threading;
using System.Threading.Tasks;
using AgentPortal.Mobile;
using Infrastructure.DailyScripture;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Authorization;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace AgentPortal.Tests;

public sealed class MobileGuestAccessTests
{
    [Fact]
    public async Task GuestReadsCanonicalPublicContentWithoutAnAccountButCannotAccessPrivateRoutes()
    {
        var today = new DateOnly(2026, 9, 9);
        var scripture = new Mock<IDailyScriptureService>(MockBehavior.Strict);
        scripture.Setup(x => x.GetBusinessDate(It.IsAny<DateTime>())).Returns(today);
        scripture.Setup(x => x.GetForDateAsync(It.IsAny<DateOnly>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((DateOnly date, CancellationToken _) => new DailyScripture("Psalm 23", "KJV", ["Canonical published reading"], date.ToString("yyyy-MM-dd")));
        using var host = await new HostBuilder().ConfigureWebHost(web => web.UseTestServer()
            .ConfigureServices(services =>
            {
                services.AddSingleton(scripture.Object);
                services.AddControllers(options => options.Filters.Add(new AuthorizeFilter(
                    new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build())))
                    .AddApplicationPart(typeof(MobileGuestController).Assembly);
                services.AddAuthentication("GuestTest").AddScheme<AuthenticationSchemeOptions, AnonymousHandler>("GuestTest", _ => { });
                services.AddAuthorization(options => options.AddPolicy(MobileApiAuthorization.PolicyName,
                    policy => policy.RequireAuthenticatedUser()));
            })
            .Configure(app =>
            {
                app.UseRouting();
                app.UseAuthentication();
                app.UseAuthorization();
                app.UseEndpoints(endpoints => endpoints.MapControllers());
            })).StartAsync();
        var client = host.GetTestClient();
        var response = await client.GetAsync("/api/v1/mobile/guest");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var content = await response.Content.ReadFromJsonAsync<MobileGuestSnapshot>();
        Assert.NotNull(content);
        Assert.Equal("https://www.mylegnd.com/privacy-terms", Assert.Single(content!.Links).Url);
        Assert.Equal(7, content!.Readings.Count);
        Assert.Equal("2026-09-09", content.Readings[0].Date);
        Assert.Equal("2026-09-03", content.Readings[6].Date);
        Assert.All(content.Readings, reading => Assert.Equal("Canonical published reading", reading.Text));
        for (var offset = 0; offset < 7; offset++)
            scripture.Verify(x => x.GetForDateAsync(today.AddDays(-offset), It.IsAny<CancellationToken>()), Times.Once);
        foreach (var role in new[] { "Guest", "Client", "Agent" })
        {
            client.DefaultRequestHeaders.Remove("X-Legend-Participant-Type");
            client.DefaultRequestHeaders.Add("X-Legend-Participant-Type", role);
            foreach (var path in new[] { "home", "account", "messaging/conversations" })
                Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/mobile/" + path)).StatusCode);
        }
        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await client.PostAsync("/api/v1/mobile/guest", null)).StatusCode);
    }

    private sealed class AnonymousHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync() => Task.FromResult(AuthenticateResult.NoResult());
    }
}
