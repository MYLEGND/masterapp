using Infrastructure.Analytics;
using Microsoft.AspNetCore.Http;
using Shared.Analytics;

namespace Infrastructure.Leads;

public sealed record WebsiteLeadOwnerResolution(
    string RecipientEmail,
    Guid AgentProfileId,
    string? AgentSlug,
    bool IsFounderPath);

/// <summary>
/// Single source of truth for public Protect lead ownership and notification recipient resolution.
/// The canonical Protect resolver owns route/form/referrer precedence and rejects invalid explicit
/// scope without falling through to another owner.
/// </summary>
public static class WebsiteLeadOwnerAuthority
{
    public static async Task<WebsiteLeadOwnerResolution> ResolveAsync(
        HttpContext? httpContext,
        AgentTrackingResolver resolver,
        WebsiteIntakeRecipientResolver recipients,
        string? founderUpn,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(resolver);
        ArgumentNullException.ThrowIfNull(recipients);

        var resolved = await ProtectWebsiteOwnerResolver.ResolveAsync(
            httpContext,
            resolver,
            founderUpn,
            ct: cancellationToken);

        if (resolved is null || resolved.Profile.Id == Guid.Empty)
            throw new InvalidOperationException("The advisor link is no longer available.");

        var owner = resolved.IsFounder
            ? MarketingOwnerScope.Founder
            : MarketingOwnerScope.Agent(resolved.Profile.Id);
        var recipient = await recipients.ResolveAsync(owner, cancellationToken);
        if (string.IsNullOrWhiteSpace(recipient))
            throw new InvalidOperationException(
                $"The primary email for scoped website owner '{owner.Key}' is unavailable.");

        return new WebsiteLeadOwnerResolution(
            recipient.Trim(),
            resolved.Profile.Id,
            resolved.Slug,
            resolved.IsFounder);
    }
}
