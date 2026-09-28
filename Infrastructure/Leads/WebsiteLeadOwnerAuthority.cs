using Domain.Entities;
using Infrastructure.Analytics;
using Microsoft.AspNetCore.Http;

namespace Infrastructure.Leads;

public sealed record WebsiteLeadOwnerResolution(
    string RecipientEmail,
    Guid? AgentProfileId,
    string? AgentSlug,
    bool IsFounderPath,
    bool ExplicitSlugInvalid);

/// <summary>
/// Single source of truth for public Protect lead ownership and notification recipient resolution.
/// Presentation labels never participate in ownership. Explicit slugs, middleware-resolved tracking
/// profiles, and founder fallback are the only accepted routing inputs.
/// </summary>
public static class WebsiteLeadOwnerAuthority
{
    public static async Task<WebsiteLeadOwnerResolution> ResolveAsync(
        HttpContext? httpContext,
        AgentTrackingResolver resolver,
        WebsiteIntakeRecipientResolver recipients,
        string? explicitAgentSlug = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(resolver);
        ArgumentNullException.ThrowIfNull(recipients);

        var explicitInvalid = false;
        var isFounderPath = httpContext?.Items["IsFounderPath"] as bool? == true;
        var slug = string.IsNullOrWhiteSpace(explicitAgentSlug) ? null : explicitAgentSlug.Trim();

        if (!string.IsNullOrWhiteSpace(slug))
        {
            var bySlug = await resolver.ResolveBySlugAsync(slug, cancellationToken);
            if (bySlug.Found && bySlug.Profile is not null)
            {
                var owner = isFounderPath
                    ? MarketingOwnerScope.Founder
                    : MarketingOwnerScope.Agent(bySlug.Profile.Id);
                var recipient = await RequirePrimaryRecipientAsync(recipients, owner, cancellationToken);
                return new WebsiteLeadOwnerResolution(
                    recipient,
                    bySlug.Profile.Id,
                    bySlug.CanonicalSlug,
                    isFounderPath,
                    ExplicitSlugInvalid: false);
            }

            explicitInvalid = true;
        }

        if (httpContext?.Items.TryGetValue("TrackingProfile", out var trackingProfileObject) == true &&
            trackingProfileObject is AgentTrackingProfile trackingProfile)
        {
            var trackingSlug = httpContext.Items["TrackingSlug"] as string;
            var owner = isFounderPath
                ? MarketingOwnerScope.Founder
                : MarketingOwnerScope.Agent(trackingProfile.Id);
            var recipient = await RequirePrimaryRecipientAsync(recipients, owner, cancellationToken);

            return new WebsiteLeadOwnerResolution(
                recipient,
                trackingProfile.Id,
                string.IsNullOrWhiteSpace(trackingSlug) ? trackingProfile.Slug : trackingSlug,
                isFounderPath,
                explicitInvalid);
        }

        var founderRecipient = await RequirePrimaryRecipientAsync(
            recipients,
            MarketingOwnerScope.Founder,
            cancellationToken);

        return new WebsiteLeadOwnerResolution(
            founderRecipient,
            AgentProfileId: null,
            AgentSlug: null,
            isFounderPath,
            explicitInvalid);
    }

    private static async Task<string> RequirePrimaryRecipientAsync(
        WebsiteIntakeRecipientResolver recipients,
        MarketingOwnerScope owner,
        CancellationToken cancellationToken)
    {
        var recipient = await recipients.ResolveAsync(owner, cancellationToken);
        if (string.IsNullOrWhiteSpace(recipient))
            throw new InvalidOperationException(
                $"The primary email for scoped website owner '{owner.Key}' is unavailable.");
        return recipient.Trim();
    }
}
