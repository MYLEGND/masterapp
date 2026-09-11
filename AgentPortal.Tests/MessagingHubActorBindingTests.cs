using System;
using System.Security.Claims;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.IdentityModel.Tokens.Jwt;
using AgentPortal.Mobile;
using AgentPortal.Services;
using AgentPortal.Services.Tracking;
using Domain.Messaging;
using Infrastructure.Mobile;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.IdentityModel.Tokens;
using Moq;
using Xunit;

namespace AgentPortal.Tests;

public sealed class MessagingHubActorBindingTests
{
    [Theory]
    [InlineData("Client")]
    [InlineData("Agent")]
    public async Task ValidatedJwtUsesSelectedTypedProfileRegardlessOfClaimsAuthenticationLabel(string type)
    {
        // Real JWT validation uses a claims authentication label that differs
        // from the ASP.NET registered handler name. Both typed profiles may
        // belong to this same Entra OID.
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes("test-signing-key-with-at-least-thirty-two-bytes"));
        var handler = new JwtSecurityTokenHandler { MapInboundClaims = false };
        var jwt = handler.WriteToken(new JwtSecurityToken("test-issuer", "test-audience",
            new[] { new Claim("oid", "same-owner") }, expires: DateTime.UtcNow.AddMinutes(5),
            signingCredentials: new SigningCredentials(key, SecurityAlgorithms.HmacSha256)));
        var principal = handler.ValidateToken(jwt, new TokenValidationParameters
        {
            ValidIssuer = "test-issuer", ValidAudience = "test-audience",
            IssuerSigningKey = key, ValidateLifetime = true
        }, out _);
        Assert.NotEqual(MobileApiAuthorization.BearerScheme, principal.Identity!.AuthenticationType);
        var fixture = Create(principal, type);
        fixture.Authentication.Setup(x => x.AuthenticateAsync(fixture.Http, MobileApiAuthorization.BearerScheme))
            .ReturnsAsync(AuthenticateResult.Success(new AuthenticationTicket(principal, MobileApiAuthorization.BearerScheme)));
        var result = await fixture.Resolver.ResolveAsync(fixture.Http);
        Assert.Equal(("same-owner", type), result!.Value);
        fixture.Mobile.Verify(x => x.ResolveAsync(principal, type, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task MobileRejectionNeverFallsBackToAgentWithSameOid()
    {
        var fixture = Create(new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim("oid", "same-owner") }, "jwt")), "Client");
        fixture.Authentication.Setup(x => x.AuthenticateAsync(fixture.Http, MobileApiAuthorization.BearerScheme))
            .ReturnsAsync(AuthenticateResult.Success(new AuthenticationTicket(fixture.Http.User, MobileApiAuthorization.BearerScheme)));
        fixture.Mobile.Setup(x => x.ResolveAsync(It.IsAny<ClaimsPrincipal>(), "Client", It.IsAny<CancellationToken>()))
            .ReturnsAsync(MobileActorResolution.Failure("unavailable", "Profile unavailable."));
        Assert.Null(await fixture.Resolver.ResolveAsync(fixture.Http));
    }

    [Fact]
    public async Task MissingMobileScopeCannotSelectAProfile()
    {
        var fixture = Create(new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim("oid", "same-owner") }, "jwt")), "Client");
        fixture.Authentication.Setup(x => x.AuthenticateAsync(fixture.Http, MobileApiAuthorization.BearerScheme))
            .ReturnsAsync(AuthenticateResult.Success(new AuthenticationTicket(fixture.Http.User, MobileApiAuthorization.BearerScheme)));
        fixture.Authorization.Setup(x => x.AuthorizeAsync(It.IsAny<ClaimsPrincipal>(), It.IsAny<object>(), MobileApiAuthorization.PolicyName))
            .ReturnsAsync(AuthorizationResult.Failed());
        Assert.Null(await fixture.Resolver.ResolveAsync(fixture.Http));
        fixture.Mobile.Verify(x => x.ResolveAsync(It.IsAny<ClaimsPrincipal>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task WebCookieKeepsExistingAgentAuthorityAndIgnoresUntrustedProfileHeader()
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim("oid", "same-owner") }, "Cookies"));
        var fixture = Create(principal, "Client");
        fixture.Authentication.Setup(x => x.AuthenticateAsync(fixture.Http, MobileApiAuthorization.BearerScheme))
            .ReturnsAsync(AuthenticateResult.NoResult());
        fixture.Authentication.Setup(x => x.AuthenticateAsync(fixture.Http, CookieAuthenticationDefaults.AuthenticationScheme))
            .ReturnsAsync(AuthenticateResult.Success(new AuthenticationTicket(principal, CookieAuthenticationDefaults.AuthenticationScheme)));
        Assert.Equal(("same-owner", "Agent"), (await fixture.Resolver.ResolveAsync(fixture.Http))!.Value);
        fixture.Mobile.Verify(x => x.ResolveAsync(It.IsAny<ClaimsPrincipal>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task FailedBearerCannotFallThroughToCookieAgent()
    {
        var fixture = Create(new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim("oid", "same-owner") }, "Cookies")), "Client");
        fixture.Authentication.Setup(x => x.AuthenticateAsync(fixture.Http, MobileApiAuthorization.BearerScheme))
            .ReturnsAsync(AuthenticateResult.Fail("Rejected token"));
        Assert.Null(await fixture.Resolver.ResolveAsync(fixture.Http));
    }

    private static Fixture Create(ClaimsPrincipal principal, string type)
    {
        var http = new DefaultHttpContext { User = principal };
        http.Request.Headers[MobileApiAuthorization.ParticipantTypeHeader] = type;
        var auth = new Mock<IAuthenticationService>(MockBehavior.Strict);
        var authorization = new Mock<IAuthorizationService>(MockBehavior.Strict);
        authorization.Setup(x => x.AuthorizeAsync(It.IsAny<ClaimsPrincipal>(), It.IsAny<object>(), MobileApiAuthorization.PolicyName))
            .ReturnsAsync(AuthorizationResult.Success());
        http.RequestServices = new ServiceCollection().AddSingleton(auth.Object).AddSingleton(authorization.Object).BuildServiceProvider();
        var mobile = new Mock<IMobileActorResolver>(MockBehavior.Strict);
        var selected = new MobileResolvedActor(new MessagingActor("same-owner", type), Guid.NewGuid(), "Test account");
        mobile.Setup(x => x.ResolveAsync(It.IsAny<ClaimsPrincipal>(), type, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MobileActorResolution(true, null, null, new[] { selected }, selected, false));
        var agent = new EffectiveAgentContext(new HttpContextAccessor { HttpContext = http },
            Mock.Of<IAgentTrackingService>(), NullLogger<EffectiveAgentContext>.Instance);
        return new(http, auth, authorization, mobile, new AgentPortalMessagingActorContextResolver(agent, mobile.Object));
    }

    private sealed record Fixture(DefaultHttpContext Http, Mock<IAuthenticationService> Authentication,
        Mock<IAuthorizationService> Authorization, Mock<IMobileActorResolver> Mobile,
        AgentPortalMessagingActorContextResolver Resolver);
}
