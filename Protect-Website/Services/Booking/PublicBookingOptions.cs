using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace ProtectWebsite.Services.Booking;

public sealed class PublicBookingOptions
{
    public bool Enabled { get; set; }
    public string? MicrosoftBookingsEmbedUrl { get; set; }
    public string? FallbackBookingUrl { get; set; }
    public string? BookingPageIdOrMailbox { get; set; }
    public string? CalendarUserId { get; set; }
    public string? CalendarEmail { get; set; }
    public bool PreferModalOnMobile { get; set; } = true;
    public Dictionary<string, PublicBookingAgentOverride> AgentOverrides { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class PublicBookingAgentOverride
{
    public bool? Enabled { get; set; }
    public string? MicrosoftBookingsEmbedUrl { get; set; }
    public string? FallbackBookingUrl { get; set; }
    public string? BookingPageIdOrMailbox { get; set; }
    public string? CalendarUserId { get; set; }
    public string? CalendarEmail { get; set; }
    public bool? PreferModalOnMobile { get; set; }
}

public sealed record PublicBookingResolveContext(
    Guid? WebsiteLeadId = null,
    Guid? AgentTrackingProfileId = null,
    string? AgentUserId = null,
    string? AgentSlug = null);

public static class PublicBookingConfigurationSources
{
    public const string None = "none";
    public const string AgentProfile = "agent_profile";
    public const string SlugOverride = "slug_override";
    public const string GlobalFallback = "global_fallback";
}

public sealed record PublicBookingResolution(
    bool Enabled,
    string? EmbedUrl,
    string? FallbackUrl,
    bool PreferModalOnMobile,
    bool IsAgentOverride,
    string Reason,
    string ConfigurationSource,
    Guid? AgentTrackingProfileId,
    string? AgentUserId,
    string? AgentSlug,
    string? CalendarUserId,
    string? CalendarEmail,
    string? BookingPageIdOrMailbox)
{
    public bool HasEmbed => !string.IsNullOrWhiteSpace(EmbedUrl);
    public bool HasFallback => !string.IsNullOrWhiteSpace(FallbackUrl);
    public bool HasAnyExperience => HasEmbed || HasFallback;
};

public interface IPublicBookingResolver
{
    PublicBookingResolution Resolve(string? agentSlug);
    Task<PublicBookingResolution> ResolveAsync(PublicBookingResolveContext context, CancellationToken cancellationToken = default);
}
