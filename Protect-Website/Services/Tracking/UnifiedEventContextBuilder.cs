using Microsoft.AspNetCore.Http;
using ProtectWebsite.Services.Meta;
using Shared.Analytics;

namespace ProtectWebsite.Services.Tracking;

public static class UnifiedEventContextBuilder
{
    public static UnifiedEventContext Build(
        HttpContext? httpContext,
        string? eventId = null,
        string? eventName = null,
        string? eventCategory = null,
        DateTime? eventUtc = null,
        string? sessionId = null,
        string? visitorId = null,
        string? url = null,
        string? referrer = null,
        string? referrerHost = null,
        string? pageKey = null,
        string? effectivePageKey = null,
        string? pageVariant = null,
        string? pageMode = null,
        string? formKey = null,
        string? utmSource = null,
        string? utmMedium = null,
        string? utmCampaign = null,
        string? utmId = null,
        string? utmContent = null,
        string? metaCampaignId = null,
        string? metaAdSetId = null,
        string? metaAdId = null,
        string? fbclid = null,
        string? agentSlug = null,
        Guid? agentTrackingProfileId = null,
        bool? isInternal = null,
        string? environment = null,
        string? host = null,
        string? quoteType = null,
        int? stepNumber = null,
        string? stepName = null,
        int? scrollPercent = null,
        long? dwellMilliseconds = null,
        long? engagedMilliseconds = null,
        bool? isBounceCandidate = null,
        bool? isExitPage = null,
        bool? browserEventSent = null,
        bool? isBrowserSignal = null,
        bool? isServerAuthority = null,
        bool? metaServerAuthorityEligible = null,
        object? metadata = null)
    {
        var clientContext = httpContext != null
            ? RequestContextAccessor.Resolve(httpContext)
            : new ClientContextResolution();

        var request = httpContext?.Request;
        var normalizedEventName = string.IsNullOrWhiteSpace(eventName) ? null : eventName.Trim();
        var resolvedIsBrowserSignal = isBrowserSignal == true;
        var resolvedMetaServerAuthorityEligible = metaServerAuthorityEligible ??
            (!resolvedIsBrowserSignal &&
             AnalyticsEventCatalog.IsServerAllowed(normalizedEventName) &&
             !AnalyticsEventCatalog.IsBrowserAllowed(normalizedEventName));

        return new UnifiedEventContext
        {
            EventId = eventId,
            EventName = eventName,
            EventCategory = eventCategory,
            EventUtc = eventUtc,

            SessionId = sessionId,
            VisitorId = visitorId,

            Url = url,
            Referrer = referrer,
            ReferrerHost = referrerHost,
            PageKey = pageKey,
            EffectivePageKey = effectivePageKey,
            PageVariant = pageVariant,
            PageMode = pageMode,
            FormKey = formKey,

            DeviceType = clientContext.DeviceType,
            Browser = clientContext.Browser,
            OperatingSystem = clientContext.OperatingSystem,
            UserAgent = clientContext.UserAgent,
            IpAddress = MetaLeadTrackingWorkflow.ResolveClientIpAddress(request),

            ViewportWidth = clientContext.ViewportWidth,
            ViewportHeight = clientContext.ViewportHeight,
            ScreenWidth = clientContext.ScreenWidth,
            ScreenHeight = clientContext.ScreenHeight,

            WebDriver = clientContext.WebDriver,
            IsHeadless = clientContext.IsHeadless,

            MouseMoveCount = clientContext.MouseMoveCount,
            HumanInteractionCount = clientContext.HumanInteractionCount,
            VisibilityChangeCount = clientContext.VisibilityChangeCount,
            ScrollPercent = scrollPercent,
            DwellMilliseconds = dwellMilliseconds,
            EngagedMilliseconds = engagedMilliseconds,
            IsBounceCandidate = isBounceCandidate,
            IsExitPage = isExitPage,

            Language = clientContext.Language,
            TimeZone = clientContext.TimeZone,

            UtmSource = utmSource,
            UtmMedium = utmMedium,
            UtmCampaign = utmCampaign,
            UtmId = utmId,
            UtmContent = utmContent,
            MetaCampaignId = metaCampaignId,
            MetaAdSetId = metaAdSetId,
            MetaAdId = metaAdId,

            Fbclid = fbclid,
            Fbc = MetaLeadTrackingWorkflow.ResolveCookieValue(request, "_fbc"),
            Fbp = MetaLeadTrackingWorkflow.ResolveCookieValue(request, "_fbp"),

            AgentSlug = agentSlug,
            AgentTrackingProfileId = agentTrackingProfileId,
            IsInternal = isInternal,
            Environment = environment,
            Host = host,

            QuoteType = quoteType,
            StepNumber = stepNumber,
            StepName = stepName,

            BrowserEventSent = browserEventSent,
            IsBrowserSignal = resolvedIsBrowserSignal,
            IsServerAuthority = isServerAuthority == true,
            MetaServerAuthorityEligible = resolvedMetaServerAuthorityEligible,
            Metadata = metadata
        };
    }
}
