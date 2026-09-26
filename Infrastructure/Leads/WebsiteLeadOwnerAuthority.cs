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
        string founderRecipientEmail,
        string? explicitAgentSlug = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(resolver);

        var founderRecipient = (founderRecipientEmail ?? string.Empty).Trim();

        var explicitInvalid = false;
        var slug = string.IsNullOrWhiteSpace(explicitAgentSlug) ? null : explicitAgentSlug.Trim();
        if (!string.IsNullOrWhiteSpace(slug))
        {
            var bySlug = await resolver.ResolveBySlugAsync(slug, cancellationToken);
            if (bySlug.Found && bySlug.Profile is not null && !string.IsNullOrWhiteSpace(bySlug.Profile.AgentUpn))
            {
                return new WebsiteLeadOwnerResolution(
                    bySlug.Profile.AgentUpn.Trim(),
                    bySlug.Profile.Id,
                    bySlug.CanonicalSlug,
                    IsFounderPath: false,
                    ExplicitSlugInvalid: false);
            }

            explicitInvalid = true;
        }

        var isFounderPath = httpContext?.Items["IsFounderPath"] as bool? == true;
        if (httpContext?.Items.TryGetValue("TrackingProfile", out var trackingProfileObject) == true &&
            trackingProfileObject is AgentTrackingProfile trackingProfile)
        {
            var trackingSlug = httpContext.Items["TrackingSlug"] as string;
            var trackingRecipient = !string.IsNullOrWhiteSpace(trackingProfile.AgentUpn)
                ? trackingProfile.AgentUpn.Trim()
                : founderRecipient;

            return new WebsiteLeadOwnerResolution(
                isFounderPath ? founderRecipient : trackingRecipient,
                trackingProfile.Id,
                string.IsNullOrWhiteSpace(trackingSlug) ? trackingProfile.Slug : trackingSlug,
                isFounderPath,
                explicitInvalid);
        }

        return new WebsiteLeadOwnerResolution(
            founderRecipient,
            AgentProfileId: null,
            AgentSlug: null,
            isFounderPath,
            explicitInvalid);
    }
}
