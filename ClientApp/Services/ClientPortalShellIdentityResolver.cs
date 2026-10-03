using Domain.Entities;
using System.Security.Claims;
using Shared.Auth;
using Shared.PortalShell;

namespace ClientApp.Services;

public sealed class ClientPortalShellIdentityResolver(
    EffectiveClientContextService clientContext) : IPortalShellIdentityResolver
{
    public async Task<PortalShellIdentity> ResolveAsync(
        ClaimsPrincipal user,
        HttpContext httpContext,
        CancellationToken cancellationToken = default)
    {
        var context = await clientContext.ResolveAsync(
            user,
            httpContext.Request.Cookies,
            allowRelink: false);

        var isAgentView = context?.IsAgentView == true;
        var fallback = isAgentView ? "Agent" : "Member";
        string? first = null;
        string? last = null;
        string? storedDisplay = null;
        var roleLabel = isAgentView ? "Agent" : "Client";
        var avatarUrl = isAgentView ? "/avatar/agent/current" : "/avatar/current";

        if (context is not null)
        {
            if (isAgentView)
            {
                storedDisplay = context.AgentDisplayName;
                if (!string.IsNullOrWhiteSpace(storedDisplay))
                {
                    var parts = storedDisplay.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    first = parts.FirstOrDefault();
                    last = parts.Length > 1 ? parts[^1] : null;
                }
            }
            else
            {
                var isBusiness = string.Equals(
                    ClientRecordClassification.Resolve(
                        context.Profile.ClientUserId,
                        context.Profile.CrmNotes),
                    ClientRecordClassification.BusinessClient,
                    StringComparison.Ordinal);

                storedDisplay = context.AccountDisplayName;
                roleLabel = isBusiness ? "Business Client" : "Client";
                if (!isBusiness)
                {
                    first = PortalShellIdentityFactory.CleanHumanToken(context.Profile.FirstName);
                    last = PortalShellIdentityFactory.CleanHumanToken(context.Profile.LastName);
                }
                else
                {
                    first = storedDisplay;
                }
            }
        }

        var displayName = PortalShellIdentityFactory.BuildDisplayName(
            fallback,
            storedDisplay,
            first,
            last,
            PortalShellIdentityFactory.ClaimDisplayName(user));

        return new PortalShellIdentity(
            displayName,
            first ?? displayName.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "Member",
            PortalShellIdentityFactory.Initials(first, last, displayName),
            roleLabel,
            avatarUrl,
            PortalShellIdentityFactory.CanonicalStoreUrl,
            "/profile",
            isAgentView ? context?.AgentPhone?.Trim() ?? string.Empty : context?.Profile.Phone?.Trim() ?? string.Empty);
    }
}
