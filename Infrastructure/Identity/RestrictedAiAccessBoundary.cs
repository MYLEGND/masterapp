using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Shared.Auth;

namespace Infrastructure.Identity;

/// <summary>Server endpoint allowlist; it cannot be supplied by a caller header or query.</summary>
public interface IRestrictedAiReadEndpoint { bool AllowsRestrictedAi { get; } }

public static class RestrictedAiIdentity
{
    public const string Role = "Legend.ReceptionistOperator";
    public static bool RegisteredSubject(string? subject, IConfiguration configuration) =>
        !string.IsNullOrWhiteSpace(subject) && configuration.GetSection("Receptionist:RestrictedOperators").GetChildren()
            .Any(x => string.Equals(x["SubjectId"], subject, StringComparison.OrdinalIgnoreCase));

    public static bool IsRestricted(ClaimsPrincipal user, IConfiguration configuration) => user.Identities
        .Where(x => x.IsAuthenticated).Any(identity =>
        {
            var principal = new ClaimsPrincipal(identity);
            return principal.IsInRole(Role) || RegisteredSubject(principal.GetCanonicalUserId(), configuration);
        });

    // Authentication callbacks use the same classifier before resolving a human
    // provisioning service. The ordinary continuation retains its original behavior.
    public static Task ContinueHumanSignInAsync(ClaimsPrincipal? user, IConfiguration configuration,
        Action reject, Func<Task> continuation)
    {
        if (user is not null && IsRestricted(user, configuration))
        { reject(); return Task.CompletedTask; }
        return continuation();
    }

    public static void DisableStatusReexecution(HttpContext context)
    {
        if (context.Features.Get<IStatusCodePagesFeature>() is { } pages) pages.Enabled = false;
    }
}

/// <summary>
/// Applies to all authenticated application lanes before human initialization.
/// The signed role and server subject classification are independent of capability
/// enablement. No registration grants access outside an exact safe endpoint.
/// </summary>
public sealed class RestrictedAiAccessBoundary(RequestDelegate next, IConfiguration configuration)
{
    public async Task InvokeAsync(HttpContext context)
    {
        if (context.GetEndpoint()?.Metadata.GetMetadata<IRestrictedAiReadEndpoint>() is not null &&
            context.Request.Headers.Authorization.ToString().StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            // Metadata uses the existing cookie lane only. Do not trigger JWT
            // discovery/key fetching or authentication callbacks during this pure
            // observation, including mixed-cookie requests.
            RestrictedAiIdentity.DisableStatusReexecution(context);
            context.Response.Headers.CacheControl = "no-store";
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }
        var principals = new List<ClaimsPrincipal>();
        if (context.User.Identities.Any(x => x.IsAuthenticated)) principals.Add(context.User);
        // Endpoint authorization can select bearer authentication after this stage.
        // Resolve registered JWT handlers now to classify that authenticated actor,
        // without replacing the ordinary endpoint's authentication policy.
        if (context.Request.Headers.Authorization.ToString().StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            var provider = context.RequestServices.GetRequiredService<IAuthenticationSchemeProvider>();
            var schemes = (await provider.GetAllSchemesAsync()).Where(x => IsJwtHandler(x.HandlerType)).ToArray();
            foreach (var scheme in schemes)
            {
                var result = await context.AuthenticateAsync(scheme.Name);
                if (result.Succeeded && result.Principal is not null) principals.Add(result.Principal);
            }
        }
        if (!principals.Any(x => RestrictedAiIdentity.IsRestricted(x, configuration))) { await next(context); return; }
        RestrictedAiIdentity.DisableStatusReexecution(context);
        context.Response.Headers.CacheControl = "no-store";
        var permitted = context.GetEndpoint()?.Metadata.GetMetadata<IRestrictedAiReadEndpoint>()?.AllowsRestrictedAi == true;
        if (!permitted || !HttpMethods.IsGet(context.Request.Method) || principals.Count != 1 ||
            principals[0].Identities.Count(x => x.IsAuthenticated) != 1)
        { context.Response.StatusCode = StatusCodes.Status403Forbidden; return; }
        await next(context); // the safe endpoint still enforces exact owner and lifecycle authorization
    }

    // The shared infrastructure project does not own a JWT package; hosts own
    // their registered, validated handlers. Inspect the server-registered type
    // lineage, never a caller-selected scheme name or a token claim.
    private static bool IsJwtHandler(Type? type)
    {
        for (; type is not null; type = type.BaseType)
            if (type.FullName == "Microsoft.AspNetCore.Authentication.JwtBearer.JwtBearerHandler") return true;
        return false;
    }
}
