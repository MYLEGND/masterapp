using System.Globalization;
using System.Security.Claims;
using AgentPortal.Security;
using Infrastructure.Mobile;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.Extensions.Options;
using Shared.Auth;
using Infrastructure.Identity;
using Domain.Accounts;

namespace AgentPortal.Mobile;

// A channel adapter for the existing assistant, never a general Portal login.
public static class MobileAssistantSurface
{
    public const string Scheme = "LegendMobileAssistant";
    public const string PolicyScheme = "LegendAssistantSurface";
    public const string CookieName = "__Secure-LegendAssistant";
    public const string Path = "/founder/legend-ai";
    public const string ProfileClaim = "legend_assistant_agent_profile";

    public static void Configure(CookieAuthenticationOptions options)
    {
        options.Cookie.Name = CookieName;
        options.Cookie.Path = Path;
        options.Cookie.HttpOnly = true;
        options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
        options.Cookie.SameSite = SameSiteMode.Strict;
        options.SlidingExpiration = false;
        options.ExpireTimeSpan = TimeSpan.FromMinutes(20);
        options.Events.OnRedirectToLogin = context => { context.Response.StatusCode = 401; return Task.CompletedTask; };
        options.Events.OnRedirectToAccessDenied = context => { context.Response.StatusCode = 403; return Task.CompletedTask; };
        options.Events.OnValidatePrincipal = async context =>
        {
            context.Response.Headers["Content-Security-Policy"] = "default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data: blob:; media-src 'self' blob:; worker-src 'self' blob:; frame-src 'none'; object-src 'none'; base-uri 'self'; form-action 'self'";
            context.Response.Headers["Referrer-Policy"] = "no-referrer";
            var principal = context.Principal;
            var configuration = context.HttpContext.RequestServices.GetRequiredService<MobileAuthConfiguration>();
            if (!context.Request.Path.StartsWithSegments(Path) || !FounderGuard.IsFounder(principal) ||
                !configuration.IsConfigured || !string.Equals(configuration.TenantId, principal?.FindFirstValue("tid"), StringComparison.OrdinalIgnoreCase))
            { context.RejectPrincipal(); return; }
            var resolver = context.HttpContext.RequestServices.GetRequiredService<IMobileActorResolver>();
            var actor = await resolver.ResolveAsync(principal!, "Agent", context.HttpContext.RequestAborted);
            if (!actor.Succeeded || actor.SelectedActor is null ||
                actor.SelectedActor.ProfileId.ToString("D") != principal!.FindFirstValue(ProfileClaim))
            { context.RejectPrincipal(); return; }
            var lifecycle = await context.HttpContext.RequestServices.GetRequiredService<IAccountLifecycleService>()
                .GetAsync(new AccountLifecycleSubject(actor.SelectedActor.Actor.UserId,
                    actor.SelectedActor.Actor.ParticipantType, actor.SelectedActor.ProfileId), context.HttpContext.RequestAborted);
            if (!lifecycle.AllowsFullAccess) context.RejectPrincipal();
        };
    }

    public static DateTimeOffset? Expiry(ClaimsPrincipal principal, DateTimeOffset now)
    {
        if (!long.TryParse(principal.FindFirstValue("exp"), NumberStyles.None, CultureInfo.InvariantCulture, out var seconds)) return null;
        DateTimeOffset expiry;
        try { expiry = DateTimeOffset.FromUnixTimeSeconds(seconds); }
        catch (ArgumentOutOfRangeException) { return null; }
        return expiry <= now ? null : expiry < now.AddMinutes(20) ? expiry : now.AddMinutes(20);
    }

    public static MobileAssistantLaunch Create(HttpContext context, Guid profileId, DateTimeOffset expires)
    {
        var claims = context.User.Claims.Where(c => c.Type is "oid" or "tid" or "sub" or "preferred_username" or "name" or
            ClaimTypes.NameIdentifier or ClaimTypes.Name or ClaimTypes.Email).ToList();
        claims.Add(new Claim(ProfileClaim, profileId.ToString("D")));
        claims.RemoveAll(c => c.Type == "oid");
        claims.Add(new Claim("oid", context.User.GetCanonicalUserId()!));
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, Scheme));
        var properties = new AuthenticationProperties { IssuedUtc = DateTimeOffset.UtcNow, ExpiresUtc = expires, AllowRefresh = false };
        var options = context.RequestServices.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>().Get(Scheme);
        var value = options.TicketDataFormat.Protect(new AuthenticationTicket(principal, properties, Scheme));
        return new MobileAssistantLaunch(Path + "/surface", CookieName, value, Path, expires);
    }
}

public sealed record MobileAssistantLaunch(string LaunchPath, string CookieName, string CookieValue, string CookiePath, DateTimeOffset ExpiresUtc)
{
    public override string ToString() => "MobileAssistantLaunch(redacted)";
}
