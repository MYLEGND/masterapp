using Microsoft.AspNetCore.Http;
using Domain.Entities;
using Infrastructure.Leads;
using Shared.Analytics;

namespace Infrastructure.Analytics;

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
        string? utmTerm = null,
        string? utmContent = null,
        string? metaCampaignId = null,
        string? metaAdSetId = null,
        string? metaAdId = null,
        string? fbclid = null,
        string? gclid = null,
        string? ttclid = null,
        string? oppref = null,
        string? obref = null,
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
        object? metadata = null,
        Guid? websiteContentVersionId = null,
        string? websiteBindingId = null,
        string? measurementConsentState = null)
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
        var measurementConsent = ResolveMeasurementConsent(request, measurementConsentState);

        return new UnifiedEventContext
        {
            WebsiteContentVersionId = websiteContentVersionId,
            WebsiteBindingId = websiteBindingId,
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
            UtmTerm = utmTerm,
            UtmContent = utmContent,
            MetaCampaignId = metaCampaignId,
            MetaAdSetId = metaAdSetId,
            MetaAdId = metaAdId,

            Fbclid = fbclid,
            Gclid = PaidAdsClickReference.NormalizeGoogle(gclid),
            Ttclid = PaidAdsClickReference.NormalizeTikTok(ttclid),
            Oppref = OpenAiClickReference.Normalize(oppref),
            Obref = ResolveOpenAiBrowserReference(request, obref),
            Fbc = ResolveMarketingCookie(request, "_fbc"),
            Fbp = ResolveMarketingCookie(request, "_fbp"),

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
            IsServerAuthority = isServerAuthority ?? (!resolvedIsBrowserSignal && AnalyticsEventCatalog.IsServerAllowed(normalizedEventName) && !AnalyticsEventCatalog.IsBrowserAllowed(normalizedEventName)),
            MetaServerAuthorityEligible = resolvedMetaServerAuthorityEligible,
            MeasurementConsentAllowed = measurementConsent.Allowed,
            MeasurementConsentState = measurementConsent.State,
            MeasurementConsentSource = measurementConsent.Source,
            Metadata = metadata
        };
    }

    public static UnifiedEventContext BuildWebsiteLead(
        HttpContext? httpContext,
        WebsiteLead lead,
        string eventName,
        object? metadata = null,
        string? pageKey = null,
        string? pageVariant = null,
        string? pageMode = null,
        DateTime? eventUtc = null,
        string? quoteType = null,
        bool? isBrowserSignal = null,
        bool? isServerAuthority = null,
        bool? metaServerAuthorityEligible = null)
    {
        ArgumentNullException.ThrowIfNull(lead);
        var effectivePageKey = string.IsNullOrWhiteSpace(pageKey) ? lead.SourcePageKey : pageKey.Trim();
        return Build(
            httpContext,
            eventId: AnalyticsEventCatalog.TryGet(eventName, out var definition) && definition.CountsAsConfirmedLead
                ? CanonicalLeadEventIdentity.Resolve(lead)
                : null,
            eventName: eventName,
            eventUtc: eventUtc,
            sessionId: lead.SessionId,
            visitorId: lead.VisitorId,
            pageKey: effectivePageKey,
            effectivePageKey: effectivePageKey,
            pageVariant: pageVariant,
            pageMode: pageMode,
            utmSource: lead.UtmSource,
            utmMedium: lead.UtmMedium,
            utmCampaign: lead.UtmCampaign,
            utmId: lead.UtmId,
            utmTerm: CanonicalAdvertisingEventProjection.ReadString(lead.MetadataJson, "UtmTerm"),
            utmContent: CanonicalAdvertisingEventProjection.ReadString(lead.MetadataJson, "UtmContent"),
            metaCampaignId: lead.MetaCampaignId,
            metaAdSetId: lead.MetaAdSetId,
            metaAdId: lead.MetaAdId,
            fbclid: lead.Fbclid,
            gclid: CanonicalAdvertisingEventProjection.ReadString(lead.MetadataJson, "Gclid"),
            ttclid: CanonicalAdvertisingEventProjection.ReadString(lead.MetadataJson, "Ttclid"),
            oppref: lead.Oppref,
            agentSlug: lead.AgentSlug,
            agentTrackingProfileId: lead.AgentTrackingProfileId,
            isInternal: lead.IsInternal,
            environment: lead.Environment,
            host: lead.Host,
            quoteType: string.IsNullOrWhiteSpace(quoteType) ? lead.InterestType : quoteType,
            isBrowserSignal: isBrowserSignal,
            isServerAuthority: isServerAuthority,
            metaServerAuthorityEligible: metaServerAuthorityEligible,
            metadata: metadata,
            websiteContentVersionId: lead.WebsiteContentVersionId,
            websiteBindingId: lead.WebsiteBindingId);
    }

    public static string? ResolveOpenAiBrowserReference(HttpRequest? request, string? explicitValue = null)
    {
        if (!CanUseOpenAiBrowserReference(request)) return null;
        return OpenAiBrowserReference.Normalize(explicitValue)
            ?? OpenAiBrowserReference.Normalize(MetaLeadTrackingWorkflow.ResolveCookieValue(request, "__obref"));
    }

    public static bool CanUseOpenAiBrowserReference(HttpRequest? request) =>
        CanUseMarketingIdentifiers(request);

    public static string? ResolveMarketingCookie(HttpRequest? request, string cookieName)
    {
        if (!CanUseMarketingIdentifiers(request)) return null;
        return MetaLeadTrackingWorkflow.ResolveCookieValue(request, cookieName);
    }

    public sealed record MeasurementConsentResolution(bool Allowed, string State, string Source);

    public static MeasurementConsentResolution ResolveMeasurementConsent(HttpRequest? request, string? submittedState = null)
    {
        if (request is null)
            return new(true, "granted", "default_policy");

        var gpc = request.Headers["Sec-GPC"].FirstOrDefault()?.Trim();
        if (string.Equals(gpc, "1", StringComparison.Ordinal))
            return new(false, "denied", "gpc");

        var cookie = MetaLeadTrackingWorkflow.ResolveCookieValue(request, "legend_measurement_consent")?.Trim();
        if (string.Equals(cookie, "denied", StringComparison.OrdinalIgnoreCase))
            return new(false, "denied", "stored_choice");

        var submitted = submittedState?.Trim();
        if (string.Equals(submitted, "denied", StringComparison.OrdinalIgnoreCase))
            return new(false, "denied", "submitted_state");
        if (string.Equals(submitted, "unknown", StringComparison.OrdinalIgnoreCase))
            return new(false, "unknown", "submitted_state");
        if (string.Equals(submitted, "granted", StringComparison.OrdinalIgnoreCase))
            return new(true, "granted", "submitted_state");

        if (string.Equals(cookie, "granted", StringComparison.OrdinalIgnoreCase))
            return new(true, "granted", "stored_choice");

        return new(true, "granted", "default_policy");
    }

    public static bool CanUseMarketingIdentifiers(HttpRequest? request) =>
        ResolveMeasurementConsent(request).Allowed;

}
