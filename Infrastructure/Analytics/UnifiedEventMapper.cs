using Domain.Entities;
using Shared.Analytics;

namespace Infrastructure.Analytics;

/// <summary>
/// SINGLE SOURCE OF TRUTH:
/// Converts UnifiedEventContext → all downstream event models
/// </summary>
public static class UnifiedEventMapper
{
    private const string SiteKey = "ProtectWebsite";
    private const string BusinessType = "Insurance";
    private const string ReportingOwner = "AgentPortal";

    public static AnalyticsEvent ToAnalytics(UnifiedEventContext ctx)
    {
        if (ctx.IsBrowserSignal == true && AnalyticsEventCatalog.RequiresServerAuthority(ctx.EventName))
            throw new InvalidOperationException("Browser observations cannot create verified server outcomes.");
        return new AnalyticsEvent
        {
            EventId = Guid.NewGuid(),
            PipelineStamp = UnifiedAnalyticsWriter.PipelineStamp,
            EventType = ctx.EventName ?? "unknown",
            Url = ctx.Url,
            PageKey = ctx.PageKey,
            ElementKey = ctx.ElementKey,
            ButtonLabel = ctx.ButtonLabel,
            FormKey = string.IsNullOrWhiteSpace(ctx.FormKey) && !string.IsNullOrWhiteSpace(ctx.PageKey)
                ? $"{ctx.PageKey}_form"
                : ctx.FormKey,
            QuoteType = ctx.QuoteType,
            Referrer = ctx.Referrer,
            ReferrerHost = ctx.ReferrerHost,
            SessionId = ctx.SessionId,
            VisitorId = ctx.VisitorId,
            UtmSource = ctx.UtmSource,
            UtmMedium = ctx.UtmMedium,
            UtmCampaign = ctx.UtmCampaign,
            UtmId = ctx.UtmId,
            UtmTerm = ctx.UtmTerm,
            UtmContent = ctx.UtmContent,
            MetaCampaignId = ctx.MetaCampaignId,
            MetaAdSetId = ctx.MetaAdSetId,
            MetaAdId = ctx.MetaAdId,

            Fbclid = ctx.Fbclid,
            Oppref = OpenAiClickReference.Normalize(ctx.Oppref),
            AgentSlug = ctx.AgentSlug,
            AgentTrackingProfileId = ctx.CommerceBusinessId.HasValue ? null : ctx.AgentTrackingProfileId,
            CommerceBusinessId = ctx.CommerceBusinessId,
            WebsiteContentVersionId = ctx.WebsiteContentVersionId,
            WebsiteBindingId = ctx.WebsiteBindingId,

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
            ScrollPercent = ctx.ScrollPercent,
            DwellMilliseconds = ctx.DwellMilliseconds,
            EngagedMilliseconds = ctx.EngagedMilliseconds,
            IsBounceCandidate = ctx.IsBounceCandidate,
            IsExitPage = ctx.IsExitPage,

            Language = ctx.Language,
            TimeZone = ctx.TimeZone,

            EventUtc = ctx.EventUtc ?? DateTime.UtcNow,
            ReceivedUtc = DateTime.UtcNow,

            MetadataJson = MetaSignalSingleTruthPolicy.BuildMetadataJson(
                ctx.EventName,
                leadId: null,
                ctx.SessionId,
                BuildAnalyticsMetadata(ctx),
                isBrowserSignal: ctx.IsBrowserSignal == true,
                isServerAuthority: ctx.IsServerAuthority == true,
                metaServerAuthorityEligible: ctx.MetaServerAuthorityEligible == true,
                metaSingleTruthDispatchEligible: false)
        };
    }

    public static MetaSignalEvent ToMetaSignal(UnifiedEventContext ctx)
    {
        return new MetaSignalEvent
        {
            CreatedUtc = ctx.EventUtc ?? DateTime.UtcNow,

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
            AgentTrackingProfileId = ctx.CommerceBusinessId.HasValue ? null : ctx.AgentTrackingProfileId,
            CommerceBusinessId = ctx.CommerceBusinessId,
            WebsiteContentVersionId = ctx.WebsiteContentVersionId,
            WebsiteBindingId = ctx.WebsiteBindingId,

            Environment = ctx.Environment,
            Host = ctx.Host
        };
    }

    private static object BuildAnalyticsMetadata(UnifiedEventContext ctx) => new
    {
        siteKey = string.IsNullOrWhiteSpace(ctx.SiteKey)
            ? (ctx.CommerceBusinessId.HasValue ? "BusinessWebsite" : SiteKey)
            : ctx.SiteKey,
        businessType = ctx.CommerceBusinessId.HasValue ? "Business" : BusinessType,
        reportingOwner = ctx.CommerceBusinessId.HasValue ? "Business" : ReportingOwner,
        behaviorKey = AnalyticsEventCatalog.TryGetBehavior(ctx.EventName, out var behavior) ? behavior.Key : null,
        actionKey = ctx.ActionKey ?? (AnalyticsEventCatalog.TryGetBehavior(ctx.EventName, out var action) ? action.Key : null),
        oppref = OpenAiClickReference.Normalize(ctx.Oppref),
        fbc = ctx.Fbc,
        fbp = ctx.Fbp,
        canonicalOutcomeEventId = ctx.IsServerAuthority == true ? ctx.EventId : null,
        payload = ctx.Metadata
    };
}
