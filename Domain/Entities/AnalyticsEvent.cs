using System;

namespace Domain.Entities;

public class AnalyticsEvent
{
    public long Id { get; set; }
    public Guid EventId { get; set; }
    public Guid? ClientEventId { get; set; }
    public string EventType { get; set; } = null!;
    public string? PageKey { get; set; }
    public string? SectionKey { get; set; }
    public string? ElementKey { get; set; }
    public string? ButtonLabel { get; set; }
    public string? FormKey { get; set; }
    public string? QuoteType { get; set; }
    public string? Url { get; set; }
    public string? Path { get; set; }
    public string? Referrer { get; set; }
    public string? SessionId { get; set; }
    public string? VisitorId { get; set; }
    public string? UtmSource { get; set; }
    public string? UtmMedium { get; set; }
    public string? UtmCampaign { get; set; }
    public string? UtmId { get; set; }
    public bool IsInternal { get; set; }
    public string? Environment { get; set; }
    public string? Host { get; set; }
    public Guid? AgentTrackingProfileId { get; set; }
    public string? AgentSlug { get; set; }
    public DateTime EventUtc { get; set; }
    public DateTime ReceivedUtc { get; set; }
    public string? SubmitOutcome { get; set; }
    public string? MetadataJson { get; set; }

    /// <summary>Analytics schema version for safe event evolution.</summary>
    public int SchemaVersion { get; set; } = 1;

    /// <summary>Tracking runtime/build version identifier.</summary>
    public string? TrackingVersion { get; set; }

    // ── Behavior Intelligence Engine (additive, all nullable for backward compat) ──

    /// <summary>Parsed hostname from Referrer (e.g. "facebook.com").</summary>
    public string? ReferrerHost { get; set; }

    /// <summary>Device category: desktop | mobile | tablet</summary>
    public string? DeviceType { get; set; }

    /// <summary>Browser family (e.g. Chrome, Safari, Firefox).</summary>
    public string? Browser { get; set; }

    /// <summary>Operating system family (e.g. Windows, macOS, iOS, Android).</summary>
    public string? OperatingSystem { get; set; }

    public string? TimeZone { get; set; }

    public string? Language { get; set; }

    /// <summary>Physical screen width in CSS pixels.</summary>
    public int? ScreenWidth { get; set; }

    /// <summary>Physical screen height in CSS pixels.</summary>
    public int? ScreenHeight { get; set; }

    /// <summary>Viewport width (window.innerWidth) in CSS pixels.</summary>
    public int? ViewportWidth { get; set; }

    /// <summary>Viewport height (window.innerHeight) in CSS pixels.</summary>
    public int? ViewportHeight { get; set; }

    /// <summary>Maximum scroll percentage reached on this page view (0–100).</summary>
    public int? ScrollPercent { get; set; }

    /// <summary>Total milliseconds elapsed since page load when this event fired.</summary>
    public long? DwellMilliseconds { get; set; }

    /// <summary>Accumulated active-engagement milliseconds (tab visible + user interacting).</summary>
    public long? EngagedMilliseconds { get; set; }

    /// <summary>True when the session left within a very short dwell (quick exit candidate).</summary>
    public bool? IsBounceCandidate { get; set; }

    /// <summary>True when this page was the last page viewed in the session.</summary>
    public bool? IsExitPage { get; set; }

    /// <summary>UTM term parameter.</summary>
    public string? UtmTerm { get; set; }

    /// <summary>UTM content parameter.</summary>
    public string? UtmContent { get; set; }

    /// <summary>Meta / Facebook campaign ID from click URL or fbclid mapping.</summary>
    public string? MetaCampaignId { get; set; }

    /// <summary>Meta campaign name if resolvable.</summary>
    public string? MetaCampaignName { get; set; }

    /// <summary>Meta ad set ID.</summary>
    public string? MetaAdSetId { get; set; }

    /// <summary>Meta ad set name.</summary>
    public string? MetaAdSetName { get; set; }

    /// <summary>Meta ad ID.</summary>
    public string? MetaAdId { get; set; }

    /// <summary>Meta ad name.</summary>
    public string? MetaAdName { get; set; }

    /// <summary>Ad placement (e.g. facebook_feed, instagram_story).</summary>
    public string? Placement { get; set; }

    /// <summary>Specific HTML form id attribute (complements FormKey).</summary>
    public string? FormId { get; set; }

    /// <summary>Form field name for field-level events (focus/complete/abandon). Never stores values.</summary>
    public string? FieldName { get; set; }

    /// <summary>HTML element id for dead_click / rage_click attribution.</summary>
    public string? ElementId { get; set; }

    /// <summary>Facebook click ID (fbclid) from the landing URL query string.</summary>
    public string? Fbclid { get; set; }

    /// <summary>Raw browser user-agent string.</summary>
    public string? UserAgent { get; set; }

    /// <summary>Resolved client IP address.</summary>
    public string? IpAddress { get; set; }

    /// <summary>True when browser automation/webdriver detected.</summary>
    public bool? WebDriver { get; set; }

    /// <summary>True when likely headless automation detected.</summary>
    public bool? IsHeadless { get; set; }

    /// <summary>Total mousemove count during page lifecycle.</summary>
    public int? MouseMoveCount { get; set; }
    public int? HumanInteractionCount { get; set; }

    /// <summary>Total visibility-state changes during session.</summary>
    public int? VisibilityChangeCount { get; set; }
}
