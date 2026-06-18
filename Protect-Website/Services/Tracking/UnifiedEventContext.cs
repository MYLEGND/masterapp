using System;

namespace ProtectWebsite.Services.Tracking;

/// <summary>
/// SINGLE SOURCE OF TRUTH EVENT CONTRACT
/// This is the unified model that all ingestion systems MUST consume.
/// </summary>
public sealed record UnifiedEventContext
{
    // =========================
    // CORE EVENT IDENTITY
    // =========================
    public string? EventId { get; init; }
    public string? EventName { get; init; }
    public string? EventCategory { get; init; }

    // =========================
    // SESSION / USER
    // =========================
    public string? SessionId { get; init; }
    public string? VisitorId { get; init; }

    // =========================
    // PAGE / NAVIGATION
    // =========================
    public string? Url { get; init; }
    public string? Referrer { get; init; }
    public string? PageKey { get; init; }
    public string? EffectivePageKey { get; init; }
    public string? PageVariant { get; init; }
    public string? PageMode { get; init; }

    // =========================
    // CLIENT CONTEXT (BROWSER)
    // =========================
    public string? DeviceType { get; init; }
    public string? Browser { get; init; }
    public string? OperatingSystem { get; init; }
    public string? UserAgent { get; init; }
    public string? IpAddress { get; init; }

    public int? ViewportWidth { get; init; }
    public int? ViewportHeight { get; init; }
    public int? ScreenWidth { get; init; }
    public int? ScreenHeight { get; init; }

    public bool? WebDriver { get; init; }
    public bool? IsHeadless { get; init; }

    public int? MouseMoveCount { get; init; }
    public int? HumanInteractionCount { get; init; }
    public int? VisibilityChangeCount { get; init; }

    public string? Language { get; init; }
    public string? TimeZone { get; init; }

    // =========================
    // ATTRIBUTION (MARKETING)
    // =========================
    public string? UtmSource { get; init; }
    public string? UtmMedium { get; init; }
    public string? UtmCampaign { get; init; }
    public string? UtmId { get; init; }
    public string? UtmContent { get; init; }

    public string? Fbclid { get; init; }
    public string? Fbc { get; init; }
    public string? Fbp { get; init; }

    // =========================
    // TRACKING META
    // =========================
    public string? AgentSlug { get; init; }
    public Guid? AgentTrackingProfileId { get; init; }

    public string? QuoteType { get; init; }
    public int? StepNumber { get; init; }
    public string? StepName { get; init; }

    public bool? BrowserEventSent { get; init; }

    public object? Metadata { get; init; }
}
