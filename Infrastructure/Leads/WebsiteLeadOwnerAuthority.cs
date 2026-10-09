using Infrastructure.Analytics;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Shared.Analytics;

namespace Infrastructure.Leads;

public sealed record WebsiteLeadOwnerResolution(
    string RecipientEmail,
    Guid AgentProfileId,
    string? AgentSlug,
    bool IsFounderPath);

/// <summary>
/// Single source of truth for public Protect lead ownership and notification recipient resolution.
/// Route/form/referrer precedence belongs exclusively to ProtectWebsiteOwnerResolver. Invalid explicit
/// scope fails closed and never falls through to another owner.
/// </summary>
public static class WebsiteLeadOwnerAuthority
{
    public static async Task<WebsiteLeadOwnerResolution> ResolveAsync(
        HttpContext? httpContext,
        AgentTrackingResolver resolver,
        WebsiteIntakeRecipientResolver recipients,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(resolver);
        ArgumentNullException.ThrowIfNull(recipients);

        var founderUpn = httpContext?.RequestServices is { } services
            ? services.GetService<IConfiguration>()?["Founder:Upn"]
            : null;
        if (string.IsNullOrWhiteSpace(founderUpn) &&
            httpContext?.Items["TrackingProfile"] is Domain.Entities.AgentTrackingProfile profile &&
            httpContext.Items["IsFounderPath"] as bool? == true)
            founderUpn = profile.AgentUpn;

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
