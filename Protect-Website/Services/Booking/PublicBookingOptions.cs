using System;
using System.Collections.Generic;

namespace ProtectWebsite.Services.Booking;

public sealed class PublicBookingOptions
{
    public bool Enabled { get; set; }
    public string? MicrosoftBookingsEmbedUrl { get; set; }
    public string? FallbackBookingUrl { get; set; }
    public bool PreferModalOnMobile { get; set; } = true;
    public Dictionary<string, PublicBookingAgentOverride> AgentOverrides { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class PublicBookingAgentOverride
{
    public bool? Enabled { get; set; }
    public string? MicrosoftBookingsEmbedUrl { get; set; }
    public string? FallbackBookingUrl { get; set; }
    public bool? PreferModalOnMobile { get; set; }
}

public sealed record PublicBookingResolution(
    bool Enabled,
    string? EmbedUrl,
    string? FallbackUrl,
    bool PreferModalOnMobile,
    bool IsAgentOverride,
    string Reason)
{
    public bool HasEmbed => !string.IsNullOrWhiteSpace(EmbedUrl);
    public bool HasFallback => !string.IsNullOrWhiteSpace(FallbackUrl);
    public bool HasAnyExperience => HasEmbed || HasFallback;
};

public interface IPublicBookingResolver
{
    PublicBookingResolution Resolve(string? agentSlug);
}
