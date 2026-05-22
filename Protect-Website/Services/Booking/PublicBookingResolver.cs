using System;
using Microsoft.Extensions.Options;

namespace ProtectWebsite.Services.Booking;

public sealed class PublicBookingResolver : IPublicBookingResolver
{
    private readonly IOptionsSnapshot<PublicBookingOptions> _options;

    public PublicBookingResolver(IOptionsSnapshot<PublicBookingOptions> options)
    {
        _options = options;
    }

    public PublicBookingResolution Resolve(string? agentSlug)
    {
        var options = _options.Value ?? new PublicBookingOptions();
        var effectiveEnabled = options.Enabled;
        var embedUrl = NormalizeHttpUrl(options.MicrosoftBookingsEmbedUrl);
        var fallbackUrl = NormalizeHttpUrl(options.FallbackBookingUrl);
        var preferModalOnMobile = options.PreferModalOnMobile;
        var isAgentOverride = false;

        var normalizedSlug = NormalizeSlug(agentSlug);
        if (!string.IsNullOrWhiteSpace(normalizedSlug) &&
            TryGetOverride(options, normalizedSlug, out var slugOverride) &&
            slugOverride != null)
        {
            isAgentOverride = true;

            if (slugOverride.Enabled.HasValue)
            {
                effectiveEnabled = slugOverride.Enabled.Value;
            }

            var overrideEmbed = NormalizeHttpUrl(slugOverride.MicrosoftBookingsEmbedUrl);
            if (!string.IsNullOrWhiteSpace(overrideEmbed))
            {
                embedUrl = overrideEmbed;
            }

            var overrideFallback = NormalizeHttpUrl(slugOverride.FallbackBookingUrl);
            if (!string.IsNullOrWhiteSpace(overrideFallback))
            {
                fallbackUrl = overrideFallback;
            }

            if (slugOverride.PreferModalOnMobile.HasValue)
            {
                preferModalOnMobile = slugOverride.PreferModalOnMobile.Value;
            }
        }

        var reason = effectiveEnabled
            ? string.IsNullOrWhiteSpace(embedUrl) && string.IsNullOrWhiteSpace(fallbackUrl)
                ? "missing_urls"
                : string.IsNullOrWhiteSpace(embedUrl)
                    ? "fallback_only"
                    : isAgentOverride
                        ? "agent_override"
                        : "configured"
            : "disabled";

        return new PublicBookingResolution(
            Enabled: effectiveEnabled,
            EmbedUrl: effectiveEnabled ? embedUrl : null,
            FallbackUrl: effectiveEnabled ? fallbackUrl : null,
            PreferModalOnMobile: preferModalOnMobile,
            IsAgentOverride: isAgentOverride,
            Reason: reason);
    }

    private static bool TryGetOverride(PublicBookingOptions options, string normalizedSlug, out PublicBookingAgentOverride? slugOverride)
    {
        slugOverride = null;
        if (options.AgentOverrides == null || options.AgentOverrides.Count == 0)
        {
            return false;
        }

        foreach (var kvp in options.AgentOverrides)
        {
            if (string.Equals(NormalizeSlug(kvp.Key), normalizedSlug, StringComparison.OrdinalIgnoreCase))
            {
                slugOverride = kvp.Value;
                return true;
            }
        }

        return false;
    }

    private static string? NormalizeSlug(string? agentSlug)
    {
        return string.IsNullOrWhiteSpace(agentSlug) ? null : agentSlug.Trim();
    }

    private static string? NormalizeHttpUrl(string? rawUrl)
    {
        if (string.IsNullOrWhiteSpace(rawUrl))
        {
            return null;
        }

        var trimmed = rawUrl.Trim();
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
        {
            return null;
        }

        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return uri.ToString();
    }
}
