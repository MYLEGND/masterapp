using System.Text.Json;

namespace ProtectWebsite.Services.MetaSignal;

public sealed class MetaSignalAttributionPayload
{
    public string? UtmSource { get; set; }
    public string? UtmMedium { get; set; }
    public string? UtmCampaign { get; set; }
    public string? UtmId { get; set; }
    public string? UtmContent { get; set; }
    public string? Fbclid { get; set; }
    public string? Fbc { get; set; }
    public string? Fbp { get; set; }
    public string? MetaCampaignId { get; set; }
    public string? MetaAdSetId { get; set; }
    public string? MetaAdId { get; set; }
}

public sealed class MetaSignalScorePayload
{
    public int IntentScore { get; set; }
    public int EngagementScore { get; set; }
    public int QualificationScore { get; set; }
    public int FrictionScore { get; set; }
    public int TotalSignalScore { get; set; }
}

public sealed class MetaSignalClientContextPayload
{

    public string? DeviceType { get; set; }
    public string? Browser { get; set; }
    public string? OperatingSystem { get; set; }
    public string? UserAgent { get; set; }
    public int? ViewportWidth { get; set; }
    public int? ViewportHeight { get; set; }
    public int? ScreenWidth { get; set; }
    public int? ScreenHeight { get; set; }
    public bool? WebDriver { get; set; }
    public bool? IsHeadless { get; set; }
    public int? MouseMoveCount { get; set; }
    public int? HumanInteractionCount { get; set; }
    public int? VisibilityChangeCount { get; set; }
    public string? Language { get; set; }
    public string? TimeZone { get; set; }
}

public sealed class MetaSignalIngestRequest
{
    public string EventName { get; set; } = string.Empty;
    public string EventId { get; set; } = string.Empty;
    public string QuoteType { get; set; } = string.Empty;
    public string? PageKey { get; set; }
    public string? EffectivePageKey { get; set; }
    public string? PageVariant { get; set; }
    public string? PageMode { get; set; }
    public string? EventCategory { get; set; }
    public int? StepNumber { get; set; }
    public string? StepName { get; set; }
    public string? Url { get; set; }
    public string? Referrer { get; set; }
    public string? SessionId { get; set; }
    public string? VisitorId { get; set; }
    public Guid? AgentTrackingProfileId { get; set; }
    public string? AgentSlug { get; set; }
    public bool BrowserEventSent { get; set; }
    public string? ScoreTier { get; set; }
    public MetaSignalScorePayload? Score { get; set; }
    public MetaSignalAttributionPayload? Attribution { get; set; }
    public MetaSignalClientContextPayload? ClientContext { get; set; }
    public JsonElement Metadata { get; set; }
}

public sealed class MetaSignalConfirmedLeadRequest
{
    public Guid LeadId { get; set; }
    public string QuoteType { get; set; } = string.Empty;
    public string PageKey { get; set; } = string.Empty;
    public string EffectivePageKey { get; set; } = string.Empty;
    public string? PageVariant { get; set; }
    public string PageMode { get; set; } = string.Empty;
    public string? Url { get; set; }
    public string? Referrer { get; set; }
    public string? SessionId { get; set; }
    public string? VisitorId { get; set; }
    public Guid? AgentTrackingProfileId { get; set; }
    public string? AgentSlug { get; set; }
    public string? UtmSource { get; set; }
    public string? UtmMedium { get; set; }
    public string? UtmCampaign { get; set; }
    public string? UtmId { get; set; }
    public string? UtmContent { get; set; }
    public string? Fbclid { get; set; }
    public string? Email { get; set; }
    public string? Phone { get; set; }
    public bool AllowHashedContactData { get; set; }
    public DateTime CreatedUtc { get; set; }
    public string LeadEventId { get; set; } = string.Empty;
    public bool LeadMetaServerSent { get; set; }
    public string? LeadMetaServerStatus { get; set; }
    public string? LeadMetaServerNote { get; set; }
    public string? PixelId { get; set; }
    public string? AccessToken { get; set; }
    public string? TestEventCode { get; set; }
    public string? PixelOwnerType { get; set; }
    public JsonElement Metadata { get; set; }
}

public sealed class MetaSignalAppointmentBookedRequest
{
    public Guid AppointmentId { get; set; }
    public Guid LeadId { get; set; }
    public string QuoteType { get; set; } = string.Empty;
    public string PageKey { get; set; } = string.Empty;
    public string EffectivePageKey { get; set; } = string.Empty;
    public string? PageVariant { get; set; }
    public string PageMode { get; set; } = string.Empty;
    public string? Url { get; set; }
    public string? Referrer { get; set; }
    public string? SessionId { get; set; }
    public string? VisitorId { get; set; }
    public Guid? AgentTrackingProfileId { get; set; }
    public string? AgentSlug { get; set; }
    public string? UtmSource { get; set; }
    public string? UtmMedium { get; set; }
    public string? UtmCampaign { get; set; }
    public string? UtmId { get; set; }
    public string? UtmContent { get; set; }
    public string? Fbclid { get; set; }
    public string? Email { get; set; }
    public string? Phone { get; set; }
    public bool AllowHashedContactData { get; set; }
    public string? CalendarEventId { get; set; }
    public string? CalendarEventWebLink { get; set; }
    public DateTime? ScheduledStartUtc { get; set; }
    public DateTime? ScheduledEndUtc { get; set; }
    public string? BookingSource { get; set; }
    public string? ConfirmationSource { get; set; }
    public string? PixelId { get; set; }
    public string? AccessToken { get; set; }
    public string? TestEventCode { get; set; }
    public string? PixelOwnerType { get; set; }
}

public sealed class MetaSignalProcessResult
{
    public bool Accepted { get; set; }
    public bool Skipped { get; set; }
    public bool Duplicate { get; set; }
    public string EventName { get; set; } = string.Empty;
    public string EventId { get; set; } = string.Empty;
    public string ScoreTier { get; set; } = string.Empty;
    public int IntentScore { get; set; }
    public int EngagementScore { get; set; }
    public int QualificationScore { get; set; }
    public int FrictionScore { get; set; }
    public int TotalSignalScore { get; set; }
    public bool MetaBrowserSent { get; set; }
    public bool MetaServerSent { get; set; }
    public string MetaServerStatus { get; set; } = "not_attempted";
    public string? MetaServerNote { get; set; }
    public string? DeduplicationKey { get; set; }
}
