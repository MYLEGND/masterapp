using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Infrastructure.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;

namespace AgentPortal.Mobile;

/// <summary>
/// Server-only configuration for Apple's App Review credential.
///
/// This does not replace Microsoft Entra authentication. It creates one narrowly
/// scoped review credential that can resolve exactly one existing client profile,
/// after which every protected request passes through the existing mobile bearer
/// policy, actor resolver, authorization, capabilities, and business services.
/// </summary>
public sealed record MobileReviewAuthenticationConfiguration(
    bool Enabled,
    string? Username,
    string? PasswordSha256,
    string? SigningKey)
{
    public const string SectionName = "AppReviewAuthentication";
    public const string Issuer = "urn:legend:mobile:app-review";
    public const string Audience = "urn:legend:mobile-api:app-review";
    public const string AuthenticationClaimType = "legend_auth";
    public const string AuthenticationClaimValue = "app_review";

    public static MobileReviewAuthenticationConfiguration FromConfiguration(
        IConfiguration configuration)
    {
        var section = configuration.GetSection(SectionName);
        return new MobileReviewAuthenticationConfiguration(
            section.GetValue<bool>("Enabled"),
            Normalize(section["Username"]),
            Normalize(section["PasswordSha256"]),
            Normalize(section["SigningKey"]));
    }

    public byte[]? PasswordHashBytes => DecodeHex(PasswordSha256);

    public byte[]? SigningKeyBytes => DecodeBase64(SigningKey);

    public bool IsConfigured =>
        Enabled &&
        !string.IsNullOrWhiteSpace(Username) &&
        PasswordHashBytes is { Length: 32 } &&
        SigningKeyBytes is { Length: >= 32 };

    private static string? Normalize(string? value)
    {
        var normalized = value?.Trim();
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }

    private static byte[]? DecodeHex(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        try
        {
            return Convert.FromHexString(value.Trim());
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static byte[]? DecodeBase64(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        try
        {
            return Convert.FromBase64String(value.Trim());
        }
        catch (FormatException)
        {
            return null;
        }
    }
}

public sealed record MobileReviewAuthenticationResult(
    bool Succeeded,
    string? AccessToken,
    int ExpiresInSeconds)
{
    public static MobileReviewAuthenticationResult Failure() =>
        new(false, null, 0);
}

/// <summary>
/// Validates the dedicated review credential and binds it to one existing
/// client-only Legend identity.
///
/// The submitted username is never placed into the bearer token as authority.
/// The resulting oid comes from the existing ClientProfile identity record.
/// </summary>
public sealed class MobileReviewAuthenticationService
{
    private static readonly TimeSpan TokenLifetime = TimeSpan.FromHours(12);

    private readonly MasterAppDbContext _db;
    private readonly MobileReviewAuthenticationConfiguration _configuration;
    private readonly IConfiguration _applicationConfiguration;

    public MobileReviewAuthenticationService(
        MasterAppDbContext db,
        MobileReviewAuthenticationConfiguration configuration,
        IConfiguration applicationConfiguration)
    {
        _db = db;
        _configuration = configuration;
        _applicationConfiguration = applicationConfiguration;
    }

    public async Task<MobileReviewAuthenticationResult> AuthenticateAsync(
        string? username,
        string? password,
        CancellationToken cancellationToken)
    {
        if (!_configuration.IsConfigured ||
            string.IsNullOrWhiteSpace(username) ||
            string.IsNullOrEmpty(password) ||
            !string.Equals(
                username.Trim(),
                _configuration.Username,
                StringComparison.OrdinalIgnoreCase) ||
            !PasswordMatches(password))
        {
            return MobileReviewAuthenticationResult.Failure();
        }

        var normalizedUsername = _configuration.Username!
            .Trim()
            .ToLowerInvariant();

        // The configured review username may locate the existing account once,
        // server-side. It never becomes protected-request identity authority.
        var profiles = await _db.ClientProfiles
            .AsNoTracking()
            .Where(profile =>
                (profile.NormalizedEmail != null &&
                 profile.NormalizedEmail.ToLower() == normalizedUsername) ||
                profile.Email.ToLower() == normalizedUsername)
            .Take(2)
            .ToListAsync(cancellationToken);

        if (profiles.Count != 1)
            return MobileReviewAuthenticationResult.Failure();

        var profile = profiles[0];

        // Prefer the canonical external identity when present. This is the same
        // value the existing MobileActorResolver already accepts as its oid.
        var canonicalUserId =
            !string.IsNullOrWhiteSpace(profile.ExternalIdentityObjectId)
                ? profile.ExternalIdentityObjectId.Trim().ToLowerInvariant()
                : profile.ClientUserId?.Trim().ToLowerInvariant();

        if (string.IsNullOrWhiteSpace(canonicalUserId))
            return MobileReviewAuthenticationResult.Failure();

        // App Review access is deliberately client-only. A shared Agent/Client
        // identity would permit role switching and therefore fails closed.
        var alsoActiveAgent = await _db.AgentProfiles
            .AsNoTracking()
            .AnyAsync(
                agent =>
                    agent.IsActive &&
                    agent.AgentUserId.ToLower() == canonicalUserId,
                cancellationToken);

        if (alsoActiveAgent)
            return MobileReviewAuthenticationResult.Failure();

        // Never allow the Founder identity through the App Review credential.
        var founderOid =
            _applicationConfiguration["Founder:Oid"] ??
            _applicationConfiguration["Founder__Oid"] ??
            Environment.GetEnvironmentVariable("FOUNDER_OID");

        if (!string.IsNullOrWhiteSpace(founderOid) &&
            string.Equals(
                founderOid.Trim(),
                canonicalUserId,
                StringComparison.OrdinalIgnoreCase))
        {
            return MobileReviewAuthenticationResult.Failure();
        }

        var signingKeyBytes = _configuration.SigningKeyBytes;
        if (signingKeyBytes is null)
            return MobileReviewAuthenticationResult.Failure();

        var now = DateTime.UtcNow;
        var expires = now.Add(TokenLifetime);
        var displayName = $"{profile.FirstName} {profile.LastName}".Trim();

        var claims = new[]
        {
            new Claim("oid", canonicalUserId),
            new Claim(
                "name",
                string.IsNullOrWhiteSpace(displayName)
                    ? "App Review"
                    : displayName),
            new Claim(
                MobileReviewAuthenticationConfiguration.AuthenticationClaimType,
                MobileReviewAuthenticationConfiguration.AuthenticationClaimValue),
            new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("D"))
        };

        var token = new JwtSecurityToken(
            issuer: MobileReviewAuthenticationConfiguration.Issuer,
            audience: MobileReviewAuthenticationConfiguration.Audience,
            claims: claims,
            notBefore: now,
            expires: expires,
            signingCredentials: new SigningCredentials(
                new SymmetricSecurityKey(signingKeyBytes),
                SecurityAlgorithms.HmacSha256));

        return new MobileReviewAuthenticationResult(
            true,
            new JwtSecurityTokenHandler().WriteToken(token),
            checked((int)TokenLifetime.TotalSeconds));
    }

    private bool PasswordMatches(string password)
    {
        var expected = _configuration.PasswordHashBytes;
        if (expected is not { Length: 32 })
            return false;

        var actual = SHA256.HashData(Encoding.UTF8.GetBytes(password));
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }
}

public sealed record MobileReviewSignInRequest(
    string? Username,
    string? Password);

public sealed record MobileReviewTokenResponse(
    string AccessToken,
    int ExpiresIn);

[ApiController]
[Route("api/v1/mobile")]
[AllowAnonymous]
[IgnoreAntiforgeryToken]
public sealed class MobileReviewAuthenticationController : ControllerBase
{
    private readonly MobileReviewAuthenticationService _authentication;

    public MobileReviewAuthenticationController(
        MobileReviewAuthenticationService authentication)
    {
        _authentication = authentication;
    }

    [HttpPost("review-session")]
    [EnableRateLimiting("mobile-review-auth")]
    public async Task<IActionResult> SignIn(
        [FromBody] MobileReviewSignInRequest? request,
        CancellationToken cancellationToken)
    {
        var result = await _authentication.AuthenticateAsync(
            request?.Username,
            request?.Password,
            cancellationToken);

        if (!result.Succeeded || string.IsNullOrWhiteSpace(result.AccessToken))
        {
            // Intentionally do not reveal whether the username, password,
            // configuration, or mapped member was the rejected component.
            return Unauthorized(new
            {
                code = "mobile_review_sign_in_failed",
                message = "The App Review credentials could not be verified."
            });
        }

        Response.Headers["X-Correlation-ID"] = HttpContext.TraceIdentifier;

        return Ok(new MobileReviewTokenResponse(
            result.AccessToken,
            result.ExpiresInSeconds));
    }
}
