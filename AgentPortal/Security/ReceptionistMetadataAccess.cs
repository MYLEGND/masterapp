using System.Security.Claims;
using Domain.Accounts;
using Domain.Messaging;
using Infrastructure.Data;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.EntityFrameworkCore;
using Shared.Analytics;
using Shared.Auth;

namespace AgentPortal.Security;

// Endpoint-owned metadata, never a URL/header/query bypass. Only the two reviewed
// metadata actions use this marker; successful access also requires a minted grant.
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class ReceptionistMetadataReadAttribute(bool restrictedOperator = false) : Attribute, Infrastructure.Identity.IRestrictedAiReadEndpoint
{
    public bool RestrictedOperator { get; } = restrictedOperator;
    public bool AllowsRestrictedAi => RestrictedOperator;
}

internal sealed record ReceptionistMetadataGrant(MarketingOwnerScope? Owner, bool Restricted);

public static class ReceptionistMetadataAccess
{
    public const string RestrictedRole = Infrastructure.Identity.RestrictedAiIdentity.Role;
    private static readonly object GrantKey = new();
    public static bool Granted(HttpContext context) => context.Items[GrantKey] is ReceptionistMetadataGrant;
    internal static ReceptionistMetadataGrant Require(HttpContext context) =>
        context.Items[GrantKey] as ReceptionistMetadataGrant ?? throw new UnauthorizedAccessException();
    internal static void Grant(HttpContext context, ReceptionistMetadataGrant grant) => context.Items[GrantKey] = grant;

    public static void ConfigureCookieObservation(CookieAuthenticationOptions options, IConfiguration configuration)
    {
        var previous = options.Events.OnCheckSlidingExpiration;
        options.Events.OnCheckSlidingExpiration = async context =>
        {
            if (context.HttpContext.GetEndpoint()?.Metadata.GetMetadata<ReceptionistMetadataReadAttribute>() is not null ||
                context.Principal is not null && Restricted(context.Principal, configuration))
            { context.ShouldRenew = false; return; }
            await previous(context);
        };
    }

    internal static bool Restricted(ClaimsPrincipal user, IConfiguration configuration)
    {
        return Infrastructure.Identity.RestrictedAiIdentity.IsRestricted(user, configuration);
    }

    internal static MarketingOwnerScope? RegisteredOwner(ClaimsPrincipal user, IConfiguration configuration)
    {
        if (user.Identities.Count(x => x.IsAuthenticated) != 1 || !user.IsInRole(RestrictedRole)) return null;
        var subject = user.GetCanonicalUserId();
        var tenant = user.FindFirstValue("tid") ?? user.FindFirstValue("http://schemas.microsoft.com/identity/claims/tenantid");
        if (string.IsNullOrWhiteSpace(subject) || string.IsNullOrWhiteSpace(tenant)) return null;
        var registrations = configuration.GetSection("Receptionist:RestrictedOperators").GetChildren().Take(129).ToArray();
        if (registrations.Length > 128) return null;
        var matches = registrations.Where(x => string.Equals(x["SubjectId"], subject, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (matches.Length != 1 || !matches[0].GetValue<bool>("Enabled") ||
            !string.Equals(matches[0]["TenantId"], tenant, StringComparison.OrdinalIgnoreCase)) return null;
        var entry = matches[0];
        if (entry["OwnerType"] == "founder" && string.IsNullOrEmpty(entry["OwnerId"])) return MarketingOwnerScope.Founder;
        if (!Guid.TryParse(entry["OwnerId"], out var id) || id == Guid.Empty) return null;
        return entry["OwnerType"] switch { "agent" => MarketingOwnerScope.Agent(id), "business" => MarketingOwnerScope.Business(id), _ => null };
    }

    internal static async Task<bool> ActiveSubjectAsync(MasterAppDbContext db, ClaimsPrincipal user, CancellationToken ct)
    {
        var subject = user.GetCanonicalUserId()?.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(subject)) return false;
        // No profile initialization, identifying field selection, email fallback,
        // assistant binding or lifecycle mutation is permitted on metadata reads.
        var profile = await db.AgentProfiles.AsNoTracking().Where(x => x.AgentUserId.ToLower() == subject)
            .Select(x => new { x.Id, x.IsActive }).SingleOrDefaultAsync(ct);
        if (profile?.IsActive != true) return false;
        var assistant = await db.AgentAssistants.AsNoTracking().Where(x => x.AssistantUserId != null && x.AssistantUserId.ToLower() == subject)
            .Select(x => new { x.IsActive }).SingleOrDefaultAsync(ct);
        // Existing assistant assignments are a different human delegation model.
        // They cannot be implicitly upgraded to this independent restricted role.
        if (assistant is not null) return false;
        var lifecycle = await db.AccountLifecycleRecords.AsNoTracking()
            .Where(x => x.UserId == subject && x.ParticipantType == MessagingParticipantTypes.Agent)
            .Select(x => new { x.ProfileId, x.State }).SingleOrDefaultAsync(ct);
        return lifecycle is null || lifecycle.ProfileId == profile.Id && lifecycle.State == AccountLifecycleStates.Active;
    }
}
