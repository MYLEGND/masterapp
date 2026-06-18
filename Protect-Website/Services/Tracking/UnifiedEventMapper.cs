using Domain.Entities;

namespace ProtectWebsite.Services.Tracking;

/// <summary>
/// SINGLE SOURCE OF TRUTH:
/// Converts UnifiedEventContext → all downstream event models
/// </summary>
public static class UnifiedEventMapper
{
    public static AnalyticsEvent ToAnalytics(UnifiedEventContext ctx)
    {
        return new AnalyticsEvent
        {
            EventId = Guid.NewGuid(),
            PipelineStamp = UnifiedAnalyticsWriter.PipelineStamp,
            EventType = ctx.EventName ?? "unknown",
            PageKey = ctx.PageKey,
            FormKey = string.IsNullOrWhiteSpace(ctx.FormKey) && !string.IsNullOrWhiteSpace(ctx.PageKey)
                ? $"{ctx.PageKey}_form"
                : ctx.FormKey,
            QuoteType = ctx.QuoteType,
            Referrer = ctx.Referrer,
            SessionId = ctx.SessionId,
            VisitorId = ctx.VisitorId,
            UtmSource = ctx.UtmSource,
            UtmMedium = ctx.UtmMedium,
            UtmCampaign = ctx.UtmCampaign,
            UtmId = ctx.UtmId,
            UtmContent = ctx.UtmContent,
            MetaCampaignId = ctx.MetaCampaignId,
            MetaAdSetId = ctx.MetaAdSetId,
            MetaAdId = ctx.MetaAdId,

            Fbclid = ctx.Fbclid,
            AgentSlug = ctx.AgentSlug,
            AgentTrackingProfileId = ctx.AgentTrackingProfileId,

            IsInternal = ctx.IsInternal ?? false,
            Environment = ctx.Environment,
            Host = ctx.Host,

            DeviceType = ctx.DeviceType,
            Browser = ctx.Browser,
            OperatingSystem = ctx.OperatingSystem,

            UserAgent = ctx.UserAgent,
            IpAddress = ctx.IpAddress,

            ViewportWidth = ctx.ViewportWidth,
            ViewportHeight = ctx.ViewportHeight,
            ScreenWidth = ctx.ScreenWidth,
            ScreenHeight = ctx.ScreenHeight,

            WebDriver = ctx.WebDriver,
            IsHeadless = ctx.IsHeadless,

            MouseMoveCount = ctx.MouseMoveCount,
            HumanInteractionCount = ctx.HumanInteractionCount,
            VisibilityChangeCount = ctx.VisibilityChangeCount,

            Language = ctx.Language,
            TimeZone = ctx.TimeZone,

            EventUtc = ctx.EventUtc ?? DateTime.UtcNow,
            ReceivedUtc = DateTime.UtcNow,

            MetadataJson = ctx.Metadata == null ? null : System.Text.Json.JsonSerializer.Serialize(ctx.Metadata)
        };
    }

    public static MetaSignalEvent ToMetaSignal(UnifiedEventContext ctx)
    {
        return new MetaSignalEvent
        {
            CreatedUtc = DateTime.UtcNow,

            EventId = ctx.EventId ?? Guid.NewGuid().ToString(),
            EventName = ctx.EventName ?? "unknown",
            EventCategory = ctx.EventCategory,

            SessionId = ctx.SessionId,
            VisitorId = ctx.VisitorId,

            QuoteType = ctx.QuoteType,
            PageKey = ctx.PageKey,
            EffectivePageKey = ctx.EffectivePageKey,
            PageVariant = ctx.PageVariant,
            PageMode = ctx.PageMode,

            UtmSource = ctx.UtmSource,
            UtmMedium = ctx.UtmMedium,
            UtmCampaign = ctx.UtmCampaign,
            UtmId = ctx.UtmId,
            UtmContent = ctx.UtmContent,

            FbclidPresent = !string.IsNullOrEmpty(ctx.Fbclid),
            FbcPresent = !string.IsNullOrEmpty(ctx.Fbc),
            FbpPresent = !string.IsNullOrEmpty(ctx.Fbp),

            Referrer = ctx.Referrer,

            DeviceType = ctx.DeviceType,
            Browser = ctx.Browser,
            OperatingSystem = ctx.OperatingSystem,
            UserAgent = ctx.UserAgent,

            ViewportWidth = ctx.ViewportWidth,
            ViewportHeight = ctx.ViewportHeight,
            ScreenWidth = ctx.ScreenWidth,
            ScreenHeight = ctx.ScreenHeight,

            WebDriver = ctx.WebDriver,
            IsHeadless = ctx.IsHeadless,

            MouseMoveCount = ctx.MouseMoveCount,
            HumanInteractionCount = ctx.HumanInteractionCount,
            VisibilityChangeCount = ctx.VisibilityChangeCount,

            Language = ctx.Language,
            TimeZone = ctx.TimeZone,

            AgentSlug = ctx.AgentSlug,
            AgentTrackingProfileId = ctx.AgentTrackingProfileId,

            Environment = null,
            Host = null
        };
    }
}
