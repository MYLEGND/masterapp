using Infrastructure.Mobile;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.IdentityModel.Tokens;

namespace AgentPortal.Mobile;

public static class MobileApiAuthorization
{
    public const string BearerScheme = "LegendMobileBearer";
    public const string PolicyName = "LegendMobileApi";
    public const string ParticipantTypeHeader = "X-Legend-Participant-Type";

    public static void ConfigurePolicy(AuthorizationPolicyBuilder policy)
    {
        policy.AuthenticationSchemes.Add(BearerScheme);
        policy.RequireAuthenticatedUser();
        policy.AddRequirements(new MobileApiScopeRequirement());
    }
}

public static class MobileApiRoute
{
    private const string Prefix = "/api/v1/mobile";

    public static bool IsMobileApi(HttpRequest request) =>
        request.Path.StartsWithSegments(Prefix, StringComparison.OrdinalIgnoreCase);
}

public sealed class MobileApiScopeRequirement : IAuthorizationRequirement;

public sealed class MobileApiScopeAuthorizationHandler : AuthorizationHandler<MobileApiScopeRequirement>
{
    private readonly MobileAuthConfiguration _configuration;

    public MobileApiScopeAuthorizationHandler(MobileAuthConfiguration configuration)
    {
        _configuration = configuration;
    }

    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        MobileApiScopeRequirement requirement)
    {
        if (!_configuration.IsConfigured ||
            string.IsNullOrWhiteSpace(_configuration.TenantId) ||
            string.IsNullOrWhiteSpace(_configuration.RequiredScopeName))
        {
            return Task.CompletedTask;
        }

        var tenantMatches = context.User.FindAll("tid")
            .Any(claim => string.Equals(claim.Value, _configuration.TenantId, StringComparison.OrdinalIgnoreCase));
        if (!tenantMatches)
            return Task.CompletedTask;

        var scopes = context.User.FindAll("scp")
            .SelectMany(claim => claim.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        if (scopes.Contains(_configuration.RequiredScopeName, StringComparer.Ordinal))
            context.Succeed(requirement);

        return Task.CompletedTask;
    }
}

public static class MobileBearerOptions
{
    public static void Configure(JwtBearerOptions options, MobileAuthConfiguration configuration)
    {
        options.MapInboundClaims = false;
        options.RequireHttpsMetadata = true;
        options.SaveToken = false;
        options.IncludeErrorDetails = false;
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ClockSkew = TimeSpan.FromMinutes(2),
            NameClaimType = "name",
            RoleClaimType = "roles"
        };

        if (configuration.IsConfigured)
        {
            options.Authority = configuration.Authority;
            options.Audience = configuration.TokenAudience;
            options.TokenValidationParameters.ValidIssuer = configuration.Authority;
            options.TokenValidationParameters.ValidAudience = configuration.TokenAudience;
        }
        else
        {
            // A deliberately impossible validation target keeps this optional,
            // configuration-driven feature fail-closed until its Entra contract
            // is supplied through deployment configuration.
            options.TokenValidationParameters.ValidIssuer = "urn:legend:mobile-auth:unconfigured";
            options.TokenValidationParameters.ValidAudience = "urn:legend:mobile-api:unconfigured";
        }

        options.Events = new JwtBearerEvents
        {
            OnAuthenticationFailed = context =>
            {
                context.HttpContext.RequestServices
                    .GetRequiredService<ILoggerFactory>()
                    .CreateLogger("LegendMobileBearer")
                    .LogWarning("Mobile bearer authentication failed for {Path}.", context.HttpContext.Request.Path);
                return Task.CompletedTask;
            },
            OnChallenge = context =>
            {
                context.HandleResponse();
                return MobileApiErrorWriter.WriteAsync(
                    context.HttpContext,
                    StatusCodes.Status401Unauthorized,
                    "mobile_authentication_required",
                    "A valid mobile session is required.");
            },
            OnForbidden = context => MobileApiErrorWriter.WriteAsync(
                context.HttpContext,
                StatusCodes.Status403Forbidden,
                "mobile_access_forbidden",
                "You do not have access to this mobile action.")
        };
    }
}

public sealed record MobileApiErrorResponse(
    string Code,
    string Message,
    string CorrelationId,
    IReadOnlyDictionary<string, string[]> Errors);

public static class MobileApiErrorWriter
{
    public static Task WriteStatusCodeAsync(HttpContext context)
    {
        var statusCode = context.Response.StatusCode;
        var code = statusCode == StatusCodes.Status404NotFound
            ? "mobile_route_not_found"
            : "mobile_request_failed";
        var message = statusCode == StatusCodes.Status404NotFound
            ? "The requested mobile API endpoint was not found."
            : "The mobile API request could not be completed.";

        return WriteAsync(context, statusCode, code, message);
    }

    public static Task WriteUnhandledExceptionAsync(HttpContext context) =>
        WriteAsync(
            context,
            StatusCodes.Status500InternalServerError,
            "mobile_request_failed",
            "The mobile API request could not be completed.");

    public static Task WriteAsync(HttpContext context, int statusCode, string code, string message)
    {
        var correlationId = context.TraceIdentifier;
        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "application/json";
        context.Response.Headers["X-Correlation-ID"] = correlationId;
        return context.Response.WriteAsJsonAsync(
            new MobileApiErrorResponse(code, message, correlationId, new Dictionary<string, string[]>()),
            cancellationToken: context.RequestAborted);
    }
}
