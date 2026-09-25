using System;
using System.IdentityModel.Tokens.Jwt;
using System.Linq;
using System.Security.Claims;
using System.Text;
using System.Threading.Tasks;
using AgentPortal.Mobile;
using Infrastructure.Mobile;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Http;
using Microsoft.IdentityModel.Tokens;
using Shared.Auth;
using Xunit;

namespace AgentPortal.Tests;

public sealed class AuthenticatedRequestBindingTests
{
    private const string Tenant = "00000000-0000-0000-0000-000000000001";
    private const string User = "00000000-0000-0000-0000-000000000002";
    private const string Issuer = "https://issuer.example.invalid/" + Tenant + "/v2.0";
    private static readonly DateTime Now = new(2026, 9, 18, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task ValidatedMobileBearerWithoutSidGetsExplicitTokenBindingWithExactTokenExpiry()
    {
        var options = new JwtBearerOptions();
        MobileBearerOptions.Configure(options, new(Tenant, Issuer, Tenant, "api://" + Tenant + "/mobile_access"));
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes("synthetic-local-token-validation-key-123456789"));
        options.TokenValidationParameters.IssuerSigningKey = key;
        var token = new JwtSecurityToken(Issuer, Tenant, TokenPrincipal().Claims,
            DateTime.UtcNow.AddMinutes(-2), DateTime.UtcNow.AddMinutes(5), new SigningCredentials(key, SecurityAlgorithms.HmacSha256));
        var handler = new JwtSecurityTokenHandler { MapInboundClaims = false };
        var principal = handler.ValidateToken(handler.WriteToken(token), options.TokenValidationParameters, out var validated);
        var context = new TokenValidatedContext(new DefaultHttpContext(),
            new AuthenticationScheme(MobileApiAuthorization.BearerScheme, null, typeof(JwtBearerHandler)), options)
            { Principal = principal, SecurityToken = validated };
        await options.Events.OnTokenValidated(context);
        var binding = AuthenticatedRequestBinding.Resolve(principal, DateTime.UtcNow);
        Assert.NotNull(binding);
        Assert.StartsWith("entra-token.", binding.Id);
        Assert.Equal(76, binding.Id.Length);
        Assert.Equal(validated.ValidTo, binding.ValidUntilUtc);
        Assert.Null(principal.FindFirst("sid"));
        Assert.DoesNotContain("synthetic-uti", binding.Id);
        Assert.Null(AuthenticatedRequestBinding.Resolve(principal, validated.ValidTo));
    }

    [Fact]
    public void TokenBindingIsStableOnlyForTheSameIssuerTenantUserAndTokenIdentifier()
    {
        string Bind(string issuer = Issuer, string tenant = Tenant, string user = User, string uti = "synthetic-uti")
        {
            var principal = TokenPrincipal(tenant, user, uti);
            AuthenticatedRequestBinding.AttachValidatedEntraToken(principal, issuer, Now.AddMinutes(5), Now);
            return AuthenticatedRequestBinding.Resolve(principal, Now)!.Id;
        }
        var expected = Bind();
        Assert.Equal(expected, Bind());
        Assert.NotEqual(expected, Bind(issuer: "https://issuer.example.invalid/other/v2.0"));
        Assert.NotEqual(expected, Bind(tenant: "00000000-0000-0000-0000-000000000003"));
        Assert.NotEqual(expected, Bind(user: "00000000-0000-0000-0000-000000000004"));
        Assert.NotEqual(expected, Bind(uti: "refreshed-token"));
    }

    [Theory]
    [InlineData("missing-uti")]
    [InlineData("duplicate-uti")]
    [InlineData("duplicate-user")]
    [InlineData("expired")]
    [InlineData("non-utc")]
    [InlineData("invalid-issuer")]
    public void InvalidMobileBindingCannotFallBackToATokenSuppliedSid(string invalid)
    {
        var principal = TokenPrincipal();
        var identity = (ClaimsIdentity)principal.Identity!;
        identity.AddClaim(new("sid", "token-supplied-session"));
        if (invalid == "missing-uti") identity.RemoveClaim(identity.FindFirst("uti")!);
        if (invalid == "duplicate-uti") identity.AddClaim(new("uti", "second-token"));
        if (invalid == "duplicate-user") identity.AddClaim(new("oid", User));
        var expiry = invalid == "expired" ? Now.AddSeconds(-1) : Now.AddMinutes(5);
        if (invalid == "non-utc") expiry = DateTime.SpecifyKind(expiry, DateTimeKind.Unspecified);
        AuthenticatedRequestBinding.AttachValidatedEntraToken(principal,
            invalid == "invalid-issuer" ? "http://issuer.example.invalid" : Issuer, expiry, Now);
        Assert.Null(AuthenticatedRequestBinding.Resolve(principal, Now));
    }

    [Fact]
    public void RawClaimMarkersAndAuthenticationTypeCannotForgeServerBinding()
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim("oid", User), new Claim("tid", Tenant), new Claim("sid", "fake-session"),
            new Claim("urn:legend:authenticated-request-binding:id", "entra-token." + new string('a', 64),
                ClaimValueTypes.String, "LegendServerRequestBinding"),
            new Claim("urn:legend:authenticated-request-binding:kind", "entra-token")
        }, "LegendServerRequestBinding"));
        Assert.Null(AuthenticatedRequestBinding.Resolve(principal, Now));
    }

    [Fact]
    public void BrowserSidRemainsUnchangedAndAmbiguousSidIsDenied()
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim("sid", "browser-session") }, "Cookies"));
        Assert.Equal(new AuthenticatedRequestBinding("browser-session", null), AuthenticatedRequestBinding.Resolve(principal, Now));
        ((ClaimsIdentity)principal.Identity!).AddClaim(new("sid", "another-session"));
        Assert.Null(AuthenticatedRequestBinding.Resolve(principal, Now));
    }

    [Fact]
    public void ServerBindingSurvivesIdentityCloningButRejectsChangedPrincipalIdentity()
    {
        var principal = TokenPrincipal();
        AuthenticatedRequestBinding.AttachValidatedEntraToken(principal, Issuer, Now.AddMinutes(5), Now);
        var clone = new ClaimsPrincipal(principal.Identities.Select(identity => identity.Clone()));
        Assert.Equal(AuthenticatedRequestBinding.Resolve(principal, Now), AuthenticatedRequestBinding.Resolve(clone, Now));
        var original = (ClaimsIdentity)clone.Identity!;
        original.RemoveClaim(original.FindFirst("oid")!);
        original.AddClaim(new("oid", "00000000-0000-0000-0000-000000000003"));
        Assert.Null(AuthenticatedRequestBinding.Resolve(clone, Now));
    }

    [Fact]
    public void PersistedOperationBindingHasNoBrowserSidAndCannotOutliveItsLease()
    {
        var id = "entra-token." + new string('a', 64);
        var principal = AuthenticatedRequestBinding.CreatePersistedDelegationPrincipal(User, Tenant, id, Now.AddSeconds(30));
        Assert.Null(principal.FindFirst("sid"));
        Assert.Equal(new AuthenticatedRequestBinding(id, Now.AddSeconds(30)), AuthenticatedRequestBinding.Resolve(principal, Now));
        Assert.Null(AuthenticatedRequestBinding.Resolve(principal, Now.AddSeconds(30)));
    }

    [Fact]
    public async Task ReviewBearerCannotDelegateEvenWhenItsTokenContainsSidAndUti()
    {
        var options = new JwtBearerOptions();
        var configuration = MobileReviewAuthenticationConfiguration.FromConfiguration(
            new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build());
        MobileReviewBearerOptions.Configure(options, configuration);
        var principal = TokenPrincipal();
        ((ClaimsIdentity)principal.Identity!).AddClaim(new("sid", "review-session"));
        var context = new TokenValidatedContext(new DefaultHttpContext(),
            new AuthenticationScheme(MobileApiAuthorization.ReviewBearerScheme, null, typeof(JwtBearerHandler)), options)
            { Principal = principal };
        await options.Events.OnTokenValidated(context);
        Assert.Null(AuthenticatedRequestBinding.Resolve(principal, Now));
    }

    private static ClaimsPrincipal TokenPrincipal(string tenant = Tenant, string user = User, string uti = "synthetic-uti") =>
        new(new ClaimsIdentity(new[] { new Claim("tid", tenant), new Claim("oid", user), new Claim("uti", uti) }, "Federation"));
}
