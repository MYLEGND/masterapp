using System;
using System.Threading;
using System.Threading.Tasks;
using System.Security.Claims;
using AgentPortal.Mobile;
using Domain.Accounts;
using Domain.Messaging;
using Infrastructure.Identity;
using Infrastructure.Mobile;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace AgentPortal.Tests;

[Collection("LegendConnectFounderEnvironment")]
public sealed class MobileAssistantSurfaceTests
{
    [Fact]
    public void SessionCannotOutliveBearerOrTwentyMinutes()
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(2000000000);
        ClaimsPrincipal Principal(string? expiry) => new(new ClaimsIdentity(expiry is null ? [] : [new Claim("exp", expiry)], "test"));
        Assert.Null(MobileAssistantSurface.Expiry(Principal(null), now));
        Assert.Null(MobileAssistantSurface.Expiry(Principal("invalid"), now));
        Assert.Null(MobileAssistantSurface.Expiry(Principal(now.ToUnixTimeSeconds().ToString()), now));
        Assert.Equal(now.AddMinutes(3), MobileAssistantSurface.Expiry(Principal(now.AddMinutes(3).ToUnixTimeSeconds().ToString()), now));
        Assert.Equal(now.AddMinutes(20), MobileAssistantSurface.Expiry(Principal(now.AddHours(1).ToUnixTimeSeconds().ToString()), now));
    }

    [Theory]
    [InlineData("/founder/legend-ai/surface", true, true, true, true)]
    [InlineData("/founder/diagnostics", true, true, true, false)]
    [InlineData("/founder/legend-ai-other", true, true, true, false)]
    [InlineData("/founder/legend-ai/chat", false, true, true, false)]
    [InlineData("/founder/legend-ai/chat", true, false, true, false)]
    [InlineData("/founder/legend-ai/chat", true, true, false, false)]
    public async Task EmbeddedAuthorityRechecksRouteTenantActorAndLifecycle(string path, bool tenantMatches, bool profileMatches, bool active, bool accepted)
    {
        var founder = Guid.NewGuid().ToString(); var tenant = Guid.NewGuid().ToString(); var profile = Guid.NewGuid();
        var previous = Environment.GetEnvironmentVariable("FOUNDER_OID");
        Environment.SetEnvironmentVariable("FOUNDER_OID", founder);
        try
        {
            var principal = new ClaimsPrincipal(new ClaimsIdentity(new[] {
                new Claim("oid", founder), new Claim("tid", tenantMatches ? tenant : Guid.NewGuid().ToString()),
                new Claim(MobileAssistantSurface.ProfileClaim, (profileMatches ? profile : Guid.NewGuid()).ToString("D"))
            }, MobileAssistantSurface.Scheme));
            var actor = new MobileResolvedActor(new MessagingActor(founder, "Agent"), profile, "Synthetic");
            var resolver = new Mock<IMobileActorResolver>();
            resolver.Setup(x => x.ResolveAsync(It.IsAny<ClaimsPrincipal>(), "Agent", It.IsAny<CancellationToken>()))
                .ReturnsAsync(new MobileActorResolution(true, null, null, [actor], actor, false));
            var lifecycle = new Mock<IAccountLifecycleService>();
            lifecycle.Setup(x => x.GetAsync(It.IsAny<AccountLifecycleSubject>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new AccountLifecycleSnapshot(active ? "Active" : "Paused", active, !active, null, null, null));
            var audience = Guid.NewGuid().ToString();
            using var services = new ServiceCollection().AddSingleton(resolver.Object).AddSingleton(lifecycle.Object)
                .AddSingleton(new MobileAuthConfiguration(tenant, "https://login.microsoftonline.com/" + tenant + "/v2.0", audience, "api://" + audience + "/access_as_user"))
                .BuildServiceProvider();
            var http = new DefaultHttpContext { RequestServices = services }; http.Request.Path = path;
            var options = new CookieAuthenticationOptions(); MobileAssistantSurface.Configure(options);
            var context = new CookieValidatePrincipalContext(http, new AuthenticationScheme(MobileAssistantSurface.Scheme, null, typeof(CookieAuthenticationHandler)), options,
                new AuthenticationTicket(principal, MobileAssistantSurface.Scheme));
            await options.Events.ValidatePrincipal(context);
            Assert.Equal(accepted, context.Principal is not null);
        }
        finally { Environment.SetEnvironmentVariable("FOUNDER_OID", previous); }
    }

    [Fact]
    public void BootstrapTicketIsPurposeProtectedAndSecretsAreRedacted()
    {
        var services = new ServiceCollection(); services.AddLogging(); services.AddDataProtection();
        services.AddAuthentication().AddCookie(MobileAssistantSurface.Scheme, MobileAssistantSurface.Configure);
        using var provider = services.BuildServiceProvider();
        var http = new DefaultHttpContext { RequestServices = provider, User = new ClaimsPrincipal(new ClaimsIdentity([
            new Claim("oid", Guid.NewGuid().ToString()), new Claim("tid", Guid.NewGuid().ToString()), new Claim("scp", "never-copy")], "bearer")) };
        var launch = MobileAssistantSurface.Create(http, Guid.NewGuid(), DateTimeOffset.UtcNow.AddMinutes(5));
        var options = provider.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>().Get(MobileAssistantSurface.Scheme);
        var ticket = options.TicketDataFormat.Unprotect(launch.CookieValue)!;
        Assert.Equal(MobileAssistantSurface.Scheme, ticket.AuthenticationScheme);
        Assert.False(ticket.Properties.AllowRefresh);
        Assert.Null(ticket.Principal.FindFirst("scp"));
        Assert.DoesNotContain(launch.CookieValue, launch.ToString());
        Assert.Equal(MobileAssistantSurface.Path, options.Cookie.Path);
        Assert.True(options.Cookie.HttpOnly); Assert.False(options.SlidingExpiration);
        Assert.Equal(CookieSecurePolicy.Always, options.Cookie.SecurePolicy);
        Assert.Equal(SameSiteMode.Strict, options.Cookie.SameSite);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CookieHandlerRejectsExpiredOrInvalidTicket(bool expired)
    {
        var services = new ServiceCollection(); services.AddLogging(); services.AddDataProtection();
        services.AddAuthentication().AddCookie(MobileAssistantSurface.Scheme, MobileAssistantSurface.Configure);
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var options = provider.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>().Get(MobileAssistantSurface.Scheme);
        var value = expired ? options.TicketDataFormat.Protect(new AuthenticationTicket(
            new ClaimsPrincipal(new ClaimsIdentity([new Claim("oid", "synthetic")], MobileAssistantSurface.Scheme)),
            new AuthenticationProperties { ExpiresUtc = DateTimeOffset.UtcNow.AddMinutes(-1) }, MobileAssistantSurface.Scheme)) : "invalid";
        var http = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
        http.Request.Scheme = "https"; http.Request.Path = MobileAssistantSurface.Path + "/surface";
        http.Request.Headers.Cookie = MobileAssistantSurface.CookieName + "=" + value;
        var result = await http.AuthenticateAsync(MobileAssistantSurface.Scheme);
        Assert.False(result.Succeeded);
        Assert.NotNull(result.Failure);
    }

}
